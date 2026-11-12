using System;

namespace ButchCrawler.App
{
    // Simple runtime logging control object
    public class LoggingControl : IDisposable
    {
        public static LoggingControl? Current { get; private set; }

        public bool SuppressInfo { get; set; }

        public LoggingControl()
        {
            Current = this;
            SuppressInfo = false;
        }

        public void Dispose()
        {
            if (Current == this) Current = null;
        }
    }
}
