using System.Diagnostics;
using System.Windows;
using System.Windows.Navigation;
using System.IO;
using Microsoft.Extensions.Configuration;

namespace ButchCrawler.Operator;

public partial class AboutWindow : Window
{
    private readonly string? _settingsPath;
    private readonly CancellationTokenSource _lifetime = new();
    private bool _loading, _closed;

    public AboutWindow(string? settingsPath = null)
    {
        _settingsPath = settingsPath;
        InitializeComponent(); AppTheme.Apply(this);
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = System.Windows.Media.Brushes.Black;
        VersionText.Text = "Version " + AppIdentity.Version;
        CompiledText.Text = "Compiled " + AppIdentity.CompiledDate + "  ·  Pacific time";
        CompiledText.ToolTip = AppIdentity.CompiledUtc?.ToString("O") + " (UTC build timestamp)";
        AiBillingStatusText.Text = ButchCrawler.App.OpenAiBilling.Status;
        ContentRendered += About_ContentRendered;
        Closed += (_, _) => { _closed = true; _lifetime.Cancel(); _lifetime.Dispose(); };
    }

    private async void About_ContentRendered(object? sender, EventArgs e)
    {
        ContentRendered -= About_ContentRendered;
        await RefreshCountsAsync();
    }

    private async void RefreshCounts_Click(object sender, RoutedEventArgs e) => await RefreshCountsAsync();

    private async Task RefreshCountsAsync()
    {
        if (_loading || _closed) return;
        _loading = true; RefreshCountsButton.IsEnabled = false;
        CountsLoadingBar.Visibility = Visibility.Visible;
        CountsStatusText.Text = "Loading… Retrieving database totals.";
        var token = _lifetime.Token;
        try
        {
            var counts = await Task.Run(async () =>
            {
            if (string.IsNullOrWhiteSpace(_settingsPath)) throw new InvalidOperationException("Settings path is unavailable.");
            var path = Path.GetFullPath(_settingsPath);
            var environment = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") ?? "Production";
            var config = new ConfigurationBuilder().SetBasePath(Path.GetDirectoryName(path)!)
                .AddJsonFile(Path.GetFileName(path)).AddJsonFile($"appsettings.{environment}.json", optional: true).AddEnvironmentVariables().Build();
            try
            {
                var connection = config.GetConnectionString("DefaultConnection") ?? throw new InvalidOperationException("Missing database connection.");
                return await new DatabaseStatisticsRepository(connection).LoadAsync(token);
            }
            finally { (config as IDisposable)?.Dispose(); }
            }, token);
                if (_closed) return;
                SeedKeywordCountText.Text = counts.SeedKeywords.ToString("N0");
                SeedUrlCountText.Text = counts.SeedUrls.ToString("N0");
                DiscoveredUrlCountText.Text = counts.DiscoveredUrls.ToString("N0");
                CountsStatusText.Text = "Database totals · discovered URLs exclude seeds";
        }
        catch (OperationCanceledException) when (_closed) { }
        catch
        {
            if (!_closed)
            {
                SeedKeywordCountText.Text = SeedUrlCountText.Text = DiscoveredUrlCountText.Text = "—";
                CountsStatusText.Text = "Database counts unavailable. Check the connection and refresh.";
            }
        }
        finally
        {
            _loading = false;
            if (!_closed)
            {
                RefreshCountsButton.IsEnabled = true;
                CountsLoadingBar.Visibility = Visibility.Collapsed;
            }
        }
    }

    private void Email_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        e.Handled = true;
        try { Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); }
        catch { MessageBox.Show(this, "Could not open your email application. Contact " + AppIdentity.Email + ".", "Email"); }
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => Close();

    private void RefreshBilling_Click(object sender, RoutedEventArgs e) => AiBillingStatusText.Text = ButchCrawler.App.OpenAiBilling.Status;
    private void Account_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        e.Handled = true;
        try { Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); }
        catch { MessageBox.Show(this, "Could not open your browser. Open this account page manually:\n" + e.Uri.AbsoluteUri, "AI account"); }
    }
}
