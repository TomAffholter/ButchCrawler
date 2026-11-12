using System.Windows;
using System.Windows.Media;
using System.Windows.Controls;

namespace ButchCrawler.Operator;

public static class AppTheme
{
    public static void Apply(Window window)
    {
        window.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/ButchCrawler.Operator;component/Theme.xaml", UriKind.Relative) });
        window.Background = new SolidColorBrush(Color.FromRgb(242, 245, 250));
        window.Foreground = new SolidColorBrush(Color.FromRgb(24, 43, 67));
        window.FontFamily = new FontFamily("Segoe UI"); window.FontSize = 13;
        window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
    }

    public static void AddHeading(DockPanel panel, string title)
    {
        var heading = new TextBlock { Text = title, FontSize = 25, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 14) };
        DockPanel.SetDock(heading, Dock.Top); panel.Children.Add(heading);
    }
}
