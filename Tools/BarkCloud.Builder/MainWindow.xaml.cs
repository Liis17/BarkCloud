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
                SetStatus($"Готово: docker-compose.yml и .env записаны в {dir}", error: false);
            }
            catch (Exception ex)
            {
                SetStatus("Ошибка: " + ex.Message, error: true);
            }
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
