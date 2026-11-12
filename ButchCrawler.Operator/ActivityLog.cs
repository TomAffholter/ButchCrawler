using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace ButchCrawler.Operator;

public sealed record ActivityAppearance(string Category, string Color, bool Bold = false);

public static class ActivityLog
{
    public static ActivityAppearance Classify(string line)
    {
        if (line.StartsWith(ButchCrawler.App.OpenAiBilling.LogPrefix, StringComparison.Ordinal)) return new("AI account notice", "#A65306", true);
        if (line.StartsWith("[CRAWL-MATCH]", StringComparison.Ordinal)) return new("Keyword match", "#7C3EAD");
        if (line.StartsWith("[CRAWL-DISCOVERY]", StringComparison.Ordinal)) return new("Discovery", "#087F8C");
        if (line.StartsWith("[CRAWL-URL] ", StringComparison.Ordinal))
        {
            var action = line[12..].Split(' ', '|')[0];
            return action switch
            {
                "RETRIEVED" => new("Retrieved", "#137A43"),
                "COMPLETE" => new("Completed", "#11613C", true),
                "START" => new("Fetching", "#245FA8"),
                "REDIRECT" => new("Redirect", "#245FA8"),
                "AUDIO" => new("Transcribing audio", "#245FA8"),
                "AUDIO-COMPLETE" or "AUDIO-CACHED" => new("Audio transcript saved", "#11613C", true),
                "DISCOVERED" or "QUEUED" => new("Discovery", "#087F8C"),
                "SKIPPED" => new("Skipped", "#66758A"),
                "BLOCKED" => new("Blocked", "#8B571E"),
                "CHALLENGE" => new("CAPTCHA", "#A02787"),
                "ERROR" => new("Processing error", "#8B2635", true),
                "FAILURE" when line.StartsWith("[CRAWL-URL] FAILURE RETRY ", StringComparison.Ordinal) => new("Retry scheduled", "#A65306"),
                "FAILURE" => new("Dead-letter", "#BE3040", true),
                "HTTP" => HttpAppearance(line),
                _ => new("Activity", "#42566E")
            };
        }
        if (line.Contains("[ERR]", StringComparison.Ordinal) || line.Contains("[FTL]", StringComparison.Ordinal)) return new("Error", "#8B2635", true);
        if (line.Contains("[WRN]", StringComparison.Ordinal)) return new("Warning", "#A65306");
        return new("Activity", "#42566E");
    }

    private static ActivityAppearance HttpAppearance(string line)
    {
        var token = line.Split(' ', StringSplitOptions.RemoveEmptyEntries).ElementAtOrDefault(2);
        return int.TryParse(token, out var status) ? status switch
        {
            >= 200 and < 300 => new("HTTP success", "#137A43"),
            >= 300 and < 400 => new("Redirect", "#245FA8"),
            408 or 425 or 429 or >= 500 => new("Temporary HTTP failure", "#A65306"),
            >= 400 => new("HTTP failure", "#BE3040"),
            _ => new("HTTP", "#42566E")
        } : new("HTTP", "#42566E");
    }

    public static void Append(RichTextBox box, string line)
    {
        var appearance = Classify(line);
        if (line.Length > 12000) line = line[..12000] + " … [display truncated]";
        var run = new Run(line) { Foreground = (Brush)new BrushConverter().ConvertFromString(appearance.Color)!, FontWeight = appearance.Bold ? FontWeights.SemiBold : FontWeights.Normal };
        var paragraph = new Paragraph(run) { Margin = new Thickness(0, 1, 0, 3), ToolTip = appearance.Category };
        box.Document.Blocks.Add(paragraph);
        var length = (box.Tag is int previous ? previous : 0) + line.Length;
        while (box.Document.Blocks.Count > 800 || length > 180000)
        {
            var first = box.Document.Blocks.FirstBlock;
            if (first == null) break;
            length -= new TextRange(first.ContentStart, first.ContentEnd).Text.TrimEnd('\r', '\n').Length;
            box.Document.Blocks.Remove(first);
        }
        box.Tag = Math.Max(0, length); box.ScrollToEnd();
    }

    public static void Clear(RichTextBox box) { box.Document.Blocks.Clear(); box.Tag = 0; }
}
