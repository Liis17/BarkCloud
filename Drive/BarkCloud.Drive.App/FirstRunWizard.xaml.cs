using System.IO;
using System.Net;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;

using BarkCloud.Drive.Contracts;
using BarkCloud.Drive.Contracts.Localization;

using Microsoft.Win32;

using Wpf.Ui.Controls;

namespace BarkCloud.Drive.App;

// Мастер первичной настройки: адрес сервера → вход → имя/буква диска → папка кэша → автозагрузка.
public partial class FirstRunWizard : FluentWindow
{
    private static readonly char[] InvalidLabelChars = "\\/:*?\"<>|".ToCharArray();

    private IDriveEngine _engine;
    private readonly AppSettings _settings;
    private readonly Func<Task<IDriveEngine?>> _restartEngine;
    private ServerConfig _appliedServer = new(); // адрес, с которым сейчас работает движок
    private bool _serverFileSaved; // server.json уже записан → движок точно стартовал с него
    private int _step;
    private bool _authenticated;
    private bool _langReady; // селектор языка инициализирован (чтобы первичный SelectionChanged не сработал)

    internal FirstRunWizard(IDriveEngine engine, AppSettings settings, Func<Task<IDriveEngine?>> restartEngine)
    {
        _engine = engine;
        _settings = settings;
        _restartEngine = restartEngine;
        InitializeComponent();

        foreach (var letter in DriveLetters.Free())
            LetterCombo.Items.Add(letter);
        if (LetterCombo.Items.Count > 0)
            LetterCombo.SelectedIndex = 0;

        LanguageCombo.ItemsSource = Languages.All;
        LanguageCombo.SelectedItem = Languages.All.FirstOrDefault(l => l.Code == Loc.CurrentCode) ?? Languages.All[0];
        _langReady = true;

        Loaded += OnLoaded;
        ShowStep();
    }

