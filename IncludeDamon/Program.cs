using System.Diagnostics;
using System.Globalization;
using Humanizer;
using Humanizer.Bytes;
using Humanizer.Localisation;
using k8s;
using k8s.Models;
using Microsoft.Extensions.Caching.Memory;
using ThrottleDebounce;
using static System.String;

namespace IncludeDamon;

internal abstract class Program
{
    private static async Task Main()
    {
        CancellationTokenSource cancellationTokenSource = new();

        CancellationToken cancellationToken = cancellationTokenSource.Token;

        AppDomain.CurrentDomain.ProcessExit += (_, _) => { cancellationTokenSource.Cancel(); };

        Console.CancelKeyPress += (_, _) => { cancellationTokenSource.Cancel(); };

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Client.StartAsync(cancellationToken);
            }
            catch (ConfigurationException e)
            {
                Console.WriteLine($"[CONFIG ERROR] {e.Message}");

                Environment.ExitCode = 1;

                return;
            }
            catch (Exception e)
            {
                Console.WriteLine(e);
            }
        }
    }

    private static readonly HttpClient HttpClient = new();

    public static async Task SendMessage(string message)
    {
        string time = DateTime.UtcNow.UtcToSe().TimeOfDay.ToString(@"hh\:mm\:ss");

        string slackWebhookUrl = MonitorConfiguration.SlackWebhookUrl;

        await HttpClient.PostAsync(slackWebhookUrl, new StringContent($"{{\"text\":\"{message} _({time})_\"}}"));
    }
}

internal sealed class ConfigurationException(string message) : Exception(message);

internal abstract class Icons
{
    public const string Check = "✅";

    public const string Warning = "⚠️";

    public const string Fail = "❌";

    public const string Start = "🏁";

    public const string RedCircle = "🔴";

    public const string GreenCircle = "🟢";

    public const string Fire = "🔥";
}

internal enum Status
{
    Unknown,

    Ok,

    Redeploying,

    Problem
}

internal class Client : IDisposable
{
    private readonly Kubernetes _kubernetes = new(KubernetesClientConfiguration.InClusterConfig());

    private readonly object _removeLock = new();

    private readonly List<PodMonitor> _monitoredPods = [];

    private readonly MonitorTarget[] _targets = MonitorConfiguration.Targets;

    private readonly Dictionary<string, Dictionary<string, IDictionary<string, ResourceQuantity>>> _metrics = [];

    private readonly RateLimitedAction<CancellationToken> _onStabilized;

    private const float DefaultWaitFactor = 2;

    private float _waitFactor = DefaultWaitFactor;

    private Client()
    {
        _onStabilized = Debouncer.Debounce<CancellationToken>(OnStabilized, TimeSpan.FromSeconds(15));
    }

    private void OnStabilized(CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested) return;

        int desiredPods = DesiredPods(_targets);

        int count = _monitoredPods.Count(monitor => monitor.Status is Status.Ok);

        Console.WriteLine(
            $"[ON STABILIZED] Desired: {desiredPods}, Count: {count}, {Join(", ", _monitoredPods.Select(monitor => $"{monitor.Name}({monitor.Status})"))}");

        if (count != desiredPods) return;

        _waitFactor = DefaultWaitFactor;

        Console.WriteLine("[STABILIZED]");

