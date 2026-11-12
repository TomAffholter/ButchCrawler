using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ButchCrawler.Operator;

public sealed class CollectionSearchHelpWindow : Window
{
    public CollectionSearchHelpWindow()
    {
        AppTheme.Apply(this);
        Title = "Collection search help";
        Width = 720; Height = 690; MinWidth = 620; MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var root = new DockPanel { Margin = new Thickness(22) };
        var close = new Button { Content = "Close (Esc)", IsCancel = true, Padding = new Thickness(16, 9, 16, 9), HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        close.Click += (_, _) => Close(); DockPanel.SetDock(close, Dock.Bottom); root.Children.Add(close);

        var content = new StackPanel();
        content.Children.Add(new TextBlock { Text = "How to search your collection", FontSize = 27, FontWeight = FontWeights.SemiBold });
        content.Children.Add(new TextBlock
        {
            Text = "You can simply type a few useful words, or use the choices below when you want more control. Boolean words may be uppercase or lowercase.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 16), FontSize = 14
        });

        AddExample(content, "A simple search", "Lincoln railroad Illinois",
            "Finds sources containing any of these words. Sources containing more of them normally appear first.");
        AddExample(content, "AND — require both", "Lincoln AND railroad",
            "Finds only sources containing both Lincoln and railroad.");
        AddExample(content, "OR — accept either", "Lincoln OR Douglas",
            "Finds sources containing Lincoln, Douglas, or both.");
        AddExample(content, "NOT — leave something out", "Lincoln NOT Nebraska",
            "Finds sources containing Lincoln but leaves out sources containing Nebraska. You may also write Lincoln AND NOT Nebraska.");
        AddExample(content, "Parentheses — group choices", "(Lincoln OR Douglas) AND debate",
            "Finds a debate source that contains either Lincoln or Douglas.");
        AddExample(content, "Quotation marks — exact phrase", "\"Gettysburg Address\"",
            "Finds those words together in that order instead of finding Gettysburg and Address separately.");
        AddExample(content, "Combine the features", "(Lincoln OR Douglas) AND \"Illinois debate\" NOT Nebraska",
            "Requires the exact phrase Illinois debate, includes Lincoln or Douglas, and excludes Nebraska.");

        content.Children.Add(new TextBlock
        {
            Text = "Helpful reminders",
            FontSize = 19, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 6)
        });
        content.Children.Add(new TextBlock
        {
            Text = "• Put quotation marks around a name or phrase that must stay together.\n• Use parentheses when an OR choice belongs with another requirement.\n• If no results appear, remove one requirement or try OR.\n• Search examines saved webpages, documents, OCR text, and completed audio transcripts.",
            TextWrapping = TextWrapping.Wrap, FontSize = 14, LineHeight = 23
        });

        root.Children.Add(new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Content = root;
    }

    private static void AddExample(Panel panel, string heading, string example, string explanation)
    {
        var card = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(255, 255, 255)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(211, 221, 234)),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(7),
            Padding = new Thickness(13, 10, 13, 10), Margin = new Thickness(0, 0, 0, 10)
        };
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock { Text = heading, FontSize = 15, FontWeight = FontWeights.SemiBold });
        stack.Children.Add(new TextBlock
        {
            Text = example, FontFamily = new FontFamily("Consolas"), FontSize = 15, FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(0, 113, 122)), Margin = new Thickness(0, 4, 0, 4), TextWrapping = TextWrapping.Wrap
        });
        stack.Children.Add(new TextBlock { Text = explanation, FontSize = 13.5, TextWrapping = TextWrapping.Wrap });
        card.Child = stack; panel.Children.Add(card);
    }
}
