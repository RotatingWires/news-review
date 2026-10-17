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

Non-loopback serving requires `NEWSREVIEW_PASSWORD` to contain at least 12 characters.

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

`.env` files are not loaded automatically. Keep environment/service credentials private and outside the repository; secrets are not stored in SQLite or returned by the settings API. `NEWSREVIEW_MODEL_API_KEY` is an optional bearer token sent by the backend to a compatible model server, never to the browser.

Database exports and recovery ZIPs contain private reading history/preferences and are unencrypted. Restoring a ZIP replaces the dataset; the app first saves a recovery ZIP. Do not delete the data directory while the app runs or copy only a live SQLite main file; use the [backup and restore instructions](MANUAL.md#backup-restore-and-updates).

See the manual for [model setup](MANUAL.md#connect-your-own-model), [configuration](MANUAL.md#data-configuration-and-logging), [service installation](MANUAL.md#run-as-a-service), and [troubleshooting](MANUAL.md#troubleshooting).
