using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;

namespace ButchCrawler.Operator;

public sealed class LogWindow : Window
{
    private readonly string _directory;
    private readonly Func<bool> _crawlerRunning;
    private readonly ComboBox _files = new() { MinWidth = 260, DisplayMemberPath = "Name", Margin = new Thickness(0, 0, 8, 0) };
    private readonly TextBox _text = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
        FontFamily = new System.Windows.Media.FontFamily("Consolas") };
    private readonly TextBlock _status = new() { Margin = new Thickness(0, 8, 0, 0) };
    private bool _busy;

    public LogWindow(string directory, Func<bool> crawlerRunning)
    {
        AppTheme.Apply(this);
        _directory = directory; _crawlerRunning = crawlerRunning;
        Title = "Crawler logs — read only"; Width = 1050; Height = 650;
        var panel = new DockPanel { Margin = new Thickness(18) };
        AppTheme.AddHeading(panel, "Log files");
        var tools = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
        tools.Children.Add(_files);
        var refresh = new Button { Content = "Refresh", Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(0, 0, 8, 0) };
        refresh.Click += async (_, _) => { RefreshFiles(); await ReadAsync(); }; tools.Children.Add(refresh);
        var delete = new Button { Content = "Delete selected log", Padding = new Thickness(12, 6, 12, 6) };
        delete.Click += async (_, _) => await DeleteAsync(); tools.Children.Add(delete);
        DockPanel.SetDock(tools, Dock.Top); panel.Children.Add(tools);
        DockPanel.SetDock(_status, Dock.Bottom); panel.Children.Add(_status);
        panel.Children.Add(_text); Content = panel;
        _files.SelectionChanged += async (_, _) => await ReadAsync();
        Loaded += (_, _) => RefreshFiles();
    }

    private void RefreshFiles()
    {
        try
        {
            Directory.CreateDirectory(_directory);
            var selected = (_files.SelectedItem as FileInfo)?.FullName;
            var files = new DirectoryInfo(_directory).GetFiles("butchcrawler-*.log").OrderByDescending(f => f.LastWriteTimeUtc).ToArray();
            _files.ItemsSource = files;
            _files.SelectedItem = files.FirstOrDefault(f => f.FullName == selected) ?? files.FirstOrDefault();
            if (files.Length == 0) { _text.Clear(); _status.Text = "No log files found."; }
        }
        catch (Exception ex) { _status.Text = ex.Message; }
    }

    private async Task ReadAsync()
    {
        if (_files.SelectedItem is not FileInfo file) return;
        try
        {
            // A bounded tail prevents large log files from freezing the operator.
            var result = await LogFiles.ReadTailAsync(file.FullName);
            if ((_files.SelectedItem as FileInfo)?.FullName != file.FullName) return;
            _text.Text = result.Text;
            _status.Text = result.Truncated ? "Showing the last 2 MiB (read only). Refresh to load new entries." : "Read only. Refresh to load new entries.";
        }
        catch (Exception ex) { _status.Text = "Unable to read log: " + ex.Message; }
    }

    private async Task DeleteAsync()
    {
        if (_busy || _files.SelectedItem is not FileInfo file) return;
        if (_crawlerRunning()) { MessageBox.Show(this, "Stop the crawler before deleting log files.", Title); return; }
        if (MessageBox.Show(this, $"Permanently delete {file.Name}?", Title, MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _busy = true;
        try
        {
            // FileShare.None also refuses deletion while another crawler holds this log open.
            await LogFiles.DeleteAsync(file.FullName);
            _text.Clear(); RefreshFiles(); _status.Text = "Log deleted.";
        }
        catch (Exception ex) { MessageBox.Show(this, "Unable to delete the log. It may be in use.\n" + ex.Message, Title); }
        finally { _busy = false; }
    }
}
