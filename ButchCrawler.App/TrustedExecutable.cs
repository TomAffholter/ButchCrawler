namespace ButchCrawler.App;

public static class TrustedExecutable
{
    public static string Resolve(string? configuredPath, params string[] expectedFileNames)
    {
        if (string.IsNullOrWhiteSpace(configuredPath) || !Path.IsPathFullyQualified(configuredPath))
            throw new InvalidOperationException("The external tool must have a complete, trusted installation path in Settings.");
        var fullPath = Path.GetFullPath(configuredPath);
        if (!File.Exists(fullPath)) throw new FileNotFoundException("The configured external tool was not found.", fullPath);
        if (!expectedFileNames.Contains(Path.GetFileName(fullPath), StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException("The configured tool has an unexpected executable name: " + Path.GetFileName(fullPath));
        if ((File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("External tool links and reparse points are not allowed.");

        var roots = new List<string>();
        foreach (var folder in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 })
        {
            var root = Environment.GetFolderPath(folder);
            if (!string.IsNullOrWhiteSpace(root)) roots.Add(Path.GetFullPath(root));
        }
        // The documented standalone FFmpeg layout used by this application.
        roots.Add(Path.GetFullPath(@"C:\ffmpeg\bin"));
        if (!roots.Any(root => IsWithin(fullPath, root)))
            throw new InvalidOperationException("External tools must be installed under Program Files or C:\\ffmpeg\\bin.");
        return fullPath;
    }

    public static bool TryResolve(string? configuredPath, string[] expectedFileNames, out string? resolved)
    {
        try { resolved = Resolve(configuredPath, expectedFileNames); return true; }
        catch { resolved = null; return false; }
    }

    private static bool IsWithin(string path, string root)
    {
        var relative = Path.GetRelativePath(root, path);
        return relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
            !Path.IsPathFullyQualified(relative);
    }
}
