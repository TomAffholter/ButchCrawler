using System.Reflection;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using ButchCrawler.App;
using ButchCrawler.Operator;

internal static class ActivityTests
{
    public static void Run(Action<bool, string> check)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var examples = new (string Line, string Category)[]
                {
                    ("[CRAWL-URL] START | example", "Fetching"),
                    ("[CRAWL-URL] RETRIEVED | example", "Retrieved"),
                    ("[CRAWL-URL] COMPLETE | example", "Completed"),
                    ("[CRAWL-URL] FAILURE RETRY | HTTP=503 | example", "Retry scheduled"),
                    ("[CRAWL-URL] FAILURE DEAD-LETTER | HTTP=404 | example", "Dead-letter"),
                    ("[CRAWL-URL] BLOCKED | robots | example", "Blocked"),
                    ("[CRAWL-URL] SKIPPED | visited | example", "Skipped"),
                    ("[CRAWL-URL] CHALLENGE | CAPTCHA | example", "CAPTCHA"),
                    ("[CRAWL-URL] ERROR | SQL failure | example", "Processing error"),
                    ("[CRAWL-MATCH] public records (2) | example", "Keyword match"),
                    ("[CRAWL-URL] HTTP 200 | example", "HTTP success"),
                    ("[CRAWL-URL] HTTP 301 | example", "Redirect"),
                    ("[CRAWL-URL] REDIRECT | HTTP=302 | source -> target", "Redirect"),
                    ("[CRAWL-URL] HTTP 404 | example", "HTTP failure"),
                    ("[CRAWL-URL] HTTP 429 | example", "Temporary HTTP failure"),
                    ("[CRAWL-DISCOVERY] Extracted=3 | example", "Discovery")
                };
                foreach (var (line, category) in examples)
                    check(ActivityLog.Classify(line).Category == category, "activity classification: " + category);
                var box = new RichTextBox { IsReadOnly = true };
                ActivityLog.Clear(box);
                ActivityLog.Append(box, examples[1].Line);
                var run = ((Paragraph)box.Document.Blocks.FirstBlock).Inlines.FirstInline as Run;
                check(((SolidColorBrush)run!.Foreground).Color.ToString() == "#FF137A43", "retrieved line renders in green");
                for (var i = 0; i < 900; i++) ActivityLog.Append(box, "line " + i + new string('x', 250));
                check(box.Document.Blocks.Count <= 800 && (int)box.Tag <= 180000 && new TextRange(box.Document.ContentStart, box.Document.ContentEnd).Text.Contains("line 899"),
                    "colored activity log retains latest entries within display limits");
                ActivityLog.Clear(box); check(box.Document.Blocks.Count == 0 && (int)box.Tag == 0, "new-run activity clear resets display bounds");
                var window = new MainWindow();
                check(window.WindowStartupLocation == System.Windows.WindowStartupLocation.CenterScreen, "Operator opens centered horizontally and vertically on screen");
                var setStarting = typeof(MainWindow).GetMethod("SetStarting", BindingFlags.NonPublic | BindingFlags.Instance)!;
                var setStartupFailed = typeof(MainWindow).GetMethod("SetStartupFailed", BindingFlags.NonPublic | BindingFlags.Instance)!;
                var status = (TextBlock)typeof(MainWindow).GetField("StatusText", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
                var start = (Button)typeof(MainWindow).GetField("StartButton", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
                var stop = (Button)typeof(MainWindow).GetField("StopButton", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
                setStarting.Invoke(window, null);
                check(status.Text == "Starting crawler…" && !start.IsEnabled && stop.IsEnabled,
                    "dashboard shows a distinct starting state");
                setStartupFailed.Invoke(window, new object?[] { "test startup failure" });
                check(status.Text == "Crawler could not start" && start.IsEnabled && !stop.IsEnabled,
                    "dashboard exposes startup failure and restores controls");
                var append = typeof(MainWindow).GetMethod("Append", BindingFlags.NonPublic | BindingFlags.Instance)!;
                append.Invoke(window, new object[] { "[CRAWL-STATE] FAILED | Database unavailable." });
                var failedFrame = new DispatcherFrame();
                Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => failedFrame.Continue = false));
                Dispatcher.PushFrame(failedFrame);
                check(status.Text == "Crawler could not start" && Equals(((Border)status.Parent).ToolTip, "Database unavailable."),
                    "crawler failure signal updates the status message and explanation");
                append.Invoke(window, new object[] { "[CRAWL-STATE] RUNNING" });
                foreach (var line in new[] { examples[0].Line, examples[1].Line, examples[3].Line, examples[4].Line, examples[8].Line, "[CRAWL-URL] SKIPPED | https://example.test/[CRAWL-MATCH] fake" })
                    append.Invoke(window, new object[] { line });
                var frame = new DispatcherFrame();
                Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
                Dispatcher.PushFrame(frame);
                check(status.Text == "Crawler Running" && !start.IsEnabled && stop.IsEnabled,
                    "ready signal updates dashboard to running");
                int Count(string name) => (int)typeof(MainWindow).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
                check(Count("_retrievedPages") == 1 && Count("_failedAttempts") == 2 && Count("_retryCount") == 1 && Count("_errors") == 1,
                    "dashboard counts retrievals, failed attempts, scheduled retries and processing errors separately");
                check(Count("_matchEvents") == 0, "URL text cannot spoof a keyword match event");
                append.Invoke(window, new object[] { "[CRAWL-TYPE] PROCESSED | HTML" });
                append.Invoke(window, new object[] { "[CRAWL-TYPE] PROCESSED | PDF" });
                append.Invoke(window, new object[] { "[CRAWL-TYPE] PROCESSED | MDB" });
                append.Invoke(window, new object[] { "[CRAWL-TYPE] PROCESSED | HTML" });
                var typeFrame = new DispatcherFrame();
                Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => typeFrame.Continue = false));
                Dispatcher.PushFrame(typeFrame);
                var processedTypes = (TextBlock)typeof(MainWindow).GetField("ProcessedTypesText", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
                check(processedTypes.Text == "HTML 2  •  PDF 1  •  MDB 1", "dashboard counts only completed file types in a compact line");
                check(ProcessedFileType.Label("application/pdf", null) == "PDF" &&
                      ProcessedFileType.Label(null, ".mdb", "access") == "MDB" &&
                      ProcessedFileType.Label("image/jpeg", ".jpg") == "JPEG",
                    "processed type labels distinguish PDF, Access and image sources");
                typeof(MainWindow).GetField("_discoveredUrlsTotal", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(window, (long?)4321);
                typeof(MainWindow).GetMethod("RefreshCounters", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, null);
                var discovered = (TextBlock)typeof(MainWindow).GetField("DiscoveredUrlsText", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
                check(discovered.Text == "Discovered URLs 4,321",
                    "dashboard displays the saved unique discovered URL total");
                window.Close();
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (failure != null) throw failure;
    }
}
