using System.Diagnostics;
using System.Globalization;
using System.Text;
using Humanizer;
using IncludeDamon.Types;
using IncludeDamon.Utilities;
using k8s;
using k8s.Models;
using ThrottleDebounce;
using static System.String;

namespace IncludeDamon;

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

    private readonly Func<string, Task> _sendMessage;

    private readonly string[] _paths;

    private readonly string _scheme;

    private readonly int _port;

    private readonly string _verb;

    private readonly string? _payload;

    private readonly string? _contentType;

    private readonly Uri _externalBaseUri;

    private static readonly TimeSpan InstabilityWindow = TimeSpan.FromMinutes(60);

    private const double InstabilityRateThresholdPerMinute = 0.12; // ~7 per hour, aligns with previous thresholds

    private const int MinInstabilityEvents = 3;

    private readonly List<DateTime> _instabilityEvents = [];

    private readonly TimeSpan _issueWindow;

    private readonly TimeSpan _startupWindowBase;

    private readonly TimeSpan _resourceIssueWindow;

    private readonly double _restartThreshold;

    private readonly bool _shouldDestroyFaultyPods;

    private readonly bool _logNotDestroying;

    private volatile bool _disposed;

    private string _lastPhase;

    private Status _status;

    private bool _resourceInstabilityRecorded;

    private CultureInfo _cultureInfo = new ("en-US");

    public string Name { get; }

    public Status Status { get; private set; }

    private double SecondsAlive => _pod.GetSecondsAlive();

    public PodMonitor(V1Pod pod, MonitorTarget target, Action<PodMonitor, bool> removePodMonitor,
        RateLimitedAction<CancellationToken> onStabilized,
        Dictionary<string, Dictionary<string, IDictionary<string, ResourceQuantity>>> metrics, float waitFactor,
        Func<string, Task> sendMessage, CancellationToken cancellationToken)
    {
        _pod = pod;

        _paths = target.Paths;

        _scheme = target.Scheme;

        _port = target.Port;

        _verb = target.Verb;

        _payload = target.Payload;

        _contentType = target.ContentType;

        _externalBaseUri = target.ExternalBaseUri;

        _issueWindow = target.IssueWindow;

        _startupWindowBase = target.StartupWindow;

        _resourceIssueWindow = target.ResourceIssueWindow;

        _restartThreshold = target.RestartThreshold;

        _shouldDestroyFaultyPods = target.ShouldDestroyFaultyPods;

        _logNotDestroying = target.LogNotDestroying;

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

        _sendMessage = sendMessage;

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

            return $" *[CPU: {$"{cpuUsage / cpuLimit:P1}({cpuUsage.ToString("0.##", _cultureInfo)}/{cpuLimit.ToString("0.##", _cultureInfo)}cores)".Replace(" ", "")}, Memory: {$"{memoryUsage / memoryLimit:P1}({ByteSize.FromBytes(memoryUsage)}/{ByteSize.FromBytes(memoryLimit)}".Replace(" ", "")})]*";
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

        string url = BuildInClusterUrl(path, podIp);

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

    private string BuildInClusterUrl(string path, string host)
    {
        string formattedHost = host.Contains(':') && !host.StartsWith("[")
            ? $"[{host}]"
            : host;

        return $"{_scheme}://{formattedHost}:{_port}{path}";
    }

    private void LogHealthCheckTargets()
    {
        string podIp = _pod.Status.PodIP ?? "<pending-pod-ip>";

        foreach (string path in _paths)
        {
            StringBuilder logMessage = new(
                $"[MONITOR HEALTH TARGET] {_pod.Metadata.Name} {_verb} {BuildInClusterUrl(path, podIp)}");

            if (_verb == HttpMethod.Post.Method)
            {
                logMessage.Append(
                    $" (content-type: {_contentType ?? "<none>"}, payload: {_payload ?? "<empty>"})");
            }

            Console.WriteLine(logMessage.ToString());
        }
    }

    private bool RegisterInstability(string category, out string? forcedReason)
    {
        DateTime now = DateTime.UtcNow;

        _instabilityEvents.Add(now);

        _instabilityEvents.RemoveAll(instant => now - instant > InstabilityWindow);

        if (_instabilityEvents.Count < MinInstabilityEvents)
        {
            forcedReason = null;
            return false;
        }

        DateTime oldest = _instabilityEvents.Min();

        TimeSpan span = now - oldest;

        double minutes = Math.Max(span.TotalMinutes, 1);

        double ratePerMinute = _instabilityEvents.Count / minutes;

        if (ratePerMinute > InstabilityRateThresholdPerMinute)
        {
            forcedReason =
                $"{category} instability rate {ratePerMinute:F2}/min over {minutes:F0} minutes (threshold {InstabilityRateThresholdPerMinute:F2}/min)";

            return true;
        }

        forcedReason = null;
        return false;
    }

    private async Task<bool> MaybeForceRestartForInstability(string category, string resourcesReport)
    {
        if (!RegisterInstability(category, out string? reason)) return false;

        bool destroyed = await TrySelfDestructPod(false);

        bool log = destroyed || _logNotDestroying;

        if (log)
        {
            string logLabel = destroyed
                ? "[INSTABILITY RESTART]"
                : "[INSTABILITY DETECTED - DESTRUCTION DISABLED]";

            Console.WriteLine(
                $"{logLabel} {_pod.Metadata.Name} (Up for {TimeSpan.FromSeconds(SecondsAlive).Humanize(4, minUnit: TimeUnit.Second)}) {reason}{resourcesReport}");

            string message = destroyed
                ? $"{Icons.Fail} Pod `{_pod.Metadata.Name}` restarted due to repeated instability ({reason}){resourcesReport}"
                : $"{Icons.Warning} Pod `{_pod.Metadata.Name}` is repeatedly unstable ({reason}) but self-destruction is disabled{resourcesReport}";

            await _sendMessage(message);
        }

        return destroyed;
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

                LogHealthCheckTargets();

                TimeSpan startupWindow = TimeSpan.FromTicks((long)(_startupWindowBase.Ticks * _waitFactor));

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

                                await _sendMessage(
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

                                await _sendMessage(
                                    $"{Icons.Check} Pod `{_pod.Metadata.Name}` works fine for {Join(", ", results.Select(result => $"{result.path}"))}{resourcesReport}");

                                _onStabilized.Invoke(_cancellationToken);
                            }
                            else if (Status is Status.Problem && _status is Status.Ok or Status.Redeploying or Status.Unknown)
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

                                await _sendMessage(
                                    $"{Icons.Warning} Problem in pod `{_pod.Metadata.Name}` {reason}{resourcesReport}");

                                if (await MaybeForceRestartForInstability("response", resourcesReport))
                                {
                                    break;
                                }
                            }

                            if (issueStopwatch.Elapsed >= _issueWindow)
                            {
                                string resourcesReport = await GetResourcesAsync(pod);

                                bool destroyed = await TrySelfDestructPod(startupStopwatch.IsRunning);

                                bool log = destroyed || _logNotDestroying;

                                if (log)
                                {
                                    string logLabel = destroyed
                                        ? "[MONITOR RESPONSE RESTARTED]"
                                        : "[MONITOR RESPONSE FAULTY - DESTRUCTION DISABLED]";

                                    Console.WriteLine(
                                        $"{logLabel} {_pod.Metadata.Name} (Up for {TimeSpan.FromSeconds(SecondsAlive).Humanize(4, minUnit: TimeUnit.Second)}){resourcesReport}");

                                    string message = destroyed
                                        ? $"{Icons.Fail} Pod `{_pod.Metadata.Name}` restarted due to connection problems{resourcesReport}"
                                        : $"{Icons.Warning} Pod `{_pod.Metadata.Name}` has connection problems but self-destruction is disabled{resourcesReport}";

                                    await _sendMessage(message);
                                }

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

                                await _sendMessage(
                                    $"{Icons.Fire} Pod `{_pod.Metadata.Name}`'s resource usage is over {_restartThreshold:P0} threshold{resourcesReport}");

                                if (!_resourceInstabilityRecorded)
                                {
                                    _resourceInstabilityRecorded = true;

                                    if (await MaybeForceRestartForInstability("resource", resourcesReport))
                                    {
                                        break;
                                    }
                                }
                            }
                            else if (resourceStopwatch.IsRunning && CheckResourcesHealthy(pod))
                            {
                                resourceStopwatch.Reset();

                                _resourceInstabilityRecorded = false;

                                string resourcesReport = await GetResourcesAsync(pod);

                                Console.WriteLine(
                                    $"[MONITOR RESOURCE HEALTHY] {_pod.Metadata.Name} (Up for {TimeSpan.FromSeconds(SecondsAlive).Humanize(4, minUnit: TimeUnit.Second)}){resourcesReport}");

                                await _sendMessage(
                                    $"{Icons.Check} Pod `{_pod.Metadata.Name}`'s resource usage is fine now{resourcesReport}");
                            }

                            if (resourceStopwatch.Elapsed >= _resourceIssueWindow)
                            {
                                string resourcesReport = await GetResourcesAsync(pod);

                                bool destroyed = await TrySelfDestructPod(false);

                                bool log = destroyed || _logNotDestroying;

                                if (log)
                                {
                                    string logLabel = destroyed
                                        ? "[MONITOR RESOURCE RESTARTED]"
                                        : "[MONITOR RESOURCE FAULTY - DESTRUCTION DISABLED]";

                                    Console.WriteLine(
                                        $"{logLabel} {_pod.Metadata.Name} (Up for {TimeSpan.FromSeconds(SecondsAlive).Humanize(4, minUnit: TimeUnit.Second)}){resourcesReport}");

                                    string message = destroyed
                                        ? $"{Icons.Fail} Pod `{_pod.Metadata.Name}` restarted due to high resource usage{resourcesReport}"
                                        : $"{Icons.Warning} Pod `{_pod.Metadata.Name}` has high resource usage but self-destruction is disabled{resourcesReport}";

                                    await _sendMessage(message);
                                }

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
