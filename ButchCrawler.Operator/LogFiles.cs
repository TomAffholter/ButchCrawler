using System.IO;
using System.Text;

namespace ButchCrawler.Operator;

public static class LogFiles
{
    public static Task<(string Text, bool Truncated)> ReadTailAsync(string path) => Task.Run(async () =>
    {
        const int limit = 2 * 1024 * 1024;
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var truncated = stream.Length > limit;
        if (truncated) stream.Seek(-limit, SeekOrigin.End);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        // Skip a partial UTF-8 line without allocating an unbounded string.
        if (truncated)
        {
            var one = new char[1];
            for (var skipped = 0; skipped < limit && await reader.ReadAsync(one, 0, 1) != 0; skipped++)
                if (one[0] == '\n') break;
        }
        var buffer = new char[limit];
        var count = await reader.ReadBlockAsync(buffer, 0, buffer.Length);
        return (new string(buffer, 0, count), truncated);
    });

    public static Task DeleteAsync(string path) => Task.Run(() =>
    {
        // An exclusive handle refuses deletion if another crawler has the log open.
        using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite,
            FileShare.None, 4096, FileOptions.DeleteOnClose);
    });
}
