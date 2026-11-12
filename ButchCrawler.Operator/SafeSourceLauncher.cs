using System.Diagnostics;
using System.IO;

namespace ButchCrawler.Operator;

public static class SafeSourceLauncher
{
    public static bool CanOpen(string? source)
    {
        if (string.IsNullOrWhiteSpace(source)) return false;
        if (Uri.TryCreate(source, UriKind.Absolute, out var uri))
            return uri.Scheme is "http" or "https" || (uri.IsFile && File.Exists(uri.LocalPath));
        return Path.IsPathFullyQualified(source) && File.Exists(source);
    }

    public static void OpenOrReveal(string source)
    {
        if (Uri.TryCreate(source, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
            return;
        }

        var path = Uri.TryCreate(source, UriKind.Absolute, out uri) && uri.IsFile ? uri.LocalPath : source;
        if (!Path.IsPathFullyQualified(path) || !File.Exists(path))
            throw new InvalidOperationException("Only HTTP/HTTPS addresses and existing local files can be opened.");

        // Never shell-execute collected local content. Reveal it in Explorer so
        // Windows cannot launch an executable or active document from database data.
        var explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
        if (!File.Exists(explorer)) throw new FileNotFoundException("Windows Explorer was not found.", explorer);
        var start = new ProcessStartInfo(explorer) { UseShellExecute = false };
        start.ArgumentList.Add("/select,"); start.ArgumentList.Add(Path.GetFullPath(path));
        Process.Start(start);
    }
}