        Program.SendMessage($"{Icons.Check}{Icons.Check} System stable").Wait(cancellationToken);
    }

    public static async Task StartAsync(CancellationToken cancellationToken)
    {
        using Client client = new();

        await client.RunAsync(cancellationToken);
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        Console.WriteLine("[REFRESH STARTED]");

        while (!cancellationToken.IsCancellationRequested)
        {
            DateTime now = DateTime.Now;

            await RefreshPodListAsync(cancellationToken);

            TimeSpan remainingMilliseconds =
                TimeSpan.FromMilliseconds(Math.Max(1000 - DateTime.Now.Subtract(now).TotalMilliseconds, 0));

            await Task.Delay(remainingMilliseconds, cancellationToken);
        }
    }

    private async Task<List<V1Pod>> GetPodsAsync(MonitorTarget target)
    {
        return
        [
            ..(await _kubernetes.ListNamespacedPodAsync(target.Namespace, labelSelector: target.LabelSelector))
            .Items
        ];
    }

    private readonly MemoryCache _cache = new(new MemoryCacheOptions());

    private int DesiredPods(IEnumerable<MonitorTarget> targets)
    {
        const string key = "DesiredPods";

        if (_cache.TryGetValue(key, out int count))
        {
            return count;
        }

        count = 0;

        foreach (MonitorTarget target in targets)
        {
            switch (target.ResourceType)
            {
                case "daemonsets":
                {
                    count += _kubernetes.ReadNamespacedDaemonSet(target.ResourceName, target.Namespace).Status
                        .DesiredNumberScheduled;

                    break;
                }
                case "deployments":
                {
                    count += _kubernetes.ReadNamespacedDeployment(target.ResourceName, target.Namespace).Spec
                        .Replicas ?? 1;

                    break;
                }
            }
        }

        _cache.Set(key, count, TimeSpan.FromSeconds(45));

        return count;
    }

    private async Task SetMetricsAsync()
    {
        foreach (string namespaceName in _targets.Select(target => target.Namespace).Distinct())
        {
            _metrics[namespaceName] =
                (await _kubernetes.GetKubernetesPodsMetricsByNamespaceAsync(namespaceName)).Items.ToDictionary(
                    pod => pod.Metadata.Name, pod => pod.Containers.First().Usage);
        }
    }

    private void RemovePodMonitor(PodMonitor monitor, bool increaseWaitFactor)
    {
        lock (_removeLock)
        {
            try
            {
                if (increaseWaitFactor)
                {
                    _waitFactor = (float)Math.Min(Math.Round(_waitFactor * 1.75), 20);
                }

                _monitoredPods.Remove(monitor);

                monitor.Dispose();
            }
            catch (Exception e)
            {
                Console.WriteLine($"[ERROR REMOVING MONITOR] {e.Message}");
            }
        }
    }

    private async Task RefreshPodListAsync(CancellationToken cancellationToken)
    {
        await SetMetricsAsync();

        var podGroups =
            await Task.WhenAll(_targets.Select(async target => new { target, pods = await GetPodsAsync(target) }));

        List<(MonitorTarget target, V1Pod pod)> pods = podGroups
            .SelectMany(group => group.pods.Select(pod => (group.target, pod)))
            .Where(tuple => tuple.pod.Status.Phase is "Running" or "Pending")
            .ToList();

        lock (_removeLock)
        {
            List<PodMonitor> extraMonitors = _monitoredPods.Where(monitor => pods.All(tuple =>
                monitor.Name != $"{tuple.pod.Metadata.NamespaceProperty}/{tuple.pod.Metadata.Name}")).ToList();

            _monitoredPods.RemoveAll(extraMonitors.Contains);

            List<(MonitorTarget target, V1Pod pod)> newPods = pods.Where(tuple =>
                _monitoredPods.All(monitor =>
                    monitor.Name != $"{tuple.pod.Metadata.NamespaceProperty}/{tuple.pod.Metadata.Name}")).ToList();

            _monitoredPods.AddRange(newPods.Select(tuple => new PodMonitor(tuple.pod, tuple.target,
                RemovePodMonitor, _onStabilized, _metrics, _waitFactor, cancellationToken).Monitor()));

            if (newPods.Count > 0 || extraMonitors.Count > 0)
            {
                if (newPods.Count > 0)
                {
                    Console.WriteLine(
                        $"[NEW PODS] {Join(", ", newPods.Select(tuple => $"{tuple.pod.Metadata.NamespaceProperty}/{tuple.pod.Metadata.Name}"))}");
                }

                if (extraMonitors.Count > 0)
                {
                    Console.WriteLine(
                        $"[EXTRA MONITORS] {Join(", ", extraMonitors.Select(monitor => monitor.Name))}");
                }

                Console.WriteLine(
                    $"[CURRENT MONITORS] {Join(", ", _monitoredPods.Select(monitor => monitor.Name))}");
            }

            extraMonitors.ForEach(monitor => monitor.Dispose());
        }
    }

    public void Dispose()
    {
        _kubernetes.Dispose();
    }
}

