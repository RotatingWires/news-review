# Tech News Feed

A small self-hosted news reader for the technical developments you actually want to follow: security, protocols, standards, formats, open source, hardware, SDR, ham radio, and PBX/VoIP.

Browse daily story tiles, bookmark useful articles, search your archive, and step backward or forward through days. SQLite keeps the history. Your own local model reviews and summarizes articles; no paid API, ChatGPT subscription, or cloud model is required.

## Stack and project conventions

- Python 3.11 or newer; developed and tested locally on Python 3.14.
- Python standard library HTTP server, SQLite, XML/HTML parsing, and HTTP transport.
- Plain HTML/CSS/JavaScript ES modules. No frontend framework, Node build, npm, ORM, or runtime Python packages.
- Purpose-based modules; changes belong in the module that owns the behavior. No monkey patches, runtime wrappers, or release-specific override files.
- `technews/__init__.py` contains the authoritative application version. The UI reads it from the server.
- Commits use the existing project convention: `v0.1.0: Describe the change`.
- Run the meaningful automated checks before committing. Update this README when setup, behavior, storage, or limitations change.

## Quick start

From the repository directory:

```bash
python3 -m technews
```

On Windows:

```powershell
py -3 -m technews
```

Open **http://127.0.0.1:8713**. There is no dependency installation step.

1. Open **Settings → Local AI** and enter the model server URL and exact installed model name.
2. Use **Test with a sample article**, then **Save AI settings**.
3. Click **Check sources**. The collector runs in the background; the page reports progress.
4. Adjust **Settings → Interests** and **Collection & history** as desired.

You can collect before setting up a model. Stories display source excerpts labeled **awaiting AI review**, rather than pretending those excerpts are generated summaries. Later, configure the model and click **Review pending**.

The daily scheduler is enabled initially for **07:00 in the server's local timezone**. An overdue run catches up about 20 seconds after startup. To prevent automatic collection while setting up:

