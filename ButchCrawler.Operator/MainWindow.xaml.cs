using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Microsoft.Extensions.Configuration;

namespace ButchCrawler.Operator;

public partial class MainWindow : Window
{
#if DEBUG
    private const string BuildConfiguration = "Debug";
#else
    private const string BuildConfiguration = "Release";
#endif
    private Process? _process;
    private bool _exiting, _exitApproved;
    private bool _crawlerReady;
    private string? _startupFailureSummary;
    private Task? _stopTask;
    private readonly string _appDir;
    private int _urlStarts, _responses, _retrievedPages, _queuedLinks, _matchEvents, _errors, _failedAttempts, _retryCount;
    private long? _successfulSeedUrls, _problemSeedUrls, _totalSeedUrls;
    private long? _discoveredUrlsTotal;
    private long? _localFilesProcessed, _localFilesTotal;
    private readonly Dictionary<string, int> _processedTypes = new(StringComparer.OrdinalIgnoreCase);

    public MainWindow()
    {
        InitializeComponent();
        AppTheme.Apply(this);
        _appDir = FindAppDirectory() ?? string.Empty;
        if (string.IsNullOrEmpty(_appDir))
        {
            StatusText.Text = "Cannot locate ButchCrawler.App. Place Operator inside the original solution.";
            StartButton.IsEnabled = false;
        }
        Closing += MainWindow_Closing;
        ContentRendered += MainWindow_ContentRendered;
    }

    private async void MainWindow_ContentRendered(object? sender, EventArgs e)
    {
        ContentRendered -= MainWindow_ContentRendered;
        await RefreshSavedSeedProgressAsync();
    }

