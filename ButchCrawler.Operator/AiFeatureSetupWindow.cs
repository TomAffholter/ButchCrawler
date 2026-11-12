using System.Windows;
using System.Windows.Controls;
using ButchCrawler.App;

namespace ButchCrawler.Operator;

public sealed class AiFeatureSetupWindow : Window
{
    private readonly CheckBox _manual = Choice("Manual AI research and handwriting", true,
        "Only runs after you choose a task and confirm that source material may be sent to OpenAI.");
    private readonly CheckBox _audio = Choice("Automatic audio transcription", false,
        "May use paid API credits automatically while the crawler processes discovered recordings.");
    private readonly CheckBox _indexer = Choice("AI embedding index", false,
        "May use paid API credits while creating a semantic search index.");

    public OpenAiFeatureSelection Selection => new(_manual.IsChecked == true, _manual.IsChecked == true,
        _audio.IsChecked == true, _indexer.IsChecked == true);

    public AiFeatureSetupWindow()
    {
        AppTheme.Apply(this);
        Title = "Optional AI features"; Width = 620; SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock { Text = "OpenAI key saved", FontSize = 22, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock
        {
            Text = "Choose which optional features to enable. These features may use paid OpenAI API credits. Ordinary crawling and collection search remain free of AI charges.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 9, 0, 14)
        });
        panel.Children.Add(_manual); panel.Children.Add(_audio); panel.Children.Add(_indexer);
        var note = new TextBlock
        {
            Text = "Recommended: enable manual tools only. Automatic audio and indexing stay off until you deliberately select them.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 14)
        };
        panel.Children.Add(note);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        var save = new Button { Content = "Save choices", Padding = new Thickness(13, 7, 13, 7), IsDefault = true };
        save.SetResourceReference(StyleProperty, "PrimaryButton");
        save.Click += (_, _) => { DialogResult = true; Close(); };
        var notNow = new Button { Content = "Not now · keep AI off", Padding = new Thickness(13, 7, 13, 7), Margin = new Thickness(10, 0, 0, 0), IsCancel = true };
        notNow.Click += (_, _) => { _manual.IsChecked = _audio.IsChecked = _indexer.IsChecked = false; DialogResult = false; Close(); };
        buttons.Children.Add(save); buttons.Children.Add(notNow); panel.Children.Add(buttons);
        Content = panel;
    }

    private static CheckBox Choice(string title, bool selected, string explanation)
    {
        var content = new StackPanel();
        content.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold });
        content.Children.Add(new TextBlock { Text = explanation, TextWrapping = TextWrapping.Wrap, MaxWidth = 520, Margin = new Thickness(0, 2, 0, 0) });
        return new CheckBox { Content = content, IsChecked = selected, Margin = new Thickness(0, 4, 0, 8), VerticalContentAlignment = VerticalAlignment.Top };
    }
}
