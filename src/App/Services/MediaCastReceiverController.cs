using IPhoneMirror.App.Interop;
using IPhoneMirror.App.Localization;

namespace IPhoneMirror.App.Services;

internal sealed class MediaCastReceiverController(
    NativeCore core, WirelessReceiverService? receiver = null,
    Func<WirelessReceiverBackend>? selectedWirelessBackend = null)
{
    private readonly WirelessReceiverService _receiver = receiver ?? new();
    private readonly Func<WirelessReceiverBackend> _selectedWirelessBackend =
        selectedWirelessBackend ?? (() => WirelessReceiverBackend.Original);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _startError;
    private DateTime _automaticStartNotBeforeUtc;
    private DateTime _startupDeadlineUtc;
    private int _consecutiveStartupFailures;

    internal bool Running { get; private set; }
    internal bool Ready { get; private set; }
    internal bool SupportsCurrentWirelessBackend =>
        WirelessReceiverConfiguration.SupportsMediaCast(_selectedWirelessBackend());
    internal bool IsAvailable => SupportsCurrentWirelessBackend &&
        _receiver.IsBackendAvailable(WirelessReceiverBackend.Original);

    internal async Task<bool> EnsureStartedAsync()
    {
        if (!SupportsCurrentWirelessBackend)
        {
            if (Running || Ready) await StopAsync();
            return false;
        }
        await _gate.WaitAsync();
        try
        {
            if (!IsAvailable) return Running = Ready = false;
            var status = core.GetMediaCastReceiverStatus();
            Running = status.Running;
            Ready = status.Ready;
            var now = DateTime.UtcNow;
            if (Running && Ready)
            {
                _startupDeadlineUtc = default;
                _automaticStartNotBeforeUtc = default;
                _consecutiveStartupFailures = 0;
                _startError = null;
                return true;
            }
            if (_startupDeadlineUtc != default && !Running)
            {
                _startupDeadlineUtc = default;
                RegisterStartupFailure(LocalizationService.Get("WirelessStartupTimedOut"), now);
                return false;
            }
            if (Running)
            {
                if (_startupDeadlineUtc == default)
                    _startupDeadlineUtc = now + WirelessReceiverStartupPolicy.ReadinessTimeout;
                if (now >= _startupDeadlineUtc)
                {
                    await Task.Run(core.StopMediaCastReceiver);
                    Running = Ready = false;
                    _startupDeadlineUtc = default;
                    RegisterStartupFailure(
                        LocalizationService.Get("WirelessStartupTimedOut"), DateTime.UtcNow);
                    return false;
                }
                return true;
            }
            if (now < _automaticStartNotBeforeUtc) return false;
            if (_receiver.GetExecutablePath(WirelessReceiverBackend.Original) is not
                { } hostPath) return false;
            var runtime = await Task.Run(() =>
                _receiver.ProbeRuntime(WirelessReceiverBackend.Original));
            if (!runtime.Success)
            {
                Running = Ready = false;
                RegisterStartupFailure(
                    WirelessReceiverService.DescribeProbeFailure(runtime), DateTime.UtcNow);
                return false;
            }
            var result = await Task.Run(() => core.StartMediaCastReceiver(
                WirelessReceiverConfiguration.DefaultReceiverName, hostPath));
            Running = result.Success;
            Ready = false;
            if (result.Success)
            {
                _startupDeadlineUtc = DateTime.UtcNow +
                    WirelessReceiverStartupPolicy.ReadinessTimeout;
                return true;
            }
            RegisterStartupFailure(result.Message, DateTime.UtcNow);
            return false;
        }
        finally
        {
            _gate.Release();
        }
    }

    private void RegisterStartupFailure(string error, DateTime now)
    {
        _startError = error;
        _consecutiveStartupFailures++;
        _automaticStartNotBeforeUtc = now +
            WirelessReceiverStartupPolicy.GetRetryDelay(_consecutiveStartupFailures);
    }

    internal async Task StopAsync()
    {
        await _gate.WaitAsync();
        try
        {
            await Task.Run(core.StopMediaCastReceiver);
            Running = Ready = false;
            _startupDeadlineUtc = default;
            _automaticStartNotBeforeUtc = default;
            _consecutiveStartupFailures = 0;
            _startError = null;
        }
        finally
        {
            _gate.Release();
        }
    }

    internal string GetStatusText()
    {
        if (!SupportsCurrentWirelessBackend)
            return LocalizationService.Get("MediaCastRequiresOriginalBackend");
        if (!IsAvailable) return LocalizationService.Get("MediaCastReceiverMissing");
        if (!Ready && !string.IsNullOrWhiteSpace(_startError))
            return LocalizationService.Format("StartFailedFormat", _startError);
        return LocalizationService.Get(Ready ? "MediaCastReady" : "MediaCastStarting");
    }
}
