using System.Windows;
using System.Windows.Controls;
using ButchCrawler.App;

namespace ButchCrawler.Operator;

public sealed class HistoricalNewspaperSettingsWindow : Window
{
    private readonly HistoricalNewspaperSettingsRepository _repository;
    private readonly CheckBox _enabled = new() { Content = "Search public historical newspaper archives", FontSize = 16, FontWeight = FontWeights.SemiBold };
    private readonly Dictionary<string, CheckBox> _states = new(StringComparer.OrdinalIgnoreCase);
    private readonly TextBox _limit = new() { Width = 70 };
    private readonly TextBox _refresh = new() { Width = 70 };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };

    public HistoricalNewspaperSettingsWindow(string path)
    {
        _repository = new HistoricalNewspaperSettingsRepository(path);
        Title = "Historical newspaper archives"; Width = 900; Height = 760; MinWidth = 760; MinHeight = 620;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; AppTheme.Apply(this);
        var settings = _repository.Load();
        var root = new DockPanel { Margin = new Thickness(20) };
        var bottom = new StackPanel(); DockPanel.SetDock(bottom, Dock.Bottom); root.Children.Add(bottom);
        bottom.Children.Add(_status);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        var save = new Button { Content = "Save and close", Padding = new Thickness(14, 8, 14, 8) };
        save.SetResourceReference(StyleProperty, "PrimaryButton"); save.Click += Save_Click; buttons.Children.Add(save);
        var cancel = new Button { Content = "Cancel", Padding = new Thickness(14, 8, 14, 8), Margin = new Thickness(10, 0, 0, 0) };
        cancel.Click += (_, _) => Close(); buttons.Children.Add(cancel); bottom.Children.Add(buttons);

        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var content = new StackPanel(); scroll.Content = content; root.Children.Add(scroll);
        content.Children.Add(new TextBlock { Text = "Historical newspaper archives", FontSize = 25, FontWeight = FontWeights.SemiBold });
        content.Children.Add(new TextBlock
        {
            Text = "The crawler submits active seed keywords to the free Library of Congress Chronicling America collection, saves OCR matches, and queues newspaper pages. No account or password is required.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 15)
        });
        _enabled.IsChecked = settings.Enabled; content.Children.Add(_enabled);
        var selection = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 8) };
        var all = new Button { Content = "Select all states", Padding = new Thickness(10, 5, 10, 5) };
        all.Click += (_, _) => { foreach (var box in _states.Values) box.IsChecked = true; };
        var none = new Button { Content = "Clear all", Padding = new Thickness(10, 5, 10, 5), Margin = new Thickness(8, 0, 0, 0) };
        none.Click += (_, _) => { foreach (var box in _states.Values) box.IsChecked = false; };
        selection.Children.Add(all); selection.Children.Add(none); content.Children.Add(selection);

        foreach (var region in HistoricalNewspaperCatalog.Regions)
        {
            var group = new GroupBox { Header = region.Name, FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 6, 0, 4), Padding = new Thickness(10) };
            var regionContent = new StackPanel();
            var regionButtons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 5) };
            var selectRegion = new Button { Content = "Select all", Padding = new Thickness(9, 4, 9, 4), FontSize = 13 };
            selectRegion.Click += (_, _) =>
            {
                foreach (var state in region.States) _states[state].IsChecked = true;
            };
            var clearRegion = new Button { Content = "Clear all", Padding = new Thickness(9, 4, 9, 4), FontSize = 13, Margin = new Thickness(7, 0, 0, 0) };
            clearRegion.Click += (_, _) =>
            {
                foreach (var state in region.States) _states[state].IsChecked = false;
            };
            regionButtons.Children.Add(selectRegion);
            regionButtons.Children.Add(clearRegion);
            regionContent.Children.Add(regionButtons);
            var states = new WrapPanel();
            regionContent.Children.Add(states);
            group.Content = regionContent;
            foreach (var state in region.States)
            {
                var box = new CheckBox { Content = state, Width = 190, Margin = new Thickness(2, 5, 4, 5), FontSize = 14,
                    FontWeight = FontWeights.Normal, IsChecked = settings.States.Contains(state, StringComparer.OrdinalIgnoreCase) };
                _states[state] = box; states.Children.Add(box);
            }
            content.Children.Add(group);
        }

        var controls = new WrapPanel { Margin = new Thickness(0, 14, 0, 8) };
        controls.Children.Add(new TextBlock { Text = "Maximum matches per keyword:", VerticalAlignment = VerticalAlignment.Center });
        _limit.Text = settings.MaxResultsPerKeyword.ToString(); _limit.Margin = new Thickness(8, 0, 22, 0); controls.Children.Add(_limit);
        controls.Children.Add(new TextBlock { Text = "Repeat searches after days:", VerticalAlignment = VerticalAlignment.Center });
        _refresh.Text = settings.RefreshDays.ToString(); _refresh.Margin = new Thickness(8, 0, 0, 0); controls.Children.Add(_refresh);
        content.Children.Add(controls);
        content.Children.Add(new TextBlock
        {
            Text = "State library sites already in Seed URLs remain available as supplemental sources. Failed public sites use Problem Seeds and human verification; they do not store passwords.",
            TextWrapping = TextWrapping.Wrap, Foreground = (System.Windows.Media.Brush)FindResource("MutedBrush"), Margin = new Thickness(0, 4, 0, 12)
        });
        Content = root;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(_limit.Text, out var limit) || limit is < 1 or > 160)
        { _status.Text = "Maximum matches must be between 1 and 160."; return; }
        if (!int.TryParse(_refresh.Text, out var refresh) || refresh is < 1 or > 365)
        { _status.Text = "Repeat-search days must be between 1 and 365."; return; }
        var selected = HistoricalNewspaperCatalog.Regions.SelectMany(r => r.States)
            .Where(state => _states[state].IsChecked == true).ToArray();
        if (_enabled.IsChecked == true && selected.Length == 0)
        { _status.Text = "Select at least one state, or turn newspaper searching off."; return; }
        try
        {
            _repository.Save(new(_enabled.IsChecked == true, selected, limit, refresh));
            DialogResult = true;
        }
        catch (Exception ex) { _status.Text = "Settings were not saved. " + ex.Message; }
    }
}
