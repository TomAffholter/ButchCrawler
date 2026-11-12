using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace ButchCrawler.Operator;

public sealed class AiResearchWindow : Window
{
    private readonly IAiResearchWorkspace _workspace;
    private readonly TabControl _tabs = new();
    private readonly TextBox _question = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 78, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxLength = 2000 };
    private readonly TextBox _search = new() { MinWidth = 240, MaxLength = 200 };
    private readonly DataGrid _documents = new() { AutoGenerateColumns = false, IsReadOnly = true, CanUserAddRows = false, SelectionMode = DataGridSelectionMode.Single, Height = 150 };
    private readonly TextBox _preview = ReadOnlyText();
    private readonly TextBox _output = ReadOnlyText();
    private readonly ListBox _history = new() { DisplayMemberPath = "HistoryLabel", Height = 150 };
    private readonly StackPanel _sources = new();
    private readonly TextBlock _status = Note("Ready. Choose a research task to begin.");
    private readonly TextBlock _fileLabel = Note("No image selected. Choose one scanned page or photograph.");
    private readonly Image _image = new() { Height = 135, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly Button _run = new() { Content = "Ask collection" };
    private readonly Button _cancel = new() { Content = "Cancel request", IsEnabled = false };
    private readonly Button _export = new() { Content = "Save result as text…", IsEnabled = false };
    private readonly Button _copy = new() { Content = "Copy result", IsEnabled = false };
    private readonly StackPanel _input = new();
    private CancellationTokenSource? _operation;
    private string? _imagePath;
    private AiResearchResult? _result;
    private bool _closed;

    public AiResearchWindow(IAiResearchWorkspace workspace)
    {
        _workspace = workspace;
        Title = "AI research · History's Web Crawler";
        Width = 1120; Height = 850; MinWidth = 900; MinHeight = 720;
        AppTheme.Apply(this);
        var root = new DockPanel { Margin = new Thickness(22) }; Content = root;
        var heading = new StackPanel(); DockPanel.SetDock(heading, Dock.Top); root.Children.Add(heading);
        heading.Children.Add(new TextBlock { Text = "AI research", FontSize = 28, FontWeight = FontWeights.SemiBold });
        heading.Children.Add(Note("Explore your sources, preserve the originals, and review the evidence."));
        heading.Children.Add(new Border { Background = new SolidColorBrush(Color.FromRgb(225, 240, 242)), CornerRadius = new CornerRadius(6), Padding = new Thickness(12), Margin = new Thickness(0, 10, 0, 12),
            Child = Note("AI requests send selected source text or your image to OpenAI and use API credits. Results may contain mistakes; verify names, dates, and handwriting against the original. " + workspace.LimitsDescription) });
        var bottom = new DockPanel { Margin = new Thickness(0, 12, 0, 0) }; DockPanel.SetDock(bottom, Dock.Bottom); root.Children.Add(bottom);
        var close = new Button { Content = "Close (Esc)", IsCancel = true, Margin = new Thickness(12, 0, 0, 0) }; close.Click += (_, _) => Close(); DockPanel.SetDock(close, Dock.Right); bottom.Children.Add(close);
        bottom.Children.Add(_status);
        var grid = new Grid(); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); root.Children.Add(grid);
        var left = new DockPanel { Margin = new Thickness(0, 0, 16, 0) }; grid.Children.Add(left);
        DockPanel.SetDock(_tabs, Dock.Top); left.Children.Add(_tabs);
        foreach (var title in new[] { "Ask collection", "Summary", "People / places / dates", "Handwriting", "Saved results" }) _tabs.Items.Add(new TabItem { Header = title });
        // The tabs choose a task; a shared panel keeps source selection and results consistent.
        _tabs.Template = TabHeaderTemplate();
        _tabs.SelectionChanged += (_, _) => UpdateTask();
        var actions = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) }; DockPanel.SetDock(actions, Dock.Bottom); left.Children.Add(actions);
        _run.Style = (Style)FindResource("PrimaryButton"); _run.Margin = new Thickness(0, 0, 8, 0); actions.Children.Add(_run); actions.Children.Add(_cancel);
        _run.Click += async (_, _) => await RunAsync(); _cancel.Click += (_, _) => _operation?.Cancel();
        left.Children.Add(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _input });
        _documents.Columns.Add(new DataGridTextColumn { Header = "Saved source", Binding = new System.Windows.Data.Binding("Source"), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        _documents.SelectionChanged += async (_, _) => await PreviewAsync();
        _history.SelectionChanged += (_, _) => { if (_history.SelectedItem is AiResearchResult item) ShowResult(item); };
        var right = new DockPanel(); Grid.SetColumn(right, 1); grid.Children.Add(right);
        var tools = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) }; DockPanel.SetDock(tools, Dock.Bottom); right.Children.Add(tools); tools.Children.Add(_copy); _export.Margin = new Thickness(8, 0, 0, 0); tools.Children.Add(_export);
        _copy.Click += (_, _) => { try { Clipboard.SetText(ResultText()); _status.Text = "Result copied."; } catch { _status.Text = "Could not access the clipboard. Select the result text and copy it manually."; } };
        _export.Click += (_, _) => Export();
        var evidence = new ScrollViewer { Content = _sources, MaxHeight = 190, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 8, 0, 0) }; DockPanel.SetDock(evidence, Dock.Bottom); right.Children.Add(evidence);
        var titleBlock = new TextBlock { Text = "Research result", FontSize = 19, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 10) }; DockPanel.SetDock(titleBlock, Dock.Top); right.Children.Add(titleBlock);
        _output.Text = "Your answer, summary, extracted details, or transcription will appear here. Supporting source passages appear below the result."; right.Children.Add(_output);
        Closing += (_, e) => { if (_operation != null) { e.Cancel = true; _operation.Cancel(); _status.Text = "Canceling… Wait for the request to stop, then close the window. OpenAI may bill a request already sent."; } };
        Closed += (_, _) => { _closed = true; workspace.Dispose(); };
        UpdateTask();
    }

    private static ControlTemplate TabHeaderTemplate()
    {
        // Wrap tab headings at narrower sizes without taking space for empty tab content.
        var factory = new FrameworkElementFactory(typeof(WrapPanel));
        factory.SetValue(Panel.IsItemsHostProperty, true);
        return new ControlTemplate(typeof(TabControl)) { VisualTree = factory };
    }
    private static TextBlock Note(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.FromRgb(75, 96, 119)), Margin = new Thickness(0, 3, 0, 6) };
    private static TextBox ReadOnlyText() => new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    private AiResearchFeature Feature => (AiResearchFeature)Math.Clamp(_tabs.SelectedIndex, 0, 3);
    private void UpdateTask()
    {
        if (_operation != null) return;
        if (_tabs.SelectedIndex < 0) _tabs.SelectedIndex = 0;
        _input.Children.Clear();
        if (_search.Parent is Panel oldSearchBar) oldSearchBar.Children.Remove(_search);
        _run.Visibility = _tabs.SelectedIndex == 4 ? Visibility.Collapsed : Visibility.Visible;
        _run.Content = _tabs.SelectedIndex switch { 1 => "Summarize selected source", 2 => "Extract people, places & dates", 3 => "Transcribe image", _ => "Ask collection" };
        if (_tabs.SelectedIndex == 4)
        {
            _input.Children.Add(Note("Saved AI research. Select an entry to review its result and supporting sources."));
            var refresh = new Button { Content = "Refresh saved results", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 8) };
            refresh.Click += async (_, _) => await LoadHistoryAsync(); _input.Children.Add(refresh); _input.Children.Add(_history);
            return;
        }
        if (Feature == AiResearchFeature.Handwriting)
        {
            _input.Children.Add(Note("Read a handwritten page"));
            _input.Children.Add(Note("Choose a clear PNG, JPEG, or WebP image. For a PDF, export the page as an image first. Unreadable words should be marked for review."));
            var choose = new Button { Content = "Choose image…", HorizontalAlignment = HorizontalAlignment.Left }; choose.Click += (_, _) => ChooseImage();
            _input.Children.Add(choose); _input.Children.Add(_fileLabel); _input.Children.Add(_image); return;
        }
        if (Feature == AiResearchFeature.Question)
        {
            _input.Children.Add(Note("What would you like to learn?")); _input.Children.Add(_question);
            _input.Children.Add(Note("Example: What do the saved sources say about travel through Montana in 1880? The answer searches saved text and cites the passages used; it may not cover every record."));
        }
        _input.Children.Add(Note(Feature == AiResearchFeature.Question ? "Browse sources (optional)" : "1. Find and select a saved source"));
        var bar = new DockPanel { Margin = new Thickness(0, 3, 0, 8) };
        var find = new Button { Content = "Find", Margin = new Thickness(8, 0, 0, 0) }; DockPanel.SetDock(find, Dock.Right); bar.Children.Add(find); bar.Children.Add(_search);
        find.Click += async (_, _) => await SearchAsync(); _input.Children.Add(bar); _input.Children.Add(_documents);
        _input.Children.Add(Note("Original source text · read only")); _preview.Height = 135; _input.Children.Add(_preview);
        if (Feature == AiResearchFeature.Entities) _input.Children.Add(Note("Names and dates are extracted as written. Connections are research leads that require checking the supporting text."));
    }
    private async Task WithOperationAsync(string message, Func<CancellationToken, Task> action)
    {
        if (_operation != null) return;
        using var operation = new CancellationTokenSource(); _operation = operation;
        _input.IsEnabled = _tabs.IsEnabled = _run.IsEnabled = false; _cancel.IsEnabled = true; _status.Text = message;
        try { await action(operation.Token); }
        catch (OperationCanceledException) { _status.Text = "Request canceled. A request already sent to OpenAI may still use API credits."; }
        catch (Exception ex) { _status.Text = "Could not complete the task. Your original sources are unchanged."; MessageBox.Show(this, ex.Message, "AI research", MessageBoxButton.OK, MessageBoxImage.Information); }
        finally { _operation = null; if (!_closed) { _input.IsEnabled = _tabs.IsEnabled = _run.IsEnabled = true; _cancel.IsEnabled = false; } }
    }
    private async Task SearchAsync() => await WithOperationAsync("Finding saved sources…", async ct =>
    {
        _documents.ItemsSource = await _workspace.SearchAsync(_search.Text.Trim(), ct); _preview.Clear();
        _status.Text = _documents.Items.Count == 0 ? "No saved text matches. Try another term or crawl sources first." : $"Found {_documents.Items.Count} sources. Select one to preview.";
    });
    private async Task PreviewAsync()
    {
        if (_documents.SelectedItem is not AiResearchDocument document || _operation != null) return;
        await WithOperationAsync("Loading original text…", async ct => { _preview.Text = await _workspace.ReadDocumentAsync(document, ct); _status.Text = "Source loaded. Original text is unchanged."; });
    }
    private async Task RunAsync()
    {
        var request = new AiResearchRequest(Feature, _question.Text.Trim(), _documents.SelectedItem as AiResearchDocument, _imagePath);
        if (Feature == AiResearchFeature.Question && request.Question.Length == 0) { _status.Text = "Enter a question first."; _question.Focus(); return; }
        if (Feature is AiResearchFeature.Summary or AiResearchFeature.Entities && request.Document == null) { _status.Text = "Find and select a saved source first."; return; }
        if (Feature == AiResearchFeature.Handwriting && _imagePath == null) { _status.Text = "Choose an image first."; return; }
        var content = Feature == AiResearchFeature.Handwriting ? "the selected image" : Feature == AiResearchFeature.Question ? "your question and relevant passages from saved sources" : "text from the selected saved source";
        if (MessageBox.Show(this, $"Send {content} to OpenAI?\n\nThis uses your configured API key and may incur charges. The result will be saved separately from the original.\n\n{_workspace.LimitsDescription}", "Run AI research", MessageBoxButton.OKCancel, MessageBoxImage.Information, MessageBoxResult.Cancel) != MessageBoxResult.OK) return;
        await WithOperationAsync("Working with OpenAI… You can cancel this request.", async ct => { var result = await _workspace.RunAsync(request, ct); ShowResult(result); _status.Text = "Completed and saved. " + result.Usage; });
    }
    private async Task LoadHistoryAsync() => await WithOperationAsync("Loading saved AI research…", async ct => { _history.ItemsSource = await _workspace.HistoryAsync(ct); _status.Text = $"{_history.Items.Count} saved results loaded."; });
    private void ShowResult(AiResearchResult result)
    {
        _result = result; _output.Text = result.Text; _sources.Children.Clear();
        _sources.Children.Add(Note($"{result.Feature} · {result.Model} · {result.CreatedAt.ToLocalTime():g}\n{result.Usage}"));
        foreach (var source in result.Sources)
        {
            var open = new Button { Content = source.Label + " · Open source", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 5, 0, 0), ToolTip = source.Source };
            open.IsEnabled = Uri.TryCreate(source.Source, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http";
            open.Click += (_, _) => { try { SafeSourceLauncher.OpenOrReveal(source.Source); } catch { _status.Text = "Could not open source. Copy its address from the exported result."; } };
            _sources.Children.Add(open); _sources.Children.Add(Note(source.Source));
            var passage = ReadOnlyText(); passage.Text = source.Passage; passage.MaxHeight = 110; _sources.Children.Add(passage);
        }
        _export.IsEnabled = _copy.IsEnabled = true;
    }
    private string ResultText() => _result == null ? string.Empty : $"{_result.Feature}\n{_result.CreatedAt.ToLocalTime():g} · {_result.Model}\n{_result.Usage}\n\n{_result.Text}\n\nSources\n" + string.Join("\n\n", _result.Sources.Select(s => $"{s.Label}: {s.Source}\n{s.Passage}"));
    private void Export()
    {
        var dialog = new SaveFileDialog { Filter = "Text document (*.txt)|*.txt", FileName = "History-research-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".txt" };
        if (dialog.ShowDialog(this) != true) return;
        try { File.WriteAllText(dialog.FileName, ResultText()); _status.Text = "Result saved to your text file."; } catch { _status.Text = "Could not save the file. Check folder permissions and try again."; }
    }
    private void ChooseImage()
    {
        var picker = new OpenFileDialog { Filter = "Page images (*.png;*.jpg;*.jpeg;*.webp)|*.png;*.jpg;*.jpeg;*.webp", CheckFileExists = true, Multiselect = false };
        if (picker.ShowDialog(this) != true) return;
        try
        {
            if (new FileInfo(picker.FileName).Length > 10_000_000) throw new InvalidOperationException("Choose an image smaller than 10 MB.");
            _imagePath = picker.FileName; _fileLabel.Text = picker.FileName;
            try { var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.DecodePixelWidth = 1000; bitmap.UriSource = new Uri(picker.FileName); bitmap.EndInit(); bitmap.Freeze(); _image.Source = bitmap; }
            catch { _image.Source = null; _fileLabel.Text += "\nPreview unavailable; the selected format may still be supported by OpenAI."; }
            _status.Text = "Image selected. Review the page before transcribing.";
        }
        catch (Exception ex) { _imagePath = null; _image.Source = null; _fileLabel.Text = ex.Message; }
    }
}
