using IncludeDamon.Types;
using IncludeDamon.Utilities;
using k8s;
using k8s.Models;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Hosting;
using ThrottleDebounce;
using static System.String;

namespace IncludeDamon;

public class Service: BackgroundService
{
    private readonly HttpClient _httpClient = new();

    private readonly Kubernetes _kubernetes = new(Utilities.Utilities.CreateKubernetesClientConfiguration());

    private readonly Lock _removeLock = new();

    private readonly List<PodMonitor> _monitoredPods = [];

    private readonly MonitorTarget[] _targets = MonitorConfiguration.Targets;

    private readonly Dictionary<string, Dictionary<string, IDictionary<string, ResourceQuantity>>> _metrics = [];

    private readonly RateLimitedAction<CancellationToken> _onStabilized;

    private const float DefaultWaitFactor = 2;

    private float _waitFactor = DefaultWaitFactor;

    public Service()
    {
        _onStabilized = Debouncer.Debounce<CancellationToken>(OnStabilized, TimeSpan.FromSeconds(15));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunAsync(stoppingToken);
            }
            catch (ConfigurationException e)
            {
                Console.WriteLine($"[CONFIG ERROR] {e.Message}");

                throw;
            }
            catch (Exception e)
            {
                Console.WriteLine($"[ERROR] {e.Message}");
            }
        }
    }

    private async Task SendMessage(string message)
    {
        string time = DateTime.UtcNow.UtcToSe().TimeOfDay.ToString(@"hh\:mm\:ss");

        string slackWebhookUrl = MonitorConfiguration.SlackWebhookUrl;

        await _httpClient.PostAsync(slackWebhookUrl, new StringContent($"{{\"text\":\"{message} _({time})_\"}}"));
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

        SendMessage($"{Icons.Check}{Icons.Check} System stable").Wait(cancellationToken);
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
                RemovePodMonitor, _onStabilized, _metrics, _waitFactor, SendMessage, cancellationToken).Monitor()));

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

    public override void Dispose()
    {
        _kubernetes.Dispose();

        base.Dispose();

        GC.SuppressFinalize(this);
    }
}
