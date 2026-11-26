using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.IO;
using Microsoft.Extensions.Configuration;

namespace ButchCrawler.Operator;

public sealed class SettingsWindow : Window
{
    private readonly SettingsRepository _repository;
    private readonly DataGrid _grid = new() { AutoGenerateColumns = false, CanUserAddRows = false,
        CanUserDeleteRows = false, CanUserReorderColumns = false, SelectionMode = DataGridSelectionMode.Single };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 8) };
    private bool _discard;
    private readonly string _path;
    private readonly Func<bool> _crawlerRunning;

    public SettingsWindow(string path, Func<bool>? crawlerRunning = null)
    {
        AppTheme.Apply(this);
        _path = Path.GetFullPath(path);
        _crawlerRunning = crawlerRunning ?? (() => false);
        _repository = new SettingsRepository(path);
        Title = "Settings"; Width = 900; Height = 650; MinWidth = 650; MinHeight = 400;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new DockPanel { Margin = new Thickness(16) };
        AppTheme.AddHeading(panel, "Settings");
        var help = new TextBlock { Text = "Edit existing values below. Properties cannot be added, renamed or deleted. OpenAI keys are managed separately. For Crawler : InitialSeedLimit, 0 means unlimited. Changes save when this window closes; restart the crawler to apply them. Environment settings may override these values.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) };
        DockPanel.SetDock(help, Dock.Top); panel.Children.Add(help);
        var bottom = new StackPanel(); DockPanel.SetDock(bottom, Dock.Bottom); panel.Children.Add(bottom);
        bottom.Children.Add(_status);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal }; bottom.Children.Add(buttons);
        var save = new Button { Content = "Save and close", Padding = new Thickness(12, 7, 12, 7) };
        save.SetResourceReference(StyleProperty, "PrimaryButton");
        save.Click += (_, _) => Close(); buttons.Children.Add(save);
        var cancel = new Button { Content = "Close without saving", Padding = new Thickness(12, 7, 12, 7), Margin = new Thickness(10, 0, 0, 0) };
        cancel.Click += (_, _) => { _discard = true; Close(); }; buttons.Children.Add(cancel);
        bottom.Children.Add(new Separator { Margin = new Thickness(0, 16, 0, 0) });
        bottom.Children.Add(new TextBlock { Text = "Crawl starting point", FontSize = 17, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 10, 0, 0) });
        bottom.Children.Add(new TextBlock { Text = "Schedule every active seed URL from the beginning again. Previously captured pages, research, matches, documents, and discovered URLs are kept.", Foreground = (System.Windows.Media.Brush)FindResource("MutedBrush"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 6) });
        var restartSeeds = new Button { Content = "Restart all seed URLs from the beginning",
            Padding = new Thickness(14, 9, 14, 9), HorizontalAlignment = HorizontalAlignment.Left,
            FontSize = 14, FontWeight = FontWeights.SemiBold,
            IsEnabled = !_crawlerRunning(), ToolTip = "Stop the crawler first. You will be asked to confirm." };
        restartSeeds.Click += async (_, _) => await RestartSeedsAsync(restartSeeds);
        bottom.Children.Add(restartSeeds);
        _grid.Columns.Add(new DataGridTextColumn { Header = "Property", MinWidth = 300, IsReadOnly = true, Binding = new Binding(nameof(SettingRow.Property)), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        _grid.Columns.Add(new DataGridTextColumn { Header = "Value", MinWidth = 280, Binding = new Binding(nameof(SettingRow.Value)) { UpdateSourceTrigger = UpdateSourceTrigger.LostFocus }, Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        _grid.ItemsSource = _repository.Rows;
        panel.Children.Add(_grid); Content = panel;
        Closing += (_, e) =>
        {
            if (_discard) return;
            if (!_grid.CommitEdit(DataGridEditingUnit.Cell, true) || !_grid.CommitEdit(DataGridEditingUnit.Row, true))
            { e.Cancel = true; _status.Text = "Finish editing the current value before saving."; return; }
            try { _repository.Save(); }
            catch (ArgumentException ex) { e.Cancel = true; _status.Text = ex.Message; }
            catch { e.Cancel = true; _status.Text = "Could not save. Check file access, or close without saving and reopen Settings if the file changed."; }
        };
    }

    private async Task RestartSeedsAsync(Button button)
    {
        if (_crawlerRunning()) { _status.Text = "Stop the crawler before restarting seed URLs."; return; }
        button.IsEnabled = false;
        try
        {
            var environment = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") ?? "Production";
            var config = new ConfigurationBuilder().SetBasePath(Path.GetDirectoryName(_path)!)
                .AddJsonFile(Path.GetFileName(_path)).AddJsonFile($"appsettings.{environment}.json", optional: true)
                .AddEnvironmentVariables().Build();
            try
            {
                var connection = config.GetConnectionString("DefaultConnection") ?? throw new InvalidOperationException("Missing database connection.");
                var service = new SeedProgressResetService(connection);
                var preview = await service.PreviewAsync();
                var answer = MessageBox.Show(this,
                    $"Are you sure you want to restart all {preview.ActiveSeeds:N0} active seed URLs from the beginning?\n\n" +
                    "This resets only their crawl starting point. Previously captured pages, research, matches, documents, and discovered URLs will not be deleted.\n\n" +
                    "The next crawl may take a long time and revisit the seed websites.",
                    "Restart seed URLs?", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
                if (answer != MessageBoxResult.Yes) { _status.Text = "Seed restart canceled. Nothing was changed."; return; }

                var restarted = await service.RestartAsync();
                _status.Text = $"Ready. {restarted:N0} existing seed URL queue records will start again on the next crawl. Any newly added active seeds will also be included.";
            }
            finally { (config as IDisposable)?.Dispose(); }
        }
        catch (Exception ex)
        {
            _status.Text = "Seed URLs were not restarted. " + ex.Message;
        }
        finally { button.IsEnabled = !_crawlerRunning(); }
    }

}
