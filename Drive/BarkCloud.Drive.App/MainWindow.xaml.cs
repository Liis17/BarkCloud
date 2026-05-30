using System.ComponentModel;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

using BarkCloud.Drive.Contracts;

using Wpf.Ui.Controls;

namespace BarkCloud.Drive.App;

public partial class MainWindow : FluentWindow
{
    private IDriveEngine? _engine;
    private readonly DispatcherTimer _statusTimer;
    private readonly AppSettings _settings = AppSettings.Load();
    private bool _reallyExit;

    public MainWindow()
    {
        InitializeComponent();

        // Иконка трея из системной (без бинарного ассета).
        TrayIcon.Icon = Imaging.CreateBitmapSourceFromHIcon(
            System.Drawing.SystemIcons.Application.Handle,
            Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());

        ContentRendered += OnContentRendered;

        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _statusTimer.Tick += async (_, _) => await PollStatusAsync();

        // Обновляем использование только когда окно видимо и не свёрнуто.
        StateChanged += (_, _) => UpdatePolling();          // свернуть/развернуть
        IsVisibleChanged += (_, _) => UpdatePolling();      // показать/скрыть в трей
    }

    // Текущий прокси движка — для модалки настроек.
    public IDriveEngine? Engine => _engine;

    private async void OnContentRendered(object? sender, EventArgs e)
    {
        if (!TrayIcon.IsRegistered)
            TrayIcon.Register();

        await InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        var engine = await EnsureEngineAsync();
        if (engine == null)
            return; // баннер «движок не запущен» показан

        await ProceedAfterEngineAsync(engine);
    }

    // Общий путь после подключения к движку: первый запуск → мастер; иначе статус + автомонтаж.
    private async Task ProceedAfterEngineAsync(IDriveEngine engine)
    {
        if (!_settings.Configured)
        {
            if (!RunWizard(engine))
            {
                ExitApp(); // первичная настройка отменена — без неё пользоваться нечем
                return;
            }
        }

        try
        {
            var status = await engine.GetStatusAsync();
            Apply(status);

            if (status.Authenticated && !status.Mounted)
                await AutoMountAsync(engine);
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Движок недоступен: {ex.Message}";
        }

        if (!_statusTimer.IsEnabled && IsVisible && WindowState != WindowState.Minimized)
            _statusTimer.Start();
    }

    private async Task<IDriveEngine?> EnsureEngineAsync()
    {
        if (_engine != null)
            return _engine;

        try
        {
            _engine = await EngineLauncher.ConnectAsync();
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Движок недоступен: {ex.Message}";
        }

        UpdateEngineBanner();
        return _engine;
    }

    // Перезапуск движка по запросу из настроек: корректное завершение + добивание процесса + реконнект.
    public async Task<IDriveEngine?> RestartEngineAsync()
    {
        if (_engine != null)
        {
            try { await _engine.ShutdownAsync(); } catch { /* возможно завис */ }
            _engine = null;
        }

        EngineLauncher.KillEngine();
        var engine = await EnsureEngineAsync();
        UpdateEngineBanner();
        return engine;
    }

    private bool RunWizard(IDriveEngine engine)
    {
        var wizard = new FirstRunWizard(engine, _settings) { Owner = this };
        return wizard.ShowDialog() == true;
    }

    private async Task AutoMountAsync(IDriveEngine engine)
    {
        var letter = PreferredLetter();
        if (letter == null)
        {
            StatusText.Text = "Нет свободной буквы диска для монтирования.";
            return;
        }

        try
        {
            var status = await engine.MountAsync(letter, _settings.DriveName);
            if (status.Mounted)
            {
                _settings.DriveLetter = letter;
                _settings.Save();
            }

            Apply(status);
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Ошибка монтирования: {ex.Message}";
        }
    }

    // Запомненная буква (если ещё свободна), иначе первая свободная.
    private string? PreferredLetter()
    {
        var free = DriveLetters.Free();
        if (_settings.DriveLetter is { } saved && free.Contains(saved))
            return saved;

        return free.FirstOrDefault();
    }

