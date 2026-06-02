using System.Threading;
using System.Windows;

using BarkCloud.Drive.Contracts.Localization;

namespace BarkCloud.Drive.App;

public partial class App : Application
{
    private const string MutexName = @"Local\BarkCloud.Drive.App.Singleton";
    private const string ShowEventName = @"Local\BarkCloud.Drive.App.Show";

    private Mutex? _mutex;
    private EventWaitHandle? _showEvent;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Язык UI: выбранный пользователем либо авто по языку Windows.
        Loc.SetCulture(AppSettings.Load().Language ?? Languages.DefaultForSystem());

        _mutex = new Mutex(initiallyOwned: true, MutexName, out var isNew);
        if (!isNew)
        {
            // Уже запущен экземпляр — просим его показать окно и выходим.
            try
            {
                if (EventWaitHandle.TryOpenExisting(ShowEventName, out var existing))
                {
                    existing.Set();
                    existing.Dispose();
                }
            }
            catch { /* ignore */ }

            Shutdown();
            return;
        }

        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        ThreadPool.RegisterWaitForSingleObject(_showEvent, OnShowSignaled, null, Timeout.Infinite, executeOnlyOnce: false);

        // --tray (автозагрузка): стартуем сразу в трей, без видимого окна.
        var startHidden = e.Args.Any(a => string.Equals(a, "--tray", StringComparison.OrdinalIgnoreCase));

        var window = new MainWindow(startHidden);
        MainWindow = window;
        if (startHidden)
            window.WindowState = WindowState.Minimized;

        window.Show();
    }

    // Другой экземпляр попросил показать окно — поднимаем его на текущем.
    private void OnShowSignaled(object? state, bool timedOut)
        => Dispatcher.Invoke(() =>
        {
            if (MainWindow is null)
                return;

            MainWindow.Show();
            if (MainWindow.WindowState == WindowState.Minimized)
                MainWindow.WindowState = WindowState.Normal;
            MainWindow.Activate();
        });

    protected override void OnExit(ExitEventArgs e)
    {
        _showEvent?.Dispose();
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
