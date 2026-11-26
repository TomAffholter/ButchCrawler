using System.Data;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ButchCrawler.Operator;

internal static class UiPreview
{
    public static void Run(string destination)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                Directory.CreateDirectory(destination);
                var main = new MainWindow();
                ((TextBlock)typeof(MainWindow).GetField("DiscoveredUrlsText", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(main)!).Text = "Discovered URLs 64,336";
                ((TextBlock)typeof(MainWindow).GetField("ProcessedTypesText", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(main)!).Text = "HTML 1,474  •  PDF 286  •  JPEG 93  •  MDB 12  •  CSV 48  •  JSON 31";
                var activity = (RichTextBox)typeof(MainWindow).GetField("UrlOutputText", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(main)!;
                ActivityLog.Clear(activity);
                foreach (var line in new[]
                {
                    "[CRAWL-URL] START | Depth=0 | https://archive.example.org/records",
                    "[CRAWL-URL] RETRIEVED | HTTP 200 | https://archive.example.org/records",
                    "[CRAWL-URL] QUEUED 24 | Depth=1 | https://archive.example.org/records",
                    "[CRAWL-URL] COMPLETE | HTML processing finished | https://archive.example.org/records",
                    "[CRAWL-URL] FAILURE RETRY | HTTP=503 | Next attempt in 60s | https://library.example.org/catalog",
                    "[CRAWL-URL] FAILURE DEAD-LETTER | HTTP=404 | No automatic retries remaining | https://archive.example.org/missing",
                    "[CRAWL-URL] BLOCKED | Robots rules | https://archive.example.org/private",
                    "[CRAWL-URL] CHALLENGE | Human verification required | https://library.example.org",
                    "[CRAWL-URL] SKIPPED | Already visited | https://archive.example.org/records"
                }) ActivityLog.Append(activity, line);
                Render(main, Path.Combine(destination, "operator.png"));
                main.Width = main.MinWidth; main.Height = main.MinHeight;
                Render(main, Path.Combine(destination, "operator-compact.png"));
                typeof(MainWindow).GetMethod("SetStarting",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(main,null);
                Render(main,Path.Combine(destination,"operator-starting.png"));
                typeof(MainWindow).GetMethod("SetRunning",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(main,new object[]{true});
                Render(main,Path.Combine(destination,"operator-running.png"));
                typeof(MainWindow).GetMethod("SetStartupFailed",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(main,new object?[]{"Missing required startup component."});
                Render(main,Path.Combine(destination,"operator-start-failed.png"));
                typeof(MainWindow).GetMethod("SetRunning",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(main,new object[]{false});
                var collection = new CollectionSearchWindow(new PreviewAiWorkspace(), () => new PreviewAiWorkspace());
                var collectionResults = (ListBox)typeof(CollectionSearchWindow).GetField("_results", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(collection)!;
                collectionResults.ItemsSource = new[]
                {
                    new AiResearchDocument("crawl", "https://archive.example.org/diary", "Mary traveled through Helena in the spring of 1880."),
                    new AiResearchDocument("document", @"D:\HistoryResearch\railroad-records.pdf", "Northern Pacific construction records and employee names."),
                    new AiResearchDocument("audio", "https://archive.example.org/interview.mp3", "Oral history interview mentioning the family homestead.")
                };
                ((TextBox)typeof(CollectionSearchWindow).GetField("_text", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(collection)!).Text =
                    "Mary traveled through Helena in the spring of 1880. This is saved source text and ordinary collection search uses no OpenAI credits.";
                ((TextBlock)typeof(CollectionSearchWindow).GetField("_source", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(collection)!).Text = "https://archive.example.org/diary";
                Render(collection, Path.Combine(destination, "collection-search.png"));
                typeof(CollectionSearchWindow).GetMethod("SetBusy", BindingFlags.NonPublic | BindingFlags.Instance)!
                    .Invoke(collection, new object[] { true, "Searching your saved collection…" });
                Render(collection, Path.Combine(destination, "collection-search-busy.png"));
                Render(new CollectionSearchHelpWindow(), Path.Combine(destination, "collection-search-help.png"));
                var ai = new AiResearchWindow(new PreviewAiWorkspace());
                var aiTabs = (TabControl)typeof(AiResearchWindow).GetField("_tabs", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(ai)!;
                foreach (var index in Enumerable.Range(0, 5))
                {
                    aiTabs.SelectedIndex = index;
                    Render(ai, Path.Combine(destination, $"ai-{index}.png"));
                }
                typeof(AiResearchWindow).GetMethod("ShowResult",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(ai,new object[] {
                    new AiResearchResult("1","Collection answer","Mary visited Helena in 1880.\n\nAI-generated. Verify against the original sources.","gpt-4.1-mini","Saved separately from the original source.",DateTime.UtcNow,
                        new[] { new AiResearchSource("S1","https://archive.example.org/diary","Mary visited Helena in 1880.") }) });
                Render(ai,Path.Combine(destination,"ai-result.png"));
                ai.Width=ai.MinWidth; ai.Height=ai.MinHeight;
                Render(ai,Path.Combine(destination,"ai-result-compact.png"));
                var about = new AboutWindow();
                foreach (var (field, value) in new[] { ("SeedKeywordCountText", "75"), ("SeedUrlCountText", "78,057"), ("DiscoveredUrlCountText", "1,234"), ("CountsStatusText", "Database totals · sample preview values") })
                    ((TextBlock)typeof(AboutWindow).GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(about)!).Text = value;
                Render(about, Path.Combine(destination, "about.png"));
                ((TextBlock)typeof(AboutWindow).GetField("AiBillingStatusText",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(about)!).Text = "Last API billing notice: OpenAI API credits have run out. Add credits in API billing. This request will not be retried automatically.\nObserved just now";
                Render(about,Path.Combine(destination,"about-billing-warning.png"));
                Render(new DonationWindow(null), Path.Combine(destination, "support-donation.png"));
                Render(new SupportContactWindow(), Path.Combine(destination, "support-contact.png"));
                ((Border)typeof(MainWindow).GetField("BillingNotice",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(main)!).Visibility=Visibility.Visible;
                ((TextBlock)typeof(MainWindow).GetField("BillingNoticeText",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(main)!).Text="OpenAI API credits have run out. Add credits in API billing. This request will not be retried automatically.";
                Render(main,Path.Combine(destination,"operator-billing-warning.png"));
                var key = new OpenAiKeyWindow(new ButchCrawler.App.OpenAiKeyStore(Path.Combine(destination, "preview-settings.json"),
                    legacyPath: string.Empty, keyPath: Path.Combine(destination, "preview-key.dpapi"))) { Height = 410 };
                Render(key, Path.Combine(destination, "openai-key.png"));
                Render(new AiFeatureSetupWindow { Height = 500 }, Path.Combine(destination, "optional-ai-setup.png"));
                var settingsPath = Path.Combine(destination, "settings-preview.json");
                File.WriteAllText(settingsPath, "{\"Crawler\":{\"MaxPages\":1000,\"MaxDepth\":2,\"AllowPdf\":false,\"UserAgent\":\"ButchCrawler/1.0\"},\"LocalCrawler\":{\"RootPath\":\"D:\\\\HistoryResearch\",\"Extensions\":[\".pdf\",\".docx\",\".txt\"]},\"Indexer\":{\"Enabled\":false,\"MaxConcurrency\":1},\"OpenAI\":{\"EncryptedApiKey\":\"hidden-preview-value\"}}");
                Render(new SettingsWindow(settingsPath), Path.Combine(destination, "settings.png"));
                var problemSeeds = new ProblemSeedsWindow("Server=(localdb)\\MSSQLLocalDB;Database=PreviewOnly;Trusted_Connection=True;");
                var problemTable = new DataTable();
                foreach (var column in new[] { "Id", "Url", "Seed URL", "Active", "Status", "Failures", "HTTP", "Last failure", "Next check", "Reason" })
                    problemTable.Columns.Add(column);
                problemTable.Rows.Add("1", "https://archive.example.org/missing", "https://archive.example.org/missing", "True", "gone", "2", "410", "11/12/2026", "", "The website reports that this page is gone.");
                ((DataGrid)typeof(ProblemSeedsWindow).GetField("_grid", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(problemSeeds)!).ItemsSource = problemTable.DefaultView;
                ((TextBlock)typeof(ProblemSeedsWindow).GetField("_busyText", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(problemSeeds)!).Text = "Retrying URL and reloading problem URLs…";
                ((Border)typeof(ProblemSeedsWindow).GetField("_busyPanel", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(problemSeeds)!).Visibility = Visibility.Visible;
                Render(problemSeeds, Path.Combine(destination, "problem-seeds-loading.png"));
                var reset = new DatabaseResetWindow(new DatabaseResetService(""), () => false);
                ((DockPanel)reset.Content).Children.OfType<DataGrid>().Single().ItemsSource = new[]
                { new ResetTable("dbo.CrawlQueue", 1200, false), new ResetTable("dbo.CrawlResults", 500, false),
                  new ResetTable("dbo.SchemaVersion", 11, true), new ResetTable("dbo.SeedKeywords", 75, true), new ResetTable("dbo.SeedUrls", 78057, true) };
                Render(reset, Path.Combine(destination, "reset-keep-seeds.png"));
                var resetKeywords = new DatabaseResetWindow(new DatabaseResetService(""), () => false);
                ((RadioButton)typeof(DatabaseResetWindow).GetField("_clearKeywords", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(resetKeywords)!).IsChecked = true;
                ((DockPanel)resetKeywords.Content).Children.OfType<DataGrid>().Single().ItemsSource = new[]
                { new ResetTable("dbo.CrawlQueue", 1200, false), new ResetTable("dbo.CrawlResults", 500, false),
                  new ResetTable("dbo.SchemaVersion", 11, true), new ResetTable("dbo.SeedKeywords", 75, false), new ResetTable("dbo.SeedUrls", 78057, true) };
                Render(resetKeywords, Path.Combine(destination, "reset-clear-keywords.png"));
                foreach (var keywords in new[] { false, true })
                {
                    var editor = new SeedEditorWindow("", keywords);
                    var table = new DataTable(); table.Columns.Add("Id", typeof(int));
                    table.Columns.Add(keywords ? "Keyword" : "Url", typeof(string));
                    if (!keywords) { table.Columns.Add("MaxDepth", typeof(int)); table.Columns.Add("Priority", typeof(int)); }
                    table.Columns.Add("IsActive", typeof(bool));
                    var row = table.NewRow(); row["Id"] = 1; row[keywords ? "Keyword" : "Url"] = keywords ? "public records" : "https://example.com/documents/";
                    if (!keywords) { row["MaxDepth"] = 2; row["Priority"] = 10; } row["IsActive"] = true; table.Rows.Add(row);
                    var grid = (DataGrid)typeof(SeedEditorWindow).GetField("_grid", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(editor)!;
                    grid.ItemsSource = table.DefaultView;
                    Render(editor, Path.Combine(destination, keywords ? "keywords.png" : "seeds.png"));
                }
                var logs = new LogWindow(destination, () => false);
                var text = (TextBox)typeof(LogWindow).GetField("_text", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(logs)!;
                if (!text.IsReadOnly || text.VerticalScrollBarVisibility != ScrollBarVisibility.Auto || text.HorizontalScrollBarVisibility != ScrollBarVisibility.Auto)
                    throw new Exception("Log viewer must be read-only and scrollable");
                text.Text = string.Join(Environment.NewLine, Enumerable.Range(1, 100).Select(i => $"2026-10-24 10:30:{i % 60:00} [INF] Crawl event {i}: https://example.com/documents/{i}"));
                Render(logs, Path.Combine(destination, "logs.png"));
                Console.WriteLine("PASS: WPF screen previews include free collection search and optional AI setup; log view is read-only with both scrollbars");
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (failure != null) throw failure;
    }
    private static void Render(Window window, string path)
    {
        var content = (FrameworkElement)window.Content;
        if (content is Panel panel) panel.Background = window.Background;
        var width = window.Width - 32; var height = window.Height - 50;
        content.Measure(new Size(width, height));
        content.Arrange(new Rect(0, 0, width, height)); content.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)width, (int)height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(content); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path); encoder.Save(stream);
    }

    private sealed class PreviewAiWorkspace : IAiResearchWorkspace
    {
        public string LimitsDescription => "Up to 20 requests per UTC day; bounded source text and response length.";
        public void Dispose() { }
        public Task<IReadOnlyList<AiResearchDocument>> SearchAsync(string query, CancellationToken ct) => Task.FromResult<IReadOnlyList<AiResearchDocument>>(Array.Empty<AiResearchDocument>());
        public Task<string> ReadDocumentAsync(AiResearchDocument document, CancellationToken ct) => Task.FromResult("");
        public Task<AiResearchResult> RunAsync(AiResearchRequest request, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<AiResearchResult>> HistoryAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<AiResearchResult>>(Array.Empty<AiResearchResult>());
    }
}
