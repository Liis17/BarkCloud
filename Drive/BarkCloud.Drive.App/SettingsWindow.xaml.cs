using System.Windows;

using BarkCloud.Drive.Contracts;

using Microsoft.Win32;

using Wpf.Ui.Controls;

namespace BarkCloud.Drive.App;

// Модалка настроек: разлогин, монтирование/размонтирование, переименование, смена буквы,
// папка кэша, перезапуск движка. Работает через прокси движка владельца (MainWindow).
public partial class SettingsWindow : FluentWindow
{
    private static readonly char[] InvalidLabelChars = "\\/:*?\"<>|".ToCharArray();

    private readonly MainWindow _owner;
    private readonly AppSettings _settings;

    internal SettingsWindow(MainWindow owner, AppSettings settings)
    {
        _owner = owner;
        _settings = settings;
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private IDriveEngine? Engine => _owner.Engine;

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        var engine = Engine;
        if (engine == null)
        {
            Status("Движок не запущен.");
            return;
        }

        try
        {
            var settings = await engine.GetSettingsAsync();
            CacheDirBox.Text = settings.CacheDir;

            var s = await engine.GetStatusAsync();
            UsernameText.Text = UserText(s);
            DriveNameBox.Text = !string.IsNullOrEmpty(s.VolumeLabel) ? s.VolumeLabel : (_settings.DriveName ?? "BarkCloud");
            PopulateLetters(s.DriveLetter ?? _settings.DriveLetter);
            UpdateMountButtons(s.Mounted);
        }
        catch (Exception ex)
        {
            Status($"Ошибка: {ex.Message}");
        }
    }

    private void PopulateLetters(string? current)
    {
        LetterCombo.Items.Clear();
        var free = DriveLetters.Free();
        if (!string.IsNullOrEmpty(current) && !free.Contains(current))
            free.Insert(0, current); // текущая (примонтированная) буква «занята» — добавим явно
        foreach (var l in free)
            LetterCombo.Items.Add(l);
        LetterCombo.SelectedItem = current ?? free.FirstOrDefault();
    }

    private void UpdateMountButtons(bool mounted)
    {
        MountButton.IsEnabled = !mounted;
        UnmountButton.IsEnabled = mounted;
    }

    private async void LogoutClick(object sender, RoutedEventArgs e)
    {
        var engine = Engine;
        if (engine == null)
            return;

        try
        {
            await engine.LogoutAsync();
            _settings.Configured = false; // следующий показ — мастер
            _settings.Save();
            Close();
        }
        catch (Exception ex)
        {
            Status($"Ошибка выхода: {ex.Message}");
        }
    }

    private async void MountClick(object sender, RoutedEventArgs e)
    {
        var engine = Engine;
        if (engine == null || LetterCombo.SelectedItem is not string letter)
            return;

        var label = DriveNameBox.Text.Trim();
        if (!ValidateLabel(label))
            return;

        try
        {
            var s = await engine.MountAsync(letter, label);
            if (s.Mounted)
                SaveDrive(letter, label);
            ApplyResult(s, "Примонтировано");
        }
        catch (Exception ex)
        {
            Status($"Ошибка: {ex.Message}");
        }
    }

    private async void UnmountClick(object sender, RoutedEventArgs e)
    {
        var engine = Engine;
        if (engine == null)
            return;

        try
        {
            ApplyResult(await engine.UnmountAsync(), "Отмонтировано");
        }
        catch (Exception ex)
        {
            Status($"Ошибка: {ex.Message}");
        }
    }

    private async void RenameClick(object sender, RoutedEventArgs e)
    {
        var engine = Engine;
        if (engine == null)
            return;

        var label = DriveNameBox.Text.Trim();
        if (!ValidateLabel(label))
            return;

        try
        {
            var s = await engine.GetStatusAsync();
            if (s.Mounted)
                s = await engine.RemountAsync(null, label); // имя применяется только при маунте
            SaveDrive(_settings.DriveLetter, label);
            ApplyResult(s, s.Mounted ? "Диск переименован" : "Имя сохранено (примонтируйте диск)");
        }
        catch (Exception ex)
        {
            Status($"Ошибка: {ex.Message}");
        }
    }

    private async void ChangeLetterClick(object sender, RoutedEventArgs e)
    {
        var engine = Engine;
        if (engine == null || LetterCombo.SelectedItem is not string letter)
            return;

        try
        {
            var s = await engine.GetStatusAsync();
            if (s.Mounted)
                s = await engine.RemountAsync(letter, null);
            SaveDrive(letter, _settings.DriveName);
            PopulateLetters(letter);
            ApplyResult(s, s.Mounted ? $"Буква изменена на {letter}:" : "Буква сохранена (примонтируйте диск)");
        }
        catch (Exception ex)
        {
            Status($"Ошибка: {ex.Message}");
        }
    }

    private async void BrowseCacheClick(object sender, RoutedEventArgs e)
    {
        var engine = Engine;
        if (engine == null)
            return;

        var dialog = new OpenFolderDialog { Title = "Выберите папку для кэша диска", Multiselect = false };
        if (!string.IsNullOrEmpty(CacheDirBox.Text))
            dialog.InitialDirectory = CacheDirBox.Text;
        if (dialog.ShowDialog() != true)
            return;

        try
        {
            var settings = await engine.SetCacheDirAsync(dialog.FolderName);
            CacheDirBox.Text = settings.CacheDir;
            Status("Папка кэша обновлена. Ранее скачанное осталось в прежней папке.");
        }
        catch (Exception ex)
        {
            Status($"Не удалось сменить папку кэша: {ex.Message}");
        }
    }

    private async void RestartClick(object sender, RoutedEventArgs e)
    {
        Status("Перезапуск движка…");
        var engine = await _owner.RestartEngineAsync();
        if (engine == null)
        {
            Status("Движок недоступен после перезапуска.");
            return;
        }

        try
        {
            var s = await engine.GetStatusAsync();
            UsernameText.Text = UserText(s);
            UpdateMountButtons(s.Mounted);
            Status("Движок перезапущен.");
        }
        catch (Exception ex)
        {
            Status($"Ошибка: {ex.Message}");
        }
    }

    private void ApplyResult(EngineStatus s, string okMessage)
    {
        UpdateMountButtons(s.Mounted);
        Status(string.IsNullOrEmpty(s.Error) ? okMessage : $"Ошибка: {s.Error}");
    }

    private void SaveDrive(string? letter, string? name)
    {
        if (!string.IsNullOrEmpty(letter)) _settings.DriveLetter = letter;
        if (!string.IsNullOrEmpty(name)) _settings.DriveName = name;
        _settings.Save();
    }

    private bool ValidateLabel(string label)
    {
        if (string.IsNullOrEmpty(label) || label.Length > 32 || label.IndexOfAny(InvalidLabelChars) >= 0)
        {
            Status("Имя диска: до 32 символов, без \\ / : * ? \" < > |");
            return false;
        }

        return true;
    }

    private static string UserText(EngineStatus s)
        => !string.IsNullOrEmpty(s.Username) ? s.Username! : (s.Authenticated ? "Вы вошли" : "Вход не выполнен");

    private void Status(string message) => SettingsStatus.Text = message;
}