internal class PodMonitor : IDisposable
{
    private readonly V1Pod _pod;

    private readonly Kubernetes _kubernetes;

    private readonly CancellationToken _cancellationToken;

    private readonly Dictionary<string, Dictionary<string, IDictionary<string, ResourceQuantity>>> _metrics;

    private readonly Action<PodMonitor, bool> _removePodMonitor;

    private readonly RateLimitedAction<CancellationToken> _onStabilized;

    private readonly HttpClient _client;

    private readonly float _waitFactor;

    private readonly string[] _paths;

    private readonly Uri _externalBaseUri;

    private volatile bool _disposed;

    private string _lastPhase;

    private Status _status;

    public string Name { get; }

    public Status Status { get; private set; }

    private double SecondsAlive => _pod.GetSecondsAlive();

    public PodMonitor(V1Pod pod, MonitorTarget target, Action<PodMonitor, bool> removePodMonitor,
        RateLimitedAction<CancellationToken> onStabilized,
        Dictionary<string, Dictionary<string, IDictionary<string, ResourceQuantity>>> metrics, float waitFactor,
        CancellationToken cancellationToken)
    {
        _pod = pod;

        _paths = target.Paths;

        _externalBaseUri = target.ExternalBaseUri;

        string hostHeader = target.HostHeader;

        _lastPhase = _pod.Status.Phase;

        _client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            Timeout = MonitorConfiguration.ResponseTimeout
        };

        _client.DefaultRequestHeaders.Host = hostHeader;

        _kubernetes = new Kubernetes(KubernetesClientConfiguration.InClusterConfig());

        _removePodMonitor = removePodMonitor;

        _onStabilized = onStabilized;

        _cancellationToken = cancellationToken;

        _waitFactor = waitFactor;

        _metrics = metrics;

        _status = Status.Unknown;

        Name = $"{_pod.Metadata.NamespaceProperty}/{_pod.Metadata.Name}";