    private void LanguageChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_langReady || LanguageCombo.SelectedItem is not Language lang)
            return;

        Loc.SetCulture(lang.Code);
        _settings.Language = lang.Code;
        _settings.Save();
        _ = _engine.SetLanguageAsync(lang.Code);
        ShowStep(); // обновить подпись кнопки «Далее/Готово» (она ставится из кода)
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Адрес сервера (defaults или ранее сохранённый server.json) — это то,
        // с чем движок стартовал, поэтому одновременно фиксируем как «применённый».
        var server = ServerConfig.Load();
        _serverFileSaved = server != null; // нет файла → движок на дефолтах appsettings.json
        server ??= new ServerConfig();
        _appliedServer = server;
        HostBox.Text = server.Host;
        IdentityPortBox.Text = server.IdentityPort.ToString();
        FilesPortBox.Text = server.FilesPort.ToString();
        UsersPortBox.Text = server.UsersPort.ToString();
        AcceptCertCheck.IsChecked = server.AcceptAnyCert;

        try
        {
            var settings = await _engine.GetSettingsAsync();
            CacheDirBox.Text = settings.CacheDir;

            var status = await _engine.GetStatusAsync();
            if (status.Authenticated)
            {
                // Сессия уже восстановлена движком — логин не нужен.
                _authenticated = true;
                LoginFields.Visibility = Visibility.Collapsed;
                AlreadyLoggedIn.Visibility = Visibility.Visible;
                AlreadyLoggedIn.Text = string.IsNullOrEmpty(status.Username)
                    ? Loc.T("Wizard_AlreadyLoggedIn")
                    : Loc.T("Wizard_LoggedInAsFmt", status.Username);
            }
        }
        catch
        {
            // не критично — мастер продолжит с пустыми дефолтами
        }
    }

    private void ShowStep()
    {
        StepServer.Visibility = _step == 0 ? Visibility.Visible : Visibility.Collapsed;
        StepLogin.Visibility = _step == 1 ? Visibility.Visible : Visibility.Collapsed;
        StepDrive.Visibility = _step == 2 ? Visibility.Visible : Visibility.Collapsed;
        StepCache.Visibility = _step == 3 ? Visibility.Visible : Visibility.Collapsed;
        StepAutostart.Visibility = _step == 4 ? Visibility.Visible : Visibility.Collapsed;

        BackButton.IsEnabled = _step > 0;
        NextButton.Content = Loc.T(_step == 4 ? "Common_Finish" : "Common_Next");
        WizardStatus.Visibility = Visibility.Collapsed;

        // Кнопка входа по ключу: только на шаге входа, если поддерживается системой
        // и сервер задан доменным именем (WebAuthn не работает на «голом» IP).
        KeyLoginButton.Visibility =
            _step == 1 && !_authenticated && WebAuthnClient.IsSupported && IsDomainHost(_appliedServer.Host)
                ? Visibility.Visible
                : Visibility.Collapsed;
    }

    private static bool IsDomainHost(string? host)
        => !string.IsNullOrWhiteSpace(host) && !IPAddress.TryParse(host, out _);

    private void BackClick(object sender, RoutedEventArgs e)
    {
        if (_step > 0)
            _step--;
        ShowStep();
    }

    private void CancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private async void NextClick(object sender, RoutedEventArgs e)
    {
        NextButton.IsEnabled = false;
        try
        {
            if (_step == 4)
            {
                await FinishAsync();
                return;
            }

            if (await ValidateStepAsync())
            {
                _step++;
                ShowStep();
            }
        }
        finally
        {
            NextButton.IsEnabled = true;
        }
    }

    private async Task<bool> ValidateStepAsync()
    {
        switch (_step)
        {
            case 0:
                return await ApplyServerAsync();

            case 1:
                if (_authenticated)
                    return true;

                var login = UsernameBox.Text.Trim();
                if (string.IsNullOrEmpty(login) || string.IsNullOrEmpty(PasswordBox.Password))
                    return Fail(Loc.T("Wizard_EnterLoginPassword"));

                var status = await _engine.LoginAsync(login, PasswordBox.Password, NullIfEmpty(OtpBox.Text));
                if (!status.Authenticated)
                    return Fail(string.IsNullOrEmpty(status.Error) ? Loc.T("Wizard_LoginFailed") : status.Error!);

                _authenticated = true;
                PasswordBox.Password = string.Empty; // не держим пароль/одноразовый OTP в полях
                OtpBox.Text = string.Empty;
                return true;

            case 2:
                var label = VolumeLabelBox.Text.Trim();
                if (string.IsNullOrEmpty(label))
                    return Fail(Loc.T("Wizard_EnterDriveName"));
                if (label.Length > 32 || label.IndexOfAny(InvalidLabelChars) >= 0)
                    return Fail(Loc.T("Common_DriveNameRule"));
                if (LetterCombo.SelectedItem is not string)
                    return Fail(Loc.T("Wizard_SelectLetter"));
                return true;

            case 3:
                var dir = CacheDirBox.Text.Trim();
                if (string.IsNullOrEmpty(dir))
                    return Fail(Loc.T("Common_SelectCache"));
                try
                {
                    Directory.CreateDirectory(dir);
                }
                catch (Exception ex)
                {
                    return Fail(Loc.T("Common_CacheUnavailableFmt", ex.Message));
                }

                return true;

            default:
                return true;
        }
    }

    // Шаг адреса: проверить, сохранить и (если изменился) перезапустить движок,
    // чтобы он подхватил новые адреса — каналы строятся только на старте.
    private async Task<bool> ApplyServerAsync()
    {
        var host = ServerInput.StripScheme(HostBox.Text);
        if (string.IsNullOrEmpty(host))
            return Fail(Loc.T("Common_EnterServer"));
        if (!ServerInput.TryPort(IdentityPortBox.Text, out var ip))
            return Fail(Loc.T("Common_BadPortIdentity"));
        if (!ServerInput.TryPort(FilesPortBox.Text, out var fp))
            return Fail(Loc.T("Common_BadPortFiles"));
        if (!ServerInput.TryPort(UsersPortBox.Text, out var up))
            return Fail(Loc.T("Common_BadPortUsers"));

        var cfg = new ServerConfig
        {
            Host = host,
            IdentityPort = ip,
            FilesPort = fp,
            UsersPort = up,
            AcceptAnyCert = AcceptCertCheck.IsChecked == true,
        };

        // Если server.json уже записан и адрес не менялся (например, вернулись назад) —
        // движок точно работает с ним, перезапуск не нужен. На первом запуске (файла нет)
        // всё равно сохраняем и перезапускаем, чтобы движок гарантированно ушёл с дефолтов.
        if (_serverFileSaved && SameServer(cfg, _appliedServer))
            return true;

        try
        {
            cfg.Save();
        }
        catch (Exception ex)
        {
            return Fail(Loc.T("Common_SaveAddressFailedFmt", ex.Message));
        }

        var engine = await _restartEngine();
        if (engine == null)
            return Fail(Loc.T("Wizard_EngineUnavailableAfterServer"));

        _engine = engine;
        _appliedServer = cfg;
        _serverFileSaved = true;

        // Против нового сервера сессия могла восстановиться из refresh-токена.
        try
        {
            var st = await _engine.GetStatusAsync();
            _authenticated = st.Authenticated;
            if (st.Authenticated)
            {
                LoginFields.Visibility = Visibility.Collapsed;
                AlreadyLoggedIn.Visibility = Visibility.Visible;
                AlreadyLoggedIn.Text = string.IsNullOrEmpty(st.Username)
                    ? Loc.T("Wizard_AlreadyLoggedIn")
                    : Loc.T("Wizard_LoggedInAsFmt", st.Username);
            }
            else
            {
                LoginFields.Visibility = Visibility.Visible;
                AlreadyLoggedIn.Visibility = Visibility.Collapsed;
            }
        }
        catch
        {
            // статус не критичен — шаг входа проверит сам
        }

        return true;
    }

    private static bool SameServer(ServerConfig a, ServerConfig b)
        => a.Host == b.Host && a.IdentityPort == b.IdentityPort && a.FilesPort == b.FilesPort
           && a.UsersPort == b.UsersPort && a.AcceptAnyCert == b.AcceptAnyCert;

    private void BrowseClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = Loc.T("Common_SelectCacheFolderTitle"), Multiselect = false };
        if (!string.IsNullOrEmpty(CacheDirBox.Text))
            dialog.InitialDirectory = CacheDirBox.Text;
        if (dialog.ShowDialog() == true)
            CacheDirBox.Text = dialog.FolderName;
    }

    private async Task FinishAsync()
    {
        var label = VolumeLabelBox.Text.Trim();
        var letter = (string)LetterCombo.SelectedItem;
        var cacheDir = CacheDirBox.Text.Trim();

        if (string.IsNullOrEmpty(cacheDir))
        {
            Fail(Loc.T("Common_SelectCache"));
            return;
        }

        try
        {
            Directory.CreateDirectory(cacheDir); // проверка, что путь создаваем/доступен
        }
        catch (Exception ex)
        {
            Fail(Loc.T("Common_CacheUnavailableFmt", ex.Message));
            return;
        }

        try
        {
            await _engine.SetCacheDirAsync(cacheDir);
            var status = await _engine.MountAsync(letter, label);
            if (!status.Mounted)
            {
                Fail(string.IsNullOrEmpty(status.Error) ? Loc.T("Wizard_CreateDriveFailed") : status.Error!);
                return;
            }

            _settings.Configured = true;
            _settings.DriveName = label;
            _settings.DriveLetter = letter;
            _settings.Save();

            Autostart.SetApp(AutostartAppCheck.IsChecked == true);
            Autostart.SetEngine(AutostartEngineCheck.IsChecked == true);

            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            Fail(Loc.T("Wizard_CreateDriveErrorFmt", ex.Message));
        }
    }

    private async void KeyLoginClick(object sender, RoutedEventArgs e)
    {
        // Passwordless: логин вводить не нужно — ключ сам определит пользователя.
        NextButton.IsEnabled = false;
        BackButton.IsEnabled = false;
        KeyLoginButton.IsEnabled = false;
        try
        {
            var challenge = await _engine.BeginWebAuthnAsync();
            if (string.IsNullOrEmpty(challenge.ChallengeId))
            {
                Fail(Loc.T("Wizard_KeyUnavailable"));
                return;
            }

            var hwnd = new WindowInteropHelper(this).Handle;

            string assertionJson;
            try
            {
                // Системный диалог WebAuthn (PIN + касание) — на фоновом потоке, чтобы не морозить UI.
                assertionJson = await Task.Run(() => WebAuthnClient.GetAssertion(hwnd, challenge.RpId, challenge.OptionsJson));
            }
            catch
            {
                // Отмена пользователем или ошибка ключа.
                Fail(Loc.T("Wizard_KeyFailed"));
                return;
            }

            var status = await _engine.CompleteWebAuthnAsync(challenge.ChallengeId, assertionJson);
            if (!status.Authenticated)
            {
                Fail(string.IsNullOrEmpty(status.Error) ? Loc.T("Wizard_LoginFailed") : status.Error!);
                return;
            }

            _authenticated = true;
            PasswordBox.Password = string.Empty;
            OtpBox.Text = string.Empty;
            _step++;
            ShowStep();
        }
        finally
        {
            NextButton.IsEnabled = true;
            BackButton.IsEnabled = _step > 0;
            KeyLoginButton.IsEnabled = true;
        }
    }

    private bool Fail(string message)
    {
        WizardStatus.Text = message;
        WizardStatus.Visibility = Visibility.Visible;
        return false;
    }

    private static string? NullIfEmpty(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
