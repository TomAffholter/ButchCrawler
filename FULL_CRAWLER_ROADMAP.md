# ButchCrawler full crawler roadmap and release notes (2026-10-23)

## What THIS source ZIP actually changes
- Fixes the **invalid SQL Server index key** in migration 010. An index on `NVARCHAR(2000)` plus an `INT` is unsafe/unsupported as a unique key; the updated table uses a `BINARY(32)` SHA-256 URL hash and keeps the original URL for provenance.
- Migration 010 can upgrade a previously created `CrawlKeywordHits` table by populating its hash and replacing the old index. It does not drop existing rows.
- Centralizes persistence of active-keyword hit counts/excerpts for HTML results (same SQL transaction as queue completion), PDF text, and other processed documents.
- On reprocessing a page, keyword rows for terms that no longer appear are removed. Rows for inactive/deleted terms are not automatically purged.
- Preserves existing source, database tables, settings, and solution layout. No compiled executables included.
- Adds code/static smoke checks and setup notes.

## REQUIRED before any large crawl
1. Back up ButchCrawlerDB and run Scripts/010_CreateDocumentKeywordHits.sql in SSMS against the backup/test database first.
2. Open the Visual Studio solution on Windows and restore/build the .NET 8 worker. Build has NOT been run in this environment (dotnet SDK absent).
3. Verify database connection string and which SQL Server instance `(local)` points to. Confirm with `SELECT @@SERVERNAME, DB_NAME(), @@VERSION;`.
4. Limit the run to a small seed sample in a copy of the database; verify links actually enqueue, traverse, and persist, including retries and failures.
5. Explicitly set `Crawler:AllowPdf` to true only after confirming your local OCR/PDF dependencies. It remains false by default in this ZIP.
6. Observe robots.txt, terms of service, host rate limits, and site access restrictions; never crawl private or internal network services.

## Remaining recommendations NOT IMPLEMENTED OR NOT VALIDATED
- Prove complete end-to-end traversal through the queue and extractor under SQL Server Developer; build and integration testing are outstanding.
- Consolidate `CrawlQueue` and `CrawlerFrontier` into a single well-defined authoritative frontier, with migration and coexistence policy.
- Consolidate `CrawlResults`, `documents`, `CrawlerHistory`, and capture tables after designing backward-compatible schema mapping.
- Replace per-URL insert loop with transactional bulk upserts/TVPs for the 78,057-seed scale.
- Enforce canonical URL rules and dedupe after normalization (fragments, default ports, redirects, query parameter policy), while preserving archival identifiers.
- Confirm leases/retry backoff/host concurrency survive crashes and multiple worker instances; add ownership checks and recoverability tests.
- Strictly enforce per-seed depth/page budgets and cross-host policies for HTTP redirects and every discovered child.
- Add robust SSRF protections at DNS, redirect, browser and final socket stages, including private IP denial.
- Broaden PDF OCR, image extraction, DOCX and scanned archive formats; retain extraction provenance and confidence.
- Add relevance ranking with historical aliases, spellings, dates, entities, false-positive handling, and match-context scoring.
- Improve searchable source attribution, quoted passages, original document snapshots and revision tracking.
- Add recrawl scheduling, ETag/Last-Modified, content-diff lifecycle, and monitoring dashboard.
- Add automated C# unit, integration, HTTP fixture and SQL database tests and stress tests.
- Guard against unbounded crawl storage, expensive embedding creation and API spending; set quotas.

## Known limitations
- Source-only changes have not been compiled or SQL-tested. Do not interpret this ZIP as a fully complete or certified crawler.
- SQL index hash computed with SQL Server `HASHBYTES('SHA2_256', CONVERT(VARBINARY(MAX), @url))`, consistently inside SQL writes.
- Keyword substring matching can produce partial-word false positives. A later text normalization/tokenization layer is recommended.
- The supplied database report lists 78,057 SeedUrls and 75 SeedKeywords, but does not provide the underlying rows or prove how the executable behaves.
