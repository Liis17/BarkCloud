using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
using Wpf.Ui.Controls;
using Button = Wpf.Ui.Controls.Button;
using TextBox = Wpf.Ui.Controls.TextBox;

namespace BarkCloud.Builder
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : FluentWindow
    {
        private BuilderModel _model;

        public MainWindow()
        {
            InitializeComponent();
            // Разделённые секреты генерируем при каждом запуске без загруженного конфига;
            // при Load() из существующего .env они восстанавливаются оттуда.
            _model = new BuilderModel
            {
                WebAdminCookieKey = BuilderModel.GenerateSecret(64),
                WebAdminPassword = GenerateKey(20),
            };
            _model.RegenerateServiceSecrets();
            DataContext = _model;
        }

        private void OnRegenerateServiceSecrets(object sender, RoutedEventArgs e) =>
            _model.RegenerateServiceSecrets();

        // Единый обработчик кнопок «Случайно»: целевое поле — в CommandParameter, длина — в Tag.
        private void OnRandom(object sender, RoutedEventArgs e)
        {
            var button = (Button)sender;
            if (button.CommandParameter is TextBox box)
            {
                int length = int.TryParse(button.Tag?.ToString(), out var n) ? n : 20;
                box.Text = GenerateKey(length);
            }
        }

        private void OnIncludeSeqClick(object sender, RoutedEventArgs e) =>
            _model.SetGlobalSeq(SeqToggle.IsChecked == true);

        private void OnIncludeLognaraClick(object sender, RoutedEventArgs e) =>
            _model.SetGlobalLognara(LognaraToggle.IsChecked == true);

        private void OnBrowse(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFolderDialog { Title = "Папка для docker-compose.yml и .env" };
            if (dialog.ShowDialog() == true)
                OutputPathBox.Text = dialog.FolderName;
        }

        // Загружает ранее созданную конфигурацию и делает её папку папкой вывода.
        private void OnOpen(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFolderDialog { Title = "Папка с docker-compose.yml и .env" };
            if (dialog.ShowDialog() != true)
                return;

            var dir = dialog.FolderName;
            var composePath = Path.Combine(dir, "docker-compose.yml");
            var envPath = Path.Combine(dir, ".env");
            var nginxPath = Path.Combine(dir, "nginx", "cloud.barkfluff.conf");
            var missing = new[] { "docker-compose.yml", ".env" }.Where(f => !File.Exists(Path.Combine(dir, f))).ToList();
            if (missing.Count > 0)
            {
                SetStatus("В папке не найдены: " + string.Join(", ", missing), error: true);
                return;
            }

            try
            {
                var compose = File.ReadAllText(composePath).Replace("\r\n", "\n");
                var nginxConf = File.Exists(nginxPath) ? File.ReadAllText(nginxPath) : null;
                if (nginxConf == null && compose.Contains("\n  cloud-nginx:\n"))
                {
                    SetStatus("Compose содержит cloud-nginx, но не найден nginx/cloud.barkfluff.conf.", error: true);
                    return;
                }

                _model = BuilderConfigLoader.Load(compose, File.ReadAllText(envPath), nginxConf, dir);
                DataContext = _model;
                var nginxNote = nginxConf == null ? " (nginx-конфиг не найден — nginx выключен)" : "";
                var lognaraNote = _model.LegacyLognaraImport
                    ? " Старый адрес relay сохранён как legacy-признак, Core URL и токен relay нужно заполнить вручную."
                    : "";
                SetStatus($"Конфигурация загружена из {dir}{nginxNote}.{lognaraNote}", error: false);
            }
            catch (Exception ex)
            {
                SetStatus("Ошибка: " + ex.Message, error: true);
            }
        }

        // Выбор файла сертификата; целевое поле передаётся в CommandParameter.
        private void OnPickCert(object sender, RoutedEventArgs e)
        {
            var button = (Button)sender;
            if (button.CommandParameter is TextBox box)
            {
                var dialog = new OpenFileDialog
                {
                    Title = "Выберите файл сертификата",
                    Filter = "Сертификаты (*.pem;*.crt;*.key)|*.pem;*.crt;*.key|Все файлы (*.*)|*.*",
                };
                if (dialog.ShowDialog() == true)
                    box.Text = dialog.FileName;
            }
        }

        private void OnGenerate(object sender, RoutedEventArgs e)
        {
            var dir = _model.OutputPath?.Trim();
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
            {
                SetStatus("Укажите существующую папку для вывода.", error: true);
                return;
            }

            var validationErrors = BuilderModelValidator.Validate(_model);
            if (validationErrors.Count > 0)
            {
                SetStatus(string.Join("\n", validationErrors), error: true);
                return;
            }

            try
            {
                var encoding = new UTF8Encoding(false);
                JwtKeyFileWriter.WriteJwtKeys(dir);
                File.WriteAllText(Path.Combine(dir, "docker-compose.yml"),
                    ToLf(BackendComposeGenerator.BuildCompose(_model)), encoding);
                File.WriteAllText(Path.Combine(dir, ".env"),
                    ToLf(BackendComposeGenerator.BuildEnv(_model)), encoding);
                MigrationScriptWriter.WriteTo(dir);

                var lognaraDockerfiles = BackendComposeGenerator.BuildLognaraDockerfiles(_model);
                if (lognaraDockerfiles.Count > 0)
                {
                    var lognaraDir = Path.Combine(dir, "lognara");
                    Directory.CreateDirectory(lognaraDir);
                    foreach (var (file, contents) in lognaraDockerfiles)
                        File.WriteAllText(Path.Combine(lognaraDir, file), ToLf(contents), encoding);
                }

                var extra = "";
                if (lognaraDockerfiles.Count > 0)
                    extra = " + lognara/*.Dockerfile";
                if (_model.UseNginx)
                {
                    var nginxDir = Path.Combine(dir, "nginx");
                    Directory.CreateDirectory(nginxDir);
                    File.WriteAllText(Path.Combine(nginxDir, "cloud.barkfluff.conf"),
                        ToLf(BackendComposeGenerator.BuildNginxConf(_model)), encoding);

                    if (_model.IncludeNginx)
                    {
                        var certsDir = Path.Combine(dir, "certs");
                        Directory.CreateDirectory(certsDir);
                        CopyCert(_model.CertCrtPath, certsDir);
                        CopyCert(_model.CertKeyPath, certsDir);
                        extra = " + nginx/ и certs/";
                    }
                    else
                    {
                        extra = " + конфиг для внешнего nginx/";
                    }
                }

                SetStatus($"Готово: docker-compose.yml, .env, migrate-secrets.sh{extra} записаны в {dir}", error: false);
            }
            catch (Exception ex)
            {
                SetStatus("Ошибка: " + ex.Message, error: true);
            }
        }

        // Копирует выбранный сертификат в certs/ под его исходным именем.
        private static void CopyCert(string? source, string certsDir)
        {
            if (string.IsNullOrWhiteSpace(source) || !File.Exists(source))
                return;
            var target = Path.Combine(certsDir, Path.GetFileName(source));
            // После «Открыть» сертификат уже лежит в certs/ — копировать файл сам в себя нельзя.
            if (!string.Equals(Path.GetFullPath(source), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
                File.Copy(source, target, overwrite: true);
        }

        // Docker/Linux ожидает LF; нормализуем переводы строк.
        private static string ToLf(string text) => text.Replace("\r\n", "\n");

        private void SetStatus(string text, bool error)
        {
            StatusText.Text = text;
            StatusText.Foreground = (Brush)FindResource(
                error ? "SystemFillColorCriticalBrush" : "SystemFillColorSuccessBrush");
        }

        private static string GenerateKey(int length)
        {
            const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
            var bytes = RandomNumberGenerator.GetBytes(length);
            var sb = new StringBuilder(length);
            foreach (var b in bytes)
                sb.Append(chars[b % chars.Length]);
            return sb.ToString();
        }

    }
}
