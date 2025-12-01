// ReSharper disable UnusedAutoPropertyAccessor.Global
namespace IncludeDamon.Types;

// ReSharper disable once ClassNeverInstantiated.Global
internal sealed class Target
{
    public string? Namespace { get; set; }

    public string? ResourceType { get; set; }

    public string? ResourceName { get; set; }

    public string? Host { get; set; }

    public string[]? Paths { get; set; }

    public int? Port { get; set; }

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

    public bool? LogNotDestroying { get; set; }
}
