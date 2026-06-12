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
        private readonly BuilderModel _model;

        public MainWindow()
        {
            InitializeComponent();
            _model = new BuilderModel
            {
                ConfigurationAccessKey = GenerateKey(50),
                WebAdminPassword = GenerateKey(20),
            };
            DataContext = _model;
        }

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

        private void OnBrowse(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFolderDialog { Title = "Папка для docker-compose.yml и .env" };
            if (dialog.ShowDialog() == true)
                OutputPathBox.Text = dialog.FolderName;
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

            try
            {
                var encoding = new UTF8Encoding(false);
                File.WriteAllText(Path.Combine(dir, "docker-compose.yml"),
                    ToLf(BackendComposeGenerator.BuildCompose(_model)), encoding);
                File.WriteAllText(Path.Combine(dir, ".env"),
                    ToLf(BackendComposeGenerator.BuildEnv(_model)), encoding);

                var extra = "";
                if (_model.IncludeNginx)
                {
                    var nginxDir = Path.Combine(dir, "nginx");
                    Directory.CreateDirectory(nginxDir);
                    File.WriteAllText(Path.Combine(nginxDir, "cloud.barkfluff.conf"),
                        ToLf(BackendComposeGenerator.BuildNginxConf(_model)), encoding);

                    var certsDir = Path.Combine(dir, "certs");
                    Directory.CreateDirectory(certsDir);
                    CopyCert(_model.CertCrtPath, certsDir);
                    CopyCert(_model.CertKeyPath, certsDir);

                    extra = " + nginx/ и certs/";
                }

                SetStatus($"Готово: docker-compose.yml, .env{extra} записаны в {dir}", error: false);
            }
            catch (Exception ex)
            {
                SetStatus("Ошибка: " + ex.Message, error: true);
            }
        }

        // Копирует выбранный сертификат в certs/ под его исходным именем.
        private static void CopyCert(string? source, string certsDir)
        {
            if (!string.IsNullOrWhiteSpace(source) && File.Exists(source))
                File.Copy(source, Path.Combine(certsDir, Path.GetFileName(source)), overwrite: true);
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