        Status = Status.Unknown;
    }

    private bool CheckResourcesHealthy(V1Pod pod)
    {
        try
        {
            if (!_metrics.TryGetValue(pod.Metadata.NamespaceProperty,
                    out Dictionary<string, IDictionary<string, ResourceQuantity>>? namespaceDictionary) ||

                !namespaceDictionary.TryGetValue(pod.Metadata.Name,
                    out IDictionary<string, ResourceQuantity>? usage)) return true;

            double cpuUsage = usage["cpu"].ToDouble();

            double memoryUsage = usage["memory"].ToDouble();

            IDictionary<string, ResourceQuantity> limits = pod.Spec.Containers.First().Resources.Limits;

            double cpuLimit = limits["cpu"].ToDouble();

            double memoryLimit = limits["memory"].ToDouble();

            double restartThreshold = MonitorConfiguration.RestartThreshold;

            return cpuUsage / cpuLimit < restartThreshold && memoryUsage / memoryLimit < restartThreshold;
        }
        catch
        {
            return true;
        }
    }

    private async Task<string> GetResourcesAsync(V1Pod pod)
    {
        try
        {
            IDictionary<string, ResourceQuantity>? usage = null;

            _metrics.TryGetValue(pod.Metadata.NamespaceProperty,
                out Dictionary<string, IDictionary<string, ResourceQuantity>>? namespaceDictionary);

            namespaceDictionary?.TryGetValue(pod.Metadata.Name, out usage);

            usage ??= (await _kubernetes.GetKubernetesPodsMetricsByNamespaceAsync(pod.Metadata.NamespaceProperty))
                .Items.SingleOrDefault(p => pod.Metadata.Name == p.Metadata.Name)?.Containers.First().Usage;

            if (usage is null) return "";

            double cpuUsage = usage["cpu"].ToDouble();

            double memoryUsage = usage["memory"].ToDouble();

            IDictionary<string, ResourceQuantity> limits = pod.Spec.Containers.First().Resources.Limits;

            double cpuLimit = limits["cpu"].ToDouble();

            double memoryLimit = limits["memory"].ToDouble();

            return
                $" *[CPU: {$"{cpuUsage / cpuLimit:P1}({cpuUsage}/{cpuLimit}cores)".Replace(" ", "")}, Memory: {$"{memoryUsage / memoryLimit:P1}({ByteSize.FromBytes(memoryUsage)}/{ByteSize.FromBytes(memoryLimit)}".Replace(" ", "")})]*";
        }
        catch
        {
            return "";
        }
    }

    private async Task<V1Pod?> CheckPodExistence()
    {
        return (await _kubernetes.ListNamespacedPodAsync(_pod.Metadata.NamespaceProperty,
                fieldSelector: $"metadata.name={_pod.Metadata.Name}", cancellationToken: _cancellationToken)).Items
            .SingleOrDefault(pod => pod.Status.Phase is "Pending" or "Running");
    }

    private async Task<(int, string)> CheckUrlStatusCode(string path, V1Pod pod)
    {
        string? podIp = pod.Status.PodIP;

        string realUrl = BuildDisplayUrl(path);

        if (IsNullOrWhiteSpace(podIp))
        {
            return (0, realUrl);
        }

        string url = $"http://{podIp}{path}";

        try
        {
            return ((int)(await _client.GetAsync(url, _cancellationToken)).StatusCode, realUrl);
        }
        catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
        {
            return (999, realUrl);
        }
        catch (HttpRequestException ex)
        {
            return ((int)(ex.StatusCode ?? 0), realUrl);
        }
        catch
        {
            return (0, realUrl);
        }
    }

    private Task<(int, string)[]> CheckPodStatusCode(V1Pod pod)
    {
        return Task.WhenAll(_paths.Select(path => CheckUrlStatusCode(path, pod)));
    }

    private string BuildDisplayUrl(string path)
    {
        return new Uri(_externalBaseUri, path).ToString();
    }

    private async Task<bool> TrySelfDestructPod(bool increaseWaitFactor)
    {
        if (!MonitorConfiguration.ShouldDestroyFaultyPods)
        {
            Console.WriteLine($"[SELF DESTRUCT SKIPPED] {_pod.Metadata.Name}");

            return false;
        }

        await _kubernetes.DeleteNamespacedPodAsync(_pod.Metadata.Name, _pod.Metadata.NamespaceProperty,
            new V1DeleteOptions { GracePeriodSeconds = 0 }, cancellationToken: _cancellationToken);

        _removePodMonitor(this, increaseWaitFactor);

        return true;
    }

    public PodMonitor Monitor()
    {
        Task.Factory.StartNew(async () =>
        {
            try
            {
                Console.WriteLine(
                    $"[MONITOR STARTED] {_pod.Metadata.Name} (up for {TimeSpan.FromSeconds(SecondsAlive).Humanize(4, minUnit: TimeUnit.Second)})");

                TimeSpan issueWindow = MonitorConfiguration.IssueWindow;

                TimeSpan startupWindowBase = MonitorConfiguration.StartupWindow;

                TimeSpan startupWindow = TimeSpan.FromTicks((long)(startupWindowBase.Ticks * _waitFactor));

                TimeSpan resourceIssueWindow = MonitorConfiguration.ResourceIssueWindow;

                double restartThreshold = MonitorConfiguration.RestartThreshold;

                Stopwatch startupStopwatch = new();

                Stopwatch issueStopwatch = new();

                Stopwatch resourceStopwatch = new();

                while (!_disposed && !_cancellationToken.IsCancellationRequested)
                {
                    DateTime now = DateTime.Now;

                    if (!_disposed && await CheckPodExistence() is { } pod)
                    {
                        string phase = pod.Status.Phase;

                        if (phase == "Running")
                        {
                            if (_lastPhase == "Pending")
                            {
                                _lastPhase = phase;

                                await Program.SendMessage(
                                    $"{Icons.Start} New pod `{_pod.Metadata.Name}` started. (Startup respite: {startupWindow.Humanize(minUnit: TimeUnit.Minute)})");

                                startupStopwatch.Start();
                            }

                            (int code, string path)[] results = await CheckPodStatusCode(pod);

                            Status = results.All(result => result.code is 200 or > 300 and < 310) switch
                            {
                                true => Status.Ok,

                                false when !startupStopwatch.IsRunning => Status.Problem,

                                false when startupStopwatch.IsRunning && startupStopwatch.Elapsed < startupWindow =>
                                    Status.Redeploying,

                                _ => Status.Problem
                            };

                            if (Status != _status)
                            {
                                Console.WriteLine(
                                    $"[MONITOR RESPONSE CHANGED] {_pod.Metadata.Name} Now: {Status}, Previous: {_status}{await GetResourcesAsync(pod)}");
                            }

                            if (Status is Status.Ok && _status is Status.Problem or Status.Redeploying)
                            {
                                issueStopwatch.Reset();

                                startupStopwatch.Stop();

                                string resourcesReport = await GetResourcesAsync(pod);

                                Console.WriteLine(
                                    $"[MONITOR RESPONSE HEALTHY] {_pod.Metadata.Name}{resourcesReport}");

                                await Program.SendMessage(
                                    $"{Icons.Check} Pod `{_pod.Metadata.Name}` works fine for {Join(", ", results.Select(result => $"{result.path}"))}{resourcesReport}");

                                _onStabilized.Invoke(_cancellationToken);
                            }
                            else if (Status is Status.Problem && _status is Status.Ok or Status.Redeploying)
                            {
                                issueStopwatch.Restart();

                                string resourcesReport = await GetResourcesAsync(pod);

                                Console.WriteLine(
                                    $"[MONITOR RESPONSE FAULTY] {_pod.Metadata.Name} (Up for {TimeSpan.FromSeconds(SecondsAlive).Humanize(4, minUnit: TimeUnit.Second)}){resourcesReport}");

                                string reason =
                                    $" {Icons.RedCircle} {Join(", ", results.Where(result => result.code is not (200 or > 300 and < 310)).Select(result => $"{result.path} `{(result.code is > 0 and < 999 ? $"returns {result.code}" : result.code > 0 ? $"times out (longer than {_client.Timeout.Humanize(4, minUnit: TimeUnit.Second)})" : "does not respond")}`"))}";

                                if (results.Any(result => result.code is 200 or > 300 and < 310))
                                {
                                    reason +=
                                        $" - {Icons.GreenCircle} {Join(", ", results.Where(result => result.code is 200 or > 300 and < 310).Select(result => $"{result.path}"))}";
                                }

                                await Program.SendMessage(
                                    $"{Icons.Warning} Problem in pod `{_pod.Metadata.Name}` {reason}{resourcesReport}");
                            }

                            if (issueStopwatch.Elapsed >= issueWindow)
                            {
                                string resourcesReport = await GetResourcesAsync(pod);

                                bool destroyed = await TrySelfDestructPod(startupStopwatch.IsRunning);

                                string logLabel = destroyed
                                    ? "[MONITOR RESPONSE RESTARTED]"
                                    : "[MONITOR RESPONSE FAULTY - DESTRUCTION DISABLED]";

                                Console.WriteLine(
                                    $"{logLabel} {_pod.Metadata.Name} (Up for {TimeSpan.FromSeconds(SecondsAlive).Humanize(4, minUnit: TimeUnit.Second)}){resourcesReport}");

                                string message = destroyed
                                    ? $"{Icons.Fail} Pod `{_pod.Metadata.Name}` restarted due to connection problems{resourcesReport}"
                                    : $"{Icons.Warning} Pod `{_pod.Metadata.Name}` has connection problems but self-destruction is disabled{resourcesReport}";

                                await Program.SendMessage(message);

                                if (destroyed)
                                {
                                    break;
                                }

                                issueStopwatch.Restart();

                                startupStopwatch.Reset();
                            }

                            if (!resourceStopwatch.IsRunning && !CheckResourcesHealthy(pod))
                            {
                                resourceStopwatch.Restart();

                                string resourcesReport = await GetResourcesAsync(pod);

                                Console.WriteLine(
                                    $"[MONITOR RESOURCE FAULTY] {_pod.Metadata.Name} (Up for {TimeSpan.FromSeconds(SecondsAlive).Humanize(4, minUnit: TimeUnit.Second)}){resourcesReport}");

                                await Program.SendMessage(
                                    $"{Icons.Fire} Pod `{_pod.Metadata.Name}`'s resource usage is over {restartThreshold:P0} threshold{resourcesReport}");
                            }
                            else if (resourceStopwatch.IsRunning && CheckResourcesHealthy(pod))
                            {
                                resourceStopwatch.Reset();

                                string resourcesReport = await GetResourcesAsync(pod);

                                Console.WriteLine(
                                    $"[MONITOR RESOURCE HEALTHY] {_pod.Metadata.Name} (Up for {TimeSpan.FromSeconds(SecondsAlive).Humanize(4, minUnit: TimeUnit.Second)}){resourcesReport}");

                                await Program.SendMessage(
                                    $"{Icons.Check} Pod `{_pod.Metadata.Name}`'s resource usage is fine now{resourcesReport}");
                            }

                            if (resourceStopwatch.Elapsed >= resourceIssueWindow)
                            {
                                string resourcesReport = await GetResourcesAsync(pod);

                                bool destroyed = await TrySelfDestructPod(false);

                                string logLabel = destroyed
                                    ? "[MONITOR RESOURCE RESTARTED]"
                                    : "[MONITOR RESOURCE FAULTY - DESTRUCTION DISABLED]";

                                Console.WriteLine(
                                    $"{logLabel} {_pod.Metadata.Name} (Up for {TimeSpan.FromSeconds(SecondsAlive).Humanize(4, minUnit: TimeUnit.Second)}){resourcesReport}");

                                string message = destroyed
                                    ? $"{Icons.Fail} Pod `{_pod.Metadata.Name}` restarted due to high resource usage{resourcesReport}"
                                    : $"{Icons.Warning} Pod `{_pod.Metadata.Name}` has high resource usage but self-destruction is disabled{resourcesReport}";

                                await Program.SendMessage(message);

                                if (destroyed)
                                {
                                    break;
                                }

                                resourceStopwatch.Restart();
                            }

                            _status = Status;
                        }
                    }
                    else
                    {
                        Console.WriteLine($"[MONITOR REMOVED] {_pod.Metadata.Name}");

                        _removePodMonitor(this, false);

                        break;
                    }

                    TimeSpan remainingMilliseconds =
                        TimeSpan.FromMilliseconds(Math.Max(1000 - DateTime.Now.Subtract(now).TotalMilliseconds, 0));

                    await Task.Delay(remainingMilliseconds, _cancellationToken);
                }
            }
            catch (Exception e)
            {
                Console.WriteLine(
                    $"[MONITOR CRASHED] {_pod.Metadata.Name}{Environment.NewLine}Message: {e.Message}{Environment.NewLine}Stacktrace: {e.StackTrace}");

                _removePodMonitor(this, false);
            }

        }, _cancellationToken, TaskCreationOptions.LongRunning, TaskScheduler.Default);

        return this;
    }

    public sealed override string ToString()
    {
        return Name;
    }

    public override int GetHashCode()
    {
        return ToString().GetHashCode();
    }

    public override bool Equals(object? obj)
    {
        return obj is PodMonitor podMonitor && podMonitor.ToString() == ToString();
    }

    public void Dispose()
    {
        if (_disposed) return;

        _disposed = true;

        _kubernetes.Dispose();

        _client.Dispose();

        Console.WriteLine($"[MONITOR DISPOSED] {_pod.Metadata.Name}");
    }
}

