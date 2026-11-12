# ButchCrawler update

## Date-field alignment (2026-11-12)

- Left-aligned the cleanup date and explicitly centered the inner DatePicker text element vertically, with a compact fixed field height matching the surrounding controls.

## Problem Seeds timeout repair (2026-11-12)

- Reworked the Problem Seeds load to identify the small set of problem seed IDs first, then retrieve verification details only for those records instead of comparing every verification task with the entire seed collection.
- Schema preparation now runs once per Problem Seeds session rather than before every button action.
- Added a supporting challenge-history index and a longer bounded timeout for legitimate large maintenance/load operations.

## Aligned cleanup criteria and numeric spinner (2026-11-12)

- Rebuilt the Problem Seeds cleanup criteria as a left-aligned two-column form so the action selector, attempt control and date control share one clean starting edge.
- Centered the action selector and date-control contents.
- Replaced the attempt text field with a numeric spinner defaulting to 5, including arrow buttons plus keyboard and mouse-wheel adjustment.

## More room for crawler activity (2026-11-12)

- Reduced the eight dashboard counter numbers slightly from 26 to 22 points.
- Tightened the counter-card padding and vertical spacing while preserving their rounded corners and readable labels, leaving more height for the activity output region.

## Reviewed bulk cleanup for problem seeds (2026-11-12)

- Added **Clean up…** to Problem Seeds with a required preview before any records change.
- Users can disable or permanently delete matching seeds based on either a minimum consecutive-failure count or the beginning date of the current uninterrupted failure streak.
- Added `FirstFailureAt` tracking. It is set on the first failed attempt in a streak and cleared by a successful crawl; existing failures are initialized conservatively from their last known failure date.
- Bulk actions recheck the rule inside the database transaction and safely skip any seed that recovered or changed after preview. Permanent deletion removes the seed and its queued work but retains previously collected pages and research.
- Problem Seeds and cleanup previews explicitly support click-to-sort on every visible column.

## Problem-seed retry refresh and count (2026-11-12)

- Renamed **Retry now** to **Retry next crawl** because the action safely requeues the selected URL; it does not run a second crawler inside the review window.
- A normal retry now removes only the selected row and updates the visible problem count immediately instead of reloading the entire problem-seed dataset.
- Seeds already scheduled with `retrying` status no longer reappear after a manual refresh. Human-verification entries remain counted until verification is actually completed and now explain why.

## Centered collection-search type selector (2026-11-12)

- Centered the selected text in the collection-search type box.
- Centered every choice in its drop-down list while preserving the existing size and spacing.

## Processed file-type counters (2026-11-12)

- Added a compact dashboard line showing successfully processed source types for the current run, including HTML, PDF, Office documents, images, Access/SQLite/DBF databases, tabular/API exports, ZIP archives and audio.
- A source type increments only after extraction and database saving succeed; discovered, started, failed and deliberately skipped files do not inflate the figures.
- Counts reset when a new crawl begins, retain their last-run display when the crawler stops, and expose the complete summary as a tooltip if the window becomes too narrow.
- The Release solution builds with zero warnings/errors, all 29 dashboard/activity checks pass, and normal plus compact dashboard previews were inspected.

## Database and structured-data discovery (2026-11-12)

- Added bounded discovery and text indexing for CSV/TSV, JSON/JSON Lines/GeoJSON, XML, SQL text dumps, XLSX, legacy XLS, SQLite, Access, DBF and ZIP bulk exports.
- Repository API/export/download links already present on crawled pages are scheduled before ordinary links without guessing endpoints or relaxing robots, seed scope, depth, private-network or permission rules.
- Added read-only SQLite and Access import; XML external entities are disabled, SQL is never executed, likely credentials in SQL text are redacted, Office macros/formulas are not run, and ZIP programs are ignored.
- Added independent byte, row, table, column, character, archive entry, expansion, nesting and compression-ratio limits.
- Added durable `StructuredDataSources` provenance and a **Structured data** collection-search filter. Extracted contents remain in the existing searchable collection.
- Added setup/testing notes in `STRUCTURED_DATA_SETUP_AND_TESTING.md`, an idempotent administrator migration, and focused fixtures covering all immediately available formats and safety controls.

## Newspapers.com automation removed (2026-11-11)

- Removed the Newspapers.com menu, sign-in windows, remembered-session handling, importer, configuration, crawler exceptions, and related tests because Cloudflare does not reliably permit an app-controlled browser session.
- History Crawler no longer claims it can automate a subscription sign-in or crawl Newspapers.com after a manual login in regular Edge.
- Existing `waiting-authentication` queue records are preserved for review and moved to `dead-letter` with a clear explanation the next time the crawler starts.
- Generic human verification for ordinary problem seeds remains available. The free public historical-newspaper archive search also remains available.
- Researchers can still use a normal browser and import permitted saved PDF, MHT, HTML, image, audio, and other supported local files.

## Guided Newspapers.com and Ancestry sign-in (removed 2026-11-11)

- Newspapers.com access now asks the user to sign in before releasing waiting URLs or offering to start the crawler.
- The instructions explicitly explain that a linked Ancestry account and Ancestry.com redirect are expected, but the user must continue until the Newspapers.com home page is visible.
- The confirmation button now reads **I see the Newspapers.com home page — continue**. The session is not saved while the browser remains on Ancestry or a Cloudflare verification page.
- Ancestry/Cloudflare **Browser Validation Error 54** is recognized and produces specific guidance to cancel automatic sign-in and use regular Edge for manual research.
- The solution builds with zero warnings or errors. Ten focused seed/verification checks, three Newspapers.com adapter checks, and the WPF visual preview passed; both updated windows were inspected and fit without clipped controls.

