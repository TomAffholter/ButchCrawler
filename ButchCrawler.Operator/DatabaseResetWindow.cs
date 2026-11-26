using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

namespace ButchCrawler.Operator;

public sealed class DatabaseResetWindow : Window
{
    private readonly DatabaseResetService _service;
    private readonly Func<bool> _crawlerRunning;
    private readonly RadioButton _keepKeywords = new()
    {
        Content = "Keep Seed Keywords — repeat the same topic from a clean starting point",
        IsChecked = true,
        Margin = new Thickness(0, 4, 0, 4)
    };
    private readonly RadioButton _clearKeywords = new()
    {
        Content = "Clear Seed Keywords — begin a different research topic",
        Margin = new Thickness(0, 4, 0, 4)
    };
    private readonly DataGrid _grid = new()
    {
        AutoGenerateColumns = false,
        IsReadOnly = true,
        CanUserAddRows = false,
        CanUserDeleteRows = false,
        Margin = new Thickness(0, 12, 0, 0)
    };
    private readonly TextBox _confirmation = new() { Margin = new Thickness(0, 6, 0, 10) };
    private readonly CheckBox _understand = new()
    {
        Content = "I understand collected research will be removed after a verified safety backup is created.",
        Margin = new Thickness(0, 8, 0, 8)
    };
    private readonly TextBlock _status = new()
    {
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 8, 0, 8),
        FontWeight = FontWeights.SemiBold
    };
    private readonly Button _reset = new()
    {
        Content = "Back up and start new project",
        IsEnabled = false,
        Padding = new Thickness(14, 8, 14, 8)
    };
    private readonly Button _cancel = new()
    {
        Content = "Cancel (Esc)",
        IsCancel = true,
        Padding = new Thickness(14, 8, 14, 8),
        Margin = new Thickness(10, 0, 0, 0)
    };
    private bool _working;

    public bool ResetCompleted { get; private set; }

    public DatabaseResetWindow(DatabaseResetService service, Func<bool> crawlerRunning)
    {
        AppTheme.Apply(this);
        _service = service;
        _crawlerRunning = crawlerRunning;
        Title = "Start a new research project";
        Width = 900;
        Height = 790;
        MinWidth = 720;
        MinHeight = 680;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var panel = new DockPanel { Margin = new Thickness(18) };
        AppTheme.AddHeading(panel, "Start a new research project");

        var introduction = new TextBlock
        {
            Text = "Create a clean research run without losing the source catalog. Seed URLs are always protected. Their crawl progress, automatic failures, and retry state are reset so the next crawl begins again.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10)
        };
        DockPanel.SetDock(introduction, Dock.Top);
        panel.Children.Add(introduction);

        var protectedNotice = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(231, 246, 239)),
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(12),
            Margin = new Thickness(0, 0, 0, 10),
            Child = new TextBlock
            {
                Text = "Always protected: Seed URLs, database schema, application settings, encrypted OpenAI key, local source files, and disk log files. Manually disabled Seed URLs stay disabled; automatically disabled seeds are made available again.",
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Color.FromRgb(22, 98, 63)),
                FontWeight = FontWeights.SemiBold
            }
        };
        DockPanel.SetDock(protectedNotice, Dock.Top);
        panel.Children.Add(protectedNotice);

        var choices = new StackPanel { Margin = new Thickness(0, 0, 0, 2) };
        choices.Children.Add(new TextBlock { Text = "Seed Keyword choice", FontSize = 16, FontWeight = FontWeights.SemiBold });
        choices.Children.Add(_keepKeywords);
        choices.Children.Add(_clearKeywords);
        DockPanel.SetDock(choices, Dock.Top);
        panel.Children.Add(choices);

        var bottom = new StackPanel();
        DockPanel.SetDock(bottom, Dock.Bottom);
        panel.Children.Add(bottom);
        bottom.Children.Add(new TextBlock
        {
            Text = "Before deletion, History Crawler creates a COPY_ONLY database backup under your private Windows History Crawler backup folder and verifies that SQL Server can read it. If backup, deletion, constraint restoration, or verification fails, the reset stops and database changes are rolled back.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 10, 0, 0)
        });
        bottom.Children.Add(_understand);
        bottom.Children.Add(new TextBlock { Text = $"Type {DatabaseResetService.ConfirmationPhrase} to confirm:" });
        bottom.Children.Add(_confirmation);
        bottom.Children.Add(_status);
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        _reset.Background = new SolidColorBrush(Color.FromRgb(190, 48, 64));
        _reset.Foreground = Brushes.White;
        actions.Children.Add(_reset);
        actions.Children.Add(_cancel);
        bottom.Children.Add(actions);

        _grid.Columns.Add(new DataGridTextColumn
        {
            Header = "Database table",
            Binding = new Binding(nameof(ResetTable.Property)),
            Width = new DataGridLength(1, DataGridLengthUnitType.Star),
            MinWidth = 300
        });
        _grid.Columns.Add(new DataGridTextColumn
        {
            Header = "Current rows",
            Binding = new Binding(nameof(ResetTable.Rows)) { StringFormat = "N0" },
            MinWidth = 120
        });
        _grid.Columns.Add(new DataGridCheckBoxColumn
        {
            Header = "Protected",
            Binding = new Binding(nameof(ResetTable.Preserved)),
            MinWidth = 100
        });
        panel.Children.Add(_grid);
        Content = panel;

        Loaded += async (_, _) => await LoadPreviewAsync();
        _keepKeywords.Checked += async (_, _) => { if (IsLoaded && !_working) await LoadPreviewAsync(); };
        _clearKeywords.Checked += async (_, _) => { if (IsLoaded && !_working) await LoadPreviewAsync(); };
        _cancel.Click += (_, _) => { if (!_working) Close(); };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            e.Handled = true;
            if (!_working) Close();
        };
        Closing += (_, e) => { if (_working) e.Cancel = true; };
        _reset.Click += async (_, _) => await ResetAsync();
    }

    private bool ClearKeywords => _clearKeywords.IsChecked == true;

    private async Task LoadPreviewAsync()
    {
        _reset.IsEnabled = false;
        _status.Text = "Loading the reset preview…";
        try
        {
            var rows = await _service.PreviewAsync(ClearKeywords);
            _grid.ItemsSource = rows;
            var rowsToDelete = rows.Where(row => !row.Preserved).Sum(row => row.Rows);
            var keywordText = ClearKeywords ? "Seed Keywords will be cleared." : "Seed Keywords will be protected.";
            _status.Text = $"Review the table list. Approximately {rowsToDelete:N0} stored rows are marked for deletion. Seed URLs are protected. {keywordText}";
            _reset.IsEnabled = true;
        }
        catch (Exception ex)
        {
            _status.Text = "Cannot prepare the new-project preview: " + ex.Message;
        }
    }

    private async Task ResetAsync()
    {
        if (_crawlerRunning())
        {
            _status.Text = "Stop the crawler before starting a new research project.";
            return;
        }
        if (_understand.IsChecked != true || _confirmation.Text != DatabaseResetService.ConfirmationPhrase)
        {
            _status.Text = $"Check the acknowledgement and type {DatabaseResetService.ConfirmationPhrase} exactly.";
            return;
        }

        var keywordWarning = ClearKeywords
            ? "Seed Keywords will be deleted. Seed URLs will remain."
            : "Seed Keywords and Seed URLs will remain.";
        var warning = "History Crawler will first create and verify a private safety backup. It will then permanently remove collected pages, discovered URLs, OCR text, transcripts, structured records, matches, failures, queues, and other research results.\n\n" +
                      keywordWarning + " Seed URL progress and failure history will be reset.\n\nChoose OK to continue or Cancel to leave everything unchanged.";
        if (MessageBox.Show(this, warning, "Start a new research project?", MessageBoxButton.OKCancel,
                MessageBoxImage.Warning, MessageBoxResult.Cancel) != MessageBoxResult.OK)
        {
            _status.Text = "New-project reset canceled. Nothing was changed.";
            return;
        }

        _working = true;
        _reset.IsEnabled = _cancel.IsEnabled = _confirmation.IsEnabled = _understand.IsEnabled = false;
        _keepKeywords.IsEnabled = _clearKeywords.IsEnabled = false;
        _status.Text = "Creating and verifying the safety backup… Keep this window open.";
        try
        {
            var result = await _service.StartNewResearchProjectAsync(ClearKeywords);
            ResetCompleted = true;
            _status.Text = $"New research project ready. Verified {result.SeedUrls:N0} protected Seed URLs and {result.SeedKeywords:N0} Seed Keywords after clearing {result.TablesCleared:N0} research tables. Safety backup: {result.BackupPath}";
            _cancel.Content = "Close (Esc)";
        }
        catch (Exception ex)
        {
            _status.Text = "The new project was not completed. Database changes are rolled back when reset verification fails. The safety backup, if created, remains available. " + ex.Message;
            _reset.IsEnabled = true;
        }
        finally
        {
            _working = false;
            _cancel.IsEnabled = true;
            _confirmation.IsEnabled = _understand.IsEnabled = true;
            _keepKeywords.IsEnabled = _clearKeywords.IsEnabled = true;
        }
    }
}
