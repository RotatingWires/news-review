> [!WARNING]
> This project is fully vibecoded, probably inefficient, but it does what I wanted lol

# NewsReview

A self-hosted news reader that collects your chosen sources and uses your own local model to review and summarize stories. Browse daily tiles, search your archive, and bookmark articles; SQLite keeps the history.

**[Full manual](MANUAL.md)** — complete setup, model configuration, sources, reading/search, scheduling, backup/restore, troubleshooting, and release notes, preserved from the original README.

## Quickstart

Requires **Python 3.11+**. No runtime packages are normally needed.

```sh
git clone https://github.com/RotatingWires/news-review.git
cd news-review
python3 -m newsreview
```

On Windows, use `py -3 -m newsreview`. Open **http://127.0.0.1:8714**.

1. Run your preferred local model server separately; NewsReview does not install or start a model.
2. In **Settings → Local AI**, enter the server URL and exact installed model name, then **Test with a sample article** and **Save**. See [Ollama](MANUAL.md#ollama) or [LM Studio](MANUAL.md#lm-studio-or-another-compatible-local-server) for examples.
3. Click **Check sources** and adjust **Settings → Interests** and **Collection & history** as desired.

You can collect without a model: stories show source excerpts marked **awaiting AI review**. Configure a model later and click **Review pending**. No paid API or cloud model is required.

The scheduler initially runs at **07:00 in the server's local timezone**, with overdue work catching up about 20 seconds after startup. To set up without automatic collection, run `python3 -m newsreview --no-scheduler`. Only one process should use a data directory.

Data and logs live in `./data`. Back up through **Settings → Collection & history → Import/Export...**. On hosts without system timezone data, install `tzdata` for time-range search (`python3 -m pip install tzdata`, or `py -3 -m pip install tzdata` on Windows).

## Security and privacy

The local quickstart uses plain HTTP on loopback. HTTP does not encrypt passwords, session cookies, or reading data in transit. Use HTTPS for remote access as described below.

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

On first startup, NewsReview prints a one-time setup token in the server console. Open the app, enter that token, and create and confirm a password (8–1024 characters). The setup/login page uses the same layout as TaskList and TaskList Stats. The token changes on restart until setup succeeds, then is discarded. It is not an API key and is not written to `server.log` (a service manager may capture console output).

The salted PBKDF2-SHA256 password hash is saved automatically in `data/auth.json` (or your chosen data directory), with owner-only permissions on POSIX. Future starts ask only for your password. Existing `NEWSREVIEW_PASSWORD` deployments seed this file once; an existing credentials file takes precedence, so the environment variable can then be removed. Malformed credentials stop startup rather than reopening setup.

ZIP export/import and Fresh start keep authentication separate: they never export, replace, or delete this file. Preserve it separately when moving the installation. To reset a forgotten password, stop the app, remove only `auth.json`, restart, and use the new console token. Your stories and settings stay intact.

The browser login issues an HttpOnly, SameSite=Strict cookie valid for seven days, signed with an in-memory random secret. Restarting the app invalidates sessions. Login is limited to five attempts per IP per minute. Setup uses the same five-attempt limit.

API writes require JSON and reject cross-origin browser requests. Static serving uses an explicit asset allowlist; database, logs, and source files are not served. Until setup is complete, all reading data and settings remain protected; only the setup/login page and authentication endpoints are accessible. Public news-source requests reject private/reserved network addresses, including on redirects. The local model endpoint intentionally permits LAN addresses.

This is a single-user/household application using Python's standard-library HTTP server, not a public multi-tenant service. Bind to loopback behind an HTTPS reverse proxy for remote access, and preserve the incoming `Host` header. Set `NEWSREVIEW_SECURE_COOKIE=1` when using HTTPS. Keep the model server on a trusted network. Source and model settings are trusted-administrator capabilities; URL checks are defense in depth, not complete isolation against hostile DNS infrastructure.

`.env` files are not loaded automatically. Keep environment/service credentials private and outside the repository; secrets are not stored in SQLite or returned by the settings API. `NEWSREVIEW_MODEL_API_KEY` is an optional bearer token sent by the backend to a compatible model server, never to the browser.

Database exports and recovery ZIPs contain private reading history/preferences and are unencrypted. Restoring a ZIP replaces the dataset; the app first saves a recovery ZIP. Do not delete the data directory while the app runs or copy only a live SQLite main file; use the [backup and restore instructions](MANUAL.md#backup-restore-and-updates).

See the manual for [model setup](MANUAL.md#connect-your-own-model), [configuration](MANUAL.md#data-configuration-and-logging), [service installation](MANUAL.md#run-as-a-service), and [troubleshooting](MANUAL.md#troubleshooting).
