# History Crawler release preparation

1. Stop the crawler.
2. Open **Settings > Prepare clean starter database**.
3. Review the successful URLs. Stable URLs are selected automatically; login, search, calendar, paging, and query-string URLs require review.
4. Create the verified starter backup and keep the private safety backup out of the release.
5. Run `Build-HistoryCrawlerRelease.ps1` and supply the folder containing `HistoryCrawlerStarter.bak` and `HistoryCrawlerStarterSeeds.json`.

The release builder creates a self-contained Windows x64 package, includes the clean starter database and current documentation, rejects settings that appear to contain an API key, and includes the first-run setup marker. Microsoft SQL Server LocalDB remains a prerequisite.

Example:

```powershell
.\Installer\Build-HistoryCrawlerRelease.ps1 -StarterDatabaseDirectory "$env:USERPROFILE\Documents\History Crawler Installer\Starter Database"
```

The package is written under `Installer\artifacts`. The public package must contain the clean starter backup, never the private safety backup stored under `%LOCALAPPDATA%\HistoryCrawler\Backups`.