internal static class Extensions
{
    public static double GetSecondsAlive(this V1Pod pod)
    {
        return pod.Status.StartTime != null ? DateTime.Now.Subtract(pod.Status.StartTime.Value).TotalSeconds : 0;
    }

    private static readonly TimeZoneInfo TimeZoneInfo = TimeZoneInfo.FindSystemTimeZoneById("Europe/Stockholm");

    public static DateTime UtcToSe(this DateTime timeSpan)
    {
        return TimeZoneInfo.ConvertTimeFromUtc(timeSpan, TimeZoneInfo);
    }
}

internal sealed class MonitorTarget(
    string namespaceName,
    string resourceType,
    string resourceName,
    string labelSelector,
    string[] paths,
    Uri externalBaseUri,
    string hostHeader)
{
    public string Namespace { get; } = namespaceName;

    public string ResourceType { get; } = resourceType;

    public string ResourceName { get; } = resourceName;

    public string LabelSelector { get; } = labelSelector;

    public string[] Paths { get; } = paths;

    public Uri ExternalBaseUri { get; } = externalBaseUri;

    public string HostHeader { get; } = hostHeader;

    public override string ToString()
    {
        return $"{Namespace}/{ResourceType}/{ResourceName}";
    }
}

internal static class MonitorConfiguration
{
    private static readonly Lazy<MonitorSettings> Settings = new(LoadSettings);