## Discovered URL total on the dashboard (2026-11-11)

- Added a teal **Discovered URLs** figure to the main saved-progress line beside Seed URLs and Local files.
- The figure is the database total of distinct non-seed URLs, so it is intentionally different from the per-run **Links queued** card.
- The saved total loads when the app opens and refreshes after a crawler run stops; no repeated count query is issued while the crawler is busy.
- Updated the dashboard footer to distinguish current-run cards from saved progress figures. The solution builds with zero warnings or errors, all 27 focused dashboard/activity checks passed, and both normal and compact WPF previews were inspected.

## Boolean collection search and novice help (2026-11-11)

- Collection search now supports `AND`, `OR`, `NOT`, quoted exact phrases, and parentheses, with `NOT` evaluated before `AND`, and `AND` before `OR`.
- Ordinary searches containing several words keep their earlier novice-friendly behavior: any word may match, and sources containing more of the words rank first.
- Added a visible **Search help** button with plain-language Lincoln examples for every supported search form, including a combined example.
- Incorrect Boolean expressions now produce a friendly explanation and point the user to Search help instead of exposing a SQL error.
- Search terms remain SQL parameters. Focused parser checks, live temporary-database result checks, the zero-warning solution build, and WPF visual preview all passed.

## Search feedback and Newspapers.com sign-in guidance (feature removed 2026-11-11)

- Collection search now displays a prominent animated **Searching your saved collection…** panel, changes the Search button text, and shows the wait pointer while SQL work is in progress. Loading a selected saved source uses the same visible feedback.
- The human-verification flow now tries the installed Microsoft Edge browser before falling back to Playwright Chromium.
- Newspapers.com access now explains that Cloudflare may reject app-controlled browsers and provides **Open in regular Edge** for manual access. The app does not attempt to bypass Turnstile or copy private cookies from the user's normal Edge profile; permitted pages saved locally can still be imported by the local-file crawler.
- The full solution builds with zero warnings or errors. The WPF preview check passed, and the behavior suite passed through the existing audio-tool section before stopping because the configured `C:\ffmpeg\bin\ffmpeg.exe` was not installed in the test environment.

## Newspapers.com authenticated access and import (removed 2026-11-11)

- Added **Settings > Newspapers.com access** as a permanent authenticated-site entry rather than misclassifying the subscription website as a failed seed.
- The user signs in through a visible Edge window. Only Playwright browser state/cookies are retained, encrypted for the current Windows user; the app never stores the username or password. The remembered state expires after 30 days by default and can be forgotten from the same screen.
- Newspapers.com URLs that display a subscription wall are retained as `waiting-authentication` and automatically returned to the pending queue after sign-in.
- Authorized Newspapers.com pages are always browser-rendered, including search-result pages, so their article links can be discovered. A site-specific importer extracts available embedded OCR, headline, publication, date, description, and page metadata into the existing searchable collection. It does not bypass subscription, robots, or other access controls.
- Both Debug and Release solution builds passed with zero warnings/errors. Focused adapter checks passed for host-boundary validation, paywall detection, and embedded OCR/metadata extraction.

## Operator startup and log reliability (2026-10-25)

- Fixed crawler startup when AI and embedding indexing are disabled: the crawler no longer constructs the paid embedding service or requires an OpenAI key.
- Added explicit Starting, Running, Could not start, Stopping, and Stopped dashboard states. Running is shown only after host startup succeeds; failed startup restores the Start button and keeps Stop disabled.
- Kept full exception stacks in dated log files while removing stack-frame noise from the novice dashboard activity view.
- Added an installed Microsoft Edge fallback for JavaScript rendering when Playwright's separately downloaded Chromium binary is unavailable.
- Extended queue ownership beyond the request timeout and retained the existing renewal heartbeat to prevent slow pages from losing their claimed queue row.
- Bounded capture provenance fields to their database column sizes so long seed metadata cannot discard an otherwise valid historical capture.
- Corrected dashboard label capitalization and added regression/visual coverage for every startup state.

## Standards-based blocked-site recovery (2026-10-25)

- Connected the browser renderer to successful, thin JavaScript pages, with one-at-a-time rendering, strict TLS, robots checks, and public-network request filtering.
- Added conditional recrawling with ETag/Last-Modified validators and clean HTTP 304 completion.
- Made timeouts and transient transport failures eligible for durable queue retries, including Retry-After handling for HTTP 503.
- Robots discovery now follows up to five redirects, retains declared Sitemap entries, uses a 24-hour cache, and fails closed when robots cannot be reached.
- Added bounded sitemap and sitemap-index discovery while retaining each seed's host/domain scope.
- Detected challenge pages now create durable, deduplicated review tasks instead of existing only in process memory.
- Extended the CAPTCHA migration with the columns already used by the review persistence code.

## Optional AI and free collection search (2026-10-25)

The supplied runtime now keeps paid AI features off by default: AI research, handwriting transcription, automatic audio transcription and embedding indexing are disabled. The encrypted OpenAI key, any existing transcripts, embeddings and saved AI results are preserved. With all paid features off, crawler startup no longer attempts to decrypt the saved key, and the collection-search workspace does not read or require it.

The main dashboard's former **AI research** button is now **Search collection**. Its novice-oriented screen searches saved successful webpages, extracted document/OCR text and completed audio transcripts without contacting OpenAI. Results can be filtered by source type, show a nearby matching excerpt, open the full saved text and link to the original source. Paid research remains available through the clearly separated **Optional AI tools** button.

