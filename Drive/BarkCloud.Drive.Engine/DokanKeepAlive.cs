using BarkCloud.Drive.Contracts.Localization;

using DokanNet;

namespace BarkCloud.Drive.Engine;

// Продлевает таймаут долгого Dokany-колбэка на время сетевой или дисковой операции.
internal sealed class DokanKeepAlive : IDisposable
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(10);
    private readonly object _lock = new();
    private readonly IDokanFileInfo _info;
    private readonly ITimer _timer;
    private bool _disposed;
    private bool _failureLogged;

    private DokanKeepAlive(IDokanFileInfo info, TimeProvider time)
    {
        _info = info;
        _timer = time.CreateTimer(Tick, null, Interval, Interval);
    }

    public static IDisposable Start(IDokanFileInfo info, TimeProvider? time = null)
        => new DokanKeepAlive(info, time ?? TimeProvider.System);

    private void Tick(object? _)
    {
        lock (_lock)
        {
            if (_disposed)
                return;

            try
            {
                if (!_info.TryResetTimeout(60_000))
                    LogFailure();
            }
            catch (Exception ex)
            {
                LogFailure(ex);
            }
        }
    }

    private void LogFailure(Exception? exception = null)
    {
        if (_failureLogged)
            return;

        _failureLogged = true;
        if (exception is null)
            EngineLog.Info(Loc.T("Eng_DokanKeepAliveFailed"));
        else
            EngineLog.Error(Loc.T("Eng_DokanKeepAliveFailed"), exception);
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
                return;
            _disposed = true;
            _timer.Dispose();
        }
    }
}