    private static readonly char[] TargetSeparators = [';', '\n'];

    private static readonly char[] PathSeparators = [','];

    public static string SlackWebhookUrl => Settings.Value.SlackWebhookUrl;

    public static MonitorTarget[] Targets => Settings.Value.Targets;

    public static TimeSpan ResponseTimeout => Settings.Value.ResponseTimeout;

    public static TimeSpan IssueWindow => Settings.Value.IssueWindow;

    public static TimeSpan StartupWindow => Settings.Value.StartupWindow;

    public static TimeSpan ResourceIssueWindow => Settings.Value.ResourceIssueWindow;

    public static double RestartThreshold => Settings.Value.RestartThreshold;

    public static bool ShouldDestroyFaultyPods => Settings.Value.ShouldDestroyFaultyPods;

    private static MonitorSettings LoadSettings()
    {
        string slackWebhookUrl = GetRequiredEnv("SLACK_WEBHOOK_URL");

        string labelSelectorFormat = GetRequiredEnv("LABEL_SELECTOR_FORMAT");

        double responseTimeoutSeconds = GetRequiredDouble("RESPONSE_TIMEOUT_SECONDS");

        double issueWindowSeconds = GetRequiredDouble("ISSUE_WINDOW_SECONDS");

        double startupWindowSeconds = GetRequiredDouble("STARTUP_WINDOW_SECONDS");

        double resourceIssueWindowSeconds = GetRequiredDouble("RESOURCE_ISSUE_WINDOW_SECONDS");

        double restartThreshold = GetRequiredDouble("RESTART_THRESHOLD");

        bool shouldDestroyFaultyPods = GetRequiredBool("DESTROY_FAULTY_PODS");

        string rawTargets = GetRequiredEnv("TARGETS");

        MonitorTarget[] targets = ParseTargets(rawTargets, labelSelectorFormat);

        if (targets.Length == 0)
        {
            throw new ConfigurationException(
                "Environment variable 'TARGETS' must contain at least one target definition.");
        }

        return new MonitorSettings(
            slackWebhookUrl,
            TimeSpan.FromSeconds(responseTimeoutSeconds),
            TimeSpan.FromSeconds(issueWindowSeconds),
            TimeSpan.FromSeconds(startupWindowSeconds),
            TimeSpan.FromSeconds(resourceIssueWindowSeconds),
            restartThreshold,
            shouldDestroyFaultyPods,
            targets);
    }