When a user saves their first OpenAI key, a feature-choice window explains costs and offers manual research/handwriting, automatic audio transcription and embedding indexing separately. Manual tools are the recommended selection; automatic audio and indexing remain unchecked. **Not now** stores the encrypted key while leaving all paid features off. When a saved key is deleted, the user can delete it and disable every paid feature, delete only the stored key for an advanced external-key configuration, or cancel. Key and feature changes are written together while preserving unrelated JSON settings and collected data.

Validation: the Release solution builds with zero warnings/errors. All 21 focused key/optional-feature checks and 15 simulated AI service checks pass; static source checks pass. Updated dashboard, collection-search and optional-AI setup previews were rendered and visually inspected. The pre-existing monolithic behavioral run again stalled and was stopped, so a new full-suite pass is not claimed. No paid API request, production crawl, database reset or production-data change was performed.

About now shows current database totals for **Seed Keywords**, **Seed URLs**, and **New discovered URLs**, loaded asynchronously when opened and reloadable with **Refresh**. Seed totals include active and inactive records. Discovered URLs are distinct case-sensitive non-seed URLs in CrawlQueue with parent/depth provenance, across all statuses since the last data reset; repeated discoveries under different seeds count once. Database failures display an unavailable state instead of misleading zero counts. Closing About cancels its read request. The same JSON/environment connection settings as the rest of the Operator are used; no data is modified by these counters.

Validation: build passed with zero warnings/errors; all 24 isolated SQL integration checks passed. A read-only check against the configured database returned 75 Seed Keywords, 78,057 Seed URLs and 387 distinct discovered non-seed URLs at verification time. The revised About layout was rendered and inspected using clearly marked sample preview values.

## History's Web Crawler — About

The product name, main window title/header and assembly product/title metadata now read **History's Web Crawler**. Project/executable and database names remain unchanged for compatibility. An **About** button immediately to the right of Settings opens a centered western-themed window with an embedded collage, **Tom Affholter**, the clickable `mailto:TomAffholter@outlook.com` contact link, assembly version, and compilation date. **Exit** (or Escape) closes only the About window and leaves the crawler running.

Version is read from the Operator assembly (currently **1.0.0**). MSBuild stamps `BuildTimestampUtc` into assembly metadata at build time; the About window displays its date in Pacific time, with the exact UTC timestamp available as a tooltip. It does not substitute a copied file's modification date for the compilation date.

The generated background is embedded from `ButchCrawler.Operator/Assets/western-about-collage.png`, so it does not require an internet connection or an external image file at runtime. It was created with the built-in image-generation tool; the exact prompt is saved beside it in `western-about-collage.prompt.txt`.

Validation: the normal solution build passed with zero warnings/errors and all 154 behavioral checks passed. The About and main-window previews were rendered and visually inspected; assembly metadata was checked for the product name, version and UTC compilation timestamp. Email navigation was not invoked during testing.

The Settings dropdown now uses its left icon column: sliders for application settings, a chain link for Seed URLs, a tag for Seed Keywords, a key for OpenAI credentials, and a document for log files. Icons are scalable WPF vector paths and dim with disabled seed-editing commands.

The main toolbar now shows only **Start crawler**, **Stop**, and one **Settings** menu. Its items open application settings, Seed URLs, Seed Keywords, the OpenAI key editor, and log files. Seed editing remains disabled while the crawler runs. Database reset controls remain at the bottom of the application Settings dialog, with the same warnings and Cancel/Escape behavior. Use Alt+S or click Settings to open the menu.

## Audio text only; audio UI removed

The audio library page, manual file transcription dialog and their toolbar button have been removed. Automatic crawl transcription remains enabled. The database retains transcript text, keyword matches, origin URLs and processing metadata; no audio recording is stored in the database. Search/query the saved text through database tools.

Directly supported uploads now stay in memory and do not create an audio file on disk. Large/converted recordings use only a temporary working directory, which is deleted after success, failure or cancellation. Cleanup retries transient file-access failures and reports a failure rather than silently leaving audio behind. Existing local source recordings are not deleted; cleanup is limited to the app's temporary working files. Abrupt process termination or power loss can interrupt cleanup.

Validation: solution build passed with zero warnings/errors. Audio tests verify in-memory direct uploads and removal of temporary source/segment files after successful transcription, failure and cancellation. The main dashboard and remaining dialogs were rendered for inspection. No existing transcript data was deleted, and no live paid API call was made.

## Automatic audio results and centered windows

Automatic transcription is now enabled in the runtime JSON (`Audio:Enabled=true`). Starting a crawl processes discovered audio under the existing crawl scope and limits, matches transcript text against active Seed Keywords, and stores text, keyword hits and the original audio URL. Audio keyword matches also appear in the live match feed. Unmatched transcripts are retained as a cache to prevent paying to transcribe the same unchanged source again; the library defaults to showing keyword matches only. Raw recordings are not stored in the database or kept as library files; temporary processing files are normally removed after processing/cancellation.

The main **Audio transcripts** button now opens a searchable database library instead of a local-file picker. Search transcript text or origin URLs, select a row to read the full text, and open/copy its origin link. Clear **Seed keyword matches only** to search all saved completed transcripts. Results show the latest completed transcript per source, up to 200 results; narrow the search for more. Reading/searching this library makes no OpenAI calls and does not require a physical audio file.

Previously completed-but-unvisited audio URLs without a completed transcript are requeued at crawl startup when audio is enabled, so recordings skipped while the feature was disabled can be processed. Visited, already transcribed and dead-letter URLs are not requeued by this recovery path. Crawl policies are checked again before fetching. Long transcriptions renew their queue lease; loss of queue ownership cancels ongoing audio work, and saved segments can resume on another attempt.