    private void Apply(EngineStatus s)
    {
        UsernameText.Text = !string.IsNullOrEmpty(s.Username)
            ? s.Username
            : (s.Authenticated ? "Вы вошли" : "Вход не выполнен");
        ServerText.Text = string.IsNullOrEmpty(s.ServerHost) ? string.Empty : $"Сервер: {s.ServerHost}";

        if (s.LimitBytes > 0)
        {
            UsageBar.Value = s.UsedBytes * 100.0 / s.LimitBytes;
            UsageText.Text = $"{Bytes(s.UsedBytes)} из {Bytes(s.LimitBytes)}";
        }
        else
        {
            UsageBar.Value = 0;
            UsageText.Text = "—";
        }

        DriveStateText.Text = s.Mounted
            ? $"Диск {s.DriveLetter}: примонтирован ({s.VolumeLabel})"
            : "Диск не примонтирован";

        StatusText.Text = !string.IsNullOrEmpty(s.Error)
            ? $"Ошибка: {s.Error}"
            : (s.Message ?? string.Empty);
    }

    private async Task PollStatusAsync()
    {
        if (_engine == null)
        {
            UpdateEngineBanner();
            return;
        }

        try
        {
            Apply(await _engine.GetStatusAsync());
        }
        catch
        {
            _engine = null; // движок мог завершиться
            UpdateEngineBanner();
        }
    }

    private void UpdatePolling()
    {
        if (IsVisible && WindowState != WindowState.Minimized)
        {
            if (!_statusTimer.IsEnabled)
                _statusTimer.Start();
            if (_engine != null)
                _ = PollStatusAsync(); // мгновенное обновление при возврате
        }
        else
        {
            _statusTimer.Stop(); // в трее / свёрнуто — не опрашиваем
        }
    }

    private void UpdateEngineBanner()
        => EngineWarning.Visibility = _engine == null ? Visibility.Visible : Visibility.Collapsed;

    private async void StartEngineClick(object sender, RoutedEventArgs e)
    {
        _engine = null;
        var engine = await EnsureEngineAsync();
        if (engine != null)
            await ProceedAfterEngineAsync(engine);
    }

    private async void OpenSettingsClick(object sender, RoutedEventArgs e)
    {
        if (_engine == null)
        {
            StatusText.Text = "Движок не запущен.";
            return;
        }

        _statusTimer.Stop(); // пауза опроса на время модалки
        new SettingsWindow(this, _settings) { Owner = this }.ShowDialog();

        // Возможно, в настройках вышли из аккаунта (Configured=false) → мастер заново.
        if (!_settings.Configured && _engine != null && !RunWizard(_engine))
        {
            ExitApp();
            return;
        }

        if (IsVisible && WindowState != WindowState.Minimized)
            _statusTimer.Start();
        await PollStatusAsync();
    }

    // ───────── трей ─────────

    private void ShowClick(object sender, RoutedEventArgs e) => ShowFromTray();

    private async void MountClick(object sender, RoutedEventArgs e)
    {
        var engine = await EnsureEngineAsync();
        if (engine != null)
            await AutoMountAsync(engine);
    }

    private async void UnmountClick(object sender, RoutedEventArgs e)
    {
        if (_engine == null)
            return;

        try
        {
            Apply(await _engine.UnmountAsync());
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Ошибка: {ex.Message}";
        }
    }

    private async void ExitClick(object sender, RoutedEventArgs e)
    {
        _reallyExit = true;

        if (_engine != null)
        {
            try { await _engine.ShutdownAsync(); }
            catch { /* движок завершается */ }
            _engine = null;
        }

        TrayIcon.Unregister();
        Application.Current.Shutdown();
    }

    private void ShowFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    // Закрытие окна (крестик) → сворачивание в трей; диск и движок продолжают работать.
    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_reallyExit)
        {
            e.Cancel = true;
            Hide();
        }

        base.OnClosing(e);
    }

    private void ExitApp()
    {
        _reallyExit = true;
        try { TrayIcon.Unregister(); } catch { /* ignore */ }
        Application.Current.Shutdown();
    }

    private static string Bytes(long b)
    {
        string[] units = ["Б", "КБ", "МБ", "ГБ", "ТБ"];
        double v = b;
        var i = 0;
        while (v >= 1024 && i < units.Length - 1)
        {
            v /= 1024;
            i++;
        }

        return $"{v:0.##} {units[i]}";
    }
}
