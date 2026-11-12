using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;

namespace ButchCrawler.Operator;

public sealed class DatabaseResetWindow : Window
{
    private readonly DatabaseResetService _service;
    private readonly bool _clearKeywords;
    private readonly Func<bool> _crawlerRunning;
    private readonly TextBox _confirmation = new() { Margin = new Thickness(0, 6, 0, 10) };
    private readonly CheckBox _stopped = new() { Content = "All crawler instances are stopped and I have a database backup.", Margin = new Thickness(0, 8, 0, 8) };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 8) };
    private readonly Button _reset = new() { Content = "Reset database", IsEnabled = false, Padding = new Thickness(12, 7, 12, 7) };
    private readonly Button _cancel = new() { Content = "Cancel (Esc)", IsCancel = true, Padding = new Thickness(12, 7, 12, 7), Margin = new Thickness(10, 0, 0, 0) };
    private readonly string _deleteWarning;
    private bool _deleting;
    public DatabaseResetWindow(DatabaseResetService service, bool clearKeywords, Func<bool> crawlerRunning)
    {
        AppTheme.Apply(this);
        _service = service; _clearKeywords = clearKeywords; _crawlerRunning = crawlerRunning;
        _reset.Content = clearKeywords ? "Delete data + keywords" : "Delete crawl data";
        Title = clearKeywords ? "Reset data and seed keywords" : "Reset data — keep both seed tables";
        Width = 800; Height = 760; MinWidth = 650; MinHeight = 650; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new DockPanel { Margin = new Thickness(16) };
        AppTheme.AddHeading(panel, clearKeywords ? "Reset data + keywords" : "Reset data · keep seeds");
        _reset.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(190, 48, 64));
        _reset.Foreground = System.Windows.Media.Brushes.White;
        var help = new TextBlock { Text = clearKeywords
            ? "Permanently clear crawl data and Seed Keywords. Preserve all Seed URLs and schema-version records."
            : "Permanently clear crawl data. Preserve all Seed URLs, Seed Keywords and schema-version records.", TextWrapping = TextWrapping.Wrap };
        DockPanel.SetDock(help, Dock.Top); panel.Children.Add(help);
        _deleteWarning = clearKeywords
            ? "This deletion cannot be undone. All crawl data and Seed Keywords will be permanently deleted. Seed URLs and your OpenAI key will be kept. Recovery requires a separate database backup."
            : "This deletion cannot be undone. All crawl data will be permanently deleted. Seed URLs, Seed Keywords and your OpenAI key will be kept. Recovery requires a separate database backup.";
        var warning = new Border { Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 237, 238)),
            CornerRadius = new CornerRadius(6), Padding = new Thickness(12), Margin = new Thickness(0, 12, 0, 0),
            Child = new TextBlock { Text = _deleteWarning + " Choose Cancel or press Escape to leave without deleting.", TextWrapping = TextWrapping.Wrap,
                Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(139, 38, 53)), FontWeight = FontWeights.SemiBold } };
        DockPanel.SetDock(warning, Dock.Top); panel.Children.Add(warning);
        var bottom = new StackPanel(); DockPanel.SetDock(bottom, Dock.Bottom); panel.Children.Add(bottom);
        bottom.Children.Add(new TextBlock { Text = "Settings, OpenAI key, source files and disk logs are unchanged. Cleared table identity counters return to their initial values.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) });
        bottom.Children.Add(_stopped);
        bottom.Children.Add(new TextBlock { Text = "Type RESET to confirm permanent deletion:" }); bottom.Children.Add(_confirmation);
        bottom.Children.Add(_status);
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        actions.Children.Add(_reset); actions.Children.Add(_cancel); bottom.Children.Add(actions);
        _cancel.Click += (_, _) => { if (!_deleting) Close(); };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            e.Handled = true;
            if (!_deleting) Close();
        };
        var grid = new DataGrid { AutoGenerateColumns = false, IsReadOnly = true, CanUserAddRows = false, CanUserDeleteRows = false, Margin = new Thickness(0, 12, 0, 0) };
        grid.Columns.Add(new DataGridTextColumn { Header = "Table", Binding = new Binding(nameof(ResetTable.Property)), Width = new DataGridLength(1, DataGridLengthUnitType.Star), MinWidth = 260 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Approx. rows", Binding = new Binding(nameof(ResetTable.Rows)), MinWidth = 100 });
        grid.Columns.Add(new DataGridCheckBoxColumn { Header = "Preserved", Binding = new Binding(nameof(ResetTable.Preserved)), MinWidth = 90 });
        panel.Children.Add(grid); Content = panel;
        Loaded += async (_, _) =>
        {
            try { grid.ItemsSource = await _service.PreviewAsync(clearKeywords); _status.Text = "Review the affected tables before confirming."; _reset.IsEnabled = true; }
            catch (Exception ex) { _status.Text = "Cannot prepare reset: " + ex.Message; }
        };
        Closing += (_, e) => { if (_deleting) e.Cancel = true; };
        _reset.Click += async (_, _) =>
        {
            if (_crawlerRunning()) { _status.Text = "Stop the crawler before resetting data."; return; }
            if (_confirmation.Text != "RESET" || _stopped.IsChecked != true) { _status.Text = "Confirm that crawlers are stopped, a backup exists, and type RESET."; return; }
            if (MessageBox.Show(this, _deleteWarning + "\n\nChoose OK to permanently delete, or Cancel to go back without deleting.",
                "Permanent deletion — cannot be undone", MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel) != MessageBoxResult.OK) return;
            _deleting = true; _reset.IsEnabled = _cancel.IsEnabled = false; _confirmation.IsEnabled = _stopped.IsEnabled = false;
            _status.Text = "Resetting database. Keep this window open.";
            try { await _service.ResetAsync(_clearKeywords); _status.Text = "Reset complete. Preserved seed records are ready for the next crawl."; }
            catch (Exception ex) { _status.Text = "Reset did not report success. SQL errors roll back the transaction; after a connection failure, reopen this window to check the table counts. " + ex.Message; }
            finally { _deleting = false; _cancel.IsEnabled = true; _cancel.Content = "Close (Esc)"; }
        };
    }
}
