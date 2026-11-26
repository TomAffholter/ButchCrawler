using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ButchCrawler.Operator;

public sealed class SupportContactWindow : Window
{
    public const string SubscriptionSubject = "Subscribe";
    public const string BugSubject = "Bug Report";
    public const string SuggestionSubject = "Suggestion";

    private readonly TextBlock _status = new()
    {
        TextWrapping = TextWrapping.Wrap,
        Foreground = Brushes.DarkSlateGray,
        VerticalAlignment = VerticalAlignment.Center
    };

    public SupportContactWindow()
    {
        Title = "Updates, bugs, and suggestions";
        Width = 760;
        Height = 760;
        MinWidth = 650;
        MinHeight = 560;
        AppTheme.Apply(this);

        var root = new DockPanel { Margin = new Thickness(28) };
        Content = root;
        AppTheme.AddHeading(root, "Updates, bugs, and suggestions");

        var bottom = new DockPanel { Margin = new Thickness(0, 18, 0, 0) };
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(bottom);
        var close = new Button
        {
            Content = "Close",
            MinWidth = 100,
            Padding = new Thickness(14, 8, 14, 8),
            IsCancel = true
        };
        close.Click += (_, _) => Close();
        DockPanel.SetDock(close, Dock.Right);
        bottom.Children.Add(close);
        bottom.Children.Add(_status);

        var panel = new StackPanel();
        root.Children.Add(new ScrollViewer
        {
            Content = panel,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        });

        panel.Children.Add(new TextBlock
        {
            Text = $"Please contact History Crawler at {SupportOptions.ContactEmail}. History Crawler opens your normal email program, where you can write the message, review it, and press Send.",
            FontSize = 15,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 16)
        });

        panel.Children.Add(Card(
            "Receive History Crawler updates",
            $"To be placed on the update mailing list, email {SupportOptions.ContactEmail} with the subject “{SubscriptionSubject}”.",
            "Email to subscribe",
            SubscriptionSubject));

        panel.Children.Add(Card(
            "Report a bug or anomaly",
            $"Please send bug reports to {SupportOptions.ContactEmail}. Describe what happened, what you expected, and the steps that led to the problem. Do not include passwords, API keys, or private research records.",
            "Email a bug report",
            BugSubject));

        panel.Children.Add(Card(
            "Send a suggestion",
            $"Please send suggestions to {SupportOptions.ContactEmail}. Explain what you would like History Crawler to do and how it would help your research.",
            "Email a suggestion",
            SuggestionSubject));

        var copy = new Button
        {
            Content = "Copy email address",
            Padding = new Thickness(12, 7, 12, 7),
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 6, 0, 0)
        };
        copy.Click += (_, _) =>
        {
            Clipboard.SetText(SupportOptions.ContactEmail);
            _status.Text = $"Copied {SupportOptions.ContactEmail}.";
        };
        panel.Children.Add(copy);
    }

    private Border Card(string heading, string description, string buttonText, string subject)
    {
        var content = new StackPanel();
        content.Children.Add(new TextBlock
        {
            Text = heading,
            FontSize = 17,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 7)
        });
        content.Children.Add(new TextBlock
        {
            Text = description,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10)
        });
        var button = new Button
        {
            Content = buttonText,
            Padding = new Thickness(12, 7, 12, 7),
            HorizontalAlignment = HorizontalAlignment.Left
        };
        button.Click += (_, _) => OpenEmail(subject);
        content.Children.Add(button);

        return new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(238, 245, 251)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(169, 197, 223)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(16),
            Margin = new Thickness(0, 0, 0, 12),
            Child = content
        };
    }

    private void OpenEmail(string subject)
    {
        try
        {
            var address = "mailto:" + SupportOptions.ContactEmail +
                          "?subject=" + Uri.EscapeDataString(subject);
            Process.Start(new ProcessStartInfo(address) { UseShellExecute = true });
            _status.Text = $"Email opened with the subject “{subject}”. Review it and press Send.";
        }
        catch
        {
            Clipboard.SetText(SupportOptions.ContactEmail);
            _status.Text = $"The email program could not be opened. {SupportOptions.ContactEmail} was copied instead.";
        }
    }
}
