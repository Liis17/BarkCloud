using System.ComponentModel;
using System.Diagnostics;
using System.IO;
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

        PopulateDriveLetters();

        // Подстраховка: гарантированно регистрируем иконку трея после первого рендера.
        ContentRendered += OnContentRendered;

        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _statusTimer.Tick += async (_, _) => await PollStatusAsync();
        _statusTimer.Start();
    }

    private void PopulateDriveLetters()
    {
        var used = DriveInfo.GetDrives().Select(d => char.ToUpperInvariant(d.Name[0])).ToHashSet();
        for (var c = 'D'; c <= 'Z'; c++)
            if (!used.Contains(c))
                LetterCombo.Items.Add(c.ToString());

        if (_settings.DriveLetter is { } saved && LetterCombo.Items.Contains(saved))
            LetterCombo.SelectedItem = saved;
        else if (LetterCombo.Items.Count > 0)
            LetterCombo.SelectedIndex = 0;
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

        return _engine;
    }

    private async void LoginClick(object sender, RoutedEventArgs e)
    {
        var login = UsernameBox.Text.Trim();
        if (string.IsNullOrEmpty(login) || string.IsNullOrEmpty(PasswordBox.Password))
        {
            StatusText.Text = "Введите логин и пароль.";
            return;
        }

        var engine = await EnsureEngineAsync();
        if (engine == null)
            return;

        try
        {
            var status = await engine.LoginAsync(login, PasswordBox.Password, NullIfEmpty(OtpBox.Text));
            Apply(status);

            if (status.Authenticated && !status.Mounted)
                await AutoMountAsync(engine);
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Ошибка входа: {ex.Message}";
        }
    }

    private async void MountClick(object sender, RoutedEventArgs e)
    {
        var engine = await EnsureEngineAsync();
        if (engine == null || LetterCombo.SelectedItem is not string letter)
            return;

        try
        {
            var status = await engine.MountAsync(letter);
            if (status.Mounted)
                RememberLetter(letter);
            Apply(status);
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Ошибка монтирования: {ex.Message}";
        }
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

    private async void OnContentRendered(object? sender, EventArgs e)
    {
        if (!TrayIcon.IsRegistered)
            TrayIcon.Register();

        await InitializeAsync();
    }

    // Старт UI: поднять/подключить движок, узнать статус. Если сессия уже восстановлена
    // движком из refresh.bin — форма входа не нужна, сразу монтируем диск.
    private async Task InitializeAsync()
    {
        var engine = await EnsureEngineAsync();
        if (engine == null)
            return;

        EngineStatus status;
        try
        {
            status = await engine.GetStatusAsync();
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Движок недоступен: {ex.Message}";
            return;
        }

        Apply(status);

        if (status.Authenticated && !status.Mounted)
            await AutoMountAsync(engine);
    }

    // Монтирование без участия пользователя: берём предпочтительную (запомненную) букву.
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
            var status = await engine.MountAsync(letter);
            if (status.Mounted)
                RememberLetter(letter);
            Apply(status);
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Ошибка автомонтирования: {ex.Message}";
        }
    }

    // Запомненная буква (если ещё свободна), иначе выбранная/первая свободная.
    private string? PreferredLetter()
    {
        if (_settings.DriveLetter is { } saved && LetterCombo.Items.Contains(saved))
            return saved;

        return LetterCombo.SelectedItem as string ?? LetterCombo.Items.Cast<string>().FirstOrDefault();
    }

    private void RememberLetter(string letter)
    {
        _settings.DriveLetter = letter;
        _settings.Save();
    }

    private void ShowClick(object sender, RoutedEventArgs e) => ShowFromTray();

    // Меню «три точки» — открыть по клику.
    private void MoreClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.ContextMenu is { } menu)
        {
            menu.PlacementTarget = fe;
            menu.IsOpen = true;
        }
    }

    // Запустить движок (если не запущен) и подключиться.
    private async void StartEngineClick(object sender, RoutedEventArgs e)
    {
        var engine = await EnsureEngineAsync();
        if (engine == null)
            return;

        try
        {
            var status = await engine.GetStatusAsync();
            status.Message ??= "Движок запущен";
            Apply(status);
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Ошибка: {ex.Message}";
        }
    }

    // Принудительно остановить движок: сначала корректно (unmount + выход), затем убить процесс.
    private async void StopEngineClick(object sender, RoutedEventArgs e)
    {
        if (_engine != null)
        {
            try { await _engine.ShutdownAsync(); }
            catch { /* возможно завис — добьём процесс ниже */ }
            _engine = null;
        }

        KillEngineProcesses();
        StatusText.Text = "Движок остановлен.";
    }

    private static void KillEngineProcesses()
    {
        foreach (var process in Process.GetProcessesByName("BarkCloud.Drive.Engine"))
        {
            try
            {
                process.Kill();
                process.WaitForExit(2000);
            }
            catch { /* уже завершился / нет доступа */ }
            finally { process.Dispose(); }
        }
    }

    private void ShowFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private async Task PollStatusAsync()
    {
        if (_engine == null)
            return;

        try
        {
            Apply(await _engine.GetStatusAsync());
        }
        catch
        {
            _engine = null; // движок мог завершиться
        }
    }

    private void Apply(EngineStatus s)
    {
        // Сессия восстановлена/выполнен вход → форма входа не нужна.
        LoginPanel.Visibility = s.Authenticated ? Visibility.Collapsed : Visibility.Visible;

        var lines = new List<string>
        {
            $"Авторизация: {(s.Authenticated ? "да" : "нет")}",
            s.Mounted ? $"Диск {s.DriveLetter}: примонтирован" : "Диск: не примонтирован",
        };

        if (s.LimitBytes > 0)
            lines.Add($"Хранилище: {Bytes(s.UsedBytes)} / {Bytes(s.LimitBytes)}");
        if (!string.IsNullOrEmpty(s.Message))
            lines.Add(s.Message!);
        if (!string.IsNullOrEmpty(s.Error))
            lines.Add($"Ошибка: {s.Error}");

        StatusText.Text = string.Join(Environment.NewLine, lines);
    }

    private static string? NullIfEmpty(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

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
