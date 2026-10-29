> [!WARNING]
> This project is fully vibecoded, probably inefficient, but it does what I wanted lol

# NewsReview

A self-hosted news reader that collects your chosen sources and uses a local model or OpenRouter to review and summarize stories. Browse daily tiles, search your archive, and bookmark articles; SQLite keeps the history.

**[Full manual](MANUAL.md)** — complete setup, model configuration, sources, reading/search, scheduling, backup/restore, troubleshooting, and release notes, preserved from the original README.

## Quickstart

Requires **Python 3.11+**. No runtime packages are normally needed.

```sh
git clone https://github.com/RotatingWires/news-review.git
cd news-review
python3 -m newsreview
```

On Windows, use `py -3 -m newsreview`. Open **http://127.0.0.1:8714**.

1. Run your preferred local model server separately, or configure an OpenRouter API key; NewsReview does not install or start a model.
2. In **Settings → AI**, choose the server type, URL, and model name, then **Test with a sample article** and **Save**. See [Ollama](MANUAL.md#ollama), [LM Studio](MANUAL.md#lm-studio-or-another-compatible-local-server), or [OpenRouter](MANUAL.md#openrouter) for examples.
3. Click **Check sources** and adjust **Settings → Interests** and **Collection & history** as desired.

You can collect without a model: stories show source excerpts marked **awaiting AI review**. Configure a model later and click **Review pending**. No paid API or cloud model is required.

Automatic collection initially runs **every 24 hours**. Change **Settings → Collection & history → Collect every (hours)** to any whole-number interval from 1 to 8760, such as 3 hours. Overdue work catches up about 20 seconds after startup. To set up without automatic collection, run `python3 -m newsreview --no-scheduler`. Only one process should use a data directory.

Data lives in `./data`; runtime logs use `./logs/console.log`, matching TaskList. Only one log file is kept: it truncates in place when the next entry would exceed 10 MiB. Back up through **Settings → Collection & history → Import/Export...**. On hosts without system timezone data, install `tzdata` for publication-date browsing and time-range search (`python3 -m pip install tzdata`, or `py -3 -m pip install tzdata` on Windows).

## Security and privacy

The local quickstart uses plain HTTP on loopback. HTTP does not encrypt passwords, API keys, session cookies, or reading data in transit. Use HTTPS for remote access as described below.

On first startup, enter the console setup token in the browser and create a password (at least 8 characters). This applies to loopback and LAN access.

## LAN access and authentication

Loopback access is the default. For LAN access, explicitly change the listening address:

```bash
python3 -m newsreview --host 0.0.0.0 --port 8714
```

PowerShell:

```powershell
py -3 -m newsreview --host 0.0.0.0 --port 8714
```

On first startup, NewsReview prints a one-time setup token in the server console. Open the app, enter that token, and create and confirm a password (8–1024 characters). The setup/login page uses the same layout as TaskList and TaskList Stats. The token changes on restart until setup succeeds, then is discarded. It is not an API key and is not written to `logs/console.log` (a service manager may capture console output).

The salted PBKDF2-SHA256 password hash is saved automatically in `data/auth.json` (or your chosen data directory), with owner-only permissions on POSIX. Future starts ask only for your password. Existing `NEWSREVIEW_PASSWORD` deployments seed this file once; an existing credentials file takes precedence, so the environment variable can then be removed. Credential creation uses an exclusive file write and does not require hard-link support, including on Windows drives. Malformed or interrupted credential files stop startup rather than reopening setup; if first-run setup was interrupted, stop the app and remove only the incomplete `auth.json` before trying again.

ZIP export/import and Fresh start keep authentication separate: they never export, replace, or delete this file. Preserve it separately when moving the installation. To reset a forgotten password, stop the app, remove only `auth.json`, restart, and use the new console token. Your stories and settings stay intact.

The browser login issues an HttpOnly, SameSite=Strict cookie valid for seven days, signed with an in-memory random secret. Restarting the app invalidates sessions. Login is limited to five attempts per IP per minute. Setup uses the same five-attempt limit.

API writes require JSON and reject cross-origin browser requests. Static serving uses an explicit asset allowlist; database, logs, and source files are not served. Until setup is complete, all reading data and settings remain protected; only the setup/login page and authentication endpoints are accessible. Public news-source requests reject private/reserved network addresses, including on redirects. The local model endpoint intentionally permits LAN addresses.

This is a single-user/household application using Python's standard-library HTTP server, not a public multi-tenant service. Bind to loopback behind an HTTPS reverse proxy for remote access, and preserve the incoming `Host` header. Set `NEWSREVIEW_SECURE_COOKIE=1` when using HTTPS. Keep the model server on a trusted network. Source and model settings are trusted-administrator capabilities; URL checks are defense in depth, not complete isolation against hostile DNS infrastructure.

Save model API keys in **Settings → AI → API key**. They are stored separately in `data/model-credentials.json` (or your chosen data directory), never in SQLite or application ZIP exports. Saved values are not returned to the browser; only configured status is shown. The file contains plaintext secrets, created owner-only on POSIX; restrict data-folder access on Windows. Keep it private if moving or backing up the installation separately.

`.env` files are not loaded automatically. `NEWSREVIEW_MODEL_API_KEY` remains an optional fallback for compatible servers/OpenRouter without a saved key. A saved provider/server key takes priority. Removing a saved key reveals that fallback if present; changing an environment variable still requires restarting the process.

Database exports and recovery ZIPs contain private reading history/preferences and are unencrypted. Restoring a ZIP replaces the dataset; the app first saves a recovery ZIP. Do not delete the data directory while the app runs or copy only a live SQLite main file; use the [backup and restore instructions](MANUAL.md#backup-restore-and-updates).

See the manual for [model setup](MANUAL.md#connect-your-own-model), [configuration](MANUAL.md#data-configuration-and-logging), [service installation](MANUAL.md#run-as-a-service), and [troubleshooting](MANUAL.md#troubleshooting).

The Daily Feed date picker and archive date ranges use story publication dates in your browser timezone, falling back to collection dates for undated stories.

On mobile, use your browser’s Add to Home Screen action to install NewsReview with its app icon. The app still needs a connection to your NewsReview server.

The default [source list](SOURCES.md) includes Zero Day Initiative published advisories, The Hacker News, 9to5Google, 9to5Mac, HackerNoon Cybersecurity, and TechRepublic Security. Approved additions appear once on restart after updating; use **Check new sources** to collect them.

Keyword and time-only searches stay within the selected day. Enter a date range to search across days; clear it to return to the day picker. Bookmarks search all saved stories unless limited by a date range.

Use **Settings → Collection & history → Purge duplicates** to remove AI-marked duplicate stories and compact the database. Bookmarks and stories referenced by other saved stories are kept.

The **Filter...** dropdown combines topic selection, relevance levels, and Include filtered / duplicates. Uncheck relevance levels to hide them; all start enabled.

Use **Settings → Topics** to add, rename, or delete article topics. All existing topics are preserved on upgrade. AI reviews only choose names from your list; sources and interests are managed separately. Renaming updates saved article labels, and deleting removes the label while keeping the articles. Topics are included in ZIP backups.

For OpenRouter, select **OpenRouter** in **Settings → AI → Server type**, use `https://openrouter.ai/api/v1`, and enter a model ID such as `anthropic/claude-haiku-5.5`. Enter your API key below and click **Save key**, then test the model and save the AI settings. **Replace key** and **Remove key...** apply immediately without restarting. Saved keys are scoped to the provider and server URL. Thinking is explicitly disabled for article reviews, sample tests, and feedback-profile updates. OpenRouter sends review data to the cloud and charges API usage; see the [setup instructions](MANUAL.md#openrouter) and [API-key details](MANUAL.md#api-keys).

Ollama unloads its model after sample tests and review batches by default; toggle **Unload Ollama model after a test or review batch** in AI settings. This releases the model while leaving Ollama running. **Maximum response tokens** defaults to 2400; truncated or empty answers now report a specific error, with diagnostic metadata in `logs/console.log`.

Model HTTP failures include the server's JSON error message, when available, in Recent runs, sample-test errors, and `logs/console.log`. An HTTP 400 means the server rejected a request; check its explanation before changing settings. Three consecutive review failures stop the batch and keep remaining stories queued.

While AI reviews run, **Review pending** becomes **Stop review**. Stopping lets the current article finish and leaves the remaining stories queued; it does not disable the collection schedule. Tiles show the article summary without a separate Why it matters section.

Use the thumbs-up/down buttons on any story to save a rating and optional reason. **Settings → Interests → Learned preferences** lets you edit, update, disable, or reset the cached profile. Automatic updates use the 10 most recent ratings once before a review batch; individual reviews receive only the profile. Explicit interests and guidance take priority. Ratings/reasons are included in ZIP backups and are separate from bookmarks.

Opening an article from NewsReview adds **Read** beneath its publisher. This marker is saved in SQLite and included in backups; unopened stories have no extra label.
