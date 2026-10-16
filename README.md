# NewsReview

A small self-hosted news reader for whatever you want to follow. Choose sources, describe your interests, and let your own local model review the stories. The initial source list and interests are tuned to technology, security, radio, and telephony; they are editable presets, not a restriction on what NewsReview can cover.

Browse daily story tiles, bookmark useful articles, search your archive, and step backward or forward through days. SQLite keeps the history. Your own local model reviews and summarizes articles; no paid API, ChatGPT subscription, or cloud model is required.

## Stack and project conventions

- Python 3.11 or newer; developed and tested locally on Python 3.14.
- Python standard library HTTP server, SQLite, XML/HTML parsing, and HTTP transport.
- Plain HTML/CSS/JavaScript ES modules. No frontend framework, Node build, npm, ORM, or runtime Python packages.
- Purpose-based modules; changes belong in the module that owns the behavior. No monkey patches, runtime wrappers, or release-specific override files.
- `newsreview/__init__.py` contains the authoritative application version. The UI reads it from the server.
- Commits use the existing project convention: `v0.4.0: Describe the change`, followed by a blank line and detailed bullets explaining behavior, implementation, and validation. Earlier release commits now include those bodies too. Their trees are unchanged, but rewriting messages changes commit IDs; existing clones should fetch and reconcile local branches before pushing.
- Run the meaningful automated checks before committing. Update this README when setup, behavior, storage, or limitations change.

## Quick start

From the repository directory:

```bash
python3 -m newsreview
```

On Windows:

```powershell
py -3 -m newsreview
```

Open **http://127.0.0.1:8714**. There is no dependency installation step.

1. Open **Settings → Local AI** and enter the model server URL and exact installed model name.
2. Use **Test with a sample article**, then **Save AI settings**.
3. Click **Check sources**. The collector runs in the background; the page reports progress.
4. Adjust **Settings → Interests** and **Collection & history** as desired.

You can collect before setting up a model. Stories display source excerpts labeled **awaiting AI review**, rather than pretending those excerpts are generated summaries. Later, configure the model and click **Review pending**.

The daily scheduler is enabled initially for **07:00 in the server's local timezone**. An overdue run catches up about 20 seconds after startup. To prevent automatic collection while setting up:

```bash
python3 -m newsreview --no-scheduler
```

This flag disables the scheduler for that process only. The UI's schedule setting is persistent. Only one application process should use a data directory.

## Connect your own model

The application does not install, download, choose, or start a model for you. Run your preferred inference server separately. Switching computers or models requires changing the AI settings, not the collector or UI code.

### Ollama