```bash
python3 -m technews --no-scheduler
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

If your compatible server requires authentication, set `TECHNEWS_MODEL_API_KEY` in the application's environment. It is sent as a bearer token to that server and never returned to the browser. Native Ollama does not use this optional token.

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
- **From / To:** an inclusive range of edition/discovery dates. With a keyword, both constraints apply.
- **Topic:** exact tag filtering. By itself it filters the selected day; with keyword/date search it filters the historical results.
- **Bookmarks:** saved stories from all days, with the same search/tag/date filters available. Removing a bookmark does not delete the story.
- **Include filtered / duplicates:** reveals editorially filtered stories and additional coverage. Bookmarked stories remain visible even if later filtered or grouped as duplicates.
- **Pagination:** 48 results per page. Relevant stories are ordered by edition day, relevance score, and collection time.
- **Themes:** explicit dark/light selection, stored in browser local storage.

Tiles show the original headline and source, tags, the generated summary when available, why it matters, publication time when known, relevant standards/availability milestones, the reviewing model, and the original link. Excerpt-based summaries and failed reviews are labeled. Dates are formatted in the browser's timezone; edition dates remain the server's recorded date.

No sample articles are inserted into your real database. The first edition starts empty until collection runs.

## Sources

The following **27 sources** are seeded once on a fresh database:

| Group | Sources |
| --- | --- |
| Original technology sources | BetaNews, Ars Technica, BleepingComputer, TechCrunch, MacRumors, Android Authority, Engadget, MIT Technology Review, PCWorld, The Register, Tom's Hardware, CNET |
| Standards, engineering, and open source | IETF Blog, W3C News, web.dev, Mozilla Hacks, Daniel Stenberg, Cloudflare Blog, LWN, Phoronix |
| Radio and SDR | RTL-SDR Blog, RadioReference Forums, GNU Radio News, Hackaday's ham radio coverage |
| Telephony | FreePBX Blog, FreePBX Community announcements, Asterisk Blog |

Defaults are defined in `technews/defaults.py`. Changes to those defaults do not overwrite an existing installation's sources or settings.

### Add or edit a source

Open **Settings → Sources → Add source**. Enter its name, website URL, and default category. Click **Find RSS/Atom feed**, or paste a known feed URL. A blank feed URL triggers discovery during collection. Save it, then check sources.

The collector supports RSS 2.0, common RDF/RSS feeds, and Atom. Discovery follows RSS/Atom links advertised in the page's HTML. Most ordinary feeds require no new code. A website with no usable feed needs a purpose-built adapter; this version does not guess articles by indiscriminately crawling every link. Forum category feeds can be entered as sources in exactly the same way as publisher feeds.

Edit a source to change its feed, default category, or enabled state. Disabling stops collection and preserves all existing stories. Sources are not deleted through the UI, keeping historical source references stable. RadioReference starts with its general forum feed; you can replace it with a narrower public forum/category feed to reduce support-thread noise.

### Feed availability

The first-version live audit retrieved usable feeds from **26 of the 27 sources**. Asterisk's website/feed returned **HTTP 403** from this environment; it remains configured and enabled, with the error visible after collection. The app does not bypass publisher access controls. Availability can vary by host, region, or time. Feed addresses can also change.

To run a live audit without changing the database:

```bash
python3 -m tests.check_sources
```

An individual source failure is recorded next to that source and in run history. Other sources continue.

## Interests and editorial behavior

**Settings → Interests** contains editable topic rows with **Favor / Normal / Less / Exclude**, freeform editorial guidance, and explicit exclusions. Initial interests include security/privacy, protocols, formats, web standards, open source, meaningful hardware capabilities, SDR/scanners, ham radio/RF projects, and FreePBX/Asterisk/VoIP.

Saving affects the next job's AI decisions. A currently running job keeps the settings snapshot it started with. A feed's topic dropdown is an immediate view filter and does not call the model.

**Reassess history** queues every saved story for review with the current interests and model. It requires confirmation because it can be a lot of work. Existing summaries remain visible while queued; a successful new review replaces the previous summary and model metadata. Story IDs, publication dates, discovery dates, and bookmarks are preserved. This is an article archive, not a version history of every generated summary.

Filtered stories stay in the database. Their default view is hidden, so you can inspect the decisions or reassess them later. Source retention remains independent of relevance.

## Collection and AI pipeline

1. Snapshot the saved source, collection, model, and editorial settings for the job.
2. Read each enabled feed, newest first. Initial limits are eight entries per source from the last three days; undated entries remain eligible. Limits select the newest feed entries, not eight guaranteed new entries per run.
3. Normalize URLs by removing fragments and known tracking parameters. A unique URL constraint prevents repeated insertion. Preserve original headlines, excerpts, and publication dates separately from discovery/edition dates.
4. If configured, process a bounded batch of pending reviews. Extract article text from JSON-LD `articleBody`, or substantial paragraphs under `<article>`/`<main>`. A failed, blocked, or unreadable article falls back to its source excerpt. Missing text leaves the headline/link with a review error.
5. Skip obvious sponsored/deal/coupon headlines using a small fixed filter. Other editorial decisions use the configured local model and interest snapshot.
6. Supply up to five likely duplicate candidates, chosen from the 150 most recently collected reviewed stories using overlapping headline terms. The model may group coverage of the same event; meaningful follow-ups should remain separate stories.
7. Validate exact output fields, types, tag limits, score range, milestone, and duplicate ID. Save only a valid review, together with provider/model, input basis, current interests, review time, and token counts when the server reports them.

The model receives article content as untrusted data, has no tools, and is instructed to ignore embedded commands. The app renders publisher/model text using DOM text nodes, never as executable HTML. Model output validation catches structural problems, not every factual or editorial error.

Deduplication is deliberately conservative and approximate. Identical URLs are deterministic duplicates; different URLs require a model decision and can occasionally be missed or grouped incorrectly. The main tile exposes additional coverage links. The **Include filtered / duplicates** option makes those decisions inspectable. No vector database or embedding model is required.

An article's publication time can differ from its edition day. Undated articles keep a null publication timestamp; the app does not invent one. The feed preserves publication dates as reported, without independently verifying publisher timestamps. Extracted article text can be partial; “article” records the extraction basis, not a guarantee of complete access.

### Queue, retries, and scheduling

- **Check sources** fetches feeds and then reviews up to the saved limit.
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
  tech-news.db         stories, sources, settings, bookmarks, run history
  tech-news.db-wal     SQLite's live write-ahead log, when present
  tech-news.db-shm     SQLite's shared-memory file, when present
  server.log          application/HTTP logs, up to 5 MB
  server.log.1        one previous log file, up to 5 MB
```