    private async Task RefreshSavedSeedProgressAsync()
    {
        if (string.IsNullOrWhiteSpace(_appDir)) return;
        try
        {
            var environment = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") ?? "Production";
            var config = new ConfigurationBuilder().SetBasePath(_appDir)
                .AddJsonFile("appsettings.json", optional: false)
                .AddJsonFile($"appsettings.{environment}.json", optional: true)
                .AddEnvironmentVariables().Build();
            try
            {
                var connection = config.GetConnectionString("DefaultConnection")
                    ?? throw new InvalidOperationException("Missing DefaultConnection connection string.");
                var repository = new DatabaseStatisticsRepository(connection);
                var progressTask = repository.LoadSeedProgressAsync();
                var statisticsTask = repository.LoadAsync();
                await Task.WhenAll(progressTask, statisticsTask);
                var progress = await progressTask;
                var statistics = await statisticsTask;
                if (!IsLoaded) return;
                _successfulSeedUrls = progress.Successful;
                _problemSeedUrls = progress.Problems;
                _totalSeedUrls = progress.Total;
                _discoveredUrlsTotal = statistics.DiscoveredUrls;
                RefreshCounters();
            }
            finally { (config as IDisposable)?.Dispose(); }
        }
        catch
        {
            // The crawler's startup diagnostics will report database problems. Keep
            // the dashboard calm when it is opened while SQL Server is unavailable.
        }
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => Close();

    private void SettingsMenu_Click(object sender, RoutedEventArgs e)
    {
        if (SettingsMenu.ContextMenu is null) return;
        SettingsMenu.ContextMenu.PlacementTarget = SettingsMenu;
        SettingsMenu.ContextMenu.IsOpen = true;
    }

    private async void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_exitApproved) return;
        e.Cancel = true;
        if (_exiting) return;
        _exiting = true;
        StartButton.IsEnabled = StopButton.IsEnabled = SettingsMenu.IsEnabled = false;
        StatusText.BeginAnimation(OpacityProperty, null);
        StatusText.Text = "Exiting… Stopping crawler.";
        // Let WPF finish the canceled Closing event before a second Close().
        // Waiting for an already stopped process can complete synchronously.
        await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
        try
        {
            await StopCrawlerAsync();
            _exitApproved = true;
            Close();
        }
        catch (Exception ex)
        {
            _exiting = false;
            SettingsMenu.IsEnabled = true;
            SetRunning(_process is { HasExited: false });
            MessageBox.Show(this, "Could not stop the crawler. The app will remain open so you can try again.\n\n" + ex.Message, "Exit failed", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static string? FindAppDirectory()
    {
        var locations = new[] { Environment.CurrentDirectory, AppContext.BaseDirectory };
        foreach (var location in locations)
        {
            var current = new DirectoryInfo(location);
            for (var i = 0; i < 8 && current != null; i++, current = current.Parent)
            {
                var path = Path.Combine(current.FullName, "ButchCrawler.App");
                if (File.Exists(Path.Combine(path, "ButchCrawler.App.csproj"))) return path;
            }
        }
        return null;
    }

    private void Start_Click(object sender, RoutedEventArgs e)
    {
        if (_exiting || _stopTask is { IsCompleted: false }) return;
        if (_process is { HasExited: false }) return;
        try
        {
            ActivityLog.Clear(OutputText);
            ActivityLog.Clear(UrlOutputText);
            ActivityLog.Clear(KeywordOutputText);
            _urlStarts = _responses = _retrievedPages = _queuedLinks = _matchEvents = _errors = _failedAttempts = _retryCount = 0;
            _processedTypes.Clear();
            _localFilesProcessed = _localFilesTotal = null;
            RefreshCounters();
            var dllPath = Path.Combine(_appDir, "bin", BuildConfiguration, "net8.0", "ButchCrawler.App.dll");
            if (!File.Exists(dllPath))
                throw new FileNotFoundException($"Crawler not built for {BuildConfiguration}. Build the entire solution in Visual Studio.", dllPath);
            _process?.Dispose();
            _crawlerReady = false;
            _startupFailureSummary = null;
            var dotnetPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "dotnet.exe");
            if (!File.Exists(dotnetPath)) throw new FileNotFoundException("The trusted .NET runtime was not found under Program Files.", dotnetPath);
            _process = new Process { StartInfo = new ProcessStartInfo(dotnetPath)
            {
                Arguments = $"\"{dllPath}\" --Operator:Controlled=true",
                WorkingDirectory = _appDir,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardInput = true,
                RedirectStandardError = true
            }, EnableRaisingEvents = true };
            _process.OutputDataReceived += (_, args) => Append(args.Data);
            _process.ErrorDataReceived += (_, args) => Append(args.Data);
            var launched = _process;
            _process.Exited += (_, _) =>
            {
                if (Dispatcher.HasShutdownStarted) return;
                Dispatcher.BeginInvoke(() =>
                {
                    if (!ReferenceEquals(_process, launched) || _exiting || _stopTask is { IsCompleted: false }) return;
                    var failedBeforeReady = !_crawlerReady && launched.ExitCode != 0;
                    if (failedBeforeReady) SetStartupFailed(_startupFailureSummary);
                    else SetRunning(false);
                });
            };
            _process.Start();
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();
            SetStarting();
        }
        catch (Exception ex) { _process?.Dispose(); _process = null; MessageBox.Show(ex.Message, "Launch error"); SetRunning(false); }
    }

    private void SetRunning(bool running)
    {
        StartButton.IsEnabled = !running && !string.IsNullOrEmpty(_appDir);
        StopButton.IsEnabled = running;
        SeedUrlsMenuItem.IsEnabled = ProblemSeedsMenuItem.IsEnabled = HistoricalNewspapersMenuItem.IsEnabled = KeywordsMenuItem.IsEnabled = !running && !string.IsNullOrEmpty(_appDir);
        StatusText.Text = running ? "Crawler Running" : "Crawler stopped";
        StatusText.Foreground = new SolidColorBrush(running ? Color.FromRgb(20, 85, 190) : Color.FromRgb(180, 35, 45));
        CrawlerStatusBadge.Background = new SolidColorBrush(running ? Color.FromRgb(233, 242, 255) : Color.FromRgb(255, 240, 240));
        CrawlerStatusBadge.BorderBrush = new SolidColorBrush(running ? Color.FromRgb(123, 166, 226) : Color.FromRgb(230, 156, 156));
        CrawlerStatusBadge.ToolTip = null;
        StatusText.BeginAnimation(OpacityProperty, null);
        StatusText.Opacity = 1;
        if (running)
            StatusText.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0.55, TimeSpan.FromSeconds(1.2))
            { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
        else if (IsLoaded && !_exiting)
            _ = RefreshSavedSeedProgressAsync();
    }

    private void SetStarting()
    {
        StartButton.IsEnabled = false;
        StopButton.IsEnabled = true;
        SeedUrlsMenuItem.IsEnabled = ProblemSeedsMenuItem.IsEnabled = HistoricalNewspapersMenuItem.IsEnabled = KeywordsMenuItem.IsEnabled = false;
        StatusText.Text = "Starting crawler…";
        StatusText.Foreground = new SolidColorBrush(Color.FromRgb(166, 83, 6));
        CrawlerStatusBadge.Background = new SolidColorBrush(Color.FromRgb(255, 247, 225));
        CrawlerStatusBadge.BorderBrush = new SolidColorBrush(Color.FromRgb(213, 154, 53));
        CrawlerStatusBadge.ToolTip = "Waiting for the crawler to finish its startup checks.";
        StatusText.BeginAnimation(OpacityProperty, null);
        StatusText.Opacity = 1;
    }

    private void SetStartupFailed(string? reason)
    {
        StartButton.IsEnabled = !string.IsNullOrEmpty(_appDir);
        StopButton.IsEnabled = false;
        SeedUrlsMenuItem.IsEnabled = ProblemSeedsMenuItem.IsEnabled = HistoricalNewspapersMenuItem.IsEnabled = KeywordsMenuItem.IsEnabled = !string.IsNullOrEmpty(_appDir);
        StatusText.Text = "Crawler could not start";
        StatusText.Foreground = new SolidColorBrush(Color.FromRgb(180, 35, 45));
        CrawlerStatusBadge.Background = new SolidColorBrush(Color.FromRgb(255, 240, 240));
        CrawlerStatusBadge.BorderBrush = new SolidColorBrush(Color.FromRgb(230, 156, 156));
        CrawlerStatusBadge.ToolTip = string.IsNullOrWhiteSpace(reason)
            ? "See All activity for the startup error."
            : reason;
        StatusText.BeginAnimation(OpacityProperty, null);
        StatusText.Opacity = 1;
    }

    private void Append(string? line)
    {
        if (line == null) return;
        Dispatcher.BeginInvoke(() =>
        {
            if (line.Equals("[CRAWL-STATE] RUNNING", StringComparison.Ordinal))
            {
                _crawlerReady = true;
                SetRunning(true);
                ActivityLog.Append(OutputText, "Crawler startup checks passed.");
                return;
            }
            const string seedProgressPrefix = "[CRAWL-SEEDS] PROGRESS | ";
            if (line.StartsWith(seedProgressPrefix, StringComparison.Ordinal))
            {
                var values = line[seedProgressPrefix.Length..].Split('|', StringSplitOptions.TrimEntries);
                if (values.Length == 3 && long.TryParse(values[0], out var successful) &&
                    long.TryParse(values[1], out var problems) && long.TryParse(values[2], out var total))
                {
                    _successfulSeedUrls = successful;
                    _problemSeedUrls = problems;
                    _totalSeedUrls = total;
                    RefreshCounters();
                }
                return;
            }
            const string localProgressPrefix = "[CRAWL-LOCAL] PROGRESS | ";
            if (line.StartsWith(localProgressPrefix, StringComparison.Ordinal))
            {
                var values = line[localProgressPrefix.Length..].Split('|', StringSplitOptions.TrimEntries);
                if (values.Length == 2 && long.TryParse(values[0], out var processed) && long.TryParse(values[1], out var total))
                {
                    _localFilesProcessed = processed;
                    _localFilesTotal = total;
                    RefreshCounters();
                }
                return;
            }
            const string failedPrefix = "[CRAWL-STATE] FAILED | ";
            if (line.StartsWith(failedPrefix, StringComparison.Ordinal))
            {
                _startupFailureSummary = line[failedPrefix.Length..].Trim();
                SetStartupFailed(_startupFailureSummary);
                ActivityLog.Append(OutputText, "Crawler startup failed: " + _startupFailureSummary);
                return;
            }
            const string processedTypePrefix = "[CRAWL-TYPE] PROCESSED | ";
            if (line.StartsWith(processedTypePrefix, StringComparison.Ordinal))
            {
                var type = line[processedTypePrefix.Length..].Trim();
                if (type.Length > 0 && type.Length <= 16 && type.All(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_'))
                    _processedTypes[type] = _processedTypes.TryGetValue(type, out var count) ? count + 1 : 1;
                RefreshCounters();
                return;
            }
            if (!_crawlerReady && (line.Contains("Hosting failed to start", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("System.", StringComparison.Ordinal)))
                _startupFailureSummary ??= line.Trim();

            // Keep the dashboard readable. Complete stack traces remain in the dated
            // log file, while the activity view shows the error headline and cause.
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("at ", StringComparison.Ordinal) ||
                trimmed.StartsWith("--- End of stack trace", StringComparison.Ordinal)) return;

            ButchCrawler.App.OpenAiBilling.Observe(line);
            if (line.StartsWith(ButchCrawler.App.OpenAiBilling.LogPrefix, StringComparison.Ordinal))
            {
                BillingNotice.Visibility = Visibility.Visible;
                BillingNoticeText.Text = line[ButchCrawler.App.OpenAiBilling.LogPrefix.Length..];
            }
            ActivityLog.Append(OutputText, line);
            if (line.StartsWith("[CRAWL-URL]", StringComparison.Ordinal) || line.StartsWith("[CRAWL-DISCOVERY]", StringComparison.Ordinal))
            {
                ActivityLog.Append(UrlOutputText, line);
                if (line.StartsWith("[CRAWL-URL] START |", StringComparison.Ordinal))
                {
                    _urlStarts++;
                    var separator = line.LastIndexOf(" | ", StringComparison.Ordinal);
                    if (separator >= 0) CurrentUrlText.Text = "Current URL: " + line[(separator + 3)..];
                }
                if (line.StartsWith("[CRAWL-URL] HTTP ", StringComparison.Ordinal)) _responses++;
                if (line.StartsWith("[CRAWL-URL] RETRIEVED |", StringComparison.Ordinal)) _retrievedPages++;
                if (line.StartsWith("[CRAWL-URL] FAILURE ", StringComparison.Ordinal)) _failedAttempts++;
                if (line.StartsWith("[CRAWL-URL] FAILURE RETRY ", StringComparison.Ordinal)) _retryCount++;
                if (line.StartsWith("[CRAWL-URL] ERROR |", StringComparison.Ordinal)) _errors++;
                if (line.StartsWith("[CRAWL-URL] QUEUED ", StringComparison.Ordinal))
                {
                    var marker = "QUEUED ";
                    var i = line.IndexOf(marker, StringComparison.Ordinal);
                    if (i >= 0)
                    {
                        var end = line.IndexOf(' ', i + marker.Length);
                        if (end > i && int.TryParse(line.Substring(i + marker.Length, end - i - marker.Length), out var n))
                            _queuedLinks += n;
                    }
                }
            }
            if (line.StartsWith("[CRAWL-MATCH] ", StringComparison.Ordinal))
            {
                _matchEvents++;
                ActivityLog.Append(KeywordOutputText, line);
            }
            RefreshCounters();
        });
    }

    private void RefreshCounters()
    {
        UrlCountText.Text = $"{_urlStarts:N0}";
        CompletedCountText.Text = $"{_responses:N0}";
        RetrievedPagesText.Text = $"{_retrievedPages:N0}";
        LinkCountText.Text = $"{_queuedLinks:N0}";
        MatchCountText.Text = $"{_matchEvents:N0}";
        ErrorCountText.Text = $"{_errors:N0}";
        FailedCountText.Text = $"{_failedAttempts:N0}";
        RetryCountText.Text = $"{_retryCount:N0}";
        SeedProgressText.Text = _successfulSeedUrls.HasValue && _problemSeedUrls.HasValue && _totalSeedUrls.HasValue
            ? $"{_successfulSeedUrls + _problemSeedUrls:N0} of {_totalSeedUrls:N0} Processed"
            : "— of — Processed";
        SeedSuccessfulText.Text = _successfulSeedUrls.HasValue
            ? $"{_successfulSeedUrls:N0} Successful"
            : string.Empty;
        SeedAttentionText.Text = _problemSeedUrls.HasValue
            ? $"{_problemSeedUrls:N0} Needs Attention"
            : string.Empty;
        LocalFileProgressText.Text = _localFilesProcessed.HasValue && _localFilesTotal.HasValue
            ? $"Local files {_localFilesProcessed:N0} of {_localFilesTotal:N0}"
            : "Local files — of —";
        DiscoveredUrlsText.Text = _discoveredUrlsTotal.HasValue
            ? $"Discovered URLs {_discoveredUrlsTotal:N0}"
            : "Discovered URLs —";
        var preferredOrder = new[] { "HTML", "PDF", "DOCX", "MHT", "JPEG", "PNG", "TIFF", "WEBP", "MDB", "ACCDB", "DBF", "SQLITE", "CSV", "TSV", "XLSX", "XLS", "JSON", "JSONL", "XML", "SQL", "ZIP", "AUDIO", "TXT", "OTHER" };
        var order = preferredOrder.Select((name, index) => (name, index)).ToDictionary(item => item.name, item => item.index, StringComparer.OrdinalIgnoreCase);
        var typeSummary = string.Join("  •  ", _processedTypes
            .OrderBy(item => order.TryGetValue(item.Key, out var index) ? index : int.MaxValue)
            .ThenBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
            .Select(item => $"{item.Key} {item.Value:N0}"));
        ProcessedTypesText.Text = typeSummary.Length == 0 ? "None yet" : typeSummary;
        ProcessedTypesText.ToolTip = typeSummary.Length == 0 ? "No files have completed processing during this run." : typeSummary;
    }

    private async void Stop_Click(object sender, RoutedEventArgs e)
    {
        if (_exiting) return;
        StartButton.IsEnabled = StopButton.IsEnabled = false;
        StatusText.Text = "Stopping crawler…";
        try { await StopCrawlerAsync(); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Could not stop crawler"); }
        finally { if (!_exiting) SetRunning(_process is { HasExited: false }); }
    }

    private Task StopCrawlerAsync()
    {
        if (_stopTask is { IsCompleted: false }) return _stopTask;
        return _stopTask = StopOwnedProcessAsync();
    }

    private async Task StopOwnedProcessAsync()
    {
        var process = _process;
        if (process is null) return;
        if (!process.HasExited)
        {
            if (process.StartInfo.RedirectStandardInput)
            {
                try
                {
                    await process.StandardInput.WriteLineAsync("stop");
                    await process.StandardInput.FlushAsync();
                    using var grace = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                    await process.WaitForExitAsync(grace.Token);
                }
                catch (Exception ex) when (ex is OperationCanceledException or IOException or InvalidOperationException) { }
            }
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) when (process.HasExited) { }
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await process.WaitForExitAsync(timeout.Token);
            }
        }
        process.Dispose();
        if (ReferenceEquals(_process, process)) _process = null;
    }

    private async void Config_Click(object sender, RoutedEventArgs e)
    {
        var path = Path.Combine(_appDir, "appsettings.json");
        try
        {
            new SettingsWindow(path, () => _process is { HasExited: false }) { Owner = this }.ShowDialog();
            await RefreshSavedSeedProgressAsync();
        }
        catch { MessageBox.Show(this, "Could not open settings. Check that appsettings.json exists and contains valid JSON.", "Settings"); }
    }

    private void About_Click(object sender, RoutedEventArgs e) => new AboutWindow(Path.Combine(_appDir, "appsettings.json")) { Owner = this }.ShowDialog();

    private void Billing_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(ButchCrawler.App.OpenAiBilling.BillingUrl) { UseShellExecute = true }); }
        catch { MessageBox.Show(this, "Open your API billing page:\n" + ButchCrawler.App.OpenAiBilling.BillingUrl, "OpenAI billing"); }
    }

    private void CollectionSearch_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var path = Path.Combine(_appDir, "appsettings.json");
            new CollectionSearchWindow(new SqlAiResearchWorkspace(path), () => new SqlAiResearchWorkspace(path)) { Owner = this }.ShowDialog();
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Search collection"); }
    }

    private void OpenAiKey_Click(object sender, RoutedEventArgs e)
    {
        try { new OpenAiKeyWindow(new ButchCrawler.App.OpenAiKeyStore(Path.Combine(_appDir, "appsettings.json"))) { Owner = this }.ShowDialog(); }
        catch { MessageBox.Show(this, "Could not read the saved OpenAI key. Check access to your Windows user-data folder.", "OpenAI key"); }
    }

    private void Logs_Click(object sender, RoutedEventArgs e)
    {
        var path = Path.Combine(_appDir, "logs");
        new LogWindow(path, () => _process is { HasExited: false }) { Owner = this }.ShowDialog();
    }


    private void SeedUrls_Click(object sender, RoutedEventArgs e) => OpenSeeds(false);
    private void HistoricalNewspapers_Click(object sender, RoutedEventArgs e)
    {
        if (_process is { HasExited: false }) { MessageBox.Show(this, "Stop the crawler before changing historical newspaper searches."); return; }
        try { new HistoricalNewspaperSettingsWindow(Path.Combine(_appDir, "appsettings.json")) { Owner = this }.ShowDialog(); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Historical newspaper archives"); }
    }
    private void ProblemSeeds_Click(object sender, RoutedEventArgs e)
    {
        if (_process is { HasExited: false }) { MessageBox.Show(this, "Stop the crawler before reviewing problem seeds."); return; }
        try
        {
            var environment = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") ?? "Production";
            var config = new ConfigurationBuilder().SetBasePath(_appDir)
                .AddJsonFile("appsettings.json", optional: false)
                .AddJsonFile($"appsettings.{environment}.json", optional: true)
                .AddEnvironmentVariables().Build();
            try
            {
                var connection = config.GetConnectionString("DefaultConnection")
                    ?? throw new InvalidOperationException("Missing DefaultConnection connection string.");
                var problemSeeds = new ProblemSeedsWindow(connection, config.GetValue<string>("Crawler:UserAgent")) { Owner = this };
                problemSeeds.ShowDialog();
                if (problemSeeds.StartCrawlerRequested) Start_Click(this, new RoutedEventArgs());
            }
            finally { (config as IDisposable)?.Dispose(); }
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Problem seeds"); }
    }
    private void Keywords_Click(object sender, RoutedEventArgs e) => OpenSeeds(true);

    private void OpenSeeds(bool keywords)
    {
        if (_process is { HasExited: false }) { MessageBox.Show(this, "Stop the crawler before editing seeds."); return; }
        try
        {
            var environment = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") ?? "Production";
            var config = new ConfigurationBuilder().SetBasePath(_appDir)
                .AddJsonFile("appsettings.json", optional: false)
                .AddJsonFile($"appsettings.{environment}.json", optional: true)
                .AddEnvironmentVariables().Build();
            try
            {
                var connection = config.GetConnectionString("DefaultConnection")
                    ?? throw new InvalidOperationException("Missing DefaultConnection connection string.");
                new SeedEditorWindow(connection, keywords) { Owner = this }.ShowDialog();
            }
            finally { (config as IDisposable)?.Dispose(); }
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Seed editor error"); }
    }
}
