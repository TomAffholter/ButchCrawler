using System.Data;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Data.SqlClient;

namespace ButchCrawler.Operator;

public sealed class ProblemSeedsWindow : Window
{
    private readonly ProblemSeedRepository _repository;
    private readonly string? _userAgent;
    private readonly DataGrid _grid = new() { IsReadOnly = true, AutoGenerateColumns = true,
        SelectionMode = DataGridSelectionMode.Single, CanUserAddRows = false, CanUserDeleteRows = false,
        CanUserSortColumns = true };
    private readonly TextBlock _status = new() { Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap };
    private readonly StackPanel _buttons = new() { Orientation = Orientation.Horizontal };
    private readonly Border _busyPanel = new()
    {
        Visibility = Visibility.Collapsed,
        Background = new SolidColorBrush(Color.FromRgb(232, 242, 255)),
        BorderBrush = new SolidColorBrush(Color.FromRgb(91, 142, 211)),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(8),
        Padding = new Thickness(16, 12, 16, 12),
        Margin = new Thickness(0, 0, 0, 12)
    };
    private readonly TextBlock _busyText = new() { Text = "Loading problem URLs…", FontSize = 18, FontWeight = FontWeights.SemiBold };
    private readonly ProgressBar _busyProgress = new() { IsIndeterminate = true, Height = 7, Margin = new Thickness(0, 9, 0, 0) };
    private bool _busy;
    public bool StartCrawlerRequested { get; private set; }

    public ProblemSeedsWindow(string connectionString, string? userAgent = null)
    {
        _repository = new ProblemSeedRepository(connectionString);
        _userAgent = userAgent;
        Title = "Problem seeds"; Width = 1180; Height = 650; MinWidth = 760; MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; AppTheme.Apply(this);
        var panel = new DockPanel { Margin = new Thickness(18) };
        AppTheme.AddHeading(panel, "Problem seeds");
        var help = new TextBlock
        {
            Text = "Nothing is deleted or disabled automatically. For sites requiring a person, use Verify and crawl. Click any column heading to sort; click it again to reverse the order.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10)
        };
        DockPanel.SetDock(help, Dock.Top); panel.Children.Add(help);
        var busyContent = new StackPanel(); busyContent.Children.Add(_busyText); busyContent.Children.Add(_busyProgress);
        _busyPanel.Child = busyContent; DockPanel.SetDock(_busyPanel, Dock.Top); panel.Children.Add(_busyPanel);
        AddButton("Verify and crawl", VerifyAsync, primary: true);
        AddButton("Retry next crawl", RetryAsync);
        AddButton("Disable", DisableAsync);
        AddButton("Delete permanently", DeleteAsync);
        AddButton("Clean up…", CleanupAsync);
        AddButton("Refresh", LoadAsync);
        var close = new Button { Content = "Close", Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(16, 0, 0, 10) };
        close.Click += (_, _) => Close(); _buttons.Children.Add(close);
        DockPanel.SetDock(_buttons, Dock.Top); panel.Children.Add(_buttons);
        DockPanel.SetDock(_status, Dock.Bottom); panel.Children.Add(_status);
        _grid.AutoGeneratingColumn += (_, e) =>
        {
            e.Column.CanUserSort = true;
            e.Column.MinWidth = e.PropertyName switch { "Url" => 320, "Reason" => 300, "First failed attempt" or "Last failure" or "Next check" => 145, _ => 75 };
            if (e.PropertyName == "Id") e.Column.Visibility = Visibility.Collapsed;
        };
        panel.Children.Add(_grid); Content = panel; Loaded += async (_, _) => await LoadAsync();
    }

    private void AddButton(string text, Func<Task> action, bool primary = false)
    {
        var button = new Button { Content = text, Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(0, 0, 8, 10) };
        if (primary) button.SetResourceReference(StyleProperty, "PrimaryButton");
        button.Click += async (_, _) => await action(); _buttons.Children.Add(button);
    }

    private DataRowView? SelectedRow() => _grid.SelectedItem as DataRowView;
    private int? SelectedId() => SelectedRow() is { } row ? Convert.ToInt32(row["Id"]) : null;
    private string SelectedUrl() => _grid.SelectedItem is DataRowView row ? Convert.ToString(row["Url"]) ?? "this seed" : "this seed";

    private void ShowProblemCount(string? message = null)
    {
        var count = (_grid.ItemsSource as DataView)?.Count ?? 0;
        var countText = count == 0 ? "No other problem seeds need attention." : $"{count:N0} seed(s) need attention.";
        _status.Text = string.IsNullOrWhiteSpace(message) ? countText : $"{message} {countText}";
    }

