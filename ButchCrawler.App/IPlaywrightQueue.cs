using System.Threading;
using System.Threading.Tasks;

namespace ButchCrawler.App
{
    public interface IPlaywrightQueue
    {
        Task<RenderResult> EnqueueRenderAsync(string url, int timeout, bool saveScreenshot, bool saveTrace, CancellationToken cancellationToken = default);
    }
}
