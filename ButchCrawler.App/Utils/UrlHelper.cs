using System;

namespace ButchCrawler.App
{
    internal static class UrlHelper
    {
        // Return normalized absolute http(s) URL or null if invalid / unsupported.
        // Normalization:
        // - scheme and host lowercased
        // - default ports omitted
        // - path, trailing slash and query preserved
        // - fragments removed
        // - query preserved
        public static string? NormalizeUrl(string? url)
        {
            if (string.IsNullOrWhiteSpace(url)) return null;
            url = url.Trim();

            // Only absolute URLs are accepted.
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
                return null;

            if (!(string.Equals(uri.Scheme, "http", StringComparison.OrdinalIgnoreCase) ||
                  string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase)))
                return null;

            var scheme = uri.Scheme.ToLowerInvariant();
            var host = uri.Host.ToLowerInvariant();
            var portPart = uri.IsDefaultPort ? "" : $":{uri.Port}";

            var path = uri.AbsolutePath;

            var query = uri.Query; // includes leading '?' if present

            // Build normalized URL without fragment
            return $"{scheme}://{host}{portPart}{path}{query}";
        }

        public static bool IsSameHostNormalized(string originNormalized, string candidateNormalized)
        {
            if (string.IsNullOrWhiteSpace(originNormalized) || string.IsNullOrWhiteSpace(candidateNormalized))
                return false;

            if (!Uri.TryCreate(originNormalized, UriKind.Absolute, out var o)) return false;
            if (!Uri.TryCreate(candidateNormalized, UriKind.Absolute, out var l)) return false;

            return HostsMatch(o.Host, l.Host)
                && (o.IsDefaultPort && l.IsDefaultPort ? true : o.Port == l.Port);
        }

        // Scope equivalence only; stored URL identities remain distinct.
        public static bool HostsMatch(string first, string second)
        {
            static string Bare(string host) => host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? host[4..] : host;
            return string.Equals(Bare(first.TrimEnd('.')), Bare(second.TrimEnd('.')), StringComparison.OrdinalIgnoreCase);
        }
    }
}
