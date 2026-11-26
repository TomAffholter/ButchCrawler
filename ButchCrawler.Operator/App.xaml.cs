using System.Configuration;
using System.Data;
using System.Windows;

namespace ButchCrawler.Operator;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var appDirectory = global::ButchCrawler.Operator.MainWindow.FindAppDirectory();
        if (string.IsNullOrWhiteSpace(appDirectory))
        {
            MessageBox.Show("History Crawler could not locate its crawler files.", "History Crawler", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        var settingsPath = System.IO.Path.Combine(appDirectory, "appsettings.json");
        if (FirstRunSetupWindow.ShouldShowAutomatically())
        {
            var setup = new FirstRunSetupWindow(settingsPath);
            if (setup.ShowDialog() != true)
            {
                Shutdown();
                return;
            }
        }

        MainWindow = new global::ButchCrawler.Operator.MainWindow();
        MainWindow.Show();
    }
}