The directory is Git-ignored. SQLite uses WAL, foreign keys, a busy timeout, and `synchronous=NORMAL`. Use local storage; WAL is not intended for an SMB/NFS network share. A sudden power loss can lose the newest commits even though the database should remain structurally consistent.

`PRAGMA user_version` tracks schema version 1. The app refuses to open a newer schema version. It creates fresh tables and seeds defaults once; future schema changes belong in explicit migrations.

The database stores original URLs/headlines, excerpts, bounded extracted text, summaries, significance, tags, milestones, dates, relevance, bookmark status, duplicate relationships, review attempts/errors, model metadata, and the interest snapshot for the latest review. History is retained indefinitely; this release does not automatically purge it.

| Environment variable | Purpose |
| --- | --- |
| `TECHNEWS_DATA_DIR` | Default runtime directory, overridden by `--data-dir` |
| `TECHNEWS_PASSWORD` | Server password, required for non-loopback serving; at least 12 characters |
| `TECHNEWS_SECURE_COOKIE=1` | Add the Secure flag when serving through HTTPS |
| `TECHNEWS_MODEL_API_KEY` | Optional bearer token for a compatible model server |

`.env.example` documents the variables. The app does **not** automatically load `.env` files. Use your shell, service manager, or a private environment file outside the repository. Secrets are not written to SQLite or returned by the settings API.

## LAN access and authentication

Loopback access is the default. For LAN access, set a password and explicitly change the listening address:

```bash
export TECHNEWS_PASSWORD='replace-with-a-long-private-password'
python3 -m technews --host 0.0.0.0 --port 8713
```

PowerShell:

```powershell
$env:TECHNEWS_PASSWORD = 'replace-with-a-long-private-password'
py -3 -m technews --host 0.0.0.0 --port 8713
```

The browser login issues an HttpOnly, SameSite=Strict cookie valid for seven days, signed with an in-memory random secret. Restarting the app invalidates sessions. Login is limited to five attempts per IP per minute. Changing the environment password requires a restart.

API writes require JSON and reject cross-origin browser requests. Static serving uses an explicit asset allowlist; database, logs, and source files are not served. Without a configured password, requests using non-local hostnames are rejected. Public news-source requests reject private/reserved network addresses, including on redirects. The local model endpoint intentionally permits LAN addresses.

This is a single-user/household application using Python's standard-library HTTP server, not a public multi-tenant service. Bind to loopback behind an HTTPS reverse proxy for remote access, and preserve the incoming `Host` header. Set `TECHNEWS_SECURE_COOKIE=1` when using HTTPS. Keep the model server on a trusted network. Source and model settings are trusted-administrator capabilities; URL checks are defense in depth, not complete isolation against hostile DNS infrastructure.

## Run as a service

The app must stay running for its scheduler to run. It can also be launched at login using your existing service/task setup.

### Linux / systemd

`deploy/tech-news-feed.service` is a template, not an installer:

