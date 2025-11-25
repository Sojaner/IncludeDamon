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
                    throw new ConfigurationException($"Target '{targetLabel}' requires a 'payload' when using POST.");
                }

                if (IsNullOrWhiteSpace(contentType))
                {
                    throw new ConfigurationException($"Target '{targetLabel}' requires a 'contentType' when using POST.");
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

            monitorTargets.Add(new MonitorTarget(namespaceName, resourceType, resourceName, resourceKind, paths,
                externalBaseUri, hostHeader, scheme, verb, payload, contentType, responseTimeout, issueWindow,
                startupWindow, resourceIssueWindow, restartThreshold, shouldDestroyFaultyPods));
        }

        return monitorTargets.ToArray();
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
