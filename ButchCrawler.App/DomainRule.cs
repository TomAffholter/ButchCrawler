namespace ButchCrawler.App
{
    // Simple DTO used by SqlStorage.GetDomainRuleAsync
    public sealed class DomainRule
    {
        public int Id { get; set; }
        public int? MaxConcurrency { get; set; }
        public int? MinDelayMs { get; set; }
        public bool RobotsOverride { get; set; }
    }
}