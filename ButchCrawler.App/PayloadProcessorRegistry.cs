using System;
using System.Collections.Concurrent;

namespace ButchCrawler.App
{
    public class PayloadProcessorRegistry
    {
        private readonly ConcurrentDictionary<string, IPayloadProcessor> _byMime = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, IPayloadProcessor> _byExt = new(StringComparer.OrdinalIgnoreCase);

        public void RegisterMime(string mime, IPayloadProcessor processor)
        {
            if (mime is null) throw new ArgumentNullException(nameof(mime));
            if (processor is null) throw new ArgumentNullException(nameof(processor));
            _byMime[mime] = processor;
        }

        public void RegisterExtension(string extension, IPayloadProcessor processor)
        {
            if (extension is null) throw new ArgumentNullException(nameof(extension));
            _byExt[extension.StartsWith(".") ? extension : "." + extension] = processor;
        }

        public IPayloadProcessor? Resolve(string? contentType, string? extension)
        {
            if (!string.IsNullOrWhiteSpace(contentType) && _byMime.TryGetValue(contentType!, out var p))
                return p;
            if (contentType?.EndsWith("+json", StringComparison.OrdinalIgnoreCase) == true && _byMime.TryGetValue("application/json", out var json))
                return json;
            if (contentType?.EndsWith("+xml", StringComparison.OrdinalIgnoreCase) == true && _byMime.TryGetValue("application/xml", out var xml))
                return xml;
            if (contentType?.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) == true && _byMime.TryGetValue("audio/mpeg", out var audio))
                return audio;

            if (!string.IsNullOrWhiteSpace(extension))
            {
                var ext = extension!.StartsWith(".") ? extension : "." + extension;
                if (_byExt.TryGetValue(ext, out var pe)) return pe;
            }

            return null;
        }
    }
}
