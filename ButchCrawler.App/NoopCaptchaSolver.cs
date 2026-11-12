using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;

namespace ButchCrawler.App
{
    // No-op solver used by default: does nothing and returns failure.
    public class NoopCaptchaSolver : ICaptchaSolver
    {
        public Task<CaptchaSolutionResult> SolveAsync(FetchResult challenge, CancellationToken ct)
        {
            var res = new CaptchaSolutionResult(false, Solution: null, Cookies: (Dictionary<string,string>?)null, Error: "noop-solver-disabled");
            return Task.FromResult(res);
        }
    }
}
