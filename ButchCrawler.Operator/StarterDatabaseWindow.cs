using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Microsoft.Win32;

namespace ButchCrawler.Operator;

public sealed class StarterDatabaseWindow : Window
{
    private readonly StarterDatabaseService _service;
    private readonly Func<bool> _crawlerRunning;
    private readonly DataGrid _grid = new()
    {
        AutoGenerateColumns = false,
        CanUserAddRows = false,
        CanUserDeleteRows = false,
        CanUserReorderColumns = false,
        SelectionMode = DataGridSelectionMode.Extended,
        Margin = new Thickness(0, 10, 0, 10)
    };
    private readonly TextBlock _summary = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.SemiBold };
    private readonly TextBox _output = new() { MinWidth = 520, Margin = new Thickness(0, 0, 8, 0) };
    private readonly CheckBox _replace = new()
    {
        Content = "Replace an existing HistoryCrawlerStarterDB copy",
        Margin = new Thickness(0, 7, 0, 4)
    };
    private readonly CheckBox _acknowledge = new()
    {
        Content = "I understand the starter copy keeps only Seed URLs, Seed Keywords, and schema-version records.",
        Margin = new Thickness(0, 4, 0, 4)
    };
    private readonly TextBox _confirmation = new() { Width = 220, Margin = new Thickness(8, 0, 8, 0) };
    private readonly Button _create = new()
    {
        Content = "Create clean starter database",
        Padding = new Thickness(14, 9, 14, 9),
        IsEnabled = false
    };
    private readonly Button _close = new() { Content = "Close", Padding = new Thickness(14, 9, 14, 9), Margin = new Thickness(10, 0, 0, 0), IsCancel = true };
    private StarterDatabasePreview? _preview;
    private bool _working;

    public StarterDatabaseWindow(StarterDatabaseService service, Func<bool> crawlerRunning)
    {
        _service = service;
        _crawlerRunning = crawlerRunning;
        AppTheme.Apply(this);
        Title = "Prepare a clean starter database";
        Width = 1180;
        Height = 820;
        MinWidth = 900;
        MinHeight = 650;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _output.Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "History Crawler Installer", "Starter Database");

        var panel = new DockPanel { Margin = new Thickness(18) };
        AppTheme.AddHeading(panel, "Prepare a clean starter database");

        var explanation = new TextBlock
        {
            Text = "This creates a separate HistoryCrawlerStarterDB for a new installation. Your working ButchCrawlerDB is never wiped or renamed. The tool first makes a private safety backup, adds only the successful URLs you approve, removes all non-seed records from the separate copy, resets seed progress, and verifies the finished backup.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10)
        };
        DockPanel.SetDock(explanation, Dock.Top);
        panel.Children.Add(explanation);

        var warning = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(255, 241, 218)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(213, 154, 53)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(12),
            Child = new TextBlock
            {
                Text = "Important: the clean .bak file is suitable for installer preparation only after this screen reports success. The private safety backup contains your research data and must never be shipped with the installer.",
                TextWrapping = TextWrapping.Wrap,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(112, 69, 20))
            }
        };
        DockPanel.SetDock(warning, Dock.Top);
        panel.Children.Add(warning);

        var bottom = BuildBottomPanel();
        DockPanel.SetDock(bottom, Dock.Bottom);
        panel.Children.Add(bottom);

        _grid.Columns.Add(new DataGridCheckBoxColumn
        {
            Header = "Include",
            Binding = new Binding(nameof(StarterUrlCandidate.Include)) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged },
            Width = 70
        });
        _grid.Columns.Add(new DataGridTextColumn
        {
            Header = "Successful URL",
            Binding = new Binding(nameof(StarterUrlCandidate.Url)),
            IsReadOnly = true,
            Width = new DataGridLength(3, DataGridLengthUnitType.Star),
            MinWidth = 430
        });
        _grid.Columns.Add(new DataGridTextColumn
        {
            Header = "Last success (UTC)",
            Binding = new Binding(nameof(StarterUrlCandidate.LastSuccessfulAtUtc)) { StringFormat = "yyyy-MM-dd HH:mm" },
            IsReadOnly = true,
            Width = 145
        });
        _grid.Columns.Add(new DataGridTextColumn
        {
            Header = "Why it is selected or needs review",
            Binding = new Binding(nameof(StarterUrlCandidate.Review)),
            IsReadOnly = true,
            Width = new DataGridLength(2, DataGridLengthUnitType.Star),
            MinWidth = 260
        });
        panel.Children.Add(_grid);
        Content = panel;

        Loaded += async (_, _) => await LoadPreviewAsync();
        Closing += OnClosing;
        _acknowledge.Checked += (_, _) => RefreshCreateEnabled();
        _acknowledge.Unchecked += (_, _) => RefreshCreateEnabled();
        _confirmation.TextChanged += (_, _) => RefreshCreateEnabled();
        _output.TextChanged += (_, _) => RefreshCreateEnabled();
        _grid.CellEditEnding += (_, _) => Dispatcher.BeginInvoke(new Action(RefreshSummary));
        _create.Click += async (_, _) => await CreateAsync();
        _close.Click += (_, _) => { if (!_working) Close(); };
    }

    private UIElement BuildBottomPanel()
    {
        var bottom = new StackPanel();
        bottom.Children.Add(_summary);
        var selection = new WrapPanel { Margin = new Thickness(0, 7, 0, 10) };
        foreach (var item in new[]
        {
            ("Select recommended", (Action)(() => SetSelections(candidate => candidate.Recommended))),
            ("Select all", (Action)(() => SetSelections(_ => true))),
            ("Clear all", (Action)(() => SetSelections(_ => false)))
        })
        {
            var button = new Button { Content = item.Item1, Padding = new Thickness(11, 6, 11, 6), Margin = new Thickness(0, 0, 8, 0) };
            button.Click += (_, _) => item.Item2();
            selection.Children.Add(button);
        }
        bottom.Children.Add(selection);

        bottom.Children.Add(new TextBlock { Text = "Where the clean starter backup and seed export will be saved:", FontWeight = FontWeights.SemiBold });
        var folder = new DockPanel { Margin = new Thickness(0, 5, 0, 0) };
        var browse = new Button { Content = "Browse…", Padding = new Thickness(11, 6, 11, 6) };
        DockPanel.SetDock(browse, Dock.Right);
        folder.Children.Add(browse);
        folder.Children.Add(_output);
        browse.Click += (_, _) =>
        {
            var dialog = new OpenFolderDialog { Title = "Choose a folder for the clean starter database", InitialDirectory = _output.Text };
            if (dialog.ShowDialog(this) == true) _output.Text = dialog.FolderName;
        };
        bottom.Children.Add(folder);
        bottom.Children.Add(_replace);
        bottom.Children.Add(_acknowledge);

        var confirmation = new WrapPanel { Margin = new Thickness(0, 5, 0, 8) };
        confirmation.Children.Add(new TextBlock { Text = "Type CREATE STARTER to confirm:", VerticalAlignment = VerticalAlignment.Center });
        confirmation.Children.Add(_confirmation);
        bottom.Children.Add(confirmation);
        bottom.Children.Add(_status);

        var actions = new DockPanel { Margin = new Thickness(0, 9, 0, 0) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        _create.SetResourceReference(StyleProperty, "PrimaryButton");
        buttons.Children.Add(_create);
        buttons.Children.Add(_close);
        actions.Children.Add(buttons);
        bottom.Children.Add(actions);
        return bottom;
    }

    private async Task LoadPreviewAsync()
    {
        if (_crawlerRunning())
        {
            _status.Text = "Stop the crawler before preparing an installer database.";
            return;
        }

        SetWorking(true, "Loading successful crawled URLs for review…");
        try
        {
            _preview = await _service.PreviewAsync();
            _grid.ItemsSource = _preview.Candidates;
            RefreshSummary();
            _status.Text = "Review the suggested URLs. Query, login, search, calendar, and paging addresses are not selected automatically.";
        }
        catch (Exception ex)
        {
            _status.Text = "The starter preview could not be loaded. " + ex.Message;
        }
        finally
        {
            SetWorking(false);
        }
    }

    private void SetSelections(Func<StarterUrlCandidate, bool> selector)
    {
        if (_preview is null || _working) return;
        foreach (var candidate in _preview.Candidates) candidate.Include = selector(candidate);
        _grid.Items.Refresh();
        RefreshSummary();
    }

    private void RefreshSummary()
    {
        if (_preview is null)
        {
            _summary.Text = "Waiting for the preview…";
            return;
        }
        var selected = _preview.Candidates.Count(candidate => candidate.Include);
        var recommended = _preview.Candidates.Count(candidate => candidate.Recommended);
        _summary.Text = $"Existing seeds: {_preview.ExistingSeedUrls:N0}   ·   Seed keywords: {_preview.SeedKeywords:N0}   ·   " +
                        $"New successful URLs available: {_preview.Candidates.Count:N0}   ·   Recommended: {recommended:N0}   ·   Selected: {selected:N0}";
        RefreshCreateEnabled();
    }

    private void RefreshCreateEnabled()
    {
        _create.IsEnabled = !_working && _preview is not null && _acknowledge.IsChecked == true &&
                            string.Equals(_confirmation.Text.Trim(), "CREATE STARTER", StringComparison.Ordinal) &&
                            !string.IsNullOrWhiteSpace(_output.Text);
    }

    private async Task CreateAsync()
    {
        if (_preview is null || _working) return;
        if (_crawlerRunning())
        {
            _status.Text = "Stop the crawler before creating the starter database.";
            return;
        }
        _grid.CommitEdit(DataGridEditingUnit.Cell, true);
        _grid.CommitEdit(DataGridEditingUnit.Row, true);
        var selected = _preview.Candidates.Where(candidate => candidate.Include).Select(candidate => candidate.Url).ToArray();
        var answer = MessageBox.Show(this,
            $"Create a separate clean starter database with {_preview.ExistingSeedUrls:N0} existing seeds, " +
            $"{_preview.SeedKeywords:N0} keywords, and {selected.Length:N0} approved successful URLs?\n\n" +
            "Your working ButchCrawlerDB will not be wiped. A private safety backup will be kept outside the installer output folder.",
            "Create clean starter database?", MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel);
        if (answer != MessageBoxResult.OK) return;

        SetWorking(true, "Starting protected starter-database preparation…");
        try
        {
            var progress = new Progress<string>(message => _status.Text = message);
            var result = await _service.BuildAsync(selected, _output.Text, _replace.IsChecked == true, progress);
            _status.Text = $"Complete. {result.SeedUrls:N0} seed URLs and {result.SeedKeywords:N0} keywords are in the verified starter backup. " +
                           $"{result.PromotedUrls:N0} successful URLs were added; {result.ClearedTables:N0} non-seed tables were cleared.";
            MessageBox.Show(this,
                "The clean starter files are ready:\n\n" + result.StarterBackupPath + "\n" + result.SeedExportPath +
                "\n\nPrivate recovery backup — do not distribute:\n" + result.SafetyBackupPath,
                "Starter database ready", MessageBoxButton.OK, MessageBoxImage.Information);
            _close.Content = "Close";
        }
        catch (Exception ex)
        {
            _status.Text = "No clean starter package was approved. The working database was not wiped. " + ex.Message;
            MessageBox.Show(this, _status.Text, "Starter database not created", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetWorking(false);
        }
    }

    private void SetWorking(bool value, string? message = null)
    {
        _working = value;
        _grid.IsEnabled = !value;
        _output.IsEnabled = _replace.IsEnabled = _acknowledge.IsEnabled = _confirmation.IsEnabled = !value;
        _close.IsEnabled = !value;
        if (message is not null) _status.Text = message;
        RefreshCreateEnabled();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_working)
        {
            e.Cancel = true;
            _status.Text = "Starter preparation is still running. Keep this window open until verification finishes.";
        }
    }
}
