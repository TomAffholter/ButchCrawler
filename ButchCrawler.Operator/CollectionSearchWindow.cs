using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ButchCrawler.App;

namespace ButchCrawler.Operator;

public sealed class CollectionSearchWindow : Window
{
    private readonly IAiResearchWorkspace _workspace;
    private readonly Func<IAiResearchWorkspace> _aiWorkspaceFactory;
    private readonly TextBox _query = new() { MaxLength = 200, MinWidth = 360 };
    private readonly ComboBox _type = new()
    {
        Width = 150,
        Margin = new Thickness(8, 0, 0, 0),
        HorizontalContentAlignment = HorizontalAlignment.Center,
        VerticalContentAlignment = VerticalAlignment.Center
    };
    private readonly ListBox _results = new() { SelectionMode = SelectionMode.Single };
    private readonly TextBox _text = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly TextBlock _source = new() { Text = "Select a result to read its saved text.", TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _status = new() { Text = "Ready. Searching saved information does not contact OpenAI.", TextWrapping = TextWrapping.Wrap };
    private readonly Button _copy = new() { Content = "Copy text", IsEnabled = false };
    private readonly Button _open = new() { Content = "Open original", IsEnabled = false };
    private readonly Button _search = new() { Content = "Search" };
    private readonly Button _help = new() { Content = "Search help", ToolTip = "See simple examples of AND, OR, NOT, quotation marks, and parentheses." };
    private readonly Border _busyPanel = new()
    {
        Visibility = Visibility.Collapsed,
        Background = new SolidColorBrush(Color.FromRgb(232, 242, 255)),
        BorderBrush = new SolidColorBrush(Color.FromRgb(91, 142, 211)),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(8),
        Padding = new Thickness(14, 10, 14, 10),
        Margin = new Thickness(0, 0, 0, 12)
    };
    private readonly TextBlock _busyText = new() { Text = "Searching…", FontSize = 17, FontWeight = FontWeights.SemiBold };
    private readonly ProgressBar _busyProgress = new() { IsIndeterminate = true, Height = 6, Margin = new Thickness(0, 8, 0, 0) };
    private IReadOnlyList<AiResearchDocument> _all = Array.Empty<AiResearchDocument>();
    private CancellationTokenSource? _operation;

    public CollectionSearchWindow(IAiResearchWorkspace workspace, Func<IAiResearchWorkspace> aiWorkspaceFactory)
    {
        _workspace = workspace; _aiWorkspaceFactory = aiWorkspaceFactory;
        AppTheme.Apply(this);
        Title = "Search collection · History's Web Crawler";
        Width = 1120; Height = 780; MinWidth = 850; MinHeight = 600;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var root = new DockPanel { Margin = new Thickness(22) }; Content = root;
        var heading = new StackPanel(); DockPanel.SetDock(heading, Dock.Top); root.Children.Add(heading);
        heading.Children.Add(new TextBlock { Text = "Search your collection", FontSize = 28, FontWeight = FontWeights.SemiBold });
        heading.Children.Add(new TextBlock
        {
            Text = "Search saved webpages, documents, OCR text, structured datasets, and completed audio transcripts. This search is local and uses no OpenAI credits.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 12)
        });
        var searchBar = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
        _search.SetResourceReference(StyleProperty, "PrimaryButton"); _search.Margin = new Thickness(8, 0, 0, 0);
        DockPanel.SetDock(_search, Dock.Right); searchBar.Children.Add(_search);
        _help.Margin = new Thickness(8, 0, 0, 0); _help.Padding = new Thickness(12, 8, 12, 8);
        _help.Click += (_, _) => new CollectionSearchHelpWindow { Owner = this }.ShowDialog();
        DockPanel.SetDock(_help, Dock.Right); searchBar.Children.Add(_help);
        var centeredChoice = new Style(typeof(ComboBoxItem));
        centeredChoice.Setters.Add(new Setter(HorizontalContentAlignmentProperty, HorizontalAlignment.Center));
        centeredChoice.Setters.Add(new Setter(VerticalContentAlignmentProperty, VerticalAlignment.Center));
        _type.ItemContainerStyle = centeredChoice;
        _type.Items.Add("Everything"); _type.Items.Add("Web pages"); _type.Items.Add("Documents"); _type.Items.Add("Audio transcripts"); _type.Items.Add("Structured data"); _type.SelectedIndex = 0;
        DockPanel.SetDock(_type, Dock.Right); searchBar.Children.Add(_type); searchBar.Children.Add(_query); heading.Children.Add(searchBar);
        heading.Children.Add(new TextBlock
        {
            Text = "Try a name, place, date, phrase, or several important words—for example, John Smith railroad Seattle 1912. Choose Search help for AND, OR, NOT, exact phrases, and parentheses.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12)
        });
        var busyContent = new StackPanel(); busyContent.Children.Add(_busyText); busyContent.Children.Add(_busyProgress);
        _busyPanel.Child = busyContent; heading.Children.Add(_busyPanel);

        var bottom = new DockPanel { Margin = new Thickness(0, 12, 0, 0) }; DockPanel.SetDock(bottom, Dock.Bottom); root.Children.Add(bottom);
        var close = new Button { Content = "Close (Esc)", IsCancel = true, Margin = new Thickness(10, 0, 0, 0) };
        close.Click += (_, _) => Close(); DockPanel.SetDock(close, Dock.Right); bottom.Children.Add(close);
        var optional = new Button { Content = "Optional AI tools…", ToolTip = "Paid AI tools are separate from ordinary collection search." };
        optional.Click += (_, _) => OpenAiTools(); DockPanel.SetDock(optional, Dock.Right); bottom.Children.Add(optional);
        bottom.Children.Add(_status);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(46, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(54, GridUnitType.Star) });
        root.Children.Add(grid);
        var resultText = new FrameworkElementFactory(typeof(TextBlock));
        resultText.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(AiResearchDocument.SearchLabel)));
        resultText.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap);
        resultText.SetValue(FrameworkElement.MarginProperty, new Thickness(7));
        _results.ItemTemplate = new DataTemplate { VisualTree = resultText };
        _results.SelectionChanged += async (_, _) => await LoadSelectedAsync();
        grid.Children.Add(_results);

        var right = new DockPanel { Margin = new Thickness(16, 0, 0, 0) }; Grid.SetColumn(right, 1); grid.Children.Add(right);
        var actions = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) }; DockPanel.SetDock(actions, Dock.Bottom); right.Children.Add(actions);
        _copy.Click += (_, _) => CopyText(); _open.Margin = new Thickness(8, 0, 0, 0); _open.Click += (_, _) => OpenOriginal();
        actions.Children.Add(_copy); actions.Children.Add(_open);
        var sourceTitle = new StackPanel { Margin = new Thickness(0, 0, 0, 9) }; DockPanel.SetDock(sourceTitle, Dock.Top); right.Children.Add(sourceTitle);
        sourceTitle.Children.Add(new TextBlock { Text = "Saved source text", FontSize = 19, FontWeight = FontWeights.SemiBold }); sourceTitle.Children.Add(_source);
        right.Children.Add(_text);

        _search.Click += async (_, _) => await SearchAsync();
        _query.KeyDown += async (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; await SearchAsync(); } };
        _type.SelectionChanged += (_, _) => ApplyFilter();
        Closing += (_, _) => _operation?.Cancel();
        Closed += (_, _) => _workspace.Dispose();
        ContentRendered += (_, _) => _query.Focus();
    }

    private async Task SearchAsync()
    {
        if (_operation != null) return;
        using var operation = new CancellationTokenSource(); _operation = operation; _search.IsEnabled = false;
        SetBusy(true, "Searching your saved collection…");
        _status.Text = "Searching saved information… Please wait.";
        await Dispatcher.Yield(DispatcherPriority.Background);
        try { _all = await _workspace.SearchAsync(_query.Text.Trim(), operation.Token); ApplyFilter(); }
        catch (OperationCanceledException) { _status.Text = "Search canceled."; }
        catch (CollectionSearchQueryException ex)
        {
            _status.Text = "Please check the search instructions.";
            MessageBox.Show(this, ex.Message + "\n\nChoose Search help for examples.", "Check this search", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) { _status.Text = "Could not search the collection."; MessageBox.Show(this, ex.Message, "Search collection", MessageBoxButton.OK, MessageBoxImage.Information); }
        finally { _operation = null; SetBusy(false); if (IsVisible) _search.IsEnabled = true; }
    }

    private void ApplyFilter()
    {
        var kind = _type.SelectedIndex switch { 1 => "crawl", 2 => "document", 3 => "audio", 4 => "structured", _ => null };
        var visible = (kind == null ? _all : _all.Where(item => item.Id == kind)).ToArray();
        _results.ItemsSource = visible; _results.SelectedItem = null; _text.Clear(); _copy.IsEnabled = _open.IsEnabled = false;
        _source.Text = "Select a result to read its saved text.";
        _status.Text = visible.Length == 0 ? "No saved text matches. Try fewer or different words, or crawl sources first." : $"Found {visible.Length:N0} matching sources. Select one to read it.";
    }

    private async Task LoadSelectedAsync()
    {
        if (_results.SelectedItem is not AiResearchDocument document || _operation != null) return;
        using var operation = new CancellationTokenSource(); _operation = operation; _search.IsEnabled = false;
        SetBusy(true, "Opening the saved source…");
        _status.Text = "Loading saved text… Please wait.";
        await Dispatcher.Yield(DispatcherPriority.Background);
        try
        {
            _text.Text = await _workspace.ReadDocumentAsync(document, operation.Token); _source.Text = document.Source;
            _copy.IsEnabled = _text.Text.Length > 0; _open.IsEnabled = CanOpen(document.Source);
            _status.Text = "Saved text loaded. The original source is unchanged.";
        }
        catch (OperationCanceledException) { _status.Text = "Loading canceled."; }
        catch (Exception ex) { _status.Text = "Could not load this saved source."; MessageBox.Show(this, ex.Message, "Search collection", MessageBoxButton.OK, MessageBoxImage.Information); }
        finally { _operation = null; SetBusy(false); if (IsVisible) _search.IsEnabled = true; }
    }

    private void SetBusy(bool busy, string message = "Searching…")
    {
        _busyText.Text = message;
        _busyPanel.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        _search.Content = busy ? "Searching…" : "Search";
        Cursor = busy ? Cursors.Wait : null;
    }

    private void CopyText()
    {
        try { Clipboard.SetText(_text.Text); _status.Text = "Saved text copied."; }
        catch { _status.Text = "Could not access the clipboard. Select the text and copy it manually."; }
    }

    private void OpenOriginal()
    {
        if (_results.SelectedItem is not AiResearchDocument document || !CanOpen(document.Source)) return;
        try { SafeSourceLauncher.OpenOrReveal(document.Source); }
        catch { _status.Text = "Could not open the original source. You can copy its address above."; }
    }

    private static bool CanOpen(string source) => SafeSourceLauncher.CanOpen(source);

    private void OpenAiTools()
    {
        try { new AiResearchWindow(_aiWorkspaceFactory()) { Owner = this }.ShowDialog(); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Optional AI tools", MessageBoxButton.OK, MessageBoxImage.Information); }
    }
}
