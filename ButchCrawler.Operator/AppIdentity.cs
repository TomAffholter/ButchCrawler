using System.Globalization;
using System.Reflection;

namespace ButchCrawler.Operator;

public static class AppIdentity
{
    public const string Name = "History's Web Crawler";
    public const string Author = "Tom Affholter";
    public const string Email = "TomAffholter@outlook.com";
    private static readonly Assembly Assembly = typeof(AppIdentity).Assembly;

    public static string Version => Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? Assembly.GetName().Version?.ToString(3) ?? "Unknown";

    public static DateTimeOffset? CompiledUtc
    {
        get
        {
            var text = Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(item => item.Key == "BuildTimestampUtc")?.Value;
            return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var timestamp) ? timestamp : null;
        }
    }

    public static string CompiledDate
    {
        get
        {
            if (CompiledUtc is not { } timestamp) return "Unavailable";
            var pacific = TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time");
            return TimeZoneInfo.ConvertTime(timestamp, pacific).ToString("MMMM d, yyyy", CultureInfo.GetCultureInfo("en-US"));
        }
    }
}
