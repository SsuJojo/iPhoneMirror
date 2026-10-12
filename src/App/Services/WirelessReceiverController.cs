using IPhoneMirror.App.Interop;
using IPhoneMirror.App.Localization;

namespace IPhoneMirror.App.Services;

internal readonly record struct WirelessReceiverStartResult(
    bool Started, string? Error, bool IsNewError);

internal sealed class WirelessReceiverController(
    NativeCore core, WirelessReceiverService? receiver = null)
{
    private readonly WirelessReceiverService _receiver = receiver ?? new();
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private static readonly TimeSpan UxPlayStartupGracePeriod =
        TimeSpan.FromSeconds(15);
    private DateTime _automaticStartNotBeforeUtc;
    private DateTime _startupDeadlineUtc;
    private DateTime _lastReceiverStartUtc;
    private int _consecutiveStartupFailures;
    private WirelessReceiverBackend _lastStartedBackend =
        WirelessReceiverBackend.Original;
    private bool _automaticRestartBlocked;
    private WirelessReceiverBackend _backend = WirelessReceiverBackend.Original;

    internal string ReceiverName { get; set; } = WirelessReceiverConfiguration.DefaultReceiverName;
    internal WirelessDisplayProfile SelectedProfile { get; set; } =
        WirelessReceiverConfiguration.DefaultDisplayProfile;
    internal WirelessReceiverBackend Backend
    {
        get => _backend;
        set => _backend = WirelessReceiverConfiguration.NormalizeBackend(value);
    }
    internal string AppliedReceiverName { get; private set; } =
        WirelessReceiverConfiguration.DefaultReceiverName;
    internal WirelessDisplayProfile AppliedProfile { get; private set; } =
        WirelessReceiverConfiguration.DefaultDisplayProfile;
    internal WirelessReceiverBackend AppliedBackend { get; private set; } =
        WirelessReceiverBackend.Original;
    internal bool Running { get; private set; }
    internal bool Ready { get; private set; }
    internal string? StartError { get; private set; }
    internal bool IsAvailable => _receiver.IsBackendAvailable(Backend);
    internal bool IsBackendAvailable(WirelessReceiverBackend backend) =>
        _receiver.IsBackendAvailable(backend);

    internal async Task<WirelessReceiverStartResult> EnsureStartedAsync(
        string? receiverName = null, WirelessDisplayProfile? displayProfile = null,
        WirelessReceiverBackend? backend = null)
    {
        await _lifecycleGate.WaitAsync();
        try
        {
            var selectedBackend = WirelessReceiverConfiguration.NormalizeBackend(
                backend ?? Backend);
            Backend = selectedBackend;
            if (!_receiver.IsBackendAvailable(selectedBackend))
            {
                Running = Ready = false;
                return new(false, StartError, false);
            }

            var automaticStart = receiverName is null && displayProfile is null &&
                backend is null;
            if (!automaticStart) _automaticRestartBlocked = false;
            var status = core.GetWirelessReceiverStatus();
            Running = status.Running;
            Ready = status.Ready;
            var now = DateTime.UtcNow;

            if (Running && Ready)
            {
                _startupDeadlineUtc = default;
                _automaticStartNotBeforeUtc = default;
                _consecutiveStartupFailures = 0;
                StartError = null;
                return new(true, null, false);
            }

            if (_startupDeadlineUtc != default && !Running)
            {
                // The child exited on its own before becoming Ready; nothing
                // was stopped, so this is not a timeout. For UxPlay this is
                // the known early-exit startup failure; report it here so the
                // grace-period guard below does not log a second error.
                _startupDeadlineUtc = default;
                var error = LocalizationService.Get(
                    _lastStartedBackend == WirelessReceiverBackend.UxPlay
                        ? "WirelessUxPlayStartupFailed"
                        : "WirelessStartupExitedEarly");
                return RegisterStartupFailure(error, now);
            }

            if (Running && !Ready)
            {
                if (_startupDeadlineUtc == default)
                    _startupDeadlineUtc = now + WirelessReceiverStartupPolicy.ReadinessTimeout;
                if (now >= _startupDeadlineUtc)
                {
                    await Task.Run(core.StopWirelessReceiver);
                    Running = Ready = false;
                    _startupDeadlineUtc = default;
                    var error = LocalizationService.Get("WirelessStartupTimedOut");
                    return RegisterStartupFailure(error, DateTime.UtcNow);
                }
                return new(false, StartError, false);
            }

            if (automaticStart && !_automaticRestartBlocked &&
                selectedBackend == WirelessReceiverBackend.UxPlay &&
                _lastStartedBackend == WirelessReceiverBackend.UxPlay &&
                _lastReceiverStartUtc != default &&
                now - _lastReceiverStartUtc < UxPlayStartupGracePeriod)
            {
                // A missing GStreamer plugin or invalid UxPlay pipeline exits
                // just after the IPC host reports Ready. Do not restart that
                // broken child every refresh tick and make AirPlay discovery
                // flap; a manual Apply is the explicit retry path.
                _automaticRestartBlocked = true;
                var uxPlayError = LocalizationService.Get("WirelessUxPlayStartupFailed");
                var isNewError = !string.Equals(StartError, uxPlayError,
                    StringComparison.Ordinal);
                StartError = uxPlayError;
                return new(false, StartError, isNewError);
            }
            if (automaticStart && _automaticRestartBlocked)
            {
                Running = Ready = false;
                return new(false, StartError, false);
            }
            if (automaticStart && now < _automaticStartNotBeforeUtc)
            {
                Running = Ready = false;
                return new(false, StartError, false);
            }

            // Running is always false here: Running && Ready returned above,
            // and Running && !Ready returns inside the readiness-timeout
            // branch. The startup deadline is likewise already cleared.
            _lastReceiverStartUtc = default;

            var hostPath = _receiver.GetExecutablePath(selectedBackend);
            if (hostPath is null) return new(false, null, false);
            var runtime = await Task.Run(() => _receiver.ProbeRuntime(selectedBackend));
            if (!runtime.Success)
            {
                Running = Ready = false;
                return RegisterStartupFailure(
                    WirelessReceiverService.DescribeProbeFailure(runtime), DateTime.UtcNow);
            }
            var sanitized = WirelessReceiverConfiguration.SanitizeReceiverName(
                receiverName ?? (automaticStart ? ReceiverName : AppliedReceiverName));
            if (receiverName is not null) ReceiverName = sanitized;
            var profile = displayProfile ?? (automaticStart ? SelectedProfile : AppliedProfile);
            var started = await Task.Run(() => core.StartWirelessReceiver(sanitized, hostPath,
                profile.Width, profile.Height, profile.FrameRate));
            Running = started.Success;
            Ready = false;
            if (started.Success)
            {
                Backend = selectedBackend;
                AppliedBackend = selectedBackend;
                _lastStartedBackend = selectedBackend;
                _lastReceiverStartUtc = DateTime.UtcNow;
                _startupDeadlineUtc = _lastReceiverStartUtc +
                    WirelessReceiverStartupPolicy.ReadinessTimeout;
                AppliedReceiverName = sanitized;
                AppliedProfile = profile;
                // Keep the last failure visible while the child process exists
                // but has not sent its Ready IPC message. Clear only on Ready.
                return new(true, null, false);
            }

            return RegisterStartupFailure(started.Message, DateTime.UtcNow);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private WirelessReceiverStartResult RegisterStartupFailure(string error,
        DateTime now)
    {
        var isNewError = !string.Equals(StartError, error, StringComparison.Ordinal);
        StartError = error;
        _consecutiveStartupFailures++;
        _automaticStartNotBeforeUtc = now +
            WirelessReceiverStartupPolicy.GetRetryDelay(_consecutiveStartupFailures);
        return new(false, error, isNewError);
    }

    internal async Task StopAsync(TimeSpan automaticStartDelay = default,
        bool preserveStartError = false)
    {
        await _lifecycleGate.WaitAsync();
        try
        {
            await Task.Run(core.StopWirelessReceiver);
        }
        finally
        {
            _automaticStartNotBeforeUtc = DateTime.UtcNow + automaticStartDelay;
            _automaticRestartBlocked = false;
            _startupDeadlineUtc = default;
            _lastReceiverStartUtc = default;
            _consecutiveStartupFailures = 0;
            Running = Ready = false;
            if (!preserveStartError) StartError = null;
            _lifecycleGate.Release();
        }
    }

    internal string GetStatusText()
    {
        if (!Running && !IsBackendAvailable(Backend))
            return LocalizationService.Format("WirelessBackendUnavailableFormat",
                WirelessReceiverConfiguration.GetBackendOption(Backend).Label);
        if (!Ready && !string.IsNullOrWhiteSpace(StartError))
            return LocalizationService.Format("StartFailedFormat", StartError);
        if (!Running || !Ready) return LocalizationService.Get("WirelessStarting");
        return LocalizationService.Format("WirelessRunningWithBackendFormat",
            WirelessReceiverConfiguration.GetBackendOption(AppliedBackend).Label,
            AppliedReceiverName);
    }
}
