using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ButchCrawler.Operator;

public sealed class DonationWindow : Window
{
    private readonly string? _settingsPath;
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DarkSlateGray };

    public DonationWindow(string? settingsPath)
    {
        _settingsPath = settingsPath;
        Title = "Support History Crawler";
        Width = 720; Height = 565; MinWidth = 620; MinHeight = 500;
        AppTheme.Apply(this);

        var root = new DockPanel { Margin = new Thickness(28) };
        Content = root;
        AppTheme.AddHeading(root, "Support History Crawler");

        var close = new Button { Content = "Close", MinWidth = 100, Padding = new Thickness(14, 8, 14, 8), IsCancel = true };
        close.Click += (_, _) => Close();
        var bottom = new DockPanel { Margin = new Thickness(0, 18, 0, 0) };
        DockPanel.SetDock(bottom, Dock.Bottom); root.Children.Add(bottom);
        DockPanel.SetDock(close, Dock.Right); bottom.Children.Add(close);
        bottom.Children.Add(_status);

        var panel = new StackPanel(); root.Children.Add(new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        panel.Children.Add(new TextBlock
        {
            Text = "History Crawler is free research software. If it has helped your research, you may make an optional contribution toward continued development, testing, and documentation.",
            FontSize = 16, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 18)
        });
        panel.Children.Add(new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(232, 242, 246)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(115, 174, 184)),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(18),
            Child = new TextBlock
            {
                Text = $"The PayPal donation page directs your contribution to {SupportOptions.ContactEmail}. PayPal handles the payment securely. History Crawler never receives or stores your card, bank, or PayPal sign-in information.",
                TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.SemiBold
            }
        });

        var donate = new Button
        {
            Content = "Donate securely with PayPal",
            FontSize = 17, FontWeight = FontWeights.SemiBold, Padding = new Thickness(20, 12, 20, 12),
            HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 22, 0, 10)
        };
        donate.Click += Donate_Click;
        panel.Children.Add(donate);

        var copy = new Button { Content = "Copy PayPal contribution link", Padding = new Thickness(12, 7, 12, 7), HorizontalAlignment = HorizontalAlignment.Left };
        copy.Click += Copy_Click;
        panel.Children.Add(copy);

        panel.Children.Add(new TextBlock
        {
            Text = "Contributions are optional, are not tax-deductible charitable donations, and do not purchase additional features or guaranteed individual support. PayPal may deduct its normal processing fee.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 22, 0, 12), Foreground = Brushes.DarkSlateGray
        });
        panel.Children.Add(new TextBlock
        {
            Text = $"Questions: {SupportOptions.ContactEmail}\nDonation history and receipts remain available in PayPal.",
            TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.SemiBold
        });
    }

    private void Donate_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var uri = SupportOptions.GetDonationUri(_settingsPath);
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
            _status.Text = $"PayPal opened in your browser for {SupportOptions.ContactEmail}.";
        }
        catch (Exception ex)
        {
            _status.Text = "The PayPal page could not be opened.";
            MessageBox.Show(this, ex.Message, "PayPal contribution page", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(SupportOptions.GetDonationUri(_settingsPath).AbsoluteUri);
            _status.Text = "The PayPal contribution link was copied.";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Copy contribution link", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
