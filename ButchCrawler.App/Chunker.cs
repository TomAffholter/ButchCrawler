using System;
using System.Collections.Generic;
using System.Text;

namespace ButchCrawler.App
{
    public static class Chunker
    {
        // Split text into ~800-word chunks (safe for embeddings)
        public static List<string> ChunkText(string text, int maxWords = 800)
        {
            var chunks = new List<string>();
            if (string.IsNullOrWhiteSpace(text))
                return chunks;

            var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var sb = new StringBuilder();
            int count = 0;

            foreach (var word in words)
            {
                sb.Append(word).Append(' ');
                count++;

                if (count >= maxWords)
                {
                    chunks.Add(sb.ToString().Trim());
                    sb.Clear();
                    count = 0;
                }
            }

            if (sb.Length > 0)
                chunks.Add(sb.ToString().Trim());

            return chunks;
        }
    }
}
