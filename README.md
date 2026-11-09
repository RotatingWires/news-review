> [!WARNING]
> This project is fully vibecoded, probably inefficient, but it does what I wanted lol

# NewsReview v1.0.49

A self-hosted news aggregator that collects your chosen sources and uses a local model or OpenRouter to review and summarize stories. Browse daily tiles, search your archive, and bookmark articles; SQLite keeps the history.

**[Full manual](MANUAL.md)** — complete setup, model configuration, sources, reading/search, scheduling, backup/restore, troubleshooting, and release notes, preserved from the original README.

## Quickstart

Requires the **.NET 10 SDK** (Windows, Linux, or macOS), like TaskList and TaskList Stats.

```sh
git clone https://github.com/RotatingWires/news-review.git
cd news-review
dotnet restore
dotnet run
```

NewsReview listens on **http://127.0.0.1:8714** unless `--urls` (or `ASPNETCORE_URLS`) chooses another address. Open it in a browser.

**Upgrading from the Python version (0.x)?** Download an export ZIP first, stop `python3 -m newsreview`, then start this version with your old data folder, for example `dotnet run --urls "http://0.0.0.0:8714" --data-dir path/to/data`. Your stories, settings, password, and saved API keys are used as they are. See [upgrading](MANUAL.md#upgrade-from-the-python-release-0x).

1. Run your preferred local model server separately, or configure an OpenRouter API key; NewsReview does not install or start a model.
2. In **Settings > AI**, choose the server type, URL, and model name, then **Test with a Sample Article** and **Save**. See [Ollama](MANUAL.md#ollama), [LM Studio](MANUAL.md#lm-studio-or-another-compatible-local-server), or [OpenRouter](MANUAL.md#openrouter) for examples.
3. NewsReview ships without any sources. Add the feeds or news websites you want in **Settings > Sources > Add Source**, then click **Check Sources**.
4. Describe what you care about in **Settings > Interests**, add article labels in **Settings > Topics**, and adjust **Collection & History** as desired.

You can collect without a model: stories show source excerpts marked **awaiting AI review**. Configure a model later and click **Review Pending**. No paid API or cloud model is required.

Automatic collection initially runs **every 24 hours**. Change **Settings > Collection & History > Collect every (hours)** to any whole-number interval from 1 to 8760, such as 3 hours. Overdue work catches up about 20 seconds after startup. To set up without automatic collection, run `dotnet run --no-scheduler`. Only one process should use a data directory.

Data lives in `./data` (or `--data-dir`); runtime logs use `./logs/console.log`, matching TaskList. Only one log file is kept: it truncates in place when the next entry would exceed 10 MiB. Back up through **File > Import/Export...**.

## Security and privacy

The local quickstart uses plain HTTP on loopback. HTTP does not encrypt passwords, API keys, session cookies, or reading data in transit. Use HTTPS for remote access as described below.

On first startup, enter the console setup token in the browser and create a password (at least 8 characters). This applies to loopback and LAN access.

## LAN access and authentication

Loopback access is the default. For LAN access, explicitly change the listening address:

```bash
dotnet run --urls "http://0.0.0.0:8714"
```

On first startup, NewsReview prints a one-time setup token in the server console. Open the app, enter that token, and create and confirm a password (8–1024 characters). The setup/login page uses the same layout as TaskList and TaskList Stats. The token changes on restart until setup succeeds, then is discarded. It is not an API key and is not written to `logs/console.log` (a service manager may capture console output).

The salted PBKDF2-SHA256 password hash (600,000 iterations) is saved automatically in `data/auth.json` (or your chosen data directory), with owner-only permissions on POSIX. Future starts ask only for your password; an older hash with fewer iterations is re-hashed once at your next login. Credential creation uses an exclusive file write and does not require hard-link support, including on Windows drives. Malformed or interrupted credential files stop startup rather than reopening setup; if first-run setup was interrupted, stop the app and remove only the incomplete `auth.json` before trying again.

ZIP export/import and Fresh start keep authentication separate: they never export, replace, or delete this file. Preserve it separately when moving the installation. To reset a forgotten password, stop the app, remove only `auth.json`, restart, and use the new console token. Your stories and settings stay intact.

The browser login issues an HttpOnly, SameSite=Strict cookie that lasts 30 days and slides while you use the app; it is marked Secure automatically over HTTPS. Its keys are kept in your Windows profile's ASP.NET key ring when NewsReview runs from a signed-in Windows account (as TaskList, TaskList Stats, and SpendTracker do), and in `data/keys/` otherwise, so restarts do not sign you out. Setup and login allow five attempts per IP address per minute.

API writes require JSON and reject cross-origin browser requests. Only the files in `wwwroot/` are served; the database, logs, and source files are not. Until setup is complete, all reading data and settings remain protected; only the setup/login page and authentication endpoints are accessible. Public news-source requests reject private/reserved network addresses, including on redirects. The local model endpoint intentionally permits LAN addresses.

This is a single-user/household application, not a public multi-tenant service. Bind to loopback behind an HTTPS reverse proxy for remote access, and preserve the incoming `Host` header. Keep the model server on a trusted network. Source and model settings are trusted-administrator capabilities; URL checks are defense in depth, not complete isolation against hostile DNS infrastructure.

Save model API keys in **Settings > AI > API Key**. They are stored separately in `data/model-credentials.json` (or your chosen data directory), never in SQLite or application ZIP exports. Saved values are not returned to the browser; only configured status is shown. The file contains plaintext secrets, created owner-only on POSIX; restrict data-folder access on Windows. Keep it private if moving or backing up the installation separately.

`.env` files are not loaded automatically. `NEWSREVIEW_MODEL_API_KEY` remains an optional fallback for compatible servers/OpenRouter without a saved key. A saved provider/server key takes priority. Removing a saved key reveals that fallback if present; changing an environment variable still requires restarting the process.

Database exports and recovery ZIPs contain private reading history/preferences and are unencrypted. Restoring a ZIP replaces the dataset; the app first saves a recovery ZIP. Do not delete the data directory while the app runs or copy only a live SQLite main file; use the [backup and restore instructions](MANUAL.md#import-and-export).

See the manual for [model setup](MANUAL.md#connect-your-own-model), [configuration](MANUAL.md#data-configuration-and-logging), [service installation](MANUAL.md#run-as-a-service), and [troubleshooting](MANUAL.md#troubleshooting).

The Daily Feed date picker and archive date ranges use story publication dates in your browser timezone, falling back to collection dates for undated stories. The **Sort** dropdown offers published newest/oldest and relevance rank highest/lowest on Daily Feed and Bookmarks. Your browser remembers the choice; rank highest is the default. Sorting applies to all matching stories before pagination.

On mobile, use your browser's Add to Home Screen action to install NewsReview with its app icon. The app still needs a connection to your NewsReview server.

A fresh install starts with no sources, no interest topics, and no article topics. Updating NewsReview never adds, removes, or edits an existing installation's sources, interests, or topics. **Settings > Sources > Check New Sources** collects only sources that have never been checked, and each source's **Check** button collects just that source.

Day/date-range browsing uses an indexed publication timestamp and reads display fields only for the selected 48-story page. Schema-2/3 ZIP backups still restore and are indexed during the restore. Opening a database on a sleeping hard drive still has wake-up latency.

Keyword and time-only searches stay within the selected day. Enter a date range to search across days; clear it to return to the day picker. Bookmarks search all saved stories unless limited by a date range.

Use **Settings > Collection & History > Purge Duplicates** to remove AI-marked duplicate stories and compact the database. Bookmarks and stories referenced by other saved stories are kept.

The **Filter...** dropdown combines topic selection, relevance levels, and Include filtered / duplicates. Uncheck relevance levels to hide them; all start enabled.

Edit article topics in **Settings > Topics**, one per line. All existing topics are preserved on upgrade. AI reviews only choose names from your list; sources and interests are managed separately. Editing a line in place renames the topic on saved articles; deleting a line removes that label after confirmation while keeping the articles. Topics are included in ZIP backups.

For OpenRouter, select **OpenRouter** in **Settings > AI > Server type**, use `https://openrouter.ai/api/v1`, and enter a model ID such as `anthropic/claude-haiku-5.5`. Enter your API key below and click **Save Key**, then test the model and save the AI settings. **Replace Key** and **Remove Key...** apply immediately without restarting. Saved keys are scoped to the provider and server URL. Thinking is explicitly disabled for article reviews, sample tests, and feedback-profile updates. OpenRouter sends review data to the cloud and charges API usage; see the [setup instructions](MANUAL.md#openrouter) and [API-key details](MANUAL.md#api-keys).

Ollama unloads its model after sample tests and review batches by default; toggle **Unload Ollama model after a test or review batch** in AI settings. This releases the model while leaving Ollama running. **Maximum response tokens** defaults to 2400; truncated or empty answers now report a specific error, with diagnostic metadata in `logs/console.log`.

Model HTTP failures include the server's JSON error message, when available, in Recent runs, sample-test errors, and `logs/console.log`. An HTTP 400 means the server rejected a request; check its explanation before changing settings. Three consecutive model-review failures stop the batch and keep remaining stories queued. Articles without readable text or an excerpt are skipped individually without calling the model or stopping other reviews; use **Reset Failed Reviews** to retry after they reach three attempts. Unknown model topic names are discarded while valid summaries and configured labels are kept; reviews can have no topics.

If startup reports **cannot open its database**, check access to the data directory and its SQLite `-wal`/`-shm` files. For a pooled drive such as DrivePool, test a copy of the complete data folder on a regular local disk using `--data-dir`; the app itself can remain on the pool. See [storage troubleshooting](MANUAL.md#database-startup-errors-and-pooled-drives). No automatic database relocation or journal-mode change is performed.

While AI reviews run, **Review Pending** becomes **Stop Review**. Stopping lets the current article finish and leaves the remaining stories queued; it does not disable the collection schedule. Tiles show the article summary without a separate Why it matters section.

Use the thumbs-up/down buttons on any story to save a rating and optional reason. **Settings > Interests > Learned preferences** lets you edit, update, disable, or reset the cached profile. Automatic updates use the 10 most recent ratings once before a review batch; individual reviews receive only the profile. Explicit interests and guidance take priority. Ratings/reasons are included in ZIP backups and are separate from bookmarks.

Opening an article from NewsReview adds **Read** beneath its publisher. This marker is saved in SQLite and included in backups; unopened stories have no extra label.

## Sibling apps

TaskList, TaskList Stats, NewsReview, and SpendTracker are four small self-hosted apps with the same Windows 95 look, sign-in, backups, and keyboard use. Each runs on its own and keeps its own data.

- [TaskList](https://github.com/RotatingWires/task-list): tasks, nested subtasks, and lists.
- [TaskList Stats](https://github.com/RotatingWires/task-list-stats): statistics and history for TaskList, read without changing it.
- [NewsReview](https://github.com/RotatingWires/news-review): a personal news reader with AI review of your sources.
- [SpendTracker](https://github.com/RotatingWires/spend-tracker): purchases, refunds, shared costs, and paybacks.

## Credits

**View > Use Retro Font** uses [W95FA](https://fontsarena.com/w95fa-by-alina-sava/) by Alina Sava, bundled in `wwwroot/fonts/` under the SIL Open Font License 1.1 (`wwwroot/fonts/OFL.txt`).
