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

    // Колбэк, который уже продлевается на этом потоке: вложенный Start не заводит второй таймер.
    [ThreadStatic] private static IDokanFileInfo? t_active;
    private readonly IDokanFileInfo? _previous;

    private DokanKeepAlive(IDokanFileInfo info, TimeProvider time)
    {
        _info = info;
        _previous = t_active;
        t_active = info;
        _timer = time.CreateTimer(Tick, null, Interval, Interval);
    }

    public static IDisposable Start(IDokanFileInfo info, TimeProvider? time = null)
        => ReferenceEquals(t_active, info)
            ? NoOp.Instance
            : new DokanKeepAlive(info, time ?? TimeProvider.System);

    private sealed class NoOp : IDisposable
    {
        public static readonly NoOp Instance = new();
        public void Dispose() { }
    }

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

        if (ReferenceEquals(t_active, _info))
            t_active = _previous;
    }
}