    private static MonitorTarget[] ParseTargets(string rawTargets, string labelSelectorFormat)
    {
        List<MonitorTarget> targets = [];

        foreach (string entry in rawTargets.Split(TargetSeparators,
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string[] segments = entry.Split('|', StringSplitOptions.TrimEntries);

            if (segments.Length < 3)
            {
                throw new ConfigurationException(
                    $"Target '{entry}' must have at least three '|' separated segments: resource, host, and paths.");
            }

            string resourceSegment = segments[0];

            string[] resourceParts = resourceSegment.Split('/',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            if (resourceParts.Length != 3)
            {
                throw new ConfigurationException(
                    $"Resource descriptor '{resourceSegment}' must follow 'namespace/resourceType/resourceName' format.");
            }

            string namespaceName = resourceParts[0];

            string resourceType = resourceParts[1];

            string resourceName = resourceParts[2];

            string hostSegment = segments[1];

            if (IsNullOrWhiteSpace(hostSegment))
            {
                throw new ConfigurationException($"Target '{entry}' must include a host segment.");
            }

            Uri externalBaseUri = ParseHost(hostSegment);

            string pathsSegment = segments[2];

            string[] paths = ParsePaths(pathsSegment);

            if (segments.Length < 4 && IsNullOrWhiteSpace(labelSelectorFormat))
            {
                throw new ConfigurationException($"Either a Label Selector for the target '{entry}' or a Label Selector Format must be provided.");
            }

            string labelSelector = segments.Length > 3 && !IsNullOrWhiteSpace(segments[3])
                ? segments[3]
                : Format(CultureInfo.InvariantCulture, labelSelectorFormat, resourceName, namespaceName);

            string hostHeader = externalBaseUri.IsDefaultPort
                ? externalBaseUri.Host
                : $"{externalBaseUri.Host}:{externalBaseUri.Port}";

            targets.Add(new MonitorTarget(namespaceName, resourceType, resourceName, labelSelector, paths,
                externalBaseUri, hostHeader));
        }

        return targets.ToArray();
    }

    private static Uri ParseHost(string hostSegment)
    {
        return !Uri.TryCreate(hostSegment, UriKind.Absolute, out Uri? uri)
            ? throw new ConfigurationException(
                $"Host '{hostSegment}' must be a valid absolute URI including scheme (e.g. https://example.com).")
            : uri;
    }

    private static string[] ParsePaths(string pathsSegment)
    {
        if (IsNullOrWhiteSpace(pathsSegment))
        {
            throw new ConfigurationException("Each target must include at least one path.");
        }

        string[] paths = pathsSegment
            .Split(PathSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(NormalizePath)
            .ToArray();

        return paths.Length == 0
            ? throw new ConfigurationException("Each target must include at least one valid path.")
            : paths;
    }

    private static string NormalizePath(string path)
    {
        return !path.StartsWith('/') ? $"/{path}" : path;
    }

    private static string GetRequiredEnv(string key)
    {
        string? value = Environment.GetEnvironmentVariable(key);

        return IsNullOrWhiteSpace(value)
            ? throw new ConfigurationException($"Environment variable '{key}' is required.")
            : value;
    }

    private static double GetRequiredDouble(string key)
    {
        string value = GetRequiredEnv(key);

        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed)
            ? parsed
            : throw new ConfigurationException($"Environment variable '{key}' must be a numeric value.");
    }

    private static bool GetRequiredBool(string key)
    {
        string value = GetRequiredEnv(key);

        if (bool.TryParse(value, out bool parsedBool))
        {
            return parsedBool;
        }

        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedInt) &&
            parsedInt is 0 or 1)
        {
            return parsedInt != 0;
        }

        throw new ConfigurationException(
            $"Environment variable '{key}' must be a boolean value (true/false or 0/1).");
    }
}

internal sealed record MonitorSettings(
    string SlackWebhookUrl,
    TimeSpan ResponseTimeout,
    TimeSpan IssueWindow,
    TimeSpan StartupWindow,
    TimeSpan ResourceIssueWindow,
    double RestartThreshold,
    bool ShouldDestroyFaultyPods,
    MonitorTarget[] Targets);
