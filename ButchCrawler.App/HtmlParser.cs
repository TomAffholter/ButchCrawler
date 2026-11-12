using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using HtmlAgilityPack;

namespace ButchCrawler.App
{
    public class HtmlParser
    {
        public string ExtractTitle(string html)
        {
            if (string.IsNullOrWhiteSpace(html))
                return string.Empty;

            try
            {
                var doc = new HtmlDocument();
                doc.LoadHtml(html);
                var title = doc.DocumentNode.SelectSingleNode("//head/title")?.InnerText;
                return WebUtility.HtmlDecode(title ?? string.Empty).Trim();
            }
            catch
            {
                return string.Empty;
            }
        }

        public string ExtractCleanText(string html)
        {
            if (string.IsNullOrWhiteSpace(html))
                return string.Empty;

            try
            {
                var doc = new HtmlDocument();
                doc.OptionFixNestedTags = true;
                doc.LoadHtml(html);

                RemoveBoilerplateNodes(doc);

                var main = doc.DocumentNode.SelectSingleNode("//main")
                           ?? doc.DocumentNode.SelectSingleNode("//article")
                           ?? doc.DocumentNode.SelectSingleNode("//*[@role='main']")
                           ?? doc.DocumentNode.SelectSingleNode("//body")
                           ?? doc.DocumentNode;

                var text = HtmlEntity.DeEntitize(main.InnerText ?? string.Empty);
                return Regex.Replace(text, @"\s+", " ").Trim();
            }
            catch
            {
                var fallback = Regex.Replace(html, "<.*?>", " ", RegexOptions.Singleline);
                return Regex.Replace(WebUtility.HtmlDecode(fallback), @"\s+", " ").Trim();
            }
        }

        public CrawlResult Parse(string baseUrl, string html)
        {
            var doc = new HtmlDocument();
            doc.OptionFixNestedTags = true;
            doc.LoadHtml(html);

            var canonical = doc.DocumentNode.SelectSingleNode("//link[@rel='canonical']")?.GetAttributeValue("href", string.Empty);
            var effectiveUrl = ResolveUrl(baseUrl, canonical) ?? baseUrl;

            var title = ExtractTitle(html);
            var text = ExtractCleanText(html);
            var links = ExtractLinks(doc, effectiveUrl);

            var result = new CrawlResult
            {
                Url = effectiveUrl,
                Title = title,
                Text = text,
                RawHtml = html,
                Links = links
            };

            // Extract structured data
            result.JsonLd = ExtractJsonLd(doc);
            result.OpenGraph = ExtractOpenGraph(doc);
            result.Microformats = ExtractMicroformats(doc);

            // Compute a content hash used by storage to detect changes
            result.ContentHash = ComputeHash(result.Text ?? string.Empty);

            return result;
        }

        private List<string> ExtractJsonLd(HtmlDocument doc)
        {
            var list = new List<string>();
            var nodes = doc.DocumentNode.SelectNodes("//script[@type='application/ld+json']");
            if (nodes == null) return list;

            foreach (var n in nodes)
            {
                var json = (n.InnerText ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(json)) continue;

                // attempt to parse and reserialize to canonical form
                try
                {
                    using var jsonDoc = JsonDocument.Parse(json);
                    var normalized = JsonSerializer.Serialize(jsonDoc.RootElement, new JsonSerializerOptions { WriteIndented = false });
                    list.Add(normalized);
                }
                catch
                {
                    // keep raw if parsing fails
                    list.Add(json);
                }
            }

            return list.Distinct(StringComparer.Ordinal).ToList();
        }

        private Dictionary<string, string> ExtractOpenGraph(HtmlDocument doc)
        {
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var nodes = doc.DocumentNode.SelectNodes("//meta[@property or @name]");
            if (nodes == null) return dict;

            foreach (var node in nodes)
            {
                var key = node.GetAttributeValue("property", node.GetAttributeValue("name", string.Empty)).Trim();
                if (!key.StartsWith("og:", System.StringComparison.OrdinalIgnoreCase)) continue;

                var val = HtmlEntity.DeEntitize(node.GetAttributeValue("content", string.Empty).Trim());
                if (!string.IsNullOrEmpty(key) && !dict.ContainsKey(key))
                    dict[key] = val;
            }

            return dict;
        }

        private List<Dictionary<string, string>> ExtractMicroformats(HtmlDocument doc)
        {
            var list = new List<Dictionary<string, string>>();
            var roots = doc.DocumentNode.SelectNodes("//*[contains(concat(' ', normalize-space(@class), ' '), ' h-')]");
            if (roots == null) return list;

            foreach (var root in roots)
            {
                var mf = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var classAttr = root.GetAttributeValue("class", string.Empty);

                mf["root_class"] = classAttr.Trim();

                // Extract common microformat properties by class name
                foreach (var prop in new[] { "p-name", "u-url", "dt-published", "e-content", "p-summary", "u-photo" })
                {
                    var propNode = root.SelectSingleNode($".//*[contains(concat(' ', normalize-space(@class), ' '), ' {prop} ')]");
                    if (propNode != null)
                    {
                        var val = HtmlEntity.DeEntitize(propNode.InnerText ?? string.Empty).Trim();
                        if (!string.IsNullOrEmpty(val)) mf[prop] = val;
                    }
                }

                list.Add(mf);
            }

            return list;
        }

        private static void RemoveBoilerplateNodes(HtmlDocument doc)
        {
            var nodes = doc.DocumentNode.SelectNodes("//script|//style|//noscript|//nav|//header|//footer|//aside|//form");
            if (nodes == null) return;

            foreach (var node in nodes)
            {
                node.Remove();
            }
        }

        private List<string> ExtractLinks(HtmlDocument doc, string baseUrl)
        {
            var list = new List<string>();
            var anchors = doc.DocumentNode.SelectNodes("//a[@href]|//audio[@src]|//audio/source[@src]|//video[@src]|//video/source[@src]");
            if (anchors == null) return list;

            foreach (var a in anchors)
            {
                var href = a.GetAttributeValue(a.Name == "a" ? "href" : "src", string.Empty).Trim();
                if (string.IsNullOrEmpty(href)) continue;

                try
                {
                    var resolved = new Uri(new Uri(baseUrl), href);
                    if (resolved.Scheme == Uri.UriSchemeHttp || resolved.Scheme == Uri.UriSchemeHttps)
                        list.Add(resolved.ToString());
                }
                catch
                {
                    // ignore malformed/unsupported URIs
                }
            }

            return list.Distinct().ToList();
        }

        private static string? ResolveUrl(string baseUrl, string? candidate)
        {
            if (string.IsNullOrWhiteSpace(candidate)) return null;
            try
            {
                return new Uri(new Uri(baseUrl), candidate).ToString();
            }
            catch
            {
                return null;
            }
        }

        private static string ComputeHash(string input)
        {
            using var sha = SHA256.Create();
            var bytes = Encoding.UTF8.GetBytes(input);
            var hash = sha.ComputeHash(bytes);
            var sb = new StringBuilder(hash.Length * 2);
            foreach (var b in hash) sb.AppendFormat("{0:x2}", b);
            return sb.ToString();
        }
    }
}