The main WPF window now opens centered horizontally and vertically on screen. Dialogs open centered on their owner; shared window defaults center other windows on screen. The normal Debug output was rebuilt after the running Operator was closed.

Validation: **151 behavioral checks** and **22 isolated SQL integration checks** pass, including centered startup, audio lease renewal/loss, database text/origin search, active keyword filtering, literal search input, partial-result exclusion and skipped-audio recovery. The build has zero warnings/errors. Library and updated dashboard previews were rendered and inspected. No live audio transcription or production data reset was run during this update.

## Audio transcription

The main Operator toolbar includes **Audio transcripts**, a searchable library of automatically transcribed recordings and origin links. The earlier local-file transcription dialog remains in the source as an optional utility, but the main toolbar now opens the automatic-results library. Automatic transcription uses the existing encrypted OpenAI key and incurs API charges; searching saved results does not.

No manual file selection is required for crawled audio. **Audio : Enabled** is now `true` in the supplied runtime settings. To include audio in local-folder scans, also enable **LocalCrawler : Enabled** and configure its root folder. Embedded `<audio>`/`<source>` links and linked media are discovered subject to existing seed scope, depth, robots and network policies. Eligible unvisited audio skipped while disabled is recovered on the next crawl startup.

Direct upload formats are MP3, MP4, MPEG, MPGA, M4A, WAV and WebM. FFmpeg converts other recognized formats (FLAC, OGG, AAC, WMA, AIFF) and splits large recordings into mono MP3 segments. The configured FFmpeg executable is `C:\ffmpeg\bin\ffmpeg.exe`, which was found and exercised on this machine. The default model is `gpt-transcribe`; model access depends on the API account. The implementation follows [OpenAI file transcription documentation](https://developers.openai.com/api/docs/guides/speech-to-text).

Audio settings are available as existing editable rows in Settings: source limit **500 MB**, upload limit **24 MB**, **600-second** segments, at most **60** segments per converted recording, **100 API requests per run** (including retries), **3 attempts** per segment, **180-second** request timeout and **300-second** conversion timeout. Automatic processing is serial within the audio processor to limit load. HTTP downloads still respect `Crawler:MaxContentBytes` (100 MB by default), and local scans respect `LocalCrawler:MaxBytes`. Manual dialog operations have their own per-operation request budget. Recordings that exceed segment/upload limits are rejected before uploading rather than silently truncated.

`Scripts/012_CreateAudioTranscripts.sql` creates `dbo.AudioTranscripts`. Full transcripts, source/content hashes, model, segment text and approximate segment start offsets are saved independently of embedding indexing. Same-source, unchanged-content, same-model transcriptions reuse saved text without another API request. Segment progress is persisted so an interrupted recording can resume; changing chunk boundaries can require reprocessing unfinished recordings. Word-level timestamps and speaker labels are not included. Fixed segment boundaries can split speech, so review transcript accuracy against the original recording.

Both database reset modes also clear audio transcripts. The existing seed/API-key preservation behavior is unchanged. The audio schema was installed in **ButchCrawlerDB**; no existing records were cleared. On another installation, run migration 012 against the configured database before using audio transcription.

Validation: solution builds with zero warnings/errors. **147 behavioral checks** pass, including multipart request content, cached reuse without API calls, disabled/empty/oversized inputs, authentication failure, retries, timeout, cancellation, request budgets, real FFmpeg splitting, partial progress/resume and activity colors. **17 isolated SQL checks** pass, including Unicode transcript persistence, idempotent migration, cache invalidation, partial-state storage and reset integration. The audio dialog and dashboard previews were rendered and inspected. All transcription API responses were simulated; no paid transcription requests were made and real-account model access/recognition accuracy has not been verified.

Database reset controls now appear at the bottom of **Settings**, below its save/discard buttons, and have been removed from the main dashboard. They use the saved database connection and remain disabled while the Operator's crawler runs. Their separate irreversible-delete warnings, confirmation and Cancel/Escape behavior are unchanged; canceling returns to Settings without saving or discarding staged setting edits.

Both database reset dialogs now show a prominent **This deletion cannot be undone** warning listing what is deleted and preserved. The first preserves Seed URLs, Seed Keywords and the OpenAI key; the second deletes Seed Keywords while preserving Seed URLs and the OpenAI key. Schema-version records remain preserved in both. A **Cancel (Esc)** button and Escape key close the dialog without deletion, including during preview loading. After typing RESET and acknowledging stopped crawlers/backup, a final warning defaults to Cancel before any deletion starts. Once the transaction starts, the window remains open until the operation finishes; its button then becomes **Close (Esc)**. No production deletion was run to validate these UI changes.

## Operator styling and colored activity

The Operator now has a shared light theme, rounded controls, a dashboard of eight metric cards, and styled dialogs. Settings, seed and reset grids use clearer headers, alternating rows, consistent spacing, and visible selection/edit focus. Settings still permits edits to existing values only and excludes OpenAI key fields.

Live activity is selectable, read-only rich text with a visible legend. Successful retrievals are green; completed processing is dark green/bold; fetching and redirects are blue; link discovery is teal; keyword matches are purple; scheduled retries are orange; dead-letter outcomes are red/bold; policy blocks are brown; CAPTCHA challenges are magenta; skipped URLs are gray; unexpected processing errors are dark red. Status and reason text remain visible alongside the colors. The displayed log is bounded to 800 lines/approximately 180,000 characters per tab, with very long individual lines truncated for display.

The crawler emits explicit events for skipped/blocked/challenged URLs, redirects, completion, and persisted failure outcomes. Failure-state reporting comes from the database update result rather than an estimate of retries remaining. HTTP status reporting now includes returned document and failure statuses, in addition to HTML responses. The dashboard separately counts successful retrievals, failed crawl attempts, scheduled retries and unexpected processing errors. A failed attempt can be followed by a successful retry of the same URL; these are per-run event counts, not unique URL totals.

Validation: solution build has zero warnings/errors. All 116 behavioral checks passed, covering color classification, bounded rich text, separate counters and event-spoof protection. Eleven isolated SQL integration checks passed, including actual retry/dead-letter state reporting. Default and minimum-size main-window previews plus all dialog previews were rendered and inspected. No production database reset or live crawl was run for this UI update.

Transport failures with no HTTP response (stored status `0`) now remain eligible for queue retries, matching a missing/null status. Permanent HTTP failures such as 403/404 remain non-retryable. All 95 behavioral checks pass, including status classification for missing, zero, transient and permanent statuses. Existing dead-letter records are not automatically requeued by this code change.

## Runtime profile and database reset controls (2026-10-24)

`appsettings.json` now uses a bounded runtime profile: 100,000 pages per run, 10,000 initial seeds, depth 5, four concurrent pages, one request per host, two-second host spacing, 120-second request/OCR timeouts and 30-second connection timeout. HTTP payloads may be up to **100,000,000 bytes (100 MB)**, and local files up to **500,000,000 bytes (500 MB)** with two local workers. These are application limits, not SQL database file sizes. PDF crawling is enabled after checking the installed Tesseract, English OCR data and Ghostscript paths. Embedding indexing and local-folder crawling remain explicitly disabled until enabled in Settings. Debug crawler logging was changed to Information. Development settings no longer override local-file limits or OCR timeout. Existing database connection and OpenAI credentials were retained.

Two Operator buttons open a table-impact preview:

- **Reset data (keep seeds):** clears non-seed tables, preserving all `SeedUrls`, `SeedKeywords` and `SchemaVersion` records.
- **Reset data + keywords:** also clears `SeedKeywords`, while preserving all `SeedUrls` and `SchemaVersion` records.

The existing procedure is **`dbo.spWipeData`**, not `spWideData`. The app calls it inside a wrapping transaction; it does not replace or modify that production procedure. The wrapper restores enabled check constraints and original constraint trust states, resets identity counters on emptied tables, and rolls back on SQL failures. Both actions require stopped crawlers, a backup acknowledgment and typing `RESET`. Active queue leases and overlapping resets are rejected. Settings, API keys, source files and disk log files are outside these database resets. Preserved seed IDs and values stay unchanged; clearing keywords resets their identity counter.

The solution builds without warnings/errors and all 80 behavioral checks pass. Nine SQL integration checks passed against a uniquely named disposable database using a copy of the actual stored procedure, covering both reset modes, preserved seeds/schema version, active-lease rejection, identity reseeding, check-constraint restoration and full rollback after keyword-delete failure. The scratch database was dropped. **No production reset was executed and no production procedure or data was changed.** Re-run the isolated integration checks with `dotnet run --project Tests/BehaviorTests/BehaviorTests.csproj -- --reset-sql`.

## Settings editor

The Operator **Settings** button opens a two-column **Property / Value** table instead of launching a raw JSON editor. Property names are read-only; users can edit existing values but cannot add or delete rows. Nested properties use ` : ` separators and existing array entries have numbered rows. Boolean values accept `true` or `false`; numbers retain their numeric type, and whole-number settings reject fractional values. OpenAI plaintext and encrypted API key properties are excluded and preserved by settings saves; use the separate **OpenAI key** dialog.

Closing the settings window saves edits, or choose **Close without saving** to discard them. Invalid values or save failures keep the window open. If the file changed externally, discard and reopen the window to load the current version. Restart the crawler after changing settings. Only `ButchCrawler.App/appsettings.json` is edited; environment-specific settings and environment variables can override its values. Saves regenerate JSON formatting and omit comments.

## Encrypted OpenAI key editor

Choose **OpenAI key** in the Operator. If no key exists, the textbox opens enabled and receives keyboard focus automatically, ready for typing or pasting. An existing key initially appears as read-only Base64 ciphertext. Choose **Decrypt and edit** to reveal it and unlock editing; keyboard focus moves into the textbox after the button click finishes. Close the dialog (or choose **Save and close**) to save changes. A save failure keeps the dialog open. Closing without unlocking leaves an existing stored value unchanged. Clearing the editable key removes the saved override.

The key is encrypted using Windows DPAPI for the current Windows user and stored outside the program and Git project at `%LOCALAPPDATA%\HistoryCrawler\openai-key.dpapi`. `appsettings.json` contains no plaintext or encrypted key. Saving preserves unrelated JSON values and removes legacy `OpenAI:ApiKey` and `OpenAI:EncryptedApiKey` properties. This protects the key at rest, while applications running as the same Windows user can decrypt it. Decrypt and edit visibly reveals plaintext in the dialog. It is not an account-password challenge.

An existing encrypted key in `appsettings.json`, a legacy plaintext `OpenAI:ApiKey`, or `%LOCALAPPDATA%\ButchCrawler\openai-key.dpapi` is migrated automatically when the editor or crawler reads it. The project setting or legacy file is removed after the protected user-data copy is written successfully. Clearing the key deletes the private key file without changing unrelated settings.

The crawler reads the saved key at startup, taking precedence over JSON/environment configuration. Restart a running crawler after changing the key. Saving a key does not enable `Indexer:Enabled`. Existing configuration is used if there is no saved override. No actual API requests were made to test this feature.

Validation: solution builds with zero warnings/errors; all 80 behavioral checks pass, including first-time key entry without decrypting, reopening a cleared key, enabled editing after decryption, DPAPI roundtrip, save-on-close, JSON preservation, legacy migration, and settings editing.

## Review fixes (2026-10-24)

- Failed queue attempts are removed from the in-memory visited set so scheduled retries fetch again.
- HTTP redirect destinations are checked against seed scope and robots rules before requesting them.
- Fetcher and the production robots client reject private-network destinations by default, resolving and connecting to the same validated address. These clients use direct connections rather than system proxies. Set `Crawler:AllowPrivateNetwork` to `true` only for intentional local/intranet crawling; the behavioral HTTP fixtures use this override.
- Robots server errors, rate limits and unresolved redirects use `Crawler:RobotsFailClosed` without caching allow-all rules. Unexpected robots-check errors honor the same setting. Missing robots files still permit crawling. Robots literal and wildcard paths now preserve case.
- Seed/queue count methods use SQL `COUNT_BIG` to match their `long` return values.
- HTML and document indexing await the bounded queue and pass cancellation, preserving backpressure.

Validation: solution build passed with zero warnings/errors (`dotnet build ButchCrawler.sln --no-restore -m:1`); all 54 behavioral checks passed, including redirect policy, private-address rejection, robots failures/case and index queue backpressure. SQL integration and a live crawl were not run. The network connection guard applies to Fetcher and the production robots HTTP client; Chromium/Playwright traffic is not covered by this guard.

## User guide and prominent crawler status (2026-10-24)

- The main window's status is now larger and bold: red when stopped, blue when running. Running text gently pulses using a WPF opacity animation; stopping removes the animation.
- Added `Documentation/Historys_Web_Crawler_User_Guide.docx`, a 17-page plain-language manual with a clickable contents page, an illustrated workspace, step-by-step procedures, field and counter definitions, settings explanations, AI and account guidance, troubleshooting and a glossary. It distinguishes usable desktop features from optional background components and unavailable interfaces.
- Release build passed with zero warnings/errors. Normal, compact and running-state previews were checked. The guide was rendered using Microsoft Word and the packaged page renderer; all 17 pages were visually reviewed.

## AI account and credits in About (2026-10-24)

- About now contains **AI credits & account**, with links to API balance/top-up, usage, limits/spend alerts and ChatGPT/Codex usage. The app uses OpenAI API billing; it does not treat ChatGPT/Codex credits as its API balance.
- The normal application API key is not used to fetch a numeric prepaid balance. About directs users to the account that owns the key and explains how to configure account spend alerts and prepaid auto-recharge. The app cannot reliably detect a low balance before a request is rejected.
- AI research, audio and embedding clients recognize `credit_balance_exhausted`, `insufficient_quota`, organization/project spend limits and organization usage limits. They show an actionable account link and do not retry billing failures as temporary rate limits. Generic temporary HTTP 429 responses retain retry handling.
- About displays the last billing notice observed in this application session. The crawler forwards notices to a visible Operator banner with an **Open API billing…** button. A successful API request after an error reports recovery without claiming to know the remaining balance. Refresh local status refreshes the observed notice, not the online balance.
- Billing behavior follows the official [error-code guide](https://developers.openai.com/api/docs/guides/error-codes) and [spend-alert guidance](https://developers.openai.com/api/docs/guides/spend-limits).

Validation: Release build passed with zero warnings/errors; 209 behavioral checks passed. Tests verify all billing error categories, no retries for exhausted credits in all three clients, no raw server error leakage, and separation from temporary rate limits. Normal and exhausted-credit About/Operator previews were rendered and visually inspected. No paid API calls or account-setting changes were made. Restart using the Release Operator to apply the change.

## Crawl stalls, failures and keyword reporting corrected (2026-10-24)

- Removed a same-host redirect deadlock: the worker held the host semaphore, then tried to acquire it again after following a redirect. Redirect requests still observe request spacing and robots rules before fetching.
- Seed scope accepts a hostname's bare and `www` forms while keeping stored URL identities separate. Other subdomains, unrelated domains and different nondefault ports remain outside same-host scope.
- Redirect-policy failures preserve the server's redirect status and explain whether seed scope or robots rules rejected the destination. Final responses report their own HTTP status. Unresolved redirects, 403/404/410 pages and other unsuccessful responses no longer become completed source content.
- Verification detection uses visible instructions, challenge titles and the `cf-mitigated` header. Embedded Cloudflare scripts alone do not cause challenge classification. Real verification pages remain blocked and available for human review.
- `Crawler:FetchMaxAttempts` defaults to 1; `Crawler:FailureMaxAttempts` is configured to 3. Queue scheduling owns retries instead of multiplying four immediate attempts by five queue attempts. Legacy `Crawler:MaxAttempts` is a fallback for the queue budget. HTTP 429 preserves Retry-After. TLS negotiation failures report the inner error and avoid automatic repeated handshakes; certificate validation remains enabled.
- PDF and other processed-document keyword matches now emit the same `[CRAWL-MATCH]` events as HTML/audio. Startup reports the active keyword count and stops explicitly if keyword loading fails. PDF/document extraction failures and cancellation no longer report successful completion.
- Read-only inspection found 75 active research keywords and 30 stored hit records totaling 167 occurrences at the time of diagnosis. The Operator's counter covers events printed in the current run, while database totals include prior runs.

Validation: Release build passed with zero warnings/errors; 197 behavioral checks, 7 isolated PDF/keyword SQL checks and 12 AI SQL/workspace checks passed. Fixtures cover redirect status/errors/loops, scope, embedded scripts versus real verification headers, TLS certificate rejection, retry classification and PDF keyword output matching persisted counts. No external-site crawl or paid API request was made. Existing historical/dead-letter queue entries were left intact. The running Debug Operator locks its executable; the corrected build is `ButchCrawler.Operator/bin/Release/net8.0-windows/ButchCrawler.Operator.exe`. Stop the old crawl and launch that build to apply these changes.

## AI research completed (2026-10-24)

- Open **AI research…** in the Operator for collection questions with quoted sources, document summaries, people/place/date/relationship extraction, and handwriting transcription from PNG/JPEG/WebP images.
- Browse and preview saved crawl text, documents and audio transcripts. Collection questions retrieve up to 12 matching passages using lexical term matching; results describe gaps and may not cover every saved record. Local PDFs now save extracted text even when embedding indexing is disabled; previously skipped PDF text needs recrawling.
- Results and supporting quotations are stored separately in `dbo.AiAnalyses`; **Saved results → Refresh saved results** reloads the newest 200 records. Copy results or export text. Source buttons open HTTP/HTTPS originals. Handwriting retains uncertain readings and requires manual verification.
- Uses the existing encrypted OpenAI key. `AI` settings control enablement, models, image/document limits, timeout, response size and daily request limit. Defaults enable the four manually invoked features with `gpt-4.1-mini`, a 10 MB image limit and 50 requests per UTC day. Each request requires confirmation before uploading content. The API request format follows the official [Structured Outputs guide](https://developers.openai.com/api/docs/guides/structured-outputs) and [model documentation](https://developers.openai.com/api/docs/models/gpt-4.1-mini).
- Cached results avoid repeated API calls; database locks prevent simultaneous duplicate work. Atomic daily counters include retries and document sections. Cancellation, authentication, timeout, quota and invalid-evidence errors are reported in the dialog.
- `Scripts/013_CreateAiResearch.sql` is idempotent and embedded in the app. It was installed in the configured crawler database, preserving existing records. The workspace also initializes missing AI tables on first use.

Validation: Debug and Release builds passed with zero warnings/errors; 169 behavioral checks and 12 isolated SQL/workspace checks passed. All four features were exercised through the workspace using simulated API responses, including caching, source links, history and handwriting uncertainty. WPF previews were rendered and inspected at normal and minimum sizes. No paid OpenAI calls or full live crawl were run.

## Build and use

1. Extract this source ZIP into a new directory. Keep your current installation and database backup.
2. Retain your connection settings in `ButchCrawler.App/appsettings.json` (or the appropriate environment configuration). The Operator reads the same JSON files and environment variables as the crawler. Set `DOTNET_ENVIRONMENT` if you use an environment override.
3. Stop crawler instances. Run `Scripts/011_PreserveUrlIdentity.sql` against your crawler database after the existing migrations. It preserves URL case in SQL comparisons and removes case folding from queue URL hashes. It rebuilds affected ordinary indexes in one transaction and can be run again. Custom foreign keys on URL columns, partitioned/compressed/disabled indexes, or other custom dependencies need database-administrator review; errors roll back the migration. SQL Server 2017 or later is required for this script.
4. Run `dotnet build ButchCrawler.sln`, or build the solution in Visual Studio with .NET desktop development installed.
5. Set **ButchCrawler.Operator** as the startup project. Open **Seed URLs** or **Seed keywords** while the crawler is stopped. Choose **Add**, edit cells, and choose **Save changes**. **Delete selected** stages a deletion; **Save changes** applies it. **Reload** discards staged changes after confirmation.
6. Choose **View / delete logs** to select a daily crawler log. The viewer is read-only, scrolls horizontally and vertically, and supports copying text. **Refresh** reloads it. Large files show a bounded tail of the last 2 MiB. **Delete selected log** requires confirmation and a stopped crawler; files held open by another process cannot be deleted.

Seed URL fields include URL, domain, category, priority, maximum depth, follow-external, active state, notes, source type, discovery source and tags. IDs and timestamps are read-only. Keywords support keyword text, source and active state. Input validation rejects blank terms, duplicate terms, invalid URLs, and negative depth. Paths, queries and trailing slashes retain their identity.

Database edits use parameterized commands, optimistic concurrency and an atomic transaction. Concurrent edits cause an error instead of overwriting another operator's change. Deleting a keyword also deletes its recorded keyword hits in the same transaction. Renaming a keyword clears its old hit records; later crawls populate fresh matches. Deleting seed URLs retains crawl history; custom foreign keys can prevent deletion and are reported rather than bypassed. Existing queued crawl work is retained. Stop other crawler instances as well before managing seeds, and restart them after changes.

## Crawler fixes

- Request deadlines cover streamed response bodies and DNS lookups. Caller cancellation propagates.
- Oversized streamed HTML and binary files return failure instead of successful truncated content.
- Host limiter slots are released when a delayed request is cancelled; concurrent starts respect spacing.
- CAPTCHA detection requires a blocking challenge signal rather than a vendor name, article mention, URL substring or embedded widget alone.
- URL collections preserve path/query case; normalization preserves trailing slashes. Migration 011 extends this behavior to SQL URL columns and computed queue hashes. Previously skipped URLs are not automatically recovered; requeue/reseed them if needed.
- Source archives omit build output, browser binaries, logs and IDE caches.

## Validation

The solution and behavioral-test project build successfully. All **26 behavioral checks passed**, covering host limiting, caller cancellation, body timeouts, oversized chunked HTML/PDF responses, challenge detection, URL identity, seed validation, log reading and log deletion. Four WPF screen previews were rendered and inspected, and the log control's read-only and scrollbar properties were checked. The existing Python source smoke test also passed.

Run behavioral tests with:

```powershell
dotnet run --project Tests/BehaviorTests/BehaviorTests.csproj
```

An optional SQL integration test creates a uniquely named scratch database with files under `Tests/work`, exercises the URL migration twice, seed CRUD, keyword-hit cleanup and optimistic concurrency, then drops only that scratch database:

```powershell
$env:BUTCHCRAWLER_TEST_SERVER = '(localdb)\MSSQLLocalDB'
dotnet run --project Tests/BehaviorTests/BehaviorTests.csproj -- --sql .
```

**SQL integration was not verified on this machine:** LocalDB could not initialize and SQL Express rejected Windows authentication with an SSPI error. No production database was changed. Run this integration check in your configured SQL environment before applying the migration to production. Package vulnerability checking was also unavailable because NuGet's online service could not be reached; package versions were retained.

The original ZIP is unchanged. This deliverable contains source, not prebuilt executables. The active crawler project is `ButchCrawler.App`; the pre-existing `csharp ButchCrawler.App` folder is retained as supplied and is not part of the solution.

## Runtime error repairs (2026-10-24)

- Embedded migration 013 under the resource name expected by AI initialization. This fixes the null-stream exception when opening the AI workspace on a database requiring setup.
- Renew queue ownership every 20 seconds throughout robots checks, host delays, downloads and extraction. Previously a 120-second fetch could outlive its 60-second lease. Losing ownership now cancels pending work and avoids attempting a failure transition against another worker's row.
- Validation: clean isolated Debug and normal Release builds (normal Debug remains locked by running worker PID 25660), 214 behavioral checks, 12 isolated AI SQL checks, 7 crawl SQL checks, and 24 reset SQL checks. SQL tests use temporary databases; no production reset or paid API call was performed.

## Local-file keyword reporting (2026-10-24)

Local PDFs, Word documents, images and audio now save extracted text and keyword hits through the same local helper and emit dashboard match events. Added local TXT/Markdown/HTML extraction; HTML excludes script/style content. An isolated fixture is available in Tests/LocalFileSample; set LocalCrawler:RootPath to that folder for a controlled one-file test, then restore the research folder. This fixture contains synthetic text, not historical evidence. Release build passed; no live audio/API transcription was performed.

## Application Exit (2026-10-24)

Added Settings > Exit with an icon. Exit, window close and Alt+F4 share a close handler that terminates the owned crawler process tree and waits up to 10 seconds for exit. A stop failure keeps the Operator open and reports the error. Release solution build passes with zero warnings/errors. The full behavioral rerun stalled during activity classification and was stopped; a full passing rerun is not claimed for this change.

## Exit reentrancy fix (2026-10-24)

Yield to the WPF dispatcher after canceling the initial Closing event, before stopping the owned crawler and calling Close again. This prevents synchronous completion from recursively closing a window already in Closing, which previously displayed a misleading crawler-stop failure. Settings Exit is present in the current XAML. Release build passed with zero warnings/errors. Dedicated STA UI checks passed for Settings Exit with no crawler and window close with an owned test process; the latter verified the test process ended. No real crawler was started or stopped by these tests.
## Public historical newspaper discovery (2026-10-29)

- Added **Settings → Historical newspaper archives…**. It searches free public archives and requires no account or password.
- States are grouped in this order: Southwest, Northwest, Midwest, Southeast, Northeast, then Territories. The catalog contains all 50 states, the District of Columbia, Puerto Rico and the U.S. Virgin Islands.
- The crawler submits active research keywords to the Library of Congress Chronicling America collection, filters matches to the selected states, saves returned OCR/description text immediately for collection search, and queues the permitted source pages for the ordinary crawler.
- SQL checkpoints prevent the same keyword/state selection from being searched again until the configured refresh period. Matches are deduplicated by source URL and keyword. API requests are paced and capped per run.
- State archive URLs already present under Seed URLs remain supplemental sources. Subscription websites must be used manually in a normal browser, with permitted saved files imported through the local-file crawler.
- An archive API failure is logged without stopping ordinary crawling.

Validation: Debug and Release solution builds pass with zero warnings/errors. Four focused behavior checks cover regional order, complete state/territory membership, state filtering with retained OCR text, and stable search checkpoints. No paid API request or bulk crawl was run.

## Improved PDF and historical-image processing (2026-11-11)

- PDFs are now evaluated page by page. A mixed PDF keeps good selectable text and uses OCR only for pages whose digital text is absent or too thin.
- Saved PDF text includes `[Page number]` markers so search results can be traced back to the original page.
- PDF rendering quality, timeout, and the minimum useful digital-text threshold are configurable under `Ocr`.
- Images are automatically oriented, converted to grayscale, deskewed, and contrast-adjusted before Tesseract OCR when ImageMagick is available.
- Tesseract engine mode, page-segmentation mode, assumed DPI, language, and interword spacing are now supplied explicitly.
- Very large single-page scans can be reduced to a bounded 20-megapixel OCR working copy instead of being rejected or exhausting memory; the original file is never changed.
- Added validated WebP support through ImageMagick conversion.
- Real-file verification succeeded on a historical newspaper PDF and a high-resolution marriage certificate. The solution builds with zero warnings or errors, and 39 focused coverage, security, dashboard, and activity checks pass.