Install [Ollama](https://docs.ollama.com/), start it, and download a model that fits your hardware. Use `ollama list` to find its exact installed name.

In **Settings → Local AI**:

| Field | Value |
| --- | --- |
| Server type | Ollama |
| Server base URL | `http://127.0.0.1:11434` |
| Model name | Exact installed model name from `ollama list` |

The adapter calls `/api/chat` with streaming disabled and the requested JSON schema in `format`. See [Ollama's chat API](https://docs.ollama.com/api/chat) and [structured outputs](https://docs.ollama.com/capabilities/structured-outputs).

### LM Studio or another compatible local server

Load a model and start the inference server. Set:

| Field | Example |
| --- | --- |
| Server type | Compatible local chat server |
| Server base URL | `http://127.0.0.1:1234/v1` |
| Model name | The model identifier exposed by your server |

The adapter appends `/chat/completions`, requests JSON output, and validates the returned object. The API prefix belongs in the base URL; the final endpoint does not. See [LM Studio's chat API](https://lmstudio.ai/docs/developer/openai-compat/chat-completions).

If your compatible server requires authentication, set `NEWSREVIEW_MODEL_API_KEY` in the application's environment. It is sent as a bearer token to that server and never returned to the browser. Native Ollama does not use this optional token.

### Move the model to another computer

Run the model server on the stronger computer, configure it to listen on the appropriate LAN interface, and use that computer's LAN address in the AI settings. Allow the connection through its firewall. The **application backend** needs access to the model server; your browser never connects to it directly.

For example, a model at `http://192.168.1.50:11434` can serve a reader running on another machine. `127.0.0.1` always refers to the computer running the app.

Existing stories, summaries, bookmarks, and dates remain in SQLite when changing models. New reviews use the selected provider and model. A blank model name pauses AI review while collection continues.

### Small-computer controls

- Lower **Maximum article characters** to reduce input size. The default is 12,000 characters; it is a character bound, not a token count.
- Lower **Maximum AI reviews per run** to limit work. The default is 40. Unprocessed stories remain queued.
- Increase **Request timeout** for slow inference; the supported range is 10–600 seconds.
- Processing is sequential: one article is reviewed at a time.
- **Review pending** processes another bounded batch without fetching the feeds again.
- Three consecutive review failures stop that batch, so an unavailable model does not cause requests for the whole archive.

A small model must still follow instructions and produce valid structured output. The connection test checks a real sample review, not just an HTTP health endpoint. It does not measure factual quality; review a few actual summaries after changing models. No external model is used as an automatic fallback.

## Reading and searching

- **Daily feed:** an edition contains stories first discovered on the selected day, using the server's local calendar date.
- **Previous/next:** steps through calendar days, including quiet days. **Today** returns to the current edition and clears filters.
- **Search:** a keyword searches across all stored days, matching headlines, summaries, source excerpts, and tags. `%` and `_` are treated literally. Search is a case-insensitive SQLite `LIKE` search for ordinary ASCII text, not a semantic search engine.
- **Date/time range:** Use one field, for example `9/1/26 - 10/1/26, 10am - 3pm`. Either range alone is also accepted. Dates and times are separated by a comma.
- **Date syntax:** TaskList-style input such as `10/3 - 10/8`. Each side accepts `m/d`, `m/d/yy`, or `m/d/yyyy`; omitted years use the server's current year. Hyphens, en dashes, and em dashes work. Ranges are inclusive and filter edition/discovery dates. Include years when crossing New Year.
- **Time syntax:** TaskList-style input such as `9am - 9pm` or `9:30pm - 2am`. Times filter the discovery timestamp in the **server's local timezone**, not publication time. Overnight ranges include both sides of midnight on each selected edition date. As in TaskList, times without am/pm use the AM interpretation (`12` means midnight); 24-hour notation is not accepted.
- Keyword, date, and time filters combine and search across the archive. Press Enter or Search to apply them; changing a range also applies it on blur.
- **Topics:** choose multiple checkboxes in the Topics dropdown. Results match **any** selected topic; select All topics to remove that restriction. By itself it filters the selected day; with keyword/date/time search it filters the historical results. Clicking a tile's tag adds that topic to the current selection.
- **Bookmarks:** saved stories from all days, with the same search/tag/date filters available. Removing a bookmark does not delete the story.
- **Include filtered / duplicates:** reveals editorially filtered stories and additional coverage. Bookmarked stories remain visible even if later filtered or grouped as duplicates.
- **Pagination:** 48 results per page. Relevant stories are ordered by edition day, relevance score, and collection time.
- **Themes:** Windows 95-style raised buttons, recessed fields, square panels, title bars, and explicit light/dark themes. Light is the default; the selection is saved in browser local storage.

Tiles show the original headline and source, tags, the generated summary when available, why it matters, publication time when known, relevant standards/availability milestones, the reviewing model, and the original link. Excerpt-based summaries and failed reviews are labeled. Dates are formatted in the browser's timezone; edition dates remain the server's recorded date.

No sample articles are inserted into your real database. The first edition starts empty until collection runs.

## Sources

The following **25 sources** are seeded once on a fresh database:

| Group | Sources |
| --- | --- |
| Original technology sources | BetaNews, Ars Technica, BleepingComputer, TechCrunch, MacRumors, Android Authority, Engadget, MIT Technology Review, PCWorld, The Register, Tom's Hardware, CNET |
| Standards, engineering, and open source | IETF Blog, W3C News, web.dev, Mozilla Hacks, Cloudflare Blog, Phoronix |
| Radio and SDR | RTL-SDR Blog, RadioReference Forums, GNU Radio News, Hackaday's ham radio coverage |
| Telephony | FreePBX Blog, FreePBX Community announcements, Asterisk Blog |

Defaults are defined in `newsreview/defaults.py`. Changes to defaults normally do not overwrite an existing installation's sources or settings. The v0.3.0 migration specifically removes Daniel Stenberg and LWN from active source configuration, as requested, while retaining the source records behind archived stories. This migration runs once; a manually restored source is not removed again.

[SOURCES.md](SOURCES.md) links to the 13 current approved additions separately from the original 12 websites.

### Add or edit a source

Open **Settings → Sources → Add source**. Enter its name, website/listing URL, and default category, then choose a collection method:

| Method | Behavior |
| --- | --- |
| Automatic | Use the specified feed or discover an advertised feed; fall back to reading the website listing if the feed is absent or fails. |
| RSS/Atom feed only | Use the specified/discovered feed and report failure rather than switching to page extraction. |
| Website page | Read the listing page directly; no feed is needed, and any saved feed URL is ignored. |

Click **Preview stories** to see the detected method and up to eight candidate headlines/links without adding anything to history. Save the source, then **Refresh sources**. The source editor also offers **Find RSS/Atom feed** for feed discovery.

The collector supports RSS 2.0, common RDF/RSS feeds, and Atom. Feed discovery follows RSS/Atom links advertised in the page's HTML. Website mode extracts structured article lists (JSON-LD), article-card headline links, and heading links. It prefers the main content area where available, excludes navigation/footer/author/category links and external hosts, and removes duplicate URLs. Article-card headline links take precedence over date archives and incidental links inside the card. Website URLs should point to a news/category listing rather than a single article.

**Article URL contains** is an optional literal substring filter for website extraction, such as `/news/`, `/posts/`, or `/threads/`. It is not a regular expression or CSS selector, and does not affect feed entries. Preview it to make sure you are collecting the intended articles. Only links on the listing's exact hostname are followed; subdomains are separate sources. Website mode reads one downloaded page, not an entire site, and does not paginate or execute JavaScript. Ambiguous layouts, JavaScript-only pages, authentication, paywalls, and bot restrictions can still require a custom adapter or a different public listing/feed. Missing publication dates remain unknown.

**Feeds or website listings find stories; article webpages provide review text.** During AI review the collector visits each article URL and attempts to extract readable text, even when the feed has only a headline. This is an HTTP download, not a JavaScript browser session. A site that requires JavaScript, authentication, or a subscription may still be unreadable. In that case the model uses the available source excerpt; if there is no usable article text or excerpt, the app keeps the headline/link and reports the review error. It does not summarize a headline as if it had read the article. Without a model configured, collection still works, but article extraction waits for the review stage.

Edit a source to change its feed, default category, or enabled state. Disabling stops collection and preserves all existing stories. Sources are not deleted through the UI, keeping historical source references stable. RadioReference starts with its general forum feed; you can replace it with a narrower public forum/category feed to reduce support-thread noise.

### RadioReference and other forum feeds

Forum entries are treated as story candidates, using the entry's title, link, and available excerpt. The app does not monitor every post or assume a discussion is verified reporting. The local model applies your interests to the retrieved text; without a model, entries appear as unreviewed excerpts. The same normalized thread URL is inserted only once, so later replies do not automatically create another tile. Publication/activity timestamps depend on what the forum feed exposes.

A broad forum feed can contain troubleshooting, casual discussion, and unrelated topics. Point the source at a specific scanner/SDR/manufacturer/category feed for a narrower selection, or disable it if you want publisher-style reporting only. AI relevance filtering reduces noise but cannot guarantee every discussion is interesting or factually correct.

### Health and refresh results

The Sources tab shows a persistent refresh status while collection/review runs, followed by its recorded result. **Refresh sources** uses the same background job as **Check sources**: collect candidates, then review a bounded batch if a model is configured. Individual failures remain attached to their sources and in run history; they do not prevent other sources from finishing.

Each source records its last check, last success, consecutive failures, entry count, actual method, newest reported publication, and last newly inserted story. Status distinguishes **Not checked**, **Healthy**, **Needs attention**, **Check failed**, **Broken — repeated failures** (three or more consecutive failed checks), and **Disabled**. A successful check resets the failure counter.

The default inactivity warning interval is **14 days**, editable per source (1–365). Warnings cover checks older than that interval, an old newest publication date, an empty result, or no newly observed stories when dates are unavailable. An inactive/stale warning is an observation, not proof a publisher has shut down. Increase the interval for monthly/occasional publishers. An automatic switch from a configured broken feed to a usable website page is marked as a warning, with the original feed error visible.

Browser-to-app connection failures are separate from publisher/source errors. A persistent banner identifies an unreachable server, unexpected non-JSON responses, or a missing runtime database. Read requests reconnect periodically; write requests are never replayed automatically. If the start request loses its response, the UI says the refresh was **not confirmed**, then checks server status rather than claiming it failed or succeeded.

### Feed availability

The first-version audit retrieved usable feeds from all retained default sources except Asterisk, whose website/feed returned **HTTP 403** in that environment. It remains configured and enabled, with the error visible after collection. The app does not bypass publisher access controls. Availability can vary by host, region, or time. Feed addresses can also change. The v0.3.0 website extractor was additionally checked against public Hackaday and GNU Radio listing pages.

To run a live audit without changing the database:

```bash
python3 -m tests.check_sources
```

To preview a website listing without saving data:

```bash
python3 -m tests.check_sources --website https://example.com/news/ --contains /news/ --details
```

An individual source failure is recorded next to that source and in run history. Other sources continue.

## Interests and editorial behavior

**Settings → Interests** contains editable topic rows with **Favor / Normal / Less / Exclude**, freeform editorial guidance, and explicit exclusions. Initial interests include security/privacy, protocols, formats, web standards, open source, meaningful hardware capabilities, SDR/scanners, ham radio/RF projects, and FreePBX/Asterisk/VoIP.

Saving affects the next job's AI decisions. A currently running job keeps the settings snapshot it started with. A feed's topic dropdown is an immediate view filter and does not call the model.

**Reassess history** queues every saved story for review with the current interests and model. It requires confirmation because it can be a lot of work. Existing summaries remain visible while queued; a successful new review replaces the previous summary and model metadata. Story IDs, publication dates, discovery dates, and bookmarks are preserved. This is an article archive, not a version history of every generated summary.

Filtered stories stay in the database. Their default view is hidden, so you can inspect the decisions or reassess them later. Source retention remains independent of relevance.

### Fresh start: selective reset

Under **Settings → Collection & history**, click **Fresh start...**, choose the categories to clear, and type `RESET SELECTED`. History and run/source-check history are initially checked. Bookmarks, interest topics/ranks, editorial guidance, exclusions, configured sources, AI settings, and collection settings can each be selected separately.

Clearing history keeps bookmarked stories unless Bookmarks is also selected. Bookmarks alone removes saved flags without deleting stories. Clearing sources retires the configuration while preserving labels on retained stories; defaults are not reseeded. Resetting AI/collection settings restores their defaults. Unchecked preferences remain intact. The operation is transactional and rejected while collection/review runs.

Clearing history or runs suppresses automatic collection for the rest of the day, including after restart. Manual collection remains available. Removed URLs may be collected again. Backups and logs are separate files and are retained. This is not a secure disk wipe.

## Collection and AI pipeline

1. Snapshot the saved source, collection, model, and editorial settings for the job.
2. Read each enabled source using its saved Automatic/feed/page method. Sort by publication date when available; undated website entries retain page order. Initial limits are eight entries per source from the last three days; undated entries remain eligible. Limits select the newest available candidates, not eight guaranteed new entries per run.
3. Normalize URLs by removing fragments and known tracking parameters. A unique URL constraint prevents repeated insertion. Preserve original headlines, excerpts, and publication dates separately from discovery/edition dates.
4. If configured, process a bounded batch of pending reviews. Extract article text from JSON-LD `articleBody`, or substantial paragraphs under `<article>`/`<main>`. A failed, blocked, or unreadable article falls back to its source excerpt. Missing text leaves the headline/link with a review error.
5. Skip obvious sponsored/deal/coupon headlines using a small fixed filter. Other editorial decisions use the configured local model and interest snapshot.
6. Supply up to five likely duplicate candidates, chosen from the 150 most recently collected reviewed stories using overlapping headline terms. The model may group coverage of the same event; meaningful follow-ups should remain separate stories.
7. Validate exact output fields, types, tag limits, score range, milestone, and duplicate ID. Save only a valid review, together with provider/model, input basis, current interests, review time, and token counts when the server reports them.

The model receives article content as untrusted data, has no tools, and is instructed to ignore embedded commands. The app renders publisher/model text using DOM text nodes, never as executable HTML. Model output validation catches structural problems, not every factual or editorial error.

Deduplication is deliberately conservative and approximate. Identical URLs are deterministic duplicates; different URLs require a model decision and can occasionally be missed or grouped incorrectly. The main tile exposes additional coverage links. The **Include filtered / duplicates** option makes those decisions inspectable. No vector database or embedding model is required.

An article's publication time can differ from its edition day. Undated articles keep a null publication timestamp; the app does not invent one. The feed preserves publication dates as reported, without independently verifying publisher timestamps. Extracted article text can be partial; “article” records the extraction basis, not a guarantee of complete access.

### Queue, retries, and scheduling

- **Check sources / Refresh sources** fetches feeds or website listings and then reviews up to the saved limit.
- **Review pending** skips feed collection and processes the existing queue.
- Manual and scheduled jobs share a lock, so only one collection/review job runs at a time.
- Each failed review increments an attempt count. After three attempts, use **Reset failed reviews** once the underlying issue is fixed.
- Fresh/unattempted entries are processed first, newest first; older pending entries can wait on busy days. Increase the per-run limit or run additional review batches to catch up.
- A daily collection starts after its configured local time if no collection was started that day. A manual source check counts for that day; review-only jobs do not.
- There is no wake-from-sleep mechanism. If the service was offline, it checks current feeds when it resumes; it cannot reconstruct stories that have already disappeared from those feeds.
- The schedule survives restarts through the SQLite run history. Partial/failed runs count as today's attempt; retry manually if needed. An interrupted process is marked interrupted at the next startup.
- Source requests are bounded in size and time, with a short pause between sources. Article downloads are limited to 3 MB; model responses to 1 MB.

## Data, configuration, and logging

By default, runtime data is stored in `./data` relative to the working directory:

```text
data/
  newsreview.db         stories, sources, settings, bookmarks, run history
  newsreview.db-wal     SQLite's live write-ahead log, when present
  newsreview.db-shm     SQLite's shared-memory file, when present
  server.log          application/HTTP logs, up to 5 MB
  server.log.1        one previous log file, up to 5 MB
```

The directory is Git-ignored. SQLite uses WAL, foreign keys, a busy timeout, and `synchronous=NORMAL`. Use local storage; WAL is not intended for an SMB/NFS network share. A sudden power loss can lose the newest commits even though the database should remain structurally consistent.

`PRAGMA user_version` tracks schema version 2. The v0.3.0 migration adds source collection/health fields and retires the two removed defaults without deleting their archived stories. The app refuses to open a newer schema version. It creates fresh tables and seeds defaults once; future schema changes belong in explicit migrations.

The database stores original URLs/headlines, excerpts, bounded extracted text, summaries, significance, tags, milestones, dates, relevance, bookmark status, duplicate relationships, review attempts/errors, model metadata, and the interest snapshot for the latest review. History is retained until explicitly cleared; there is no automatic expiration.

| Environment variable | Purpose |
| --- | --- |
| `NEWSREVIEW_DATA_DIR` | Default runtime directory, overridden by `--data-dir` |
| `NEWSREVIEW_PASSWORD` | Server password, required for non-loopback serving; at least 12 characters |
| `NEWSREVIEW_SECURE_COOKIE=1` | Add the Secure flag when serving through HTTPS |
| `NEWSREVIEW_MODEL_API_KEY` | Optional bearer token for a compatible model server |

`.env.example` documents the variables. The app does **not** automatically load `.env` files. Use your shell, service manager, or a private environment file outside the repository. Secrets are not written to SQLite or returned by the settings API.

## LAN access and authentication

Loopback access is the default. For LAN access, set a password and explicitly change the listening address:

```bash
export NEWSREVIEW_PASSWORD='replace-with-a-long-private-password'
python3 -m newsreview --host 0.0.0.0 --port 8714
```

PowerShell:

```powershell
$env:NEWSREVIEW_PASSWORD = 'replace-with-a-long-private-password'
py -3 -m newsreview --host 0.0.0.0 --port 8714
```

The browser login issues an HttpOnly, SameSite=Strict cookie valid for seven days, signed with an in-memory random secret. Restarting the app invalidates sessions. Login is limited to five attempts per IP per minute. Changing the environment password requires a restart.

API writes require JSON and reject cross-origin browser requests. Static serving uses an explicit asset allowlist; database, logs, and source files are not served. Without a configured password, requests using non-local hostnames are rejected. Public news-source requests reject private/reserved network addresses, including on redirects. The local model endpoint intentionally permits LAN addresses.

This is a single-user/household application using Python's standard-library HTTP server, not a public multi-tenant service. Bind to loopback behind an HTTPS reverse proxy for remote access, and preserve the incoming `Host` header. Set `NEWSREVIEW_SECURE_COOKIE=1` when using HTTPS. Keep the model server on a trusted network. Source and model settings are trusted-administrator capabilities; URL checks are defense in depth, not complete isolation against hostile DNS infrastructure.

## Run as a service

The app must stay running for its scheduler to run. It can also be launched at login using your existing service/task setup.

### Linux / systemd

`deploy/newsreview.service` is a template, not an installer:

1. Create a dedicated service account and a private writable data directory.
2. Place the checkout at the desired application path.
3. Edit `User`, `WorkingDirectory`, `ExecStart`, and `ReadWritePaths` to match those paths. `ProtectHome=true` means this template expects code outside `/home`, such as `/opt/newsreview`.
4. If needed, put variables in `/etc/newsreview.env`, readable only by the service administrator. The template loads it as an optional environment file.
5. Install the unit using your normal system administration process, then enable/start it.

The template listens on loopback. Edit the bind address and set the required password if you want direct LAN access. The system's timezone controls daily collection. The app does not install or alter system services automatically.

### Windows / Task Scheduler

Create a task that starts your Python executable with `-m newsreview`, and set **Start in** to the checkout directory. Configure it to run at startup/login and restart on failure. Set environment variables for the account/process that launches it. Keep the process running rather than scheduling a new copy each day; the app owns the daily schedule.

## Backup, restore, and updates

To create a consistent online backup, including while the app runs:

```bash
python3 -m newsreview.backup --output backups/newsreview-2026-10-15.db
```

For a custom runtime directory:

```bash
python3 -m newsreview.backup --database /path/to/data/newsreview.db --output /path/to/backups/news.db
```

The command refuses to overwrite an existing file and checks the new database's integrity. Store backups privately: they contain your reading history and preferences. No credentials are in the database, but environment/service configuration must be backed up separately if needed.

To restore: stop the app, move the entire existing data directory aside as a recovery copy, create a fresh data directory, and place the backup there as `newsreview.db`. Restart using that directory. Do not leave old `-wal`/`-shm` files beside a restored database. Do not copy only the main SQLite file while the app is writing; use the backup command or stop the process first.

Before an update, make a backup, pull the code, and restart. Runtime data is ignored by Git, and saved defaults are not reset. There is no browser service worker caching old application files.

**Do not delete the data folder while the app is running.** This removes its database, preferences, and logs outside the application's lifecycle. If this happened, stop and restart NewsReview to create a fresh database, or restore a backup before restarting. The running server now returns an actionable HTTP 503 instead of silently recreating an empty SQLite file or reporting a generic internal error. Use **Fresh start** in Settings when you only want to clear reading history and retain configuration.

### Upgrade from the first version

Stop the old process before updating. The Python package and launch command are now `newsreview`; environment variables use `NEWSREVIEW_` (update the previous `TECHNEWS_` names in your shell/service configuration). The default port is now **8714**, so update reverse-proxy targets or bookmarks that used **8713**. Services use the `newsreview.service` template and new checkout path. Browser sessions and theme preferences start fresh under the new name/port.

Point `--data-dir` at your existing data directory. If `newsreview.db` does not exist but `tech-news.db` does, startup makes a consistent SQLite backup into the new filename and preserves your sources, interests, settings, bookmarks, and archive. The original is left untouched as a recovery copy. Once the new database exists, the old file is never imported again, including after a history reset. Keep a separate backup and remove the old recovery file yourself when no longer needed. Do not run old and new versions against that directory simultaneously.

## Tests and verification

The automated suite needs only Python:

```bash
python3 -m unittest discover -v
```

It uses real temporary SQLite databases and local HTTP fixtures for RSS, article pages, Ollama, and compatible model responses. Tests cover parsing, extraction/excerpt fallback, settings validation, model output validation, duplicate references, queue retries, idempotent collection, source failures, history/search, bookmarks, authentication, origin checks, and static-file boundaries. No tests contact the live sources or require a real model. No monkey patches are used.

Optional real-browser verification requires Playwright and its Chromium browser in your development environment:

```bash
python3 -m tests.browser_smoke
```

This exercises desktop/mobile reading, bookmarks, day navigation, keyword/date/time search, multi-topic selection, editing interests, feed discovery, website preview, source-health display, refresh completion, offline/reconnect handling, a fixture model connection, confirmed history deletion, and both Windows 95 themes. It writes screenshots under `/tmp` on Linux and uses temporary data. Playwright is a development-only tool, not a runtime dependency. The live source audit is a separate opt-in command described above.

GitHub Actions runs the standard-library suite on Python 3.11 and 3.14. Local verification for the initial release uses Python 3.14; a real installed user model still needs its own connection and quality check.

## Project layout

```text
newsreview/
  __init__.py       authoritative application version
  __main__.py       CLI, server startup, logging, daily scheduler startup
  defaults.py       initial source list, interests, collection/model defaults
  store.py          SQLite schema, queries, queue and settings persistence
  migration.py      safe import of the first-version database filename
  search.py         TaskList-compatible date/time range parsing
  network.py        bounded HTTP transport and news-source URL checks
  feeds.py          RSS/Atom parsing/discovery and article extraction
  listings.py       website article-card/headline/structured-list extraction
  sources.py        Automatic/feed/page collection strategy
  health.py         observed source health and inactivity warnings
  reviewer.py       local model adapters, editorial prompt, output validation
  collector.py      collection/review worker and daily scheduler
  validation.py     source/settings input validation
  server.py         same-origin HTTP API, authentication and static assets
  backup.py         consistent online SQLite backup command
  transfer.py       validated ZIP snapshots and transactional restoration
static/
  index.html        reader, settings panels, dialogs
  style.css         responsive Windows 95 light/dark layout
  app.js            startup, navigation and collection status
  shared.js         API, DOM, notification and formatting helpers
  feed.js           tiles, search, dates, pagination and bookmarks
  settings.js       interests, sources, local AI and scheduling controls
  transfer.js       browser ZIP download/upload and restore confirmation
  icon.svg          application icon
tests/              unit/integration tests and optional browser/live-feed audits
deploy/             example systemd unit
```

## Release notes

### v0.4.0

- Add Help after Settings, explaining AI filtering, summaries, listing/feed collection, and forum scope.
- Add validated ZIP export/restore with automatic recovery backups and stale-tab write protection.
- Add selective fresh starts with bookmarked-story preservation by default.
- Fill the viewport, pin application/dialog chrome, and scroll content internally in both themes.
- Combine date/time search, restore the blinking collection indicator, and refine labels/dropdown spacing.
- Expand backup/reset/search/browser tests and restore detailed release commit bodies.


### v0.3.0

- Remove Daniel Stenberg and LWN from fresh and existing active source lists, preserving archived stories.
- Add no-RSS website collection, feed-first automatic fallback, article URL filters, and preview before saving.
- Track source success, failures, activity, publication age, entry counts, and actual collection method; show adjustable stale/inactive/broken warnings.
- Add Sources-tab refresh progress/results, persistent connection/storage errors, and safe read-only reconnection without replaying writes.
- Detect a data folder/database deleted during runtime and explain how to restart or restore; do not silently create an empty database during requests.
- Expand deterministic regression/browser coverage and document forum-feed behavior and website-extraction limits.

### v0.2.0

- Rebrand the application, Python package, environment variables, service, and database to NewsReview; move the default port to 8714.
- Import existing first-version databases without resetting stored sources, preferences, or history.
- Replace the editorial UI with a responsive Windows 95 light/dark interface while retaining tiles, bookmarks, collection controls, and settings.
- Add multi-topic checkbox filtering and TaskList-style date/time range search, including overnight ranges.
- Add a confirmed fresh-start action that clears archive/run history while retaining sources and preferences, and prevents an immediate scheduled refill.
- Document RSS discovery versus article webpage fetching, and provide the separately linked approved source additions.

### v0.1.0

- Initial daily feed, SQLite history, bookmarks, keyword/date/tag search and calendar navigation.
- Seed the 27 selected sources; support UI-based RSS/Atom discovery and source editing.
- Add editable interests, exclusions, collection schedule and bounded processing controls.
- Support local Ollama and compatible model servers with validated summaries, significance, tags and conservative duplicate grouping.
- Preserve readable source excerpts while AI is unavailable; expose retry/reassessment and run history.
- Add password protection for LAN serving, light/dark themes, online backups, deployment guidance and automated coverage.

## ZIP import/export and interface help

**Help**, immediately after Settings, explains reviews, source selection, search, warnings, and backups. Add a homepage/category listing or RSS/Atom feed, then preview candidates. A single article is not an ongoing source. For forums, prefer a relevant subforum/category or its feed listing threads; each linked thread becomes a candidate. A single thread is one discussion, and later replies do not create fresh stories for the same URL.

**Import/Export... → Download Export** snapshots the live database, including committed WAL changes. The ZIP contains exactly `newsreview.db` and `manifest.json` (version, checksum, counts). It includes stories, bookmarks, sources, interests, editorial guidance, exclusions, model/collection settings, and run history. Environment credentials, logs, previous backups, and browser theme are excluded. Backups contain private reading/preferences data and are not encrypted.

Choose an export ZIP and check the replacement confirmation to restore. This replaces rather than merges the dataset. Uploads are limited to 256 MiB compressed and 1 GiB of database data. The server rejects extra archive members, damaged checksums, unsupported/newer versions, unexpected schema objects, invalid relationships, or invalid configuration. It imports validated rows into its own schema, without executing imported schema SQL. Before replacing data, it writes a recovery ZIP to `data/backups/before-restore-*.zip`; failure to save it aborts the operation. Keep enough free disk space for the current database, upload, extracted database, and recovery ZIP. Restore is blocked while collection/review runs. Interrupted imported runs are marked interrupted.

Restore and reset change a dataset revision: other tabs refresh before making further writes. After an interrupted upload response, the UI reloads actual server data and reports that the restore could not be confirmed instead of retrying it automatically. Recovery ZIPs can be restored through the same dialog. The offline SQLite backup command remains available for larger datasets.

The app fills the viewport. Its top and bottom bars stay visible while the central content scrolls. Dialog titles and action bars similarly stay visible while their bodies scroll. A yellow/orange blinking status light indicates collection/review; reduced-motion preferences use a static orange indicator.
