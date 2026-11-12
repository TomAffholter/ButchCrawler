using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ButchCrawler.App
{
    public record CaptchaSolutionResult(bool Success, string? Solution = null, Dictionary<string, string>? Cookies = null, string? Error = null);

    // Minimal interface for pluggable captcha solvers used by Fetcher and Program.cs
    public interface ICaptchaSolver
    {
        // Try to solve a challenge and return a CaptchaSolutionResult with fields: Success, Solution, Cookies, Error.
        Task<CaptchaSolutionResult> SolveAsync(FetchResult challenge, CancellationToken ct);
    }
}
