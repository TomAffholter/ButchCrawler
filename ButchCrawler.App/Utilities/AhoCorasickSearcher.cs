using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace ButchCrawler.App.Utilities
{
    // Lightweight, non-optimized multi-pattern search helper.
    // Purpose: provide the required API for CrawlerService.FilterPdfTextToSeedMatchesAsync.
    // Replace with a full Aho–Corasick implementation or NuGet package if you need performance.
    public static class AhoCorasickSearcher
    {
        public sealed class Match
        {
            public int Index { get; init; }
            public int Length { get; init; }
            public string Pattern { get; init; } = string.Empty;
        }

        // Finds up to maxMatches occurrences of patterns in text. Case-insensitive.
        public static List<Match> FindAll(string? text, IEnumerable<string>? patterns, CancellationToken cancellationToken, int maxMatches = 100)
        {
            var results = new List<Match>();
            if (string.IsNullOrEmpty(text) || patterns == null) return results;

            foreach (var rawPattern in patterns)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(rawPattern)) continue;

                var pattern = rawPattern;
                int startPos = 0;
                while (startPos <= text.Length - pattern.Length)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var idx = text.IndexOf(pattern, startPos, StringComparison.OrdinalIgnoreCase);
                    if (idx < 0) break;

                    results.Add(new Match { Index = idx, Length = pattern.Length, Pattern = pattern });

                    if (results.Count >= maxMatches)
                        return results.OrderBy(m => m.Index).Take(maxMatches).ToList();

                    // advance by one to allow overlapping matches
                    startPos = idx + 1;
                }
            }

            return results.OrderBy(m => m.Index).Take(maxMatches).ToList();
        }
    }
}