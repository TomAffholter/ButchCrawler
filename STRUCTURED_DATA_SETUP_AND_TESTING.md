# History Crawler: database and structured-data discovery

## What the feature does

History Crawler now recognizes useful datasets linked from ordinary pages and repository sites. Repository API, export, download, bulk-data and known dataset links are placed before ordinary discovered links while retaining the same robots.txt, seed-domain, depth and permission rules.

Supported searchable imports:

- CSV, TSV and TAB tables, including quoted and multiline cells
- JSON, JSON Lines, NDJSON and GeoJSON repository exports
- XML exports with external entities and DTDs disabled
- SQL dump files as **text only**; statements are never executed and likely password/token values are redacted
- modern `.xlsx` workbooks; formulas and macros are not executed
- legacy `.xls` workbooks when the Microsoft Access Database Engine is installed
- SQLite databases opened read-only with write queries and extension loading disabled
- Access `.mdb` and `.accdb` databases opened read-only when the Microsoft Access Database Engine is installed
- DBF tables using a bounded built-in reader
- ZIP bulk exports containing any supported structured format, including bounded nested ZIP files

The extracted rows and values are saved in the existing `CrawlResults` collection so normal Boolean collection search can find them. `StructuredDataSources` stores format, category, original source, byte size, record/table/archive counts, content hash, truncation status and warnings. Search collection includes a new **Structured data** filter.

## Safety and permission behavior

- Nothing from an SQL file is attached to SQL Server or executed.
- SQLite and Access sources are opened read-only and copied to a temporary file first. The original is not changed.
- Excel formulas, VBA macros and programs inside ZIP files are not executed.
- XML DTDs and external entities are rejected.
- Web downloads remain subject to robots.txt, public-network checks, seed scope, crawl depth, host delays and HTTP timeouts.
- Structured downloads default to 50 MB. ZIP files default to 200 entries, 100 MB expanded data, two nested archive levels and a 100:1 compression-ratio ceiling.
- Import defaults to 5,000 rows per table, 100 tables, 200 columns and 20 million extracted characters. A limited import is marked as truncated rather than expanding indefinitely.
- Repository endpoints are followed only when the crawler actually discovers their links; History Crawler does not guess API addresses or bypass login/permission controls.

## Setup

No new account or paid service is required. The feature is enabled by `StructuredData : Enabled` in `ButchCrawler.App/appsettings.json`. Its limits can be changed in the ordinary Settings window; restart the crawler after changing them.

SQLite, CSV/TSV, JSON/XML, SQL text, `.xlsx`, DBF and ZIP work immediately. Reading Access and legacy `.xls` files additionally requires the **64-bit Microsoft Access Database Engine** that matches this 64-bit application. If it is absent, History Crawler safely reports that prerequisite instead of trying to execute or alter the file.

The crawler creates `dbo.StructuredDataSources` automatically without deleting or changing existing research. `Scripts/017_CreateStructuredDataSources.sql` is also supplied for administrators who prefer to install the table manually.

## What the user sees

- A successful import adds a `STRUCTURED` activity line showing the detected format and number of indexed records.
- The source then appears under **Search collection > Structured data** and also under **Everything**.
- Opening a result shows a plain-text, provenance-labelled view of the indexed rows and values.
- If a safety limit is reached, the saved text and provenance say that the preview was limited.
- Unsupported or unreadable files remain visible in the crawler log with a reason; their contents are not executed.

## Verification performed

Run these commands from the solution folder after building:

```powershell
Tests\BehaviorTests\bin\Debug\net8.0-windows\BehaviorTests.exe --structured-data-tests
Tests\BehaviorTests\bin\Debug\net8.0-windows\BehaviorTests.exe --ai-sql
Tests\BehaviorTests\bin\Debug\net8.0-windows\BehaviorTests.exe --security-tests
Tests\BehaviorTests\bin\Debug\net8.0-windows\BehaviorTests.exe --coverage-tests
Tests\BehaviorTests\bin\Debug\net8.0-windows\BehaviorTests.exe --activity-tests
```

The structured-data suite creates representative CSV, JSON, XML, SQL, XLSX, SQLite, DBF and ZIP fixtures. It also verifies XML entity rejection, SQL secret redaction, ZIP program exclusion and independent size limits. The SQL integration suite uses a temporary database and verifies that structured results are searchable without duplicate webpage results.

