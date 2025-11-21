using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text;
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

internal enum MonitorResourceType
{
    Unknown,

    DaemonSet,

    Deployment
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
        string? selector = target.LabelSelector;

        if (IsNullOrWhiteSpace(selector))
        {
            selector = ResolveSelectorFromResource(target);

            if (target.TryUpdateLabelSelector(selector))
            {
                Console.WriteLine($"[SELECTOR RESOLVED] {target} -> {selector}");
            }
        }

        if (IsNullOrWhiteSpace(selector))
        {
            Console.WriteLine($"[SELECTOR MISSING] {target} could not determine label selector; skipping pod lookup.");
            return [];
        }

        List<V1Pod> pods = await ListPodsAsync(target.Namespace, selector);

        if (pods.Count > 0)
        {
            return pods;
        }

        string? resolvedSelector = ResolveSelectorFromResource(target);

        if (!IsNullOrWhiteSpace(resolvedSelector) &&
            !string.Equals(resolvedSelector, selector, StringComparison.Ordinal))
        {
            if (target.TryUpdateLabelSelector(resolvedSelector))
            {
                Console.WriteLine($"[SELECTOR RESOLVED] {target} -> {resolvedSelector}");
            }

            pods = await ListPodsAsync(target.Namespace, resolvedSelector);
        }

        if (pods.Count == 0)
        {
            Console.WriteLine($"[NO PODS FOUND] {target} using selector '{target.LabelSelector ?? selector}'");
        }

