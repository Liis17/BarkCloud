using System.Windows;
using System.Windows.Controls;

using BarkCloud.Drive.Contracts;
using BarkCloud.Drive.Contracts.Localization;

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
    private bool _langReady; // селектор языка инициализирован (чтобы первичный SelectionChanged не сработал)

    internal SettingsWindow(MainWindow owner, AppSettings settings)
    {
        _owner = owner;
        _settings = settings;
        InitializeComponent();

        LanguageCombo.ItemsSource = Languages.All;
        LanguageCombo.SelectedItem = Languages.All.FirstOrDefault(l => l.Code == Loc.CurrentCode) ?? Languages.All[0];
        _langReady = true;

        Loaded += OnLoaded;
    }

    private IDriveEngine? Engine => _owner.Engine;

    private void LanguageChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_langReady || LanguageCombo.SelectedItem is not Language lang)
            return;

        Loc.SetCulture(lang.Code);
        _settings.Language = lang.Code;
        _settings.Save();
        _ = Engine?.SetLanguageAsync(lang.Code);
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Состояние автозагрузки — из реестра, не зависит от движка.
        AutostartAppCheck.IsChecked = Autostart.IsAppEnabled();
        AutostartEngineCheck.IsChecked = Autostart.IsEngineEnabled();

        // Адрес сервера читается из файла, не зависит от движка.
        var server = ServerConfig.Load() ?? new ServerConfig();
        HostBox.Text = server.Host;
        IdentityPortBox.Text = server.IdentityPort.ToString();
        FilesPortBox.Text = server.FilesPort.ToString();
        UsersPortBox.Text = server.UsersPort.ToString();
        AcceptCertCheck.IsChecked = server.AcceptAnyCert;

        var engine = Engine;
        if (engine == null)
        {
            Status(Loc.T("Common_EngineNotRunning"));
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
            Status(Loc.T("Common_ErrorFmt", ex.Message));
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
            Status(Loc.T("Settings_LogoutErrorFmt", ex.Message));
        }
    }

    private async void ApplyServerClick(object sender, RoutedEventArgs e)
    {
        var host = ServerInput.StripScheme(HostBox.Text);
        if (string.IsNullOrEmpty(host)) { Status(Loc.T("Common_EnterServer")); return; }
        if (!ServerInput.TryPort(IdentityPortBox.Text, out var ip)) { Status(Loc.T("Common_BadPortIdentity")); return; }
        if (!ServerInput.TryPort(FilesPortBox.Text, out var fp)) { Status(Loc.T("Common_BadPortFiles")); return; }
        if (!ServerInput.TryPort(UsersPortBox.Text, out var up)) { Status(Loc.T("Common_BadPortUsers")); return; }

        var cfg = new ServerConfig
        {
            Host = host,
            IdentityPort = ip,
            FilesPort = fp,
            UsersPort = up,
            AcceptAnyCert = AcceptCertCheck.IsChecked == true,
        };

        try
        {
            cfg.Save();
        }
        catch (Exception ex)
        {
            Status(Loc.T("Common_SaveAddressFailedFmt", ex.Message));
            return;
        }

        Status(Loc.T("Settings_ApplyingAddress"));
        var engine = await _owner.RestartEngineAsync();
        if (engine == null)
        {
            Status(Loc.T("Settings_EngineUnavailableAfterServer"));
            return;
        }

        try
        {
            var s = await engine.GetStatusAsync();
            if (!s.Authenticated)
            {
                // Новый сервер — сессия не восстановилась. Возврат к мастеру (там есть вход).
                _settings.Configured = false;
                _settings.Save();
                Status(Loc.T("Settings_AddressSavedWizard"));
                Close();
                return;
            }

            UsernameText.Text = UserText(s);
            UpdateMountButtons(s.Mounted);
            Status(Loc.T("Settings_AddressUpdated"));
        }
        catch (Exception ex)
        {
            Status(Loc.T("Common_ErrorFmt", ex.Message));
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
            ApplyResult(s, Loc.T("Settings_Mounted"));
        }
        catch (Exception ex)
        {
            Status(Loc.T("Common_ErrorFmt", ex.Message));
        }
    }

    private async void UnmountClick(object sender, RoutedEventArgs e)
    {
        var engine = Engine;
        if (engine == null)
            return;

        try
        {
            ApplyResult(await engine.UnmountAsync(), Loc.T("Settings_Unmounted"));
        }
        catch (Exception ex)
        {
            Status(Loc.T("Common_ErrorFmt", ex.Message));
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
            ApplyResult(s, Loc.T(s.Mounted ? "Settings_DriveRenamed" : "Settings_NameSaved"));
        }
        catch (Exception ex)
        {
            Status(Loc.T("Common_ErrorFmt", ex.Message));
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
            ApplyResult(s, s.Mounted ? Loc.T("Settings_LetterChangedFmt", letter) : Loc.T("Settings_LetterSaved"));
        }
        catch (Exception ex)
        {
            Status(Loc.T("Common_ErrorFmt", ex.Message));
        }
    }

    private async void BrowseCacheClick(object sender, RoutedEventArgs e)
    {
        var engine = Engine;
        if (engine == null)
            return;

        var dialog = new OpenFolderDialog { Title = Loc.T("Common_SelectCacheFolderTitle"), Multiselect = false };
        if (!string.IsNullOrEmpty(CacheDirBox.Text))
            dialog.InitialDirectory = CacheDirBox.Text;
        if (dialog.ShowDialog() != true)
            return;

        try
        {
            var settings = await engine.SetCacheDirAsync(dialog.FolderName);
            CacheDirBox.Text = settings.CacheDir;
            Status(Loc.T("Settings_CacheUpdated"));
        }
        catch (Exception ex)
        {
            Status(Loc.T("Settings_CacheChangeFailedFmt", ex.Message));
        }
    }

    private async void RestartClick(object sender, RoutedEventArgs e)
    {
        Status(Loc.T("Settings_RestartingEngine"));
        var engine = await _owner.RestartEngineAsync();
        if (engine == null)
        {
            Status(Loc.T("Settings_EngineUnavailableAfterRestart"));
            return;
        }

        try
        {
            var s = await engine.GetStatusAsync();
            UsernameText.Text = UserText(s);
            UpdateMountButtons(s.Mounted);
            Status(Loc.T("Settings_EngineRestarted"));
        }
        catch (Exception ex)
        {
            Status(Loc.T("Common_ErrorFmt", ex.Message));
        }
    }

    private void AutostartAppClick(object sender, RoutedEventArgs e)
    {
        Autostart.SetApp(AutostartAppCheck.IsChecked == true);
        Status(Loc.T("Settings_AutostartUpdated"));
    }

    private void AutostartEngineClick(object sender, RoutedEventArgs e)
    {
        Autostart.SetEngine(AutostartEngineCheck.IsChecked == true);
        Status(Loc.T("Settings_AutostartUpdated"));
    }

    private void ApplyResult(EngineStatus s, string okMessage)
    {
        UpdateMountButtons(s.Mounted);
        Status(string.IsNullOrEmpty(s.Error) ? okMessage : Loc.T("Common_ErrorFmt", s.Error));
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
            Status(Loc.T("Common_DriveNameRule"));
            return false;
        }

        return true;
    }

    private static string UserText(EngineStatus s)
        => !string.IsNullOrEmpty(s.Username) ? s.Username! : Loc.T(s.Authenticated ? "User_LoggedIn" : "User_NotLoggedIn");

    private void Status(string message) => SettingsStatus.Text = message;
}
