using System.IO;
using System.Windows;

using BarkCloud.Drive.Contracts;

using Microsoft.Win32;

using Wpf.Ui.Controls;

namespace BarkCloud.Drive.App;

// Мастер первичной настройки: вход → имя/буква диска → папка кэша → создание диска.
public partial class FirstRunWizard : FluentWindow
{
    private static readonly char[] InvalidLabelChars = "\\/:*?\"<>|".ToCharArray();

    private readonly IDriveEngine _engine;
    private readonly AppSettings _settings;
    private int _step;
    private bool _authenticated;

    internal FirstRunWizard(IDriveEngine engine, AppSettings settings)
    {
        _engine = engine;
        _settings = settings;
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
        StepLogin.Visibility = _step == 0 ? Visibility.Visible : Visibility.Collapsed;
        StepDrive.Visibility = _step == 1 ? Visibility.Visible : Visibility.Collapsed;
        StepCache.Visibility = _step == 2 ? Visibility.Visible : Visibility.Collapsed;
        StepAutostart.Visibility = _step == 3 ? Visibility.Visible : Visibility.Collapsed;

        BackButton.IsEnabled = _step > 0;
        NextButton.Content = _step == 3 ? "Готово" : "Далее";
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
            if (_step == 3)
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
                if (_authenticated)
                    return true;

                var login = UsernameBox.Text.Trim();
                if (string.IsNullOrEmpty(login) || string.IsNullOrEmpty(PasswordBox.Password))
                    return Fail("Введите логин и пароль.");

                var status = await _engine.LoginAsync(login, PasswordBox.Password, NullIfEmpty(OtpBox.Text));
                if (!status.Authenticated)
                    return Fail(string.IsNullOrEmpty(status.Error) ? "Не удалось войти." : status.Error!);

                _authenticated = true;
                return true;

            case 1:
                var label = VolumeLabelBox.Text.Trim();
                if (string.IsNullOrEmpty(label))
                    return Fail("Введите имя диска.");
                if (label.Length > 32 || label.IndexOfAny(InvalidLabelChars) >= 0)
                    return Fail("Имя диска: до 32 символов, без \\ / : * ? \" < > |");
                if (LetterCombo.SelectedItem is not string)
                    return Fail("Выберите букву диска.");
                return true;

            case 2:
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
