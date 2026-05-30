using System.IO;
using System.Windows;

using BarkCloud.Drive.Contracts;

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

        Loaded += OnLoaded;
        ShowStep();
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
                    ? "Вы уже вошли. Нажмите «Далее»."
                    : $"Вы вошли как {status.Username}. Нажмите «Далее».";
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
        NextButton.Content = _step == 4 ? "Готово" : "Далее";
        WizardStatus.Visibility = Visibility.Collapsed;
    }

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
                    return Fail("Введите логин и пароль.");

                var status = await _engine.LoginAsync(login, PasswordBox.Password, NullIfEmpty(OtpBox.Text));
                if (!status.Authenticated)
                    return Fail(string.IsNullOrEmpty(status.Error) ? "Не удалось войти." : status.Error!);

                _authenticated = true;
                PasswordBox.Password = string.Empty; // не держим пароль/одноразовый OTP в полях
                OtpBox.Text = string.Empty;
                return true;

            case 2:
                var label = VolumeLabelBox.Text.Trim();
                if (string.IsNullOrEmpty(label))
                    return Fail("Введите имя диска.");
                if (label.Length > 32 || label.IndexOfAny(InvalidLabelChars) >= 0)
                    return Fail("Имя диска: до 32 символов, без \\ / : * ? \" < > |");
                if (LetterCombo.SelectedItem is not string)
                    return Fail("Выберите букву диска.");
                return true;

            case 3:
                var dir = CacheDirBox.Text.Trim();
                if (string.IsNullOrEmpty(dir))
                    return Fail("Выберите папку кэша.");
                try
                {
                    Directory.CreateDirectory(dir);
                }
                catch (Exception ex)
                {
                    return Fail($"Папка кэша недоступна: {ex.Message}");
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
            return Fail("Введите адрес сервера.");
        if (!ServerInput.TryPort(IdentityPortBox.Text, out var ip))
            return Fail("Неверный порт Identity (1–65535).");
        if (!ServerInput.TryPort(FilesPortBox.Text, out var fp))
            return Fail("Неверный порт Files (1–65535).");
        if (!ServerInput.TryPort(UsersPortBox.Text, out var up))
            return Fail("Неверный порт Users (1–65535).");

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
            return Fail($"Не удалось сохранить адрес: {ex.Message}");
        }

        var engine = await _restartEngine();
        if (engine == null)
            return Fail("Движок недоступен после смены адреса сервера.");

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
                    ? "Вы уже вошли. Нажмите «Далее»."
                    : $"Вы вошли как {st.Username}. Нажмите «Далее».";
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
        var dialog = new OpenFolderDialog { Title = "Выберите папку для кэша диска", Multiselect = false };
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
            Fail("Выберите папку кэша.");
            return;
        }

        try
        {
            Directory.CreateDirectory(cacheDir); // проверка, что путь создаваем/доступен
        }
        catch (Exception ex)
        {
            Fail($"Папка кэша недоступна: {ex.Message}");
            return;
        }

        try
        {
            await _engine.SetCacheDirAsync(cacheDir);
            var status = await _engine.MountAsync(letter, label);
            if (!status.Mounted)
            {
                Fail(string.IsNullOrEmpty(status.Error) ? "Не удалось создать диск." : status.Error!);
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
            Fail($"Ошибка создания диска: {ex.Message}");
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
