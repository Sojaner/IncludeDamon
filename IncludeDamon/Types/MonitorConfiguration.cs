using System.Text.Json;
using IncludeDamon.Utilities;
using static System.String;

namespace IncludeDamon.Types;

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

        return targets.Length == 0 ?
            throw new ConfigurationException("Environment variable 'TARGETS' must contain at least one target definition.") :
            new MonitorSettings(slackWebhookUrl, targets);
    }

    private static MonitorTarget[] ParseTargets(string rawTargets)
    {
        Target[]? targets;

        try
        {
            targets = JsonSerializer.Deserialize<Target[]>(rawTargets, TargetJsonOptions);
        }
        catch (JsonException ex)
        {
            throw new ConfigurationException($"Environment variable 'TARGETS' must be valid JSON describing an array of targets. {ex.Message}");
        }

        if (targets is null || targets.Length == 0)
        {
            throw new ConfigurationException("Environment variable 'TARGETS' must contain at least one target definition.");
        }

        List<MonitorTarget> monitorTargets = [];

        for (int i = 0; i < targets.Length; i++)
        {
            Target dto = targets[i];

            string targetLabel = !IsNullOrWhiteSpace(dto.ResourceName) ? dto.ResourceName! : $"index {i}";

            string namespaceName = RequireValue(dto.Namespace, "namespace", targetLabel);

            string resourceType = RequireValue(dto.ResourceType, "resourceType", targetLabel);

            string resourceName = RequireValue(dto.ResourceName, "resourceName", targetLabel);

            if (!MonitorTarget.TryParseResourceKind(resourceType, out MonitorResourceType resourceKind))
            {
                throw new ConfigurationException($"Resource type '{resourceType}' for target '{targetLabel}' is not supported. Use 'daemonset' or 'deployment'.");
            }

            MonitorDefinition[] monitors = ExpandMonitors(dto);

            for (int monitorIndex = 0; monitorIndex < monitors.Length; monitorIndex++)
            {
                MonitorDefinition monitor = monitors[monitorIndex];

                string monitorLabel = monitors.Length > 1 ? $"{targetLabel} monitor[{monitorIndex}]" : targetLabel;

                string hostSegment = RequireValue(monitor.Host ?? dto.Host, "host", monitorLabel);

                Uri externalBaseUri = ParseHost(hostSegment);

                string[] paths = ParsePaths(monitor.Paths ?? dto.Paths);

                string scheme = NormalizeScheme(monitor.Scheme ?? dto.Scheme ?? externalBaseUri.Scheme);

                int defaultPort = GetDefaultPortForScheme(scheme);

                int port = ResolvePort(monitor.Port ?? dto.Port, externalBaseUri, defaultPort, monitorLabel);

                Uri externalBaseUriWithPort = EnsurePort(externalBaseUri, port);

                string verb = NormalizeVerb(monitor.Verb ?? dto.Verb);

                string? payload = monitor.Payload ?? dto.Payload;

                string? contentType = monitor.ContentType ?? dto.ContentType;

                if (verb == HttpMethod.Post.Method)
                {
                    if (IsNullOrWhiteSpace(payload))
                    {
                        throw new ConfigurationException($"Target '{monitorLabel}' requires a 'payload' when using POST.");
                    }

                    if (IsNullOrWhiteSpace(contentType))
                    {
                        throw new ConfigurationException($"Target '{monitorLabel}' requires a 'contentType' when using POST.");
                    }
                }
                else
                {
                    payload = null;

                    contentType = null;
                }

                TimeSpan responseTimeout = ResolveTimeSpan(monitor.TimeoutSeconds ?? dto.TimeoutSeconds,
                    MonitorTarget.DefaultResponseTimeout);

                TimeSpan issueWindow = ResolveTimeSpan(monitor.IssueWindowSeconds ?? dto.IssueWindowSeconds,
                    MonitorTarget.DefaultIssueWindow);

                TimeSpan startupWindow = ResolveTimeSpan(monitor.StartupWindowSeconds ?? dto.StartupWindowSeconds,
                    MonitorTarget.DefaultStartupWindow);

                TimeSpan resourceIssueWindow = ResolveTimeSpan(
                    monitor.ResourceIssueWindowSeconds ?? dto.ResourceIssueWindowSeconds,
                    MonitorTarget.DefaultResourceIssueWindow);

                TimeSpan restartCooldown = ResolveTimeSpan(
                    monitor.RestartCooldownSeconds ?? dto.RestartCooldownSeconds,
                    MonitorTarget.DefaultRestartCooldown);

                double restartThreshold = ResolveRestartThreshold(monitor.RestartThreshold ?? dto.RestartThreshold);

                bool shouldDestroyFaultyPods = ResolveBool(monitor.DestroyFaultyPods, dto.DestroyFaultyPods,
                    MonitorTarget.DefaultShouldDestroyFaultyPods);

                bool logNotDestroying = ResolveBool(monitor.LogNotDestroying, dto.LogNotDestroying,
                    MonitorTarget.DefaultLogNotDestroying);

                string? hostHeaderSource = monitor.HostHeader ?? dto.HostHeader;

                string hostHeader = !IsNullOrWhiteSpace(hostHeaderSource)
                    ? hostHeaderSource
                    : port == defaultPort
                        ? externalBaseUriWithPort.Host
                        : $"{externalBaseUriWithPort.Host}:{port}";

                monitorTargets.Add(new MonitorTarget(namespaceName, resourceType, resourceName, resourceKind, paths,
                    externalBaseUri, port, hostHeader, scheme, verb, payload, contentType, responseTimeout,
                    issueWindow, startupWindow, resourceIssueWindow, restartCooldown, restartThreshold,
                    shouldDestroyFaultyPods, logNotDestroying));
            }
        }

        return monitorTargets.ToArray();
    }

    private static MonitorDefinition[] ExpandMonitors(Target dto)
    {
        return dto.Monitors is { Length: > 0 }
            ? dto.Monitors
            :
            [
                new MonitorDefinition
                {
                    Host = dto.Host,
                    Paths = dto.Paths,
                    Port = dto.Port,
                    HostHeader = dto.HostHeader,
                    Scheme = dto.Scheme,
                    Verb = dto.Verb,
                    Payload = dto.Payload,
                    ContentType = dto.ContentType,
                    TimeoutSeconds = dto.TimeoutSeconds,
                    IssueWindowSeconds = dto.IssueWindowSeconds,
                    StartupWindowSeconds = dto.StartupWindowSeconds,
                    ResourceIssueWindowSeconds = dto.ResourceIssueWindowSeconds,
                    RestartCooldownSeconds = dto.RestartCooldownSeconds,
                    RestartThreshold = dto.RestartThreshold,
                    DestroyFaultyPods = dto.DestroyFaultyPods,
                    LogNotDestroying = dto.LogNotDestroying
                }
            ];
    }

    private static int ResolvePort(int? port, Uri externalBaseUri, int defaultPort, string monitorLabel)
    {
        return port switch
        {
            null when !externalBaseUri.IsDefaultPort => externalBaseUri.Port,
            null => defaultPort,
            >= 1 and <= 65535 => port.Value,
            _ => throw new ConfigurationException($"Target '{monitorLabel}' must specify a valid 'port' between 1 and 65535 when provided.")
        };
    }

    private static TimeSpan ResolveTimeSpan(double? seconds, TimeSpan fallback)
    {
        return seconds is > 0 ? TimeSpan.FromSeconds(seconds.Value) : fallback;
    }

    private static double ResolveRestartThreshold(double? restartThreshold)
    {
        return restartThreshold is > 0 ? restartThreshold.Value : MonitorTarget.DefaultRestartThreshold;
    }

    private static bool ResolveBool(bool? monitorValue, bool? targetValue, bool fallback)
    {
        return monitorValue ?? targetValue ?? fallback;
    }

    private static int GetDefaultPortForScheme(string scheme)
    {
        return scheme == "https" ? 443 : 80;
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
            _ => throw new ConfigurationException($"Unsupported scheme '{scheme}'. Only 'http' or 'https' are allowed.")
        };
    }

    private static string NormalizeVerb(string? verb)
    {
        string normalized = (verb ?? HttpMethod.Get.Method).Trim().ToUpperInvariant();

        return normalized switch
        {
            "" or "GET" => HttpMethod.Get.Method,
            "POST" => HttpMethod.Post.Method,
            _ => throw new ConfigurationException($"Unsupported HTTP verb '{verb}'. Only GET and POST are allowed.")
        };
    }

    private static Uri ParseHost(string hostSegment)
    {
        return !Uri.TryCreate(hostSegment, UriKind.Absolute, out Uri? uri)
            ? throw new ConfigurationException($"Host '{hostSegment}' must be a valid absolute URI including scheme (e.g. https://example.com).")
            : uri;
    }

    private static Uri EnsurePort(Uri baseUri, int port)
    {
        UriBuilder builder = new(baseUri)
        {
            Port = port
        };

        return builder.Uri;
    }

    private static string[] ParsePaths(string[]? paths)
    {
        if (paths is null || paths.Length == 0)
        {
            return ["/"];
        }

        string[] normalized = paths
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

    private static string GetRequiredEnv(string key)
    {
        string? value = Environment.GetEnvironmentVariable(key);

        return IsNullOrWhiteSpace(value)
            ? throw new ConfigurationException($"Environment variable '{key}' is required.")
            : value;
    }
}
