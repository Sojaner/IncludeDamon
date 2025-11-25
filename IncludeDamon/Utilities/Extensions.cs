using k8s.Models;

namespace IncludeDamon.Utilities;

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
