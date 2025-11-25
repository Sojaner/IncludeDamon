namespace IncludeDamon.Types;

internal sealed record MonitorSettings(string SlackWebhookUrl, MonitorTarget[] Targets);
