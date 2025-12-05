using static System.String;

namespace IncludeDamon.Types;

internal sealed class MonitorTarget
{
    internal static readonly TimeSpan DefaultResponseTimeout = TimeSpan.FromSeconds(5);
    internal static readonly TimeSpan DefaultIssueWindow = TimeSpan.FromSeconds(60);
    internal static readonly TimeSpan DefaultStartupWindow = TimeSpan.FromSeconds(120);
    internal static readonly TimeSpan DefaultResourceIssueWindow = TimeSpan.FromSeconds(300);
    internal static readonly TimeSpan DefaultRestartCooldown = TimeSpan.FromSeconds(60);
    internal const double DefaultRestartThreshold = 0.9;
    internal const double DefaultInstabilityRateThresholdPerMinute = 0.1;
    internal const int DefaultInstabilityEventThreshold = 5;
    internal const bool DefaultShouldDestroyFaultyPods = false;
    internal const bool DefaultLogNotDestroying = false;

    internal MonitorTarget(
        string namespaceName,
        string resourceType,
        string resourceName,
        MonitorResourceType resourceKind,
        string[] paths,
        Uri externalBaseUri,
        int port,
        string hostHeader,
        string scheme,
        string verb,
        string? payload,
        string? contentType,
        TimeSpan responseTimeout,
        TimeSpan issueWindow,
        TimeSpan startupWindow,
        TimeSpan resourceIssueWindow,
        TimeSpan restartCooldown,
        double restartThreshold,
        double instabilityRateThresholdPerMinute,
        int instabilityEventThreshold,
        bool shouldDestroyFaultyPods,
        bool logNotDestroying)
    {
        Namespace = namespaceName;
        ResourceType = resourceType;
        ResourceName = resourceName;
        ResourceKind = resourceKind;
        LabelSelector = null;
        Paths = paths;
        ExternalBaseUri = externalBaseUri;
        Port = port;
        HostHeader = hostHeader;
        Scheme = scheme;
        Verb = verb;
        Payload = payload;
        ContentType = contentType;
        ResponseTimeout = responseTimeout;
        IssueWindow = issueWindow;
        StartupWindow = startupWindow;
        ResourceIssueWindow = resourceIssueWindow;
        RestartCooldown = restartCooldown;
        RestartThreshold = restartThreshold;
        InstabilityRateThresholdPerMinute = instabilityRateThresholdPerMinute;
        InstabilityEventThreshold = instabilityEventThreshold;
        ShouldDestroyFaultyPods = shouldDestroyFaultyPods;
        LogNotDestroying = logNotDestroying;
    }

    public string Namespace { get; }

    public string ResourceType { get; }

    public string ResourceName { get; }

    public MonitorResourceType ResourceKind { get; }

    public string? LabelSelector { get; private set; }

    public string[] Paths { get; }

    public Uri ExternalBaseUri { get; }

    public int Port { get; }

    public string HostHeader { get; }

    public string Scheme { get; }

    public string Verb { get; }

    public string? Payload { get; }

    public string? ContentType { get; }

    public TimeSpan ResponseTimeout { get; }

    public TimeSpan IssueWindow { get; }

    public TimeSpan StartupWindow { get; }

    public TimeSpan ResourceIssueWindow { get; }

    public TimeSpan RestartCooldown { get; }

    public double RestartThreshold { get; }

    public double InstabilityRateThresholdPerMinute { get; }

    public int InstabilityEventThreshold { get; }

    public bool ShouldDestroyFaultyPods { get; }

    public bool LogNotDestroying { get; }

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
