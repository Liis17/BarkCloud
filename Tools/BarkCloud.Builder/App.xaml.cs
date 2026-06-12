using System.Windows;
using Wpf.Ui.Appearance;

namespace BarkCloud.Builder
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            ApplicationThemeManager.ApplySystemTheme();
        }
    }
}
