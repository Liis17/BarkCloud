using System.IO;
using System.Windows;
using System.Windows.Threading;

using BarkCloud.Drive.Contracts;

namespace BarkCloud.Drive.App;

public partial class MainWindow : Window
{
    private IDriveEngine? _engine;
    private readonly DispatcherTimer _statusTimer;

    public MainWindow()
    {
        InitializeComponent();
        PopulateDriveLetters();

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

        if (LetterCombo.Items.Count > 0)
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
        var engine = await EnsureEngineAsync();
        if (engine == null)
            return;

        try
        {
            var status = await engine.LoginAsync(UsernameBox.Text.Trim(), PasswordBox.Password, NullIfEmpty(OtpBox.Text));
            Apply(status);
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
            Apply(await engine.MountAsync(letter));
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

    private async void ShutdownClick(object sender, RoutedEventArgs e)
    {
        if (_engine == null)
            return;

        try { await _engine.ShutdownAsync(); }
        catch { /* движок завершается */ }

        _engine = null;
        StatusText.Text = "Движок остановлен.";
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
