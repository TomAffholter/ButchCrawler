using System.Diagnostics;
using System.Text;

namespace ButchCrawler.App;

public static class ExternalProcessRunner
{
    public static async Task RunAsync(ProcessStartInfo start, TimeSpan timeout, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        start.UseShellExecute = false;
        start.CreateNoWindow = true;
        start.RedirectStandardOutput = start.RedirectStandardError = true;
        using var process = Process.Start(start) ?? throw new IOException("Could not start " + start.FileName);
        // Drain both pipes immediately, keeping only bounded diagnostics.
        var stdout = DrainAsync(process.StandardOutput);
        var stderr = DrainAsync(process.StandardError);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            await Task.WhenAll(stdout, stderr).WaitAsync(deadline.Token).ConfigureAwait(false);
            if (process.ExitCode != 0)
                throw new IOException($"{Path.GetFileName(start.FileName)} failed (exit {process.ExitCode}): {await stderr}");
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            await process.WaitForExitAsync().ConfigureAwait(false);
            if (ct.IsCancellationRequested) throw;
            throw new TimeoutException($"{Path.GetFileName(start.FileName)} exceeded its {timeout.TotalSeconds:0}-second limit.");
        }
    }

    private static async Task<string> DrainAsync(StreamReader reader)
    {
        var result = new StringBuilder(); var buffer = new char[4096];
        int read;
        while ((read = await reader.ReadAsync(buffer).ConfigureAwait(false)) > 0)
            if (result.Length < 8192) result.Append(buffer, 0, Math.Min(read, 8192 - result.Length));
        return result.ToString();
    }
}