1. Create a dedicated service account and a private writable data directory.
2. Place the checkout at the desired application path.
3. Edit `User`, `WorkingDirectory`, `ExecStart`, and `ReadWritePaths` to match those paths. `ProtectHome=true` means this template expects code outside `/home`, such as `/opt/tech-news-feed`.
4. If needed, put variables in `/etc/tech-news-feed.env`, readable only by the service administrator. The template loads it as an optional environment file.
5. Install the unit using your normal system administration process, then enable/start it.

The template listens on loopback. Edit the bind address and set the required password if you want direct LAN access. The system's timezone controls daily collection. The app does not install or alter system services automatically.

### Windows / Task Scheduler

Create a task that starts your Python executable with `-m technews`, and set **Start in** to the checkout directory. Configure it to run at startup/login and restart on failure. Set environment variables for the account/process that launches it. Keep the process running rather than scheduling a new copy each day; the app owns the daily schedule.

## Backup, restore, and updates

To create a consistent online backup, including while the app runs:

```bash
python3 -m technews.backup --output backups/tech-news-2026-10-15.db
```

For a custom runtime directory:

```bash
python3 -m technews.backup --database /path/to/data/tech-news.db --output /path/to/backups/news.db
```

The command refuses to overwrite an existing file and checks the new database's integrity. Store backups privately: they contain your reading history and preferences. No credentials are in the database, but environment/service configuration must be backed up separately if needed.

To restore: stop the app, move the entire existing data directory aside as a recovery copy, create a fresh data directory, and place the backup there as `tech-news.db`. Restart using that directory. Do not leave old `-wal`/`-shm` files beside a restored database. Do not copy only the main SQLite file while the app is writing; use the backup command or stop the process first.

Before an update, make a backup, pull the code, and restart. Runtime data is ignored by Git, and saved defaults are not reset. There is no browser service worker caching old application files.

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

This exercises desktop/mobile reading, bookmarks, day navigation, search, editing interests, adding/discovering a source, a fixture model connection, and themes. It writes screenshots under `/tmp` on Linux and uses temporary data. Playwright is a development-only tool, not a runtime dependency. The live source audit is a separate opt-in command described above.

GitHub Actions runs the standard-library suite on Python 3.11 and 3.14. Local verification for the initial release uses Python 3.14; a real installed user model still needs its own connection and quality check.

## Project layout

```text
technews/
  __init__.py       authoritative application version
  __main__.py       CLI, server startup, logging, daily scheduler startup
  defaults.py       initial source list, interests, collection/model defaults
  store.py          SQLite schema, queries, queue and settings persistence
  network.py        bounded HTTP transport and news-source URL checks
  feeds.py          RSS/Atom parsing/discovery and article extraction
  reviewer.py       local model adapters, editorial prompt, output validation
  collector.py      collection/review worker and daily scheduler
  validation.py     source/settings input validation
  server.py         same-origin HTTP API, authentication and static assets
  backup.py         consistent online SQLite backup command
static/
  index.html        reader, settings panels, dialogs
  style.css         responsive dark/light editorial layout
  app.js            startup, navigation and collection status
  shared.js         API, DOM, notification and formatting helpers
  feed.js           tiles, search, dates, pagination and bookmarks
  settings.js       interests, sources, local AI and scheduling controls
  icon.svg          application icon
tests/              unit/integration tests and optional browser/live-feed audits
deploy/             example systemd unit
```

## Release notes

### v0.1.0

- Initial daily feed, SQLite history, bookmarks, keyword/date/tag search and calendar navigation.
- Seed the 27 selected sources; support UI-based RSS/Atom discovery and source editing.
- Add editable interests, exclusions, collection schedule and bounded processing controls.
- Support local Ollama and compatible model servers with validated summaries, significance, tags and conservative duplicate grouping.
- Preserve readable source excerpts while AI is unavailable; expose retry/reassessment and run history.
- Add password protection for LAN serving, light/dark themes, online backups, deployment guidance and automated coverage.
