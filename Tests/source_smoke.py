"""Basic source-level smoke checks. Not a replacement for dotnet build/SQL integration tests."""
from pathlib import Path
root = Path(__file__).resolve().parents[1]
source = (root/'ButchCrawler.App'/'SqlStorage.Queue.cs').read_text()
crawler = (root/'ButchCrawler.App'/'CrawlerService.cs').read_text()
migration = (root/'Scripts'/'010_CreateDocumentKeywordHits.sql').read_text()
assert 'SaveKeywordHitsInTransactionAsync' in source
assert 'RecordKeywordHitsAsync' in source
assert 'UrlHash BINARY(32)' in migration
assert 'UX_CrawlKeywordHits_UrlHash_Keyword' in migration
assert 'await _storage.RecordKeywordHitsAsync(url, fullText)' in crawler
assert crawler.count('await _storage.RecordKeywordHitsAsync(url, fullText)') >= 2
assert 'FilterSeedsByKeywords", false' in crawler
print('Static source checks passed. Build, database integration and real crawling are NOT verified.')
