using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace ButchCrawler.App;

/// <summary>
/// Stores short-lived Playwright browser state after a user completes a site's
/// human-verification screen. Files live in the current Windows user's profile
/// and are never shared between machines or users.
/// </summary>
public static class HumanVerificationSessionStore
{
    public const int DefaultLifetimeHours = 4;

    private static string DirectoryPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ButchCrawler", "BrowserSessions");

    public static string GetStorageStatePath(string url)
    {
        var uri = new Uri(url, UriKind.Absolute);
        if (uri.Scheme is not ("http" or "https"))
            throw new ArgumentException("Only HTTP and HTTPS sites can be verified.", nameof(url));
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(uri.IdnHost.ToLowerInvariant())));
        return Path.Combine(DirectoryPath, key + ".dpapi");
    }

    public static bool HasValidSession(string url, int lifetimeHours = DefaultLifetimeHours)
    {
        try
        {
            CleanupExpired(lifetimeHours);
            var temporary = GetStorageStatePath(url);
            return File.Exists(temporary) && File.GetLastWriteTimeUtc(temporary) >= DateTime.UtcNow.AddHours(-Math.Clamp(lifetimeHours, 1, 24));
        }
        catch { return false; }
    }

    /// <summary>Decrypts browser state to a uniquely named temporary file for Playwright.</summary>
    public static string? CreateReadableStorageStateFile(string url, int lifetimeHours = DefaultLifetimeHours)
    {
        if (!OperatingSystem.IsWindows() || !HasValidSession(url, lifetimeHours)) return null;
        var source = GetStorageStatePath(url);
        var encrypted = File.ReadAllBytes(source);
        byte[]? plain = null;
        var temporary = Path.Combine(Path.GetTempPath(), "ButchCrawler-session-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            plain = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(temporary, plain);
            return temporary;
        }
        catch
        {
            try { File.Delete(temporary); } catch { }
            Delete(url);
            return null;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(encrypted);
            if (plain != null) CryptographicOperations.ZeroMemory(plain);
        }
    }

    public static void SaveEncrypted(string url, string plaintextPath)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Browser sessions require Windows user encryption.");
        PrepareDirectory();
        var plain = File.ReadAllBytes(plaintextPath);
        byte[]? encrypted = null;
        var destination = GetStorageStatePath(url);
        var temporary = destination + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            encrypted = ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(temporary, encrypted);
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
            if (encrypted != null) CryptographicOperations.ZeroMemory(encrypted);
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
        }
    }

    public static void PrepareDirectory() => Directory.CreateDirectory(DirectoryPath);

    public static void Delete(string url)
    {
        try
        {
            var path = GetStorageStatePath(url);
            if (File.Exists(path)) File.Delete(path);
        }
        catch { }
    }

    public static void CleanupExpired(int lifetimeHours = DefaultLifetimeHours)
    {
        try
        {
            if (Directory.Exists(DirectoryPath))
            {
                var cutoff = DateTime.UtcNow.AddHours(-Math.Clamp(lifetimeHours, 1, 24));
                foreach (var path in Directory.EnumerateFiles(DirectoryPath, "*.dpapi"))
                    if (File.GetLastWriteTimeUtc(path) < cutoff) File.Delete(path);
                // Remove any plaintext files created by versions before session encryption.
                foreach (var path in Directory.EnumerateFiles(DirectoryPath, "*.json")) File.Delete(path);
            }
        }
        catch { }
    }

    public static void DeleteAll()
    {
        try { if (Directory.Exists(DirectoryPath)) Directory.Delete(DirectoryPath, recursive: true); }
        catch { }
    }

    public static bool LooksLikeChallenge(string? html, string? finalUrl = null)
    {
        if (string.IsNullOrWhiteSpace(html)) return true;
        var title = Regex.Match(html, "<title[^>]*>(.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline)
            .Groups[1].Value.Trim().ToLowerInvariant();
        var visible = Regex.Replace(html, "<(script|style|noscript)[^>]*>.*?</\\1>", " ", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        visible = Regex.Replace(visible, "<[^>]+>", " ").ToLowerInvariant();
        var instruction = visible.Contains("verify you are human") || visible.Contains("human verification")
            || visible.Contains("complete the captcha") || visible.Contains("enter the characters you see")
            || visible.Contains("enter the text shown");
        var challengeTitle = title is "just a moment..." or "just a moment…" or "attention required! | cloudflare";
        var challengeUrl = (finalUrl ?? string.Empty).Contains("/cdn-cgi/challenge-platform/", StringComparison.OrdinalIgnoreCase);
        return challengeTitle || challengeUrl || (instruction && visible.Length < 6000);
    }
}
