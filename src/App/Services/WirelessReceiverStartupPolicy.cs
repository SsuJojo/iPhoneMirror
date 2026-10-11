namespace IPhoneMirror.App.Services;

internal static class WirelessReceiverStartupPolicy
{
    internal static readonly TimeSpan ReadinessTimeout = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan InitialRetryDelay = TimeSpan.FromSeconds(2);
    internal static readonly TimeSpan MaximumRetryDelay = TimeSpan.FromSeconds(60);

    internal static TimeSpan GetRetryDelay(int consecutiveFailures)
    {
        if (consecutiveFailures <= 0) return TimeSpan.Zero;

        var seconds = InitialRetryDelay.TotalSeconds;
        for (var attempt = 1; attempt < consecutiveFailures &&
             seconds < MaximumRetryDelay.TotalSeconds; attempt++)
            seconds = Math.Min(seconds * 2, MaximumRetryDelay.TotalSeconds);

        return TimeSpan.FromSeconds(seconds);
    }
}