    private async Task LoadAsync()
    {
        await RunAsync(async () =>
        {
            var table = await _repository.LoadAsync(); _grid.ItemsSource = table.DefaultView;
            ShowProblemCount();
        }, refresh: false, busyMessage: "Loading problem seeds…");
    }

    private async Task RetryAsync()
    {
        var row = SelectedRow();
        if (row == null) { _status.Text = "Select a seed first."; return; }
        var id = Convert.ToInt32(row["Id"]);
        var status = Convert.ToString(row["Status"]) ?? string.Empty;
        var stillNeedsVerification = status is "human-verification" or "domain-verification";
        await RunAsync(async () =>
        {
            await _repository.RetryNowAsync(id);
            if (stillNeedsVerification)
            {
                ShowProblemCount("Retry scheduled, but this site remains listed because human verification is still required. Use Verify and crawl when you are ready.");
                return;
            }

            row.Row.Table.Rows.Remove(row.Row);
            ShowProblemCount("Retry scheduled for the next crawler run.");
        }, refresh: false, busyMessage: "Scheduling the selected URL for retry…");
    }

    private async Task VerifyAsync()
    {
        var id = SelectedId(); if (id == null) { _status.Text = "Select a seed first."; return; }
        var url = SelectedUrl();
        var verifier = new HumanVerificationWindow(url, _userAgent) { Owner = this };
        if (verifier.ShowDialog() != true || !verifier.VerificationSaved)
        { _status.Text = "Verification was not saved. The seed remains available for later."; return; }
        await RunAsync(() => _repository.CompleteHumanVerificationAsync(id.Value, url), busyMessage: "Saving verification and preparing related URLs…");
        StartCrawlerRequested = true;
        MessageBox.Show(this, "Verification was saved temporarily. The crawler will now retry this URL and every other Turnstile-blocked URL on the same website while continuing its other work.",
            "Ready to crawl", MessageBoxButton.OK, MessageBoxImage.Information);
        DialogResult = true;
    }

    private async Task DisableAsync()
    {
        var id = SelectedId(); if (id == null) { _status.Text = "Select a seed first."; return; }
        if (MessageBox.Show(this, $"Disable this seed?\n\n{SelectedUrl()}", "Disable seed", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        await RunAsync(() => _repository.DisableAsync(id.Value), busyMessage: "Disabling URL and reloading problem URLs…");
    }

    private async Task DeleteAsync()
    {
        var id = SelectedId(); if (id == null) { _status.Text = "Select a seed first."; return; }
        if (MessageBox.Show(this, $"Permanently delete this seed URL?\n\n{SelectedUrl()}\n\nPreviously collected pages will be retained.",
            "Delete seed", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        await RunAsync(() => _repository.DeleteAsync(id.Value), busyMessage: "Deleting URL and reloading problem URLs…");
    }

    private async Task CleanupAsync()
    {
        if (_busy) return;
        var cleanup = new ProblemSeedCleanupWindow(_repository) { Owner = this };
        if (cleanup.ShowDialog() == true) await LoadAsync();
    }

    private async Task RunAsync(Func<Task> action, bool refresh = true, string busyMessage = "Updating problem seed…")
    {
        if (_busy) return; _busy = true; _buttons.IsEnabled = _grid.IsEnabled = false;
        _status.Text = busyMessage;
        _busyText.Text = busyMessage;
        _busyPanel.Visibility = Visibility.Visible;
        Cursor = System.Windows.Input.Cursors.Wait;
        await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
        try { await action(); if (refresh) await LoadCoreAsync(); }
        catch (SqlException ex) when (ex.Number == -2)
        {
            _status.Text = "The database took too long to answer. No problem seeds were changed.";
            MessageBox.Show(this,
                "The Problem Seeds request took too long to finish. No records were changed.\n\n" +
                "Close any large SQL queries that may still be running, wait a moment, and choose Refresh. If this repeats, verify that SQL Server is running.",
                Title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex) { _status.Text = "Operation failed. No problem seeds were changed."; MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error); }
        finally { _busy = false; _buttons.IsEnabled = _grid.IsEnabled = true; _busyPanel.Visibility = Visibility.Collapsed; Cursor = null; }
    }

    private async Task LoadCoreAsync()
    {
        var table = await _repository.LoadAsync(); _grid.ItemsSource = table.DefaultView;
        ShowProblemCount();
    }
}
