using System.Data;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Data.SqlClient;

namespace ButchCrawler.Operator;

// Changes are staged in memory until Save. SQL command builder supplies parameters
// and optimistic concurrency checks; a failed batch is rolled back in full.
public sealed class SeedEditorWindow : Window
{
    private readonly SeedRepository _repository;
    private readonly bool _keywords;
    private readonly DataGrid _grid = new() { AutoGenerateColumns = true, CanUserAddRows = false,
        CanUserDeleteRows = false, SelectionMode = DataGridSelectionMode.Single };
    private readonly TextBlock _status = new() { Margin = new Thickness(8), TextWrapping = TextWrapping.Wrap };
    private readonly StackPanel _buttons = new() { Orientation = Orientation.Horizontal };
    private DataTable? _table;
    private bool _busy;

    public SeedEditorWindow(string connectionString, bool keywords)
    {
        AppTheme.Apply(this);
        _repository = new SeedRepository(connectionString, keywords);
        _keywords = keywords;
        Title = keywords ? "Seed keywords" : "Seed URLs";
        Width = 1050; Height = 600; MinWidth = 600; MinHeight = 350;
        var panel = new DockPanel { Margin = new Thickness(18) };
        AppTheme.AddHeading(panel, keywords ? "Seed keywords" : "Seed URLs");
        var help = new TextBlock { Text = "Edit cells, then choose Save changes. New rows and deletions are also staged until Save. Restart the crawler to use changes.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) };
        DockPanel.SetDock(help, Dock.Top); panel.Children.Add(help);
        DockPanel.SetDock(_buttons, Dock.Top); panel.Children.Add(_buttons);
        AddButton("Add", Add);
        AddButton("Delete selected", Delete);
        AddButton("Save changes", async () => await SaveAsync());
        AddButton("Reload", async () => { if (DiscardChanges()) await LoadAsync(); });
        DockPanel.SetDock(_status, Dock.Bottom); panel.Children.Add(_status);
        panel.Children.Add(_grid); Content = panel;
        _grid.AutoGeneratingColumn += (_, e) =>
        {
            e.Column.Header = e.PropertyName switch { "Id" => "ID", "Url" => "URL", _ => System.Text.RegularExpressions.Regex.Replace(e.PropertyName, "(?<=[a-z])(?=[A-Z])", " ") };
            e.Column.MinWidth = e.PropertyName switch { "Id" => 60, "Url" => 330, "Keyword" => 240, "CreatedAt" or "LastCrawledAt" => 160, _ => 110 };
            if (e.PropertyName is "Id" or "CreatedAt" or "LastCrawledAt") e.Column.IsReadOnly = true;
        };
        Loaded += async (_, _) => await LoadAsync();
        Closing += (_, e) => { if (_busy || !DiscardChanges()) e.Cancel = true; };
    }

    private void AddButton(string label, Action action)
    {
        var button = new Button { Content = label, Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(0, 0, 8, 10) };
        if (label == "Save changes") button.SetResourceReference(StyleProperty, "PrimaryButton");
        if (label == "Delete selected") button.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(139, 38, 53));
        button.Click += (_, _) => action(); _buttons.Children.Add(button);
    }

    private void SetBusy(bool busy)
    {
        _busy = busy; _buttons.IsEnabled = !busy; _grid.IsEnabled = !busy;
    }

    private async Task LoadAsync()
    {
        SetBusy(true);
        try
        {
            var table = await _repository.LoadAsync();
            _table = table; _grid.ItemsSource = table.DefaultView;
            _status.Text = $"Loaded {table.Rows.Count:N0} rows. ID and crawl timestamps are read-only.";
        }
        catch (Exception ex) { ShowError(ex); }
        finally { SetBusy(false); }
    }

    private void Add()
    {
        if (_table == null) return;
        var row = _table.NewRow();
        row[_keywords ? "Keyword" : "Url"] = _keywords ? "New keyword" : "https://example.com/";
        row["IsActive"] = true; row["CreatedAt"] = DateTime.UtcNow;
        if (!_keywords)
        {
            row["Priority"] = 0; row["MaxDepth"] = 2; row["FollowExternal"] = false;
        }
        _table.Rows.Add(row);
        _grid.SelectedItem = _table.DefaultView.Cast<DataRowView>().First(v => ReferenceEquals(v.Row, row));
        _grid.ScrollIntoView(_grid.SelectedItem);
        _status.Text = "New row added. Edit its values and save.";
    }

    private void Delete()
    {
        if (_grid.SelectedItem is not DataRowView view) return;
        if (MessageBox.Show(this, "Delete this row when you save changes?" + (_keywords ? " Its recorded keyword hits will also be removed." : " Existing crawl history is retained; database references may prevent deletion."),
            "Delete " + Title, MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        view.Row.Delete(); _status.Text = "Deletion staged. Choose Save changes to apply.";
    }

    private bool DiscardChanges()
    {
        _grid.CommitEdit(DataGridEditingUnit.Cell, true); _grid.CommitEdit(DataGridEditingUnit.Row, true);
        return _table?.GetChanges() == null || MessageBox.Show(this, "Discard unsaved changes?", Title,
            MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
    }

    private async Task SaveAsync()
    {
        if (_table == null) return;
        if (!_grid.CommitEdit(DataGridEditingUnit.Cell, true) || !_grid.CommitEdit(DataGridEditingUnit.Row, true))
        { _status.Text = "Correct the highlighted cell before saving."; return; }
        SetBusy(true);
        try
        {
            ValidateRows(_table, _keywords);
            // Use a copy so rollback leaves all staged edits available for correction/retry.
            var changes = _table.GetChanges();
            if (changes == null) { _status.Text = "No changes to save."; return; }
            await _repository.SaveAsync(changes);
            // Data is committed. Do not retain pending edits if reloading fails.
            _table = null;
            _grid.ItemsSource = null;
            await LoadAsync();
            _status.Text = _table != null ? "Changes saved. Restart the crawler to use the updated seeds."
                : "Changes saved, but refresh failed. Reload before editing further.";
        }
        catch (Exception ex) { ShowError(ex); }
        finally { SetBusy(false); }
    }

    internal static void ValidateRows(DataTable table, bool keywords)
    {
        var values = new HashSet<string>(keywords ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (DataRow row in table.Rows)
        {
            if (row.RowState == DataRowState.Deleted) continue;
            var field = keywords ? "Keyword" : "Url";
            var value = Convert.ToString(row[field])?.Trim() ?? "";
            if (value.Length == 0) throw new InvalidOperationException(field + " cannot be blank.");
            if (!keywords)
            {
                if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https") || !string.IsNullOrEmpty(uri.UserInfo))
                    throw new InvalidOperationException("Seed URL must be an absolute HTTP or HTTPS address without credentials.");
                value = new UriBuilder(uri) { Fragment = "" }.Uri.AbsoluteUri;
                if (value.Length > 2000) throw new InvalidOperationException("Seed URL must be at most 2000 characters.");
                if (row.IsNull("MaxDepth") || Convert.ToInt32(row["MaxDepth"]) < 0) throw new InvalidOperationException("MaxDepth must be zero or greater.");
            }
            if (!values.Add(value)) throw new InvalidOperationException("Duplicate " + field + ": " + value);
            if (!Equals(row[field], value)) row[field] = value;
        }
    }

    private void ShowError(Exception ex)
    {
        _status.Text = "Operation failed. Review the error and reload if needed.";
        MessageBox.Show(this, ex is DBConcurrencyException ? "A row changed in the database. Reload and reapply your edits." : ex.Message,
            Title, MessageBoxButton.OK, MessageBoxImage.Error);
    }
}
