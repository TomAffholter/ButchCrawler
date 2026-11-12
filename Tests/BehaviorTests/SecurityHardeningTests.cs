using System.Buffers.Binary;
using System.IO;
using ButchCrawler.App;
using ButchCrawler.Operator;

internal static class SecurityHardeningTests
{
    public static void Run(Action<bool, string> check)
    {
        var sessionPath = HumanVerificationSessionStore.GetStorageStatePath("https://example.org/check");
        check(sessionPath.EndsWith(".dpapi", StringComparison.OrdinalIgnoreCase) &&
              !sessionPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase),
            "browser session state is stored in Windows-encrypted form");

        var rejectedRelativeTool = false;
        try { TrustedExecutable.Resolve("tesseract.exe", "tesseract.exe"); }
        catch (InvalidOperationException) { rejectedRelativeTool = true; }
        check(rejectedRelativeTool, "external tools cannot be selected through an untrusted PATH entry");
        var installedToolsAccepted = true;
        foreach (var (path, names) in new[]
        {
            (@"C:\Program Files\Tesseract-OCR\tesseract.exe", new[] { "tesseract.exe" }),
            (@"C:\Program Files\gs\gs10.08.0\bin\gswin64c.exe", new[] { "gswin64c.exe", "gswin32c.exe", "gs.exe" }),
            (@"C:\Program Files\ImageMagick-7.1.2-Q16-HDRI\magick.exe", new[] { "magick.exe" })
        })
            if (File.Exists(path)) installedToolsAccepted &= TrustedExecutable.Resolve(path, names) == Path.GetFullPath(path);
        check(installedToolsAccepted, "installed OCR and PDF tools pass trusted-location validation");

        check(SafeSourceLauncher.CanOpen("https://example.org/archive") &&
              !SafeSourceLauncher.CanOpen("javascript:alert(1)") &&
              !SafeSourceLauncher.CanOpen("custom-protocol:payload"),
            "source launcher accepts web links but rejects executable custom protocols");

        var png = new byte[24];
        new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }.CopyTo(png, 0);
        BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(16, 4), 100);
        BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(20, 4), 100);
        ImageSecurityValidator.Validate(png, ".png", 50_000_000);
        var pixelLimitWorked = false;
        BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(16, 4), 50_000);
        BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(20, 4), 50_000);
        try { ImageSecurityValidator.Validate(png, ".png", 50_000_000); }
        catch (InvalidDataException) { pixelLimitWorked = true; }
        check(pixelLimitWorked, "oversized image dimensions are rejected before OCR");

        var webp = new byte[30];
        "RIFF"u8.CopyTo(webp.AsSpan(0)); "WEBP"u8.CopyTo(webp.AsSpan(8)); "VP8X"u8.CopyTo(webp.AsSpan(12));
        webp[24] = 0xFF; webp[25] = 0x03; // 1,024 pixels wide
        webp[27] = 0xFF; webp[28] = 0x03; // 1,024 pixels high
        ImageSecurityValidator.Validate(webp, ".webp", 2_000_000);
        check(true, "validated WebP graphics are accepted for bounded OCR conversion");
    }
}
