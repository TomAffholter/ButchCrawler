using System;
using System.Security.Cryptography;
using System.Text;

namespace ButchCrawler.App
{
    internal static class HashHelper
    {
        // Returns lowercase hex SHA256, matching SQL: LOWER(CONVERT(VARCHAR(64), HASHBYTES('SHA2_256', Url), 2))
        public static string ComputeSha256Hex(string input)
        {
            if (input is null) throw new ArgumentNullException(nameof(input));
            using var sha = SHA256.Create();
            var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(input));
            var sb = new StringBuilder(bytes.Length * 2);
            foreach (var b in bytes) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }
    }
}