        return pods;
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
            switch (target.ResourceKind)
            {
                case MonitorResourceType.DaemonSet:
                {
                    V1DaemonSet daemonSet =
                        _kubernetes.ReadNamespacedDaemonSet(target.ResourceName, target.Namespace);

                    count += daemonSet.Status?.DesiredNumberScheduled ?? 0;

                    TryUpdateLabelSelectorFromResource(target, daemonSet.Spec?.Selector);

                    break;
                }
                case MonitorResourceType.Deployment:
                {
                    V1Deployment deployment =
                        _kubernetes.ReadNamespacedDeployment(target.ResourceName, target.Namespace);

                    count += deployment.Spec?.Replicas ?? 1;

                    TryUpdateLabelSelectorFromResource(target, deployment.Spec?.Selector);

                    break;
                }
                default:
                {
                    Console.WriteLine($"[UNSUPPORTED TARGET] {target} ({target.ResourceType})");

                    break;
                }
            }
        }

        _cache.Set(key, count, TimeSpan.FromSeconds(45));

        return count;
    }

    private async Task<List<V1Pod>> ListPodsAsync(string namespaceName, string? labelSelector)
    {
        try
        {
            string? selector = IsNullOrWhiteSpace(labelSelector) ? null : labelSelector;

            V1PodList podList = await _kubernetes.ListNamespacedPodAsync(namespaceName, labelSelector: selector);

            return [..podList.Items];
        }
        catch (Exception e)
        {
            Console.WriteLine(
                $"[LIST PODS FAILED] {namespaceName} selector '{labelSelector ?? "<none>"}': {e.Message}");

            return [];
        }
    }

    private void TryUpdateLabelSelectorFromResource(MonitorTarget target, V1LabelSelector? selector)
    {
        string? resolvedSelector = BuildSelectorString(selector);

        if (target.TryUpdateLabelSelector(resolvedSelector))
        {
            Console.WriteLine($"[SELECTOR RESOLVED] {target} -> {resolvedSelector}");
        }
    }

    private string? ResolveSelectorFromResource(MonitorTarget target)
    {
        try
        {
            return target.ResourceKind switch
            {
                MonitorResourceType.DaemonSet => BuildSelectorString(
                    _kubernetes.ReadNamespacedDaemonSet(target.ResourceName, target.Namespace).Spec?.Selector),
                MonitorResourceType.Deployment => BuildSelectorString(
                    _kubernetes.ReadNamespacedDeployment(target.ResourceName, target.Namespace).Spec?.Selector),
                _ => null
            };
        }
        catch (Exception e)
        {
            Console.WriteLine($"[SELECTOR RESOLUTION FAILED] {target} ({target.ResourceType}): {e.Message}");

            return null;
        }
    }

    private static string? BuildSelectorString(V1LabelSelector? selector)
    {
        if (selector is null)
        {
            return null;
        }

        List<string> expressions = [];

        if (selector.MatchLabels is { Count: > 0 })
        {
            expressions.AddRange(selector.MatchLabels.Select(kvp => $"{kvp.Key}={kvp.Value}"));
        }

        if (selector.MatchExpressions is { Count: > 0 })
        {
            foreach (V1LabelSelectorRequirement requirement in selector.MatchExpressions)
            {
                if (IsNullOrWhiteSpace(requirement.Key) || IsNullOrWhiteSpace(requirement.OperatorProperty))
                {
                    continue;
                }

                IList<string>? values = requirement.Values;

                switch (requirement.OperatorProperty)
                {
                    case "In" when values is { Count: > 0 }:
                        expressions.Add($"{requirement.Key} in ({Join(",", values)})");
                        break;
                    case "NotIn" when values is { Count: > 0 }:
                        expressions.Add($"{requirement.Key} notin ({Join(",", values)})");
                        break;
                    case "Exists":
                        expressions.Add(requirement.Key);
                        break;
                    case "DoesNotExist":
                        expressions.Add($"!{requirement.Key}");
                        break;
                }
            }
        }

        return expressions.Count == 0 ? null : Join(",", expressions);
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

    private readonly string _scheme;

    private readonly string _verb;

    private readonly string? _payload;

    private readonly string? _contentType;

    private readonly Uri _externalBaseUri;

    private readonly TimeSpan _issueWindow;

    private readonly TimeSpan _startupWindowBase;

    private readonly TimeSpan _resourceIssueWindow;

    private readonly double _restartThreshold;

    private readonly bool _shouldDestroyFaultyPods;

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

        _scheme = target.Scheme;

        _verb = target.Verb;

        _payload = target.Payload;

        _contentType = target.ContentType;

        _externalBaseUri = target.ExternalBaseUri;

        _issueWindow = target.IssueWindow;

        _startupWindowBase = target.StartupWindow;

        _resourceIssueWindow = target.ResourceIssueWindow;

        _restartThreshold = target.RestartThreshold;

        _shouldDestroyFaultyPods = target.ShouldDestroyFaultyPods;

        string hostHeader = target.HostHeader;

        _lastPhase = _pod.Status.Phase;

        HttpClientHandler httpMessageHandler = new()
        {
            AllowAutoRedirect = false,

            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
        };

        _client = new HttpClient(httpMessageHandler)
        {
            Timeout = target.ResponseTimeout
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

            return cpuUsage / cpuLimit < _restartThreshold && memoryUsage / memoryLimit < _restartThreshold;
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

        string url = $"{_scheme}://{podIp}{path}";

        try
        {
            using HttpRequestMessage request = new(new HttpMethod(_verb), url);

            if (_verb == HttpMethod.Post.Method)
            {
                request.Content = new StringContent(_payload!, Encoding.UTF8, _contentType!);
            }

            return ((int)(await _client.SendAsync(request, _cancellationToken)).StatusCode, realUrl);
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
        if (!_shouldDestroyFaultyPods)
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

                TimeSpan issueWindow = _issueWindow;

                TimeSpan startupWindow = TimeSpan.FromTicks((long)(_startupWindowBase.Ticks * _waitFactor));

                TimeSpan resourceIssueWindow = _resourceIssueWindow;

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
                                    $"{Icons.Fire} Pod `{_pod.Metadata.Name}`'s resource usage is over {_restartThreshold:P0} threshold{resourcesReport}");
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

internal sealed class MonitorTarget
{
    internal static readonly TimeSpan DefaultResponseTimeout = TimeSpan.FromSeconds(5);
    internal static readonly TimeSpan DefaultIssueWindow = TimeSpan.FromSeconds(60);
    internal static readonly TimeSpan DefaultStartupWindow = TimeSpan.FromSeconds(120);
    internal static readonly TimeSpan DefaultResourceIssueWindow = TimeSpan.FromSeconds(300);
    internal const double DefaultRestartThreshold = 0.9;
    internal const bool DefaultShouldDestroyFaultyPods = false;

    internal MonitorTarget(
        string namespaceName,
        string resourceType,
        string resourceName,
        MonitorResourceType resourceKind,
        string[] paths,
        Uri externalBaseUri,
        string hostHeader,
        string scheme,
        string verb,
        string? payload,
        string? contentType,
        TimeSpan responseTimeout,
        TimeSpan issueWindow,
        TimeSpan startupWindow,
        TimeSpan resourceIssueWindow,
        double restartThreshold,
        bool shouldDestroyFaultyPods)
    {
        Namespace = namespaceName;
        ResourceType = resourceType;
        ResourceName = resourceName;
        ResourceKind = resourceKind;
        LabelSelector = null;
        Paths = paths;
        ExternalBaseUri = externalBaseUri;
        HostHeader = hostHeader;
        Scheme = scheme;
        Verb = verb;
        Payload = payload;
        ContentType = contentType;
        ResponseTimeout = responseTimeout;
        IssueWindow = issueWindow;
        StartupWindow = startupWindow;
        ResourceIssueWindow = resourceIssueWindow;
        RestartThreshold = restartThreshold;
        ShouldDestroyFaultyPods = shouldDestroyFaultyPods;
    }

    public string Namespace { get; }

    public string ResourceType { get; }

    public string ResourceName { get; }

    public MonitorResourceType ResourceKind { get; }

    public string? LabelSelector { get; private set; }

    public string[] Paths { get; }

    public Uri ExternalBaseUri { get; }

    public string HostHeader { get; }

    public string Scheme { get; }

    public string Verb { get; }

    public string? Payload { get; }

    public string? ContentType { get; }

    public TimeSpan ResponseTimeout { get; }

    public TimeSpan IssueWindow { get; }

    public TimeSpan StartupWindow { get; }

    public TimeSpan ResourceIssueWindow { get; }

    public double RestartThreshold { get; }

    public bool ShouldDestroyFaultyPods { get; }

    public bool TryUpdateLabelSelector(string? labelSelector)
    {
        if (IsNullOrWhiteSpace(labelSelector))
        {
            return false;
        }

        if (string.Equals(LabelSelector, labelSelector, StringComparison.Ordinal))
        {
            return false;
        }

        LabelSelector = labelSelector;

        return true;
    }

    public override string ToString()
    {
        return $"{Namespace}/{ResourceType}/{ResourceName}";
    }

    public static bool TryParseResourceKind(string resourceType, out MonitorResourceType resourceKind)
    {
        resourceKind = resourceType.Trim().ToLowerInvariant() switch
        {
            "ds" or "daemonset" or "daemonsets" => MonitorResourceType.DaemonSet,
            "deploy" or "deployment" or "deployments" => MonitorResourceType.Deployment,
            _ => MonitorResourceType.Unknown
        };

        return resourceKind != MonitorResourceType.Unknown;
    }
}

internal static class MonitorConfiguration
{
    private static readonly Lazy<MonitorSettings> Settings = new(LoadSettings);

    private static readonly JsonSerializerOptions TargetJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public static string SlackWebhookUrl => Settings.Value.SlackWebhookUrl;

    public static MonitorTarget[] Targets => Settings.Value.Targets;

    private static MonitorSettings LoadSettings()
    {
        string slackWebhookUrl = GetRequiredEnv("SLACK_WEBHOOK_URL");

        string rawTargets = GetRequiredEnv("TARGETS");

        MonitorTarget[] targets = ParseTargets(rawTargets);

        if (targets.Length == 0)
        {
            throw new ConfigurationException(
                "Environment variable 'TARGETS' must contain at least one target definition.");
        }

        return new MonitorSettings(
            slackWebhookUrl,
            targets);
    }

    private static MonitorTarget[] ParseTargets(string rawTargets)
    {
        TargetDto[]? targetDtos;

        try
        {
            targetDtos = JsonSerializer.Deserialize<TargetDto[]>(rawTargets, TargetJsonOptions);
        }
        catch (JsonException ex)
        {
            throw new ConfigurationException(
                $"Environment variable 'TARGETS' must be valid JSON describing an array of targets. {ex.Message}");
        }

        if (targetDtos is null || targetDtos.Length == 0)
        {
            throw new ConfigurationException(
                "Environment variable 'TARGETS' must contain at least one target definition.");
        }

        List<MonitorTarget> targets = [];

        for (int i = 0; i < targetDtos.Length; i++)
        {
            TargetDto dto = targetDtos[i];

            string targetLabel = !IsNullOrWhiteSpace(dto.ResourceName)
                ? dto.ResourceName!
                : $"index {i}";

            string namespaceName = RequireValue(dto.Namespace, "namespace", targetLabel);

            string resourceType = RequireValue(dto.ResourceType, "resourceType", targetLabel);

            string resourceName = RequireValue(dto.ResourceName, "resourceName", targetLabel);

            if (!MonitorTarget.TryParseResourceKind(resourceType, out MonitorResourceType resourceKind))
            {
                throw new ConfigurationException(
                    $"Resource type '{resourceType}' for target '{targetLabel}' is not supported. Use 'daemonset' or 'deployment'.");
            }

            Uri externalBaseUri = ParseHost(RequireValue(dto.Host, "host", targetLabel));

            string[] paths = ParsePaths(dto.Paths);

            string scheme = NormalizeScheme(dto.Scheme ?? externalBaseUri.Scheme);

            string verb = NormalizeVerb(dto.Verb);

            string? payload = dto.Payload;
            string? contentType = dto.ContentType;

            if (verb == HttpMethod.Post.Method)
            {
                if (IsNullOrWhiteSpace(payload))
                {
                    throw new ConfigurationException(
                        $"Target '{targetLabel}' requires a 'payload' when using POST.");
                }

                if (IsNullOrWhiteSpace(contentType))
                {
                    throw new ConfigurationException(
                        $"Target '{targetLabel}' requires a 'contentType' when using POST.");
                }
            }
            else
            {
                payload = null;
                contentType = null;
            }

            TimeSpan responseTimeout = dto.TimeoutSeconds is > 0
                ? TimeSpan.FromSeconds(dto.TimeoutSeconds!.Value)
                : MonitorTarget.DefaultResponseTimeout;

            TimeSpan issueWindow = dto.IssueWindowSeconds is > 0
                ? TimeSpan.FromSeconds(dto.IssueWindowSeconds!.Value)
                : MonitorTarget.DefaultIssueWindow;

            TimeSpan startupWindow = dto.StartupWindowSeconds is > 0
                ? TimeSpan.FromSeconds(dto.StartupWindowSeconds!.Value)
                : MonitorTarget.DefaultStartupWindow;

            TimeSpan resourceIssueWindow = dto.ResourceIssueWindowSeconds is > 0
                ? TimeSpan.FromSeconds(dto.ResourceIssueWindowSeconds!.Value)
                : MonitorTarget.DefaultResourceIssueWindow;

            double restartThreshold = dto.RestartThreshold is > 0
                ? dto.RestartThreshold!.Value
                : MonitorTarget.DefaultRestartThreshold;

            bool shouldDestroyFaultyPods = dto.DestroyFaultyPods ?? MonitorTarget.DefaultShouldDestroyFaultyPods;

            string hostHeader = !IsNullOrWhiteSpace(dto.HostHeader)
                ? dto.HostHeader!
                : externalBaseUri.IsDefaultPort
                    ? externalBaseUri.Host
                    : $"{externalBaseUri.Host}:{externalBaseUri.Port}";

            targets.Add(new MonitorTarget(namespaceName, resourceType, resourceName, resourceKind, paths,
                externalBaseUri, hostHeader, scheme, verb, payload, contentType, responseTimeout, issueWindow,
                startupWindow, resourceIssueWindow, restartThreshold, shouldDestroyFaultyPods));
        }

        return targets.ToArray();
    }

    private static string NormalizeScheme(string? scheme)
    {
        string normalized = (scheme ?? "").Trim().TrimEnd(':', '/').ToLowerInvariant();

        if (IsNullOrWhiteSpace(normalized))
        {
            return "http";
        }

        return normalized switch
        {
            "http" or "https" => normalized,
            _ => throw new ConfigurationException(
                $"Unsupported scheme '{scheme}'. Only 'http' or 'https' are allowed.")
        };
    }

    private static string NormalizeVerb(string? verb)
    {
        string normalized = (verb ?? HttpMethod.Get.Method).Trim().ToUpperInvariant();

        return normalized switch
        {
            "" or "GET" => HttpMethod.Get.Method,
            "POST" => HttpMethod.Post.Method,
            _ => throw new ConfigurationException(
                $"Unsupported HTTP verb '{verb}'. Only GET and POST are allowed.")
        };
    }

    private static Uri ParseHost(string hostSegment)
    {
        return !Uri.TryCreate(hostSegment, UriKind.Absolute, out Uri? uri)
            ? throw new ConfigurationException(
                $"Host '{hostSegment}' must be a valid absolute URI including scheme (e.g. https://example.com).")
            : uri;
    }

    private static string[] ParsePaths(string[]? paths)
    {
        if (paths is null || paths.Length == 0)
        {
            return ["/"];
        }

        string[] normalized = paths
            .Select(path => path ?? "")
            .Select(NormalizePath)
            .Where(path => !IsNullOrWhiteSpace(path))
            .ToArray();

        return normalized.Length == 0 ? ["/"] : normalized;
    }

    private static string NormalizePath(string path)
    {
        return !path.StartsWith('/') ? $"/{path}" : path;
    }

    private static string RequireValue(string? value, string propertyName, string targetLabel)
    {
        return IsNullOrWhiteSpace(value)
            ? throw new ConfigurationException($"Target '{targetLabel}' must specify '{propertyName}'.")
            : value;
    }

    private sealed class TargetDto
    {
        public string? Namespace { get; set; }

        public string? ResourceType { get; set; }

        public string? ResourceName { get; set; }

        public string? Host { get; set; }

        public string[]? Paths { get; set; }

        public string? HostHeader { get; set; }

        public string? Scheme { get; set; }

        public string? Verb { get; set; }

        public string? Payload { get; set; }

        public string? ContentType { get; set; }

        public double? TimeoutSeconds { get; set; }

        public double? IssueWindowSeconds { get; set; }

        public double? StartupWindowSeconds { get; set; }

        public double? ResourceIssueWindowSeconds { get; set; }

        public double? RestartThreshold { get; set; }

        public bool? DestroyFaultyPods { get; set; }
    }

    private static string GetRequiredEnv(string key)
    {
        string? value = Environment.GetEnvironmentVariable(key);

        return IsNullOrWhiteSpace(value)
            ? throw new ConfigurationException($"Environment variable '{key}' is required.")
            : value;
    }

}

internal sealed record MonitorSettings(
    string SlackWebhookUrl,
    MonitorTarget[] Targets);
