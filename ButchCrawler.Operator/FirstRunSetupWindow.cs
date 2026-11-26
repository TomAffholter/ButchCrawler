using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Data.SqlClient;
using Microsoft.Win32;

namespace ButchCrawler.Operator;

public sealed class FirstRunSetupWindow : Window
{
    private const string FirstRunFlag = "HistoryCrawler.FirstRun";
    private readonly string _settingsPath;
    private readonly StackPanel _page = new();
    private readonly TextBlock _step = new() { Foreground = new SolidColorBrush(Color.FromRgb(92, 108, 130)), FontWeight = FontWeights.SemiBold };
    private readonly TextBlock _title = new() { FontSize = 28, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 5, 0, 10) };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
    private readonly Button _back = new() { Content = "Back", Padding = new Thickness(15, 9, 15, 9), IsEnabled = false };
    private readonly Button _next = new() { Content = "Next", Padding = new Thickness(15, 9, 15, 9), Margin = new Thickness(10, 0, 0, 0) };
    private readonly TextBox _researchFolder = new() { MinWidth = 530 };
    private readonly TextBox _researchTopics = new() { AcceptsReturn = true, Height = 80, TextWrapping = TextWrapping.Wrap };
    private readonly ComboBox _region = new() { MinWidth = 240 };
    private readonly CheckBox _scanLocalFiles = new() { Content = "Include files in my research folder when I start the crawler", IsChecked = false };
    private readonly CheckBox _newspapers = new() { Content = "Search free Library of Congress historical newspapers", IsChecked = true };
    private readonly CheckBox _safeDefaults = new() { Content = "Use the recommended polite and secure crawl settings", IsChecked = true };
    private readonly List<(string Name, string Value, bool Ready)> _checks = [];
    private int _pageIndex;
    private bool _saving;

    public FirstRunSetupWindow(string settingsPath)
    {
        _settingsPath = Path.GetFullPath(settingsPath);
        AppTheme.Apply(this);
        Title = "Welcome to History Crawler";
        Width = 820;
        Height = 620;
        MinWidth = 700;
        MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        _researchFolder.Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "History Research");
        foreach (var item in new[] { "Southwest", "Northwest", "Midwest", "Southeast", "Northeast", "National / all regions", "Outside the United States" })
            _region.Items.Add(item);
        _region.SelectedIndex = 5;

        var outer = new DockPanel { Margin = new Thickness(24) };
        var header = new StackPanel();
        header.Children.Add(_step);
        header.Children.Add(_title);
        DockPanel.SetDock(header, Dock.Top);
        outer.Children.Add(header);

        var footer = new DockPanel { Margin = new Thickness(0, 18, 0, 0) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(_back);
        _next.SetResourceReference(StyleProperty, "PrimaryButton");
        buttons.Children.Add(_next);
        footer.Children.Add(buttons);
        footer.Children.Add(_status);
        DockPanel.SetDock(footer, Dock.Bottom);
        outer.Children.Add(footer);
        outer.Children.Add(new ScrollViewer { Content = _page, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Content = outer;

        _back.Click += (_, _) => { if (_pageIndex > 0) { _pageIndex--; ShowPage(); } };
        _next.Click += async (_, _) =>
        {
            if (_pageIndex < 4) { _pageIndex++; ShowPage(); }
            else await FinishAsync();
        };
        Closing += (_, e) => { if (_saving) e.Cancel = true; };
        ShowPage();
    }

    public static bool ShouldShowAutomatically()
    {
        if (Environment.GetCommandLineArgs().Any(value => value.Equals("--first-run", StringComparison.OrdinalIgnoreCase))) return true;
        var flag = Path.Combine(AppContext.BaseDirectory, FirstRunFlag);
        return File.Exists(flag) && !File.Exists(CompletionMarkerPath);
    }

    public static string CompletionMarkerPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "HistoryCrawler", "first-run-complete.json");

    private void ShowPage()
    {
        _page.Children.Clear();
        _step.Text = $"Step {_pageIndex + 1} of 5";
        _back.IsEnabled = _pageIndex > 0 && !_saving;
        _next.Content = _pageIndex == 4 ? "Save and finish" : "Next";
        _status.Text = string.Empty;
        switch (_pageIndex)
        {
            case 0: ShowWelcome(); break;
            case 1: ShowResearchChoices(); break;
            case 2: ShowFolderChoices(); break;
            case 3: _ = ShowSystemChecksAsync(); break;
            default: ShowReady(); break;
        }
    }

    private void ShowWelcome()
    {
        _title.Text = "Welcome to History Crawler";
        AddParagraph("This short guide prepares the program for your computer. It will not start a crawl, spend AI credits, or change your research database.");
        AddCard("What happens here", "Choose a research folder, select a few basic preferences, and check the optional document-reading tools installed on this computer.");
        AddCard("AI is optional", "AI, audio transcription, and semantic indexing start turned off. You can use the crawler and collection search without an OpenAI key.");
        AddCard("You stay in control", "The crawler runs only after you choose Start crawler on the main screen.");
    }

    private void ShowResearchChoices()
    {
        _title.Text = "Tell History Crawler about your research";
        AddParagraph("These notes make the setup easier to remember. They do not restrict what you can search later.");
        _page.Children.Add(new TextBlock { Text = "Primary research region", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 4) });
        _page.Children.Add(_region);
        _page.Children.Add(new TextBlock { Text = "Topics, people, places, or time periods", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 16, 0, 4) });
        _page.Children.Add(_researchTopics);
        _page.Children.Add(_newspapers);
    }

    private void ShowFolderChoices()
    {
        _title.Text = "Choose your research folder";
        AddParagraph("History Crawler can read supported files you already have, including PDF, Word, images, saved web pages, spreadsheets, and structured data. Local scanning remains off unless you select it below.");
        var row = new DockPanel { Margin = new Thickness(0, 10, 0, 8) };
        var browse = new Button { Content = "Browse…", Padding = new Thickness(11, 6, 11, 6) };
        DockPanel.SetDock(browse, Dock.Right);
        row.Children.Add(browse);
        row.Children.Add(_researchFolder);
        browse.Click += (_, _) =>
        {
            var dialog = new OpenFolderDialog { Title = "Choose your History Crawler research folder", InitialDirectory = _researchFolder.Text };
            if (dialog.ShowDialog(this) == true) _researchFolder.Text = dialog.FolderName;
        };
        _page.Children.Add(row);
        _page.Children.Add(_scanLocalFiles);
        _page.Children.Add(_safeDefaults);
        AddCard("Safe file handling", "Downloaded and local files are treated as data. The crawler limits file sizes and archive expansion, does not execute SQL files, and does not automatically launch collected files.");
    }

    private async Task ShowSystemChecksAsync()
    {
        _title.Text = "Checking this computer";
        AddParagraph("Required items are shown first. Optional tools improve OCR, scanned PDFs, images, and audio, but the crawler can run without them.");
        _checks.Clear();
        _checks.Add(("Windows", OperatingSystem.IsWindows() ? "Ready" : "Windows is required", OperatingSystem.IsWindows()));

        JsonObject root;
        try { root = JsonNode.Parse(await File.ReadAllTextAsync(_settingsPath))?.AsObject() ?? new JsonObject(); }
        catch { root = new JsonObject(); }
        var connection = root["ConnectionStrings"]?["DefaultConnection"]?.GetValue<string>();
        var databaseReady = false;
        var databaseText = "Not configured";
        if (!string.IsNullOrWhiteSpace(connection))
        {
            var installer = new StarterDatabaseInstaller(connection, BundledStarterBackupPath);
            var databaseStatus = await installer.CheckAsync();
            databaseReady = databaseStatus.Ready || databaseStatus.CanInstall;
            databaseText = databaseStatus.Message;
        }
        _checks.Add(("Microsoft SQL database", databaseText, databaseReady));
        AddFileCheck(root, "Tesseract OCR", "Tesseract", "Path");
        AddFileCheck(root, "ImageMagick", "ImageMagick", "Path");
        AddFileCheck(root, "Ghostscript", "Ghostscript", "Path");
        AddFileCheck(root, "FFmpeg", "Audio", "FFmpegPath");

        foreach (var check in _checks)
        {
            var border = new Border
            {
                BorderBrush = new SolidColorBrush(check.Ready ? Color.FromRgb(168, 216, 191) : Color.FromRgb(213, 154, 53)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(10),
                Margin = new Thickness(0, 0, 0, 7)
            };
            var row = new DockPanel();
            var state = new TextBlock
            {
                Text = check.Ready ? "Ready" : "Optional / attention",
                Foreground = new SolidColorBrush(check.Ready ? Color.FromRgb(19, 122, 67) : Color.FromRgb(166, 83, 6)),
                HorizontalAlignment = HorizontalAlignment.Right
            };
            DockPanel.SetDock(state, Dock.Right);
            row.Children.Add(state);
            row.Children.Add(new TextBlock { Text = check.Name + " — " + check.Value, TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.SemiBold });
            border.Child = row;
            _page.Children.Add(border);
        }
    }

    private void ShowReady()
    {
        _title.Text = "Ready to use History Crawler";
        AddParagraph("Choose Save and finish to keep these preferences. The main window will open with the crawler stopped.");
        AddCard("Research folder", _researchFolder.Text);
        AddCard("Local files", _scanLocalFiles.IsChecked == true ? "Included when you start the crawler" : "Off — you can enable this later in Settings");
        AddCard("Historical newspapers", _newspapers.IsChecked == true ? "Free Library of Congress searches enabled" : "Off");
        AddCard("AI and paid features", "Off by default");
        AddCard("First things to try", "Review Seed URLs and Seed Keywords under Settings, then choose Start crawler when you are ready.");
    }

    private async Task FinishAsync()
    {
        _saving = true;
        _back.IsEnabled = _next.IsEnabled = false;
        _status.Text = "Saving your setup…";
        try
        {
            var folder = Path.GetFullPath(_researchFolder.Text.Trim());
            Directory.CreateDirectory(folder);
            var root = JsonNode.Parse(await File.ReadAllTextAsync(_settingsPath))?.AsObject()
                ?? throw new InvalidDataException("appsettings.json is invalid.");
            Set(root, "AI", "Enabled", false);
            Set(root, "Audio", "Enabled", false);
            Set(root, "Indexer", "Enabled", false);
            Set(root, "LocalCrawler", "RootPath", folder);
            Set(root, "LocalCrawler", "Enabled", _scanLocalFiles.IsChecked == true);
            Set(root, "HistoricalNewspapers", "Enabled", _newspapers.IsChecked == true);
            if (_safeDefaults.IsChecked == true)
            {
                Set(root, "Crawler", "RobotsFailClosed", true);
                Set(root, "Crawler", "PerHostConcurrency", 1);
                Set(root, "Crawler", "MaxConcurrencyPerHost", 1);
                Set(root, "Crawler", "DefaultCrawlDelaySeconds", 2);
                Set(root, "Crawler", "MaxConnectionsPerServer", 8);
            }
            await AtomicWriteAsync(_settingsPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

            var connection = root["ConnectionStrings"]?["DefaultConnection"]?.GetValue<string>()
                ?? throw new InvalidDataException("The database connection is missing from appsettings.json.");
            var databaseProgress = new Progress<string>(message => _status.Text = message);
            await new StarterDatabaseInstaller(connection, BundledStarterBackupPath)
                .InstallIfMissingAsync(databaseProgress);

            var marker = CompletionMarkerPath;
            Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
            var profile = new
            {
                completedAtUtc = DateTime.UtcNow,
                researchRegion = _region.SelectedItem?.ToString(),
                researchTopics = _researchTopics.Text.Trim(),
                researchFolder = folder,
                localFilesEnabled = _scanLocalFiles.IsChecked == true,
                historicalNewspapersEnabled = _newspapers.IsChecked == true,
                aiEnabled = false
            };
            await AtomicWriteAsync(marker, JsonSerializer.Serialize(profile, new JsonSerializerOptions { WriteIndented = true }));
            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            _saving = false;
            _next.IsEnabled = true;
            _back.IsEnabled = true;
            _status.Text = "Setup was not saved. " + ex.Message;
        }
    }

    private static void Set(JsonObject root, string sectionName, string name, JsonNode? value)
    {
        var section = root[sectionName] as JsonObject ?? new JsonObject();
        root[sectionName] = section;
        section[name] = value;
    }

    private static string BundledStarterBackupPath => Path.Combine(AppContext.BaseDirectory, "Database", "HistoryCrawlerStarter.bak");

    private static async Task AtomicWriteAsync(string path, string contents)
    {
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await File.WriteAllTextAsync(temporary, contents);
            File.Move(temporary, path, true);
        }
        finally { try { if (File.Exists(temporary)) File.Delete(temporary); } catch { } }
    }

    private void AddFileCheck(JsonObject root, string name, string section, string property)
    {
        var path = root[section]?[property]?.GetValue<string>();
        var ready = !string.IsNullOrWhiteSpace(path) && File.Exists(path);
        _checks.Add((name, ready ? "Ready" : "Not found — optional", ready));
    }

    private void AddParagraph(string text) => _page.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 15, Margin = new Thickness(0, 0, 0, 12) });

    private void AddCard(string heading, string text)
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = heading, FontWeight = FontWeights.SemiBold, FontSize = 16 });
        panel.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0) });
        _page.Children.Add(new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(238, 245, 251)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(169, 197, 223)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(12),
            Margin = new Thickness(0, 0, 0, 9),
            Child = panel
        });
    }
}
