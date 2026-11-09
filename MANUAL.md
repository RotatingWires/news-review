> [!WARNING]
> This project is fully vibecoded, probably inefficient, but it does what I wanted lol

# NewsReview v1.0.38

A small self-hosted news reader for whatever you want to follow. Choose sources, describe your interests, and let a local model or OpenRouter review the stories. NewsReview ships without bundled sources or preset interests: add the feeds and websites you want to follow, then describe what matters to you.

Browse daily story tiles, bookmark useful articles, search your archive, and step backward or forward through days. SQLite keeps the history. Local model review needs no paid API, ChatGPT subscription, or cloud model; OpenRouter is an optional paid API provider.
<img width="2560" height="1301" alt="image" src="https://github.com/user-attachments/assets/55098e81-e3db-46bb-8583-f5ed1b1b9398" />

## Stack and project conventions

- ASP.NET Core on .NET 10, the same stack as TaskList and TaskList Stats; runs on Windows, Linux, and macOS.
- SQLite through `Microsoft.Data.Sqlite`, the only package dependency. XML parsing, HTTP, ZIP, and time zones use the .NET runtime.
- Plain HTML/CSS/JavaScript ES modules in `wwwroot/`. No frontend framework, Node build, npm, or ORM.
- Purpose-based source files; changes belong in the file that owns the behavior. No monkey patches, runtime wrappers, or release-specific override files.
- `<Version>` in `NewsReview.csproj` is the authoritative application version. The UI reads it from the server.
- Commits use the existing project convention: `v0.4.15: Describe the change`, followed by a blank line and detailed bullets explaining behavior, implementation, and validation. Earlier release commits now include those bodies too. Their trees are unchanged, but rewriting messages changes commit IDs; existing clones should fetch and reconcile local branches before pushing.
- Run the meaningful automated checks before committing. Update this README when setup, behavior, storage, or limitations change.

## Quick start

Install the **.NET 10 SDK**, then from the repository directory:

```bash
dotnet restore
dotnet run
```

Open **http://127.0.0.1:8714**. NewsReview listens on loopback port 8714 unless `--urls` (or `ASPNETCORE_URLS`) chooses another address; see [LAN access](#lan-access-and-authentication).

1. Open **Settings > AI**, choose your server type, and enter the server URL and model name.
2. Use **Test with a Sample Article**, then **Save**.
3. Add your sources in **Settings > Sources > Add Source**. A fresh install has none; see [Add or edit a source](#add-or-edit-a-source).
4. Click **Check Sources**. The collector runs in the background; the page reports progress.
5. Describe your interests in **Settings > Interests**, add article labels in **Settings > Topics**, and adjust **Collection & History** as desired.

You can collect before setting up a model. Stories display source excerpts labeled **awaiting AI review**, rather than pretending those excerpts are generated summaries. Later, configure the model and click **Review Pending**.

Automatic collection is enabled initially **every 24 hours**, adjustable under **Settings > Collection & History > Collect every (hours)** (1–8760 whole hours). An overdue run catches up about 20 seconds after startup. To prevent automatic collection while setting up:

```bash
dotnet run --no-scheduler
```

This flag disables the scheduler for that process only. The UI's schedule setting is persistent. Only one application process should use a data directory.

## Connect your own model

The application does not install, download, choose, or start a model for you. Run your preferred inference server separately, or configure OpenRouter. Switching computers or models requires changing the AI settings, not the collector or UI code.

### Ollama

Install [Ollama](https://docs.ollama.com/), start it, and download a model that fits your hardware. Use `ollama list` to find its exact installed name.

In **Settings > AI**:

| Field | Value |
| --- | --- |
| Server type | Ollama |
| Server base URL | `http://127.0.0.1:11434` |
| Model name | Exact installed model name from `ollama list` |

The adapter calls `/api/chat` with streaming disabled, `think: false`, and the requested JSON schema in `format`. This requests a direct review without a separate thinking phase on models that support disabling it. The same setting applies to the sample-article test and normal reviews. See [Ollama's chat API](https://docs.ollama.com/api/chat), [thinking controls](https://docs.ollama.com/capabilities/thinking), and [structured outputs](https://docs.ollama.com/capabilities/structured-outputs).

### Ollama memory and response troubleshooting

**Unload Ollama model after a test or review batch** is enabled by default, including for existing installations. Sample tests send `keep_alive: 0` with the chat request so Ollama releases the model after responding, including when NewsReview rejects the answer. Review batches keep it warm between articles (`keep_alive: 5m`) and send an empty `/api/generate` request with `keep_alive: 0` in batch cleanup, including after failed reviews. The model loads again when needed; Ollama itself remains running. Uncheck the option to leave model residency to Ollama/server configuration. This control applies only to native Ollama, not compatible servers. Model tests are blocked while collection/review or maintenance holds the shared job lock, preventing a sample test from unloading an active batch. Other applications using the same model can still affect its residency.

Ollama normally retains a model for five minutes. To release one manually, run `ollama stop <model-name>` on the Ollama host (inside its container if applicable). Check `ollama ps` to see loaded models. Unloading releases model allocations, but the service and operating-system caches can still use some memory. The API behavior is documented in [Ollama's memory FAQ](https://docs.ollama.com/faq#how-do-i-keep-a-model-loaded-in-memory-or-make-it-unload-immediately).

**Maximum response tokens** defaults to 2400 (previously a fixed 1200), adjustable from 256 to 8192. A response ending with `length`/`max_tokens` is reported as truncated; increase the limit and allow enough request time on slower hardware. An empty answer is reported separately, including whether the server returned only thinking text. Invalid JSON reports its line/column and finish reason. A single JSON Markdown fence is accepted, but the object still must pass the full schema and duplicate-ID validation; arbitrary text or partial JSON is never treated as a valid review. Schema string lengths now match the stored review limits.

Diagnostic logs record provider/model, finish reason, answer/thinking character counts, and the token limit, without dumping article text or model reasoning. These diagnostics help distinguish response truncation, missing final answers, and malformed output; a generic JSON error alone does not establish that a particular model or Ollama version is incompatible. If errors persist, note the exact installed model, Ollama version, and the diagnostic line from `logs/console.log`. An unload request failure is logged and shown in job status without discarding successful summaries. If a process exits abruptly or a request remains active after a network timeout, immediate unloading cannot be guaranteed; batch requests retain the bounded five-minute fallback.

If the model server rejects a request with an HTTP error, NewsReview includes its JSON `error` or `message` explanation in sample-test errors, Recent runs, and `logs/console.log`. Explanations are normalized to one line and limited to 240 characters, with at most 8 KiB read from the error body. HTML, invalid JSON, and unrecognized error objects retain the HTTP status without exposing the body. Publisher error handling is unchanged. HTTP 400 indicates a rejected request, not necessarily a connection problem; use the returned explanation and the model server's own logs to identify the cause. After three consecutive model-review failures the batch stops, preserving stories and queued work. Fix the reported problem, test the model, and run Review pending again; stories that have reached three attempts need the existing retry/reassessment controls.

### LM Studio or another compatible local server

Load a model and start the inference server. Set:

| Field | Example |
| --- | --- |
| Server type | Compatible chat server |
| Server base URL | `http://127.0.0.1:1234/v1` |
| Model name | The model identifier exposed by your server |

The adapter appends `/chat/completions`, requests JSON output, and validates the returned object. The API prefix belongs in the base URL; the final endpoint does not. See [LM Studio's chat API](https://lmstudio.ai/docs/developer/openai-compat/chat-completions).

If your compatible server requires authentication, save its key in **Settings > AI > API Key**. `NEWSREVIEW_MODEL_API_KEY` remains an optional fallback. The backend sends the key as a bearer token; native Ollama does not use it. See [API keys](#api-keys).

### OpenRouter

In **Settings > AI**, set:

| Field | Value |
| --- | --- |
| Server type | OpenRouter |
| Server base URL | `https://openrouter.ai/api/v1` |
| Model name | `anthropic/claude-haiku-5.5` or another supported model ID |

Enter the API key in the API key section and click Save Key. Then use Test with a Sample Article and Save the AI settings. Key updates apply immediately; no environment variable or restart is needed for UI-saved keys. See [API keys](#api-keys) for replacement/removal and the optional environment fallback.

The OpenRouter server type sends `reasoning: {"enabled": false}` for article reviews, sample tests, and learned-preference updates. It disables thinking instead of merely hiding reasoning text; see [OpenRouter reasoning controls](https://openrouter.ai/docs/guides/best-practices/reasoning-tokens). It omits temperature for compatibility with [Haiku 5.5](https://platform.claude.com/docs/en/models/haiku-5-5/overview). Choose a model that supports disabling thinking. Local compatible-server requests retain their existing format, and native Ollama still sends `think: false`.

OpenRouter is a paid cloud service. Requests send article text, interests/guidance, topic names, cached preferences, and candidate coverage to OpenRouter and its selected provider; preference generation sends the bounded recent rating examples. The API key remains on the app server and is not included in ZIP backups. Changing providers preserves saved summaries and does not automatically reassess history.

### API keys

For OpenRouter or an authenticated compatible server, **Settings > AI > API Key** provides a masked, write-only entry field. Enter the key and use **Save Key**; an existing saved key changes that button to **Replace Key**. Saving AI settings separately with an empty key field does not remove a saved key. **Remove Key...** opens a Cancel/Remove confirmation. The browser receives only configured/source status, never the stored value. The section is hidden for native Ollama.

Saved keys belong to the selected provider and normalized server base URL, including its scheme, host, port, and path. A trailing slash or explicit default port does not create a different scope. Changing models on the same provider/server reuses its saved key; changing the provider or server does not. Use a base URL without embedded credentials, query parameters, or fragments. Provider/URL changes clear an unsaved key entry and refresh its configured status.

Keys live in `model-credentials.json` beside the reading database, separate from SQLite and `auth.json`. The file contains plaintext secrets; POSIX files are created with owner-only permissions, while Windows access follows the data folder's ACLs. Writes flush a temporary file in the same directory and replace the destination atomically, without hard links. Failed writes retain the previous saved key. Invalid credential-file formats stop startup without discarding the file; restore a valid file or move it aside and re-enter keys. Protect this file and any independent filesystem backups of it.

Save, replace, and remove apply to the next model request, including requests in an active review batch. An in-flight request keeps the key it already received. This applies to article reviews, sample tests, and learned-preference generation. Keys are sent only as Authorization headers, never as model prompt fields or reading settings. Authenticated requests cannot redirect to a different origin, and model error messages redact the Authorization secret before entering logs/history. The authenticated key API returns status only; static file serving does not expose the credentials file.

Application ZIP exports, recovery ZIPs, database backups, restores, and Fresh start exclude or preserve the separate credential file. Copy it privately when moving an installation, or re-enter keys on the new host. It is Git-ignored. The key is sent from your browser to NewsReview when saved; use HTTPS for remote access, since a password field does not encrypt HTTP traffic.

`NEWSREVIEW_MODEL_API_KEY` remains a process-environment fallback for non-Ollama servers without a saved key. Saved keys take priority. The UI reports an environment-provided key and disables removal when no saved key exists. If both are present, the removal dialog explains that the environment key will be used afterward. The UI cannot remove an environment variable; remove it from the shell/service configuration and restart if you want to disable the fallback. Existing deployments can instead save a replacement through the UI immediately. `.env` files are not loaded automatically.

### Move the model to another computer

Run the model server on the stronger computer, configure it to listen on the appropriate LAN interface, and use that computer's LAN address in the AI settings. Allow the connection through its firewall. The **application backend** needs access to the model server; your browser never connects to it directly.

For example, a model at `http://192.168.1.50:11434` can serve a reader running on another machine. `127.0.0.1` always refers to the computer running the app.

Existing stories, summaries, bookmarks, and dates remain in SQLite when changing models. New reviews use the selected provider and model. A blank model name pauses AI review while collection continues.

### Small-computer controls

- Lower **Maximum article characters** to reduce input size. The default is 12,000 characters; it is a character bound, not a token count.
- Lower **Maximum AI reviews per run** to limit work. The default is 40. Unprocessed stories remain queued.
- Increase **Request timeout** for slow inference; the supported range is 10–600 seconds.
- Processing is sequential: one article is reviewed at a time.
- **Review Pending** processes another bounded batch without fetching the feeds again.
- Three consecutive review failures stop that batch, so an unavailable model does not cause requests for the whole archive.

A small model must still follow instructions and produce valid structured output. The connection test checks a real sample review, not just an HTTP health endpoint. It does not measure factual quality; review a few actual summaries after changing models. No external model is used as an automatic fallback.

## Interface and help

**Help**, immediately after Settings, opens **About NewsReview**: a two-paragraph summary about as long as TaskList Stats' About (what NewsReview does, plus tips on choosing sources, collecting without a model, and backups), ending with about.lehighradio.com above the application version, as TaskList and TaskList Stats do. This manual is the full guide. Add a homepage/category listing or RSS/Atom feed, then preview candidates. A single article is not an ongoing source. For forums, prefer a relevant subforum/category or its feed listing threads; each linked thread becomes a candidate. A single thread is one discussion, and later replies do not create fresh stories for the same URL.

The app fills the viewport. Its top and bottom bars stay visible while the central content scrolls. Dialog titles and action bars similarly stay visible while their bodies scroll. A yellow/orange blinking status light indicates collection/review; reduced-motion preferences use a static orange indicator.

Notifications and errors appear in the fixed bottom-left status bar. Hover over a truncated message to read its full text. Routine notifications clear after eight seconds. Failed-action messages stay until dismissed or replaced by a later action; successful background reads do not erase them. Errors wrap in the footer so long messages are readable. Live connection warnings clear on successful server responses, while any failed-action notice remains available. Use Dismiss to clear the displayed warning; another failed connection check can show it again. The idle message shows collection/review status. Source health and run details remain in Settings. Settings save buttons use **Save**; source dialogs place **Cancel** before **Save**.

The menu bar works like TaskList, TaskList Stats, and SpendTracker: **File** has Refresh, Import/Export..., Log Out, and Fresh Start...; **View** switches between Daily Feed, Bookmarks, and Settings and chooses the Light or Dark theme; **Help** shows the About box. Daily Feed, Bookmarks, and Settings are also tabs below the toolbar, which holds **Check Sources** and **Review Pending**.

Collection/review messages and their blinking activity light appear only in the footer. Review progress keeps the full article title; the status bar adds an ellipsis when it exceeds the available width, and hovering reveals the full message. **Review Pending** sits beside **Check Sources**. Bookmark icons stay unchanged while saving; progress appears in the status bar. Custom Win95 dropdowns support arrow keys, Home/End, Enter, Escape, and outside-click dismissal. Default category suggests the current Settings > Topics list and remains editable for custom values; the daily topic selector retains multi-select checkboxes.

The Daily Feed Filter... menu uses the same Win95 arrow and popup styling as other dropdowns, with checkboxes for multiple topics. Story counts sit below the edition heading. Story text remains selectable on both feeds; application chrome and controls do not highlight during text selection. Input and textarea values stay selectable for editing. Titlebar window-control symbols are decorative CSS drawings in equal-size boxes, so minimize, maximize, and close stay aligned regardless of font; TaskList/Stats use the same markup and styles.

Manage article labels in **Settings > Topics**: a text box with one topic per line and **Save**. Names are 1–60 characters (surrounding spaces and blank lines are ignored) and cannot duplicate another topic regardless of capitalization. When saving, lines that did not change anchor the comparison; a line edited in place (between the same unchanged neighbors) renames that topic, so saved stories keep the label under its new name, and a capitalization-only change is also a rename. Moving a line never removes it. A deleted line removes the topic and its saved labels after a **Remove Topics** confirmation listing the names; articles are kept. New lines add topics. Unsaved edits survive switching Settings tabs. The list is saved in one transaction (`PUT /api/topics` with the full list, an explicit rename map, and the `DELETE TOPIC` confirmation when topics are removed). AI reviews receive only this list as permitted tag values, and response validation discards invented topic names while keeping otherwise valid summaries. Interests and editorial guidance control relevance separately; source categories do not add or rename topics automatically.

On upgrade, the app imports every existing story tag and source category into the initial list without changing stored story labels or other data. This runs only when the owned list is missing, so deleted topics and intentionally empty lists stay that way after restart. Fresh installs start with an empty list; add topics if you want reviewed articles labeled. The feed filter shows the managed list immediately, including newly added topics without matching stories yet.

Adding a topic makes it available to future reviews; existing articles are not automatically reassessed. Renaming updates that label on saved articles in one transaction and preserves other labels, summaries, scores, bookmarks, dates, read markers, and feedback. Deleting requires confirmation and removes only that label from saved articles; articles with no remaining labels are still kept. Topic editing waits until collection/review or restore finishes. To reclassify old articles after adding topics, use Reassess History.

Sources retain their independent Default category. Its suggestions are the current managed topics, refreshed whenever topics are added, renamed, or deleted; a new source starts with the first topic (or News when the list is empty). Renaming or deleting a topic does not change a source's saved category, so edit the source to pick a current topic. Newly collected stories receive it as an initial label only if it matches a managed topic; otherwise they start without a topic until reviewed. Editing a source cannot grow the topic list or restore deleted topics. Reviews may have no topic when none fit. Unknown model-suggested names are discarded, while configured names are matched without case sensitivity, deduplicated, and saved using their exact configured spelling. This keeps otherwise valid summaries and scores without creating topics or spending another model request. Invalid field types and other malformed output still fail validation. If the list is empty, AI review still summarizes and scores stories with an empty tags array. History resets keep the managed list; the existing Fresh start Interest topics option applies only to relevance interests.

The owned topic list is stored in SQLite and included in ZIP backups. Restoring a current backup restores its list, including an empty list. Restoring an older backup without a topic list imports all its existing labels and source categories during the same restore transaction, preserving its current topics. Topic changes do not rewrite historical feedback snapshots or your interests/source settings.

### When the browser refreshes

NewsReview does **not** poll `/api/overview` while idle. Opening the app, changing the main view, or **File > Refresh** reads current state. Like TaskList, TaskList Stats, and SpendTracker, returning to the window or tab does not reload. Unsaved interest/model/collection forms are preserved unless a reset or restored dataset requires a reload.

While a visible tab knows a collection/review job is running, it checks progress every **2.5 seconds** and reloads the relevant content when the job finishes. Connection failures retry read requests every **5 seconds** until recovery. Hidden tabs pause these checks and resume them when shown again; a read already in flight may finish. Checks also pause during ZIP transfer and resume afterward if needed. Idle checks stop again once the job or connection recovery finishes.

The interval scheduler runs independently on the server and still collects with no browser open. If a scheduled job starts while an open tab is idle, the display catches up when you choose File > Refresh, change the main view, or reload the page. Other-tab bookmark/source changes are picked up the same way; there is no continuous background synchronization.

## Mobile home-screen installation

Use Safari's Share > Add to Home Screen on iPhone/iPad, or the browser's Install/Add to Home Screen action on Android. Both the login and reader pages advertise the NewsReview icon and manifest. Existing home-screen shortcuts may need to be removed and added again to refresh their icon. This is an online reader: the NewsReview server must remain reachable; installing it does not add offline storage.

## Reading and searching

- **Sort:** choose Published: newest first / oldest first, or Rank: highest first / lowest first. Rank means the saved AI relevance score (0–100), with highest first as the default. The choice is shared between Daily Feed and Bookmarks and remembered in this browser when storage is available. It applies to the entire matching day, date range, or bookmark set before pagination. Equal ranks use newest publication time first; equal timestamps use a stable story ID order so paging does not reshuffle ties. Undated stories use collection time. Sorting does not call the model or reassess stories, and Clear Search / Today keep your chosen sort.
- **Daily feed:** the selected day shows stories published on that date in your browser timezone. Stories without a publication date use their collection date. This applies to existing history too.
- **Previous/next:** steps through calendar days, including quiet days. **Today** returns to the current edition and clears filters.
- **Search:** a keyword searches the currently selected publication day, unless an explicit date range is entered, matching headlines, **source names**, summaries, source excerpts, and tags. Source-name matching uses the name shown on tiles and also works for retained historical sources. `%` and `_` are treated literally. Search is a case-insensitive SQLite `LIKE` search for ordinary ASCII text, not a semantic search engine.
- **Date/time range:** Use one field, for example `9/1/26 - 10/1/26, 10am - 3pm`. Either range alone is also accepted. Dates and times are separated by a comma.
- **Date syntax:** TaskList-style input such as `10/3 - 10/8`. Each side accepts `m/d`, `m/d/yy`, or `m/d/yyyy`; omitted years use the server's current year. Hyphens, en dashes, and em dashes work. Ranges are inclusive and filter publication dates (collection dates for undated stories). Include years when crossing New Year.
- **Time syntax:** TaskList-style input such as `9am - 9pm` or `9:30pm - 2am`. Times match the displayed publication timestamp in the **browser's timezone**, falling back to collection time for undated stories. Endpoints include the entire displayed minute, so `10am - 12pm` includes noon. Named timezone data handles daylight-saving changes for historical timestamps; direct API requests without `timeZone` use the server timezone. Overnight ranges include both sides of midnight on each selected calendar date. As in TaskList, times without am/pm use the AM interpretation (`12` means midnight); 24-hour notation is not accepted.
- Keywords and time-only filters combine within the selected day. An explicit date range replaces the day picker as the date scope and searches that range; clearing it returns to the selected day. The day picker and previous/next buttons are disabled while an explicit date range is active; Today clears the range and restores daily browsing. Bookmarks continue to search all saved stories unless a date range is supplied. Press Enter or Search to apply them; changed keyword and date/time fields also apply when you click or tab out. Clearing the keyword and leaving the field removes that filter.
- **Filter...:** choose multiple topic checkboxes in the Topics section. Results match **any** selected topic; select All topics to remove that restriction. It filters the selected day with keywords or time-only searches, or the explicit date range when one is entered. Clicking a tile's tag adds that topic to the current selection.
- **Bookmarks:** saved stories from all days, with the same search/tag/date filters available. Removing a bookmark does not delete the story.
- **Relevance:** all four levels start checked in Filter...: highly relevant, somewhat relevant, low relevance, and awaiting review. Uncheck any level to hide it, including bookmarked stories in that level. Levels combine with topics, search, dates, and Include filtered / duplicates on Daily Feed and Bookmarks. Unchecking all levels intentionally shows no stories. Clear Search, Today, and previous/next navigation restore all levels.
- **Include filtered / duplicates** (inside Filter...): reveals editorially filtered stories and additional coverage. Bookmarked stories remain visible even if later filtered or grouped as duplicates.
- **Ordering and pagination:** 48 results per page using your chosen Sort order. Rank sorting compares scores across the entire selected date range, so an older green story can precede a newer yellow one. Publication sorting compares the actual timestamp, including the time within a day. Pending stories retain their stored/default score (initially 50) for sorting even though their square is gray.
- **Themes:** Windows 95-style raised buttons, recessed fields, square panels, title bars, and explicit light/dark themes. Light is the default; the selection is saved in browser local storage. Every color is a token in `wwwroot/theme.css`, defined once per theme with the same names TaskList, TaskList Stats, and SpendTracker use; the other stylesheets contain no color values of their own.
- **Retro font:** **View > Use Retro Font** switches the whole app, including the login page and chart labels, to W95FA, an open-source recreation of the Windows 95 MS Sans Serif font bundled in `wwwroot/fonts`, so the interface looks the same on Windows, Linux, macOS, Android, and iOS without installing fonts. It is off by default and saved per browser, separately for each app.

Tiles show the original headline and source, tags, the generated summary when available, publication time when known (otherwise collection time), relevant standards/availability milestones, the reviewing model, and the original link. Excerpt-based summaries and failed reviews are labeled. Dates are formatted in the browser's timezone; day and date-range filters match those displayed timestamps.

**Summaries only:** the model is asked for 2–4 short factual English sentences, plus relevance, tags, milestones, and duplicate decisions. Why it matters has been removed from both tile rendering and the requested model schema for every relevance level. Older explanations remain in the legacy SQLite column only for database/ZIP compatibility and are excluded from story API responses. New reviews clear that legacy field. No additional explanation is generated or hidden after generation.

No sample articles are inserted into your real database. The first edition starts empty until collection runs.

Publication-date browsing and time search use the browser's IANA timezone, resolved by the .NET runtime on both Windows and Linux. An unknown timezone name returns an explicit error rather than silently applying the wrong clock.

### Relevance colors

Each story has a relevance square beside its bookmark: green **75–100**, yellow **40–74**, red **0–39**, or gray **awaiting AI review**. The key sits below the story count on both feeds. Hover over a square for its score; accessible labels include the same information. Scores reflect the interests used during the last successful review, not certainty or factual reliability. Pending/reassessment stories stay gray until reviewed. Changing interests does not rescore the archive automatically; use Reassess History when desired. This display uses stored scores and adds no statistics tracking.

## Sources

NewsReview does not bundle any sources. A fresh database starts with an empty source list; add each feed or website as described below. Updating the application never adds, removes, or edits an existing installation's sources, and Fresh start does not reseed any.

### Add or edit a source

Open **Settings > Sources > Add Source**. Enter its name, website/listing URL, and default category, then choose a collection method:

| Method | Behavior |
| --- | --- |
| Automatic | Prefer a specified/advertised feed. Check the website if the feed fails, is empty, or its newest publication exceeds the source warning interval. Merge readable website results with usable stale-feed entries. |
| RSS/Atom feed only | Use the specified/discovered feed and report failure rather than switching to page extraction. |
| Website page | Read the listing page directly; no feed is needed, and any saved feed URL is ignored. |

Click **Preview Stories** to see the detected method and up to eight candidate headlines/links without adding anything to history. Save the source, then **Refresh Sources**. The source editor also offers **Find RSS/Atom Feed** for feed discovery.

The collector supports RSS 2.0, common RDF/RSS feeds, and Atom. Feed discovery follows RSS/Atom links advertised in the page's HTML. Website mode extracts structured article lists (JSON-LD), article-card headline links, and heading links. It prefers the main content area where available, excludes navigation/footer/author/category links and external hosts, and removes duplicate URLs. Article-card headline links take precedence over date archives and incidental links inside the card. Website URLs should point to a news/category listing rather than a single article.

**Article URL contains** is an optional literal substring filter for website extraction, such as `/news/`, `/posts/`, or `/threads/`. It is not a regular expression or CSS selector, and does not affect feed entries. Preview it to make sure you are collecting the intended articles. Only links on the listing's exact hostname are followed; subdomains are separate sources. Website mode reads one downloaded page, not an entire site, and does not paginate or execute JavaScript. Ambiguous layouts, JavaScript-only pages, authentication, paywalls, and bot restrictions can still require a custom adapter or a different public listing/feed. Missing publication dates remain unknown.

**Feeds or website listings find stories; article webpages provide review text.** During AI review the collector visits each article URL and attempts to extract readable text, even when the feed has only a headline. This is an HTTP download, not a JavaScript browser session. A site that requires JavaScript, authentication, or a subscription may still be unreadable. In that case the model uses the available source excerpt; if there is no usable article text or excerpt, the app keeps the headline/link and reports the review error. It does not summarize a headline as if it had read the article. Without a model configured, collection still works, but article extraction waits for the review stage.

Edit a source to change its feed, default category, or enabled state. Disabling stops collection and preserves all existing stories. Sources are not deleted through the UI, keeping historical source references stable.

### Forum feeds

Forum entries are treated as story candidates, using the entry's title, link, and available excerpt. The app does not monitor every post or assume a discussion is verified reporting. Your configured model applies your interests to the retrieved text; without a model, entries appear as unreviewed excerpts. The same normalized thread URL is inserted only once, so later replies do not automatically create another tile. Publication/activity timestamps depend on what the forum feed exposes.

A broad forum feed can contain troubleshooting, casual discussion, and unrelated topics. Point the source at a specific subforum or category feed for a narrower selection, or disable it if you want publisher-style reporting only. AI relevance filtering reduces noise but cannot guarantee every discussion is interesting or factually correct.

### Automatic stale-feed checks

Automatic mode checks actual feed freshness on every collection/preview. A feed is stale when its newest dated item is at least the source's **Warn after this many days without activity** interval old (14 days by default). Empty feeds also trigger the website check. For undated feeds, an existing source with no new observed stories over that interval triggers it; new undated feeds are not assumed stale. A fresh feed does not trigger extra website requests just because the previous check was old.

When stale, NewsReview reads the configured website/listing URL with its Article URL contains filter. The preview reports **RSS/Atom + website** when both contribute. It merges normalized URLs, retains feed-only stories, and fills missing website dates/excerpts from matching feed entries. The original feed URL stays configured, so later collections can detect when the feed resumes. Source notes explain the fallback. Standard lookback/per-source limits, archive URL deduplication, and AI review still apply.

If the website cannot be downloaded or parsed, usable feed entries are retained and the source note explains the failed fallback. This is not a browser renderer: JavaScript-only, restricted, or unusual listings still require an adapter. In the live web.dev check, its RSS stopped at October 2, 2026 while a newer October 13 post existed, but its downloaded blog HTML contained only a JavaScript-loaded listing. Automatic mode now checks that page and reports the extraction limitation rather than claiming to recover unseen articles. RSS/Atom-only mode never performs this stale-content website check; Website page mode continues to ignore feeds.

### Collection buttons and new sources

- **Check Sources** in the toolbar and **Refresh Sources** in Settings check every enabled source and then review a bounded batch from the pending archive.
- **Check New Sources (N)** in Settings > Sources checks only enabled, active sources whose `last_checked` is empty. It shows the eligible count and is disabled when the count is zero or a job is running.
- **Check** on a source row collects only that enabled source, then reviews only its pending stories, up to the usual review limit. It is disabled for disabled sources and while a job runs. Run history marks it **(source name) only**, and like a new-source check it does not restart the scheduled full-collection interval.
- A successful or failed collection attempt records a check, so failed sources are excluded from Check New Sources thereafter. Use Refresh Sources to retry them. Disabled sources are skipped by both buttons.
- Adding a source makes it eligible; Preview Stories and Find RSS/Atom Feed do not record collection checks. Editing a checked source does not reset its check history. Fresh start's run/source-check reset makes retained enabled sources eligible again.
- New-source collection uses the same lookback/per-source limits and URL deduplication as full collection. If a model is configured, it reviews only pending stories from the sources selected for this run, up to the usual review limit; unrelated backlog stays queued. No eligible sources means no downloads or reviews.
- The job shares the normal collection lock and writes a collection run with `details.scope="unchecked"` and `sourcesChecked`. Run history marks it **new sources only**. It does not restart the scheduled full-collection interval; normal manual full collection still does.

The API equivalent is `POST /api/jobs/start` with JSON `{"kind":"collect","uncheckedOnly":true}`. Omit `uncheckedOnly` or use `false` for the existing full-source behavior. Use `{"kind":"collect","sourceId":N}` to check one enabled source; it cannot be combined with `unchecked_only`. A review-only job cannot be combined with either. The previous `/api/collect` endpoint remains a compatible alias. Successful starts return HTTP 202; HTTP 409 means another job holds the shared lock. Browser writes use the same authentication, origin, and dataset-revision protections as other actions.

### Health and refresh results

The Sources tab shows a persistent refresh status while collection/review runs, followed by its recorded result. **Refresh Sources** uses the same background job as **Check Sources**: collect candidates, then review a bounded batch if a model is configured. Individual failures remain attached to their sources and in run history; they do not prevent other sources from finishing.

Each source records its last check, last success, consecutive failures, entry count, actual method, newest reported publication, and last newly inserted story. Status distinguishes **Not checked**, **Healthy**, **Check failed**, **Broken** (three or more consecutive failed checks), and **Disabled**. Stale/inactivity warnings show their detailed red explanation without a redundant Needs attention heading. When the collection note already explains a stale or undated feed, the separate age warning is omitted so the same observation is not listed twice. A successful check resets the failure counter.

The default inactivity warning interval is **14 days**, editable per source (1–365). Warnings cover checks older than that interval, an old newest publication date, an empty result, or no newly observed stories when dates are unavailable. An inactive/stale warning is an observation, not proof a publisher has shut down. Increase the interval for monthly/occasional publishers. An automatic switch from a configured broken feed to a usable website page is marked as a warning, with the original feed error visible.

Browser-to-app connection failures are separate from publisher/source errors. A persistent bottom-left status message identifies an unreachable server, unexpected non-JSON responses, or a missing runtime database. While the tab is visible, failed connections retry read requests every five seconds; write requests are never replayed automatically. If the start request loses its response, the UI says the refresh was **not confirmed**, then checks server status rather than claiming it failed or succeeded.

### Feed availability

Some publishers reject automated requests (for example with **HTTP 403**); the error stays visible beside the source after collection. The app does not bypass publisher access controls. Sites behind a Cloudflare browser check ("Just a moment...") open normally in a browser, which runs the check's JavaScript, but refuse NewsReview; the error says so. Look for a feed address the site does not challenge, or disable the source. Availability can vary by host, region, or time, and feed addresses can change. Use **Preview Stories** in the source editor to check a feed or website listing without saving anything.

An individual source failure is recorded next to that source and in run history. Other sources continue.

## Interests and editorial behavior

**Settings > Interests** contains editable topic rows with **Favor / Normal / Less / Exclude**, freeform editorial guidance, and explicit exclusions. A fresh install starts with no interest topics, short guidance asking for concise factual summaries that favor substantive reporting over rumors and marketing, and exclusions for deals/coupons, sponsored content, and buying guides. Edit all of them to match what you follow.

Saving affects the next job's AI decisions. A currently running job keeps the settings snapshot it started with. A feed's topic dropdown is an immediate view filter and does not call the model.

**Reassess History** queues every saved story for review with the current interests and model. It requires confirmation because it can be a lot of work. Existing summaries remain visible while queued; a successful new review replaces the previous summary and model metadata. Story IDs, publication dates, discovery dates, and bookmarks are preserved. This is an article archive, not a version history of every generated summary.

Filtered stories stay in the database. Their default view is hidden, so you can inspect the decisions or reassess them later. Source retention remains independent of relevance.

### Fresh Start: selective reset

Choose **File > Fresh Start...**, which works like TaskList's and SpendTracker's: each option shows what it would clear (counts, plus the size of the pre-restore recovery backups), the database size is shown below the options, and after clearing the database is compacted to free disk space. **Pre-restore recovery backups** deletes the ZIPs in `data/backups` and is checked by default. Choose the categories to clear, and type `RESET SELECTED`. History and run/source-check history are initially checked. Bookmarks, interest topics/ranks, editorial guidance, exclusions, configured sources, AI settings, and collection settings can each be selected separately.

Clearing history keeps bookmarked stories unless Bookmarks is also selected. Bookmarks alone removes saved flags without deleting stories. Clearing sources retires the configuration while preserving labels on retained stories; defaults are not reseeded. Resetting AI/collection settings restores their defaults. Unchecked preferences remain intact. The operation is transactional and rejected while collection/review runs.

Clearing history or runs restarts the automatic collection interval from the reset time, including after restart. Manual collection remains available. Removed URLs may be collected again. Backups and logs are separate files and are retained. This is not a secure disk wipe.

## Collection and AI pipeline

1. Snapshot the saved source, collection, model, and editorial settings for the job.
2. Read each enabled source using its saved Automatic/feed/page method. Sort by publication date when available; undated website entries retain page order. Initial limits are eight entries per source from the last three days; undated entries remain eligible. Limits select the newest available candidates, not eight guaranteed new entries per run.
3. Normalize URLs by removing fragments and known tracking parameters. A unique URL constraint prevents repeated insertion. Preserve original headlines, excerpts, and publication dates separately from discovery/edition dates.
4. If configured, process a bounded batch of pending reviews. Extract article text from JSON-LD `articleBody`, or substantial paragraphs under `<article>`/`<main>`. A failed, blocked, or unreadable article falls back to its source excerpt. Missing text leaves the headline/link with a review error that says whether the article page could not be downloaded (with the reason, such as a Cloudflare browser check or a timeout) or downloaded without readable text.
5. Skip obvious sponsored/deal/coupon headlines using a small fixed filter. Other editorial decisions use the configured review model and interest snapshot.
6. Supply up to five likely duplicate candidates, chosen from the 150 most recently collected reviewed stories using overlapping headline terms. The model may group coverage of the same event; meaningful follow-ups should remain separate stories.
7. Validate exact output fields, types, tag limits, score range, milestone, and duplicate ID. Keep only tags from the managed Topics list; unknown names are discarded without invalidating the summary. Save the valid review together with provider/model, input basis, current interests, review time, and token counts when the server reports them. Reviews can have an empty tags array even when configured topics exist.

Without a configured model, collection still saves headlines and available excerpts; **Review Pending** runs the AI stage later.

The model receives article content as untrusted data, has no tools, and is instructed to ignore embedded commands. The app renders publisher/model text using DOM text nodes, never as executable HTML. Model output validation catches structural problems, not every factual or editorial error.

Deduplication is deliberately conservative and approximate. Identical URLs are deterministic duplicates; different URLs require a model decision and can occasionally be missed or grouped incorrectly. The main tile exposes additional coverage links. The **Include filtered / duplicates** option makes those decisions inspectable. No vector database or embedding model is required.

Stored discovery/edition dates are retained for collection history; Daily Feed browsing and date searches use publication dates with collection-date fallback. Undated articles keep a null publication timestamp; the app does not invent one. The feed preserves publication dates as reported, without independently verifying publisher timestamps. Extracted article text can be partial; "article" records the extraction basis, not a guarantee of complete access.

### Queue, retries, and scheduling

- **Check Sources / Refresh Sources** fetches feeds or website listings and then reviews up to the saved limit.
- **Review Pending** skips feed collection and processes the existing queue. While reviews are running, the button becomes **Stop Review** (also during the AI phase of a full collection). Stop finishes the article already being fetched/reviewed, then stops the batch and leaves unprocessed stories queued. The button shows **Stopping review...** until the in-flight article finishes. Network/model timeouts still apply; this does not forcibly terminate an Ollama process or interrupt its active request.
- Completed reviews remain saved, the configured model-unload cleanup runs, and the review stop is recorded in run details as a partial run. Click Review Pending again to resume. The collection scheduler stays enabled, so a later scheduled/full run can process pending stories. Review stopping never cancels source collection or disables future automatic work.
- Manual and scheduled jobs share a lock, so only one collection/review job runs at a time.
- Each failed review increments an attempt count. Unreadable articles with no usable excerpt are recorded individually and skipped without a model call; they do not increase or reset the consecutive model-failure counter. Later articles continue. After three actual model-review failures without a successful review, the batch stops. After an article reaches three attempts, use **Reset Failed Reviews** once the underlying issue is fixed. Previously saved topic-validation errors also need Reset Failed Reviews to retry; old run records are retained.
- Fresh/unattempted entries are processed first, newest first; older pending entries can wait on busy days. Increase the per-run limit or run additional review batches to catch up.
- Collection is due when the configured hours have elapsed since the most recent full collection started. Manual full collections restart the interval; review-only and new-source-only jobs do not. With no previous full collection it runs on the next scheduler tick. Existing daily settings become a 24-hour interval; their old hour/minute fields are retained for archive compatibility but no longer control scheduling.
- There is no wake-from-sleep mechanism. If the service was offline, it checks current feeds when it resumes; it cannot reconstruct stories that have already disappeared from those feeds.
- The schedule survives restarts through the SQLite run history. Partial/failed full runs also restart the interval to avoid rapid retries; retry manually if needed. Missed intervals produce one catch-up run, not a replay of every missed run. An interrupted process is marked interrupted at the next startup.
- Source requests are bounded in size and time, with a short pause between sources. Feed, website, and article downloads are limited to 10 MB, enough for full-content feeds; model responses to 1 MB.

Opening a story headline with a click, keyboard activation, or middle click adds **Read** beneath the publisher after the save succeeds. The first-open timestamp persists across reloads/devices and ZIP backups, and is preserved when a story is reviewed again or bookmarked. Opening additional-source links marks that linked story, not the main article. No Unread label or automatic read-content detection is added; this tracks openings through NewsReview. Clearing/deleting a story also removes its marker. Existing stories begin with no marker. Schema-2 backups remain importable and get empty read markers.

## Article feedback and learned preferences

Each Daily Feed and Bookmarks tile has thumbs-up/down buttons, separate from bookmarking. Click one, optionally explain what you liked or disliked, then Save. You can edit the vote/reason or remove that article's feedback from the same dialog. Reasons can distinguish the topic from the article's quality or an incorrect AI summary. Feedback affects future review prompts; it does not instantly change existing scores, summaries, or sorting. Reassess History explicitly if you want older stories reviewed again.

Ratings, optional reasons, and short article snapshots are saved by URL inside the SQLite interests preferences. Repeatedly saving the same vote/reason is a no-op. Recollected URLs recover their rating; deleting/purging story history does not erase feedback. Up to 10000 ratings are retained, with reasons limited to 320 characters. There is no new runtime dependency or model retraining.

Under **Settings > Interests > Learned Preferences**, view/edit the compact profile, enable/disable its use, or control automatic updates. When ratings change, the next batch with pending reviews makes at most one extra model call to rebuild the profile from the **10 most recent ratings** (bounded headline, source, tags, excerpt/summary, and reason). Older ratings stay stored but are not included in that bounded refresh. Unchanged feedback reuses the cached profile. Each normal article review receives only the profile (at most 2400 characters), never the full rating/reason archive. Explicit written interests, guidance, and exclusions take priority; feedback guides relevance rather than supplying article facts.

**Update from Feedback** rebuilds it immediately using your configured model; it is blocked while another job runs and requires at least one saved rating. **Save** stores your edited profile and marks it current. Later ratings may replace it if automatic updates remain enabled, so turn automatic updates off to keep a manual profile. Disabling Use learned preferences retains data but removes the profile from model prompts. Failed profile generation keeps the previous profile and lets article reviews continue; run details report the failure. New votes/manual edits arriving during generation prevent a stale generated result from overwriting newer preferences. Model loading/unloading follows the existing AI controls, with warm reuse within a review batch.

**Reset Feedback...** confirms before clearing all votes, reasons, and the profile. Fresh Start also has a separate unchecked feedback option. Written interests, guidance, bookmarks, and articles remain unless separately selected. Feedback is included in ZIP exports/restores; older backups without it load with empty feedback defaults. Because it is stored as validated optional fields in existing SQLite settings, the read-marker schema update preserves import support for version-2 ZIP table layouts.

## Purging duplicate stories

Open **Settings > Collection & History > Purge Duplicates**. The confirmation dialog previews the current eligible count; Cancel leaves the archive unchanged. Purging permanently deletes stories marked duplicate by AI review, excluding bookmarks and any story referenced by another saved story. Main stories, other history, preferences, sources, and run history remain intact. It does not guess duplicates from similar headlines or remove unrelated filtered/pending stories. Without AI-marked duplicates there is nothing to purge.

The server rechecks eligibility inside a write transaction and refuses purging during collection, review, or restore. It updates the dataset revision so other tabs reload. SQLite VACUUM runs after deletion to shrink the database, which can take time and needs temporary free disk space. If compaction fails, the deletion remains committed and the UI reports the limitation; freed pages remain reusable. Space saved depends on the number and size of eligible stories; existing backup ZIPs and logs are separate and unchanged. Deleted URLs may be collected again while sources still list them. Export a backup before purging if you need a recovery path.

## Data, configuration, and logging

By default, runtime data is stored in `data/` under the folder you start NewsReview from (its content root):

```text
data/
  newsreview.db         stories, sources, settings, bookmarks, run history
  newsreview.db-wal     SQLite's live write-ahead log, when present
  newsreview.db-shm     SQLite's shared-memory file, when present
  auth.json             saved login password hash, excluded from exports
  model-credentials.json  saved model API keys, excluded from exports
  keys/                 sign-in cookie keys when there is no Windows profile key ring (Linux, Task Scheduler), excluded from exports
  backups/              recovery ZIPs written before each restore
logs/
  console.log          application/HTTP logs, one file up to 10 MiB
```

Both directories are Git-ignored. Runtime logs use TaskList's logger: application startup/shutdown, HTTP requests, warnings, errors, and exceptions go to the interactive console and `logs/console.log` under the content root. Before an entry would exceed 10 MiB, that same file is truncated and writing continues; no rotated copies are created. `--data-dir` moves the database, credentials, and cookie keys, not the log directory. File logging failures leave console logging available. Logs are excluded from exports and Fresh start. SQLite uses WAL, foreign keys, a busy timeout, and `synchronous=NORMAL`. Use local storage; WAL is not intended for an SMB/NFS network share. A sudden power loss can lose the newest commits even though the database should remain structurally consistent.

`Store.SchemaVersion` in `src/Storage/Store.cs` is the authoritative current SQLite schema version (4); database startup and ZIP validation use the same value. `PRAGMA user_version` records that version in each database. The v0.3.0 migration adds source collection/health fields. The app refuses to open a newer schema version. It creates fresh tables and default settings once; future schema changes belong in explicit migrations.

### Feed reads and upgrade work

Schema 4 adds an indexed UTC publication timestamp in exact integer microseconds, falling back to collection time only when publication is unknown. Original publication/collection strings are retained. On first upgrade, existing stories are backfilled in bounded batches and compact publication/bookmark indexes are built. This is a one-time pass over existing history that requires disk activity and a little additional index space; later restarts do not backfill the archive again. Login credentials, model keys, interests, bookmarks, read markers, summaries, and feedback remain intact.

Day and date-range searches translate browser-local calendar boundaries into UTC bounds and look up the matching index range. Adjacent local midnights are converted separately so 23/25-hour daylight-saving days and offset timestamps remain correct. Time-of-day filters then apply within the selected date range. Without an explicit browser timezone, the existing server-local interpretation is retained.

The feed counts matching records, selects at most 48 page IDs, and only then retrieves display fields for those stories. Article bodies, legacy significance text, and review-interest snapshots are not selected for feed reads. Related-source counts use the duplicate index. Keyword, topic, and all-time searches must check every story, so they scan a covering search index holding the headline, summary, excerpt, topics, source, status, score, bookmark, and timestamp instead of the story rows; reading those rows would also read each stored article body from disk. The search index adds roughly the size of those text fields and is built once on the first start after upgrading. If the browser abandons a feed request (it gives up after 30 seconds), the server interrupts the query instead of finishing it. Overview requests still calculate archive counts and available discovery days from compact indexes and return the eight most recent runs; the app does not keep the entire database in RAM. There is no added idle polling or full-history browser download.

Broad archive keyword searches and ranking across all history can still examine many matching records; those requests intentionally span the archive. Opening SQLite, reading assets/logging requests, and scheduled collection still involve disk access. A sleeping drive can therefore still need to spin up even though a daily feed no longer scans unrelated article records.

Current ZIP backups include the derived timestamp/indexes. Schema-2 and schema-3 backups are accepted, validated against their original table layouts, and backfilled transactionally when restored; schema-2 read markers remain empty. A newer backup is still refused by an older app. Keep an export before upgrading if you need a recovery copy.

The database stores original URLs/headlines, excerpts, bounded extracted text, summaries, tags, milestones, dates, relevance, bookmark status, duplicate relationships, review attempts/errors, model metadata, and the interest snapshot for the latest review. History is retained until explicitly cleared; there is no automatic expiration.

| Environment variable | Purpose |
| --- | --- |
| `NEWSREVIEW_DATA_DIR` | Default runtime directory, overridden by `--data-dir` |
| `ASPNETCORE_URLS` | Listening address, like `--urls`; default `http://127.0.0.1:8714` |
| `NEWSREVIEW_MODEL_API_KEY` | Optional fallback bearer token when the provider/server has no UI-saved key |

| Command-line option | Purpose |
| --- | --- |
| `--urls URL` | Listening address, for example `http://0.0.0.0:8714` for LAN access |
| `--data-dir PATH` | Runtime data folder |
| `--no-scheduler` | Disable automatic collection for this process |
| `--version` | Print the version and exit |
| `backup --output FILE [--database FILE]` | Consistent SQLite backup; see [Offline SQLite backup](#offline-sqlite-backup-and-restore) |

`appsettings.json` holds the default listen address (`Urls`) and log level, the same keys in every app; the variables above and `--urls` override it. The app does **not** automatically load `.env` files. Use your shell, service manager, or a private environment file outside the repository. Secrets are not written to SQLite or returned by the settings API.

The JSON API uses camelCase names, like TaskList, TaskList Stats, and SpendTracker (for example `feedUrl` and `uncheckedOnly`); older snake_case names in requests are still accepted.

## LAN access and authentication

Loopback access is the default. For LAN access, explicitly change the listening address:

```bash
dotnet run --urls "http://0.0.0.0:8714"
```

On first startup, NewsReview prints a one-time setup token in the server console. Open the app, enter that token, and create and confirm a password (8–1024 characters). The setup/login page uses the same layout as TaskList and TaskList Stats. The token changes on restart until setup succeeds, then is discarded. It is not an API key and is not written to `logs/console.log` (a service manager may capture console output).

The salted PBKDF2-SHA256 password hash (600,000 iterations) is saved automatically in `data/auth.json` (or your chosen data directory), with owner-only permissions on POSIX. Future starts ask only for your password. A hash saved with fewer iterations still verifies and is re-hashed once, on the next successful login, by writing a temporary file and renaming it. The password code is the same file (`src/PasswordAuth.cs`) in all four apps; files saved by earlier NewsReview releases still verify. A malformed or interrupted `auth.json` signs nobody in rather than reopening setup; if first-run setup was interrupted, stop the app and remove only the incomplete `auth.json` before trying again.

ZIP export/import and Fresh start keep authentication separate: they never export, replace, or delete this file. Preserve it separately when moving the installation. To reset a forgotten password, stop the app, remove only `auth.json`, restart, and use the new console token. Your stories and settings stay intact.

The browser login issues an HttpOnly, SameSite=Strict cookie that lasts 30 days and slides while you keep using the app. It is marked Secure automatically when the request uses HTTPS. The keys that protect the cookie survive restarts, so sessions do too. When NewsReview runs from a signed-in Windows account, they are kept in that profile's ASP.NET key ring (`%LOCALAPPDATA%\ASP.NET\DataProtection-Keys`), as for TaskList, TaskList Stats, and SpendTracker. Otherwise (Linux, or Windows Task Scheduler without a loaded profile) they are stored in `data/keys/`, protected on Windows with machine-scope DPAPI. The startup log names the location (`Sign-in keys: ...`). Delete the `data/keys/` folder while NewsReview is stopped to sign every browser out there. Setup and login share a limit of five attempts per IP address per minute; a rejected cross-origin request does not count.

API writes require JSON and reject cross-origin browser requests. Only the files in `wwwroot/` are served; the database, logs, and source files are not. Until setup is complete, all reading data and settings remain protected; only the setup/login page and authentication endpoints are accessible. Public news-source requests reject private/reserved network addresses, including on redirects. The local model endpoint intentionally permits LAN addresses.

This is a single-user/household application, not a public multi-tenant service. Bind to loopback behind an HTTPS reverse proxy for remote access, and preserve the incoming `Host` header. Keep the model server on a trusted network. Source and model settings are trusted-administrator capabilities; URL checks are defense in depth, not complete isolation against hostile DNS infrastructure.

## Run as a service

The app must stay running for its scheduler to run. It can also be launched at login using your existing service/task setup.

### Linux / systemd

`deploy/newsreview.service` is a template, not an installer:

1. Create a dedicated service account and a private writable data directory.
2. Build the app into the desired application path with `dotnet publish -c Release -o /opt/newsreview`, and create its `logs/` directory, writable by the service account.
3. Edit `User`, `WorkingDirectory`, `ExecStart`, and `ReadWritePaths` to match those paths. `ProtectHome=true` means this template expects the app outside `/home`, such as `/opt/newsreview`.
4. If needed, put variables in `/etc/newsreview.env`, readable only by the service administrator. The template loads it as an optional environment file.
5. Install the unit using your normal system administration process, then enable/start it.

The template listens on loopback. Change its `--urls` value for direct LAN access; find the first-run setup token in the service console/journal. The scheduler uses elapsed hours, independent of timezone or daylight-saving changes. The app does not install or alter system services automatically.

### Windows / Task Scheduler

Build once with `dotnet build` (or `dotnet publish -c Release -o C:\path\to\newsreview`), then create a task that starts `NewsReview.exe` directly with arguments such as `--urls "http://0.0.0.0:8714"`, and set **Start in** to the folder containing `wwwroot` (the checkout, or the publish folder). Configure it to run at startup/login and restart on failure. Set environment variables for the account/process that launches it. Keep the process running rather than scheduling a new copy each day; the app owns the interval schedule.

## First-run backup reminder

When NewsReview creates a fresh database, it records a pending reminder outside SQLite, like TaskList. After password setup/login, the main application shows **Restore a Backup Before Starting**, the same box as TaskList's:

> If you have a NewsReview backup ZIP, restore it before adding sources. Use File > Import/Export... to restore it. Restoring a backup replaces all current data, so anything you set up first would be replaced by a later restore. If you don't have a backup, choose Start Fresh.

**Import/Export...** acknowledges the reminder and opens the Import/Export dialog. **Start Fresh** acknowledges it and continues; it does not reset or delete data. Escape follows Start Fresh. Acknowledgement is saved for the server, so the reminder stays dismissed across browsers and restarts. Existing installations do not acquire the reminder just by updating. A successful restore also acknowledges it, including restores made directly through the API. The reminder marker (`data/import-reminder.pending`) is outside the database and is not part of backups.

## Backup, restore, and updates

### ZIP import/export in Settings

**File > Import/Export... > Download Backup** downloads `newsreview-backup-YYYYMMDD-HHMMSS.zip` (local time, like the other apps' downloads), a snapshot of the live database including committed WAL changes. The ZIP contains exactly `newsreview.db` and `manifest.json` (`format`, `formatVersion`, `applicationVersion`, `schemaVersion`, `exportedAt`, `sha256`, and per-table `totals`), the same manifest naming TaskList and SpendTracker use. Backups from v1.0.9 and earlier, which spell those keys in snake_case, still restore. It includes stories, bookmarks, sources, interests, editorial guidance, exclusions, model/collection settings, and run history. Environment credentials, logs, previous backups, and browser theme are excluded. Backups contain private reading/preferences data and are not encrypted.

**Choose File to Restore...** picks a backup ZIP, then a **Restore Backup** dialog asks for confirmation, like the other apps. This replaces rather than merges the dataset. Uploads are limited to 256 MiB compressed and 1 GiB of database data. The server rejects extra archive members, damaged checksums, unsupported/newer versions, unexpected schema objects, invalid relationships, or invalid configuration. It imports validated rows into its own schema, without executing imported schema SQL. Before replacing data, it writes a recovery ZIP to `data/backups/before-restore-*.zip`; failure to save it aborts the operation. Keep enough free disk space for the current database, upload, extracted database, and recovery ZIP. Restore is blocked while collection/review runs. Interrupted imported runs are marked interrupted.

Restore and reset change a dataset revision: other tabs refresh before making further writes. After an interrupted upload response, the UI reloads actual server data and reports that the restore could not be confirmed instead of retrying it automatically. Recovery ZIPs can be restored through the same dialog. The offline SQLite backup command remains available for larger datasets.

### Offline SQLite backup and restore

To create a consistent online backup, including while the app runs:

```bash
dotnet run -- backup --output backups/newsreview-2026-10-15.db
```

For a custom runtime directory, or with a built/published app:

```bash
dotnet NewsReview.dll backup --database /path/to/data/newsreview.db --output /path/to/backups/news.db
```

The command refuses to overwrite an existing file and checks the new database's integrity. Store backups privately: they contain your reading history and preferences. No credentials are in the database, but environment/service configuration must be backed up separately if needed.

To restore: stop the app, move the entire existing data directory aside as a recovery copy, create a fresh data directory, and place the backup there as `newsreview.db`. Restart using that directory. Do not leave old `-wal`/`-shm` files beside a restored database. Do not copy only the main SQLite file while the app is writing; use the backup command or stop the process first.

Before an update, make a backup, pull the code, rebuild (or run `dotnet run`, which builds), and restart. Runtime data is ignored by Git, and saved defaults are not reset. There is no browser service worker caching old application files.

**Do not delete the data folder while the app is running.** This removes its database and preferences outside the application's lifecycle. If this happened, stop and restart NewsReview to create a fresh database, or restore a backup before restarting. The running server now returns an actionable HTTP 503 instead of silently recreating an empty SQLite file or reporting a generic internal error. Use **Fresh start** in Settings when you only want to clear reading history and retain configuration.

### Upgrade from the Python release (0.x)

NewsReview 1.0 replaces the Python implementation with ASP.NET Core and keeps your data. Point it at the same data folder:

1. Download an export ZIP from the old version (**File > Import/Export...**) and keep it as a recovery copy. The Python release cannot restore ZIPs exported by 1.0, so this is your way back.
2. Stop the old `python3 -m newsreview` process (or its service/scheduled task).
3. Install the .NET 10 SDK, pull this release, and start it with your old data folder and listening address, for example `dotnet run --urls "http://0.0.0.0:8714" --data-dir C:\path\to\data`. `--host`/`--port` became `--urls`.
4. Sign in with your existing password. Everyone is signed out once, because the session cookie changed.

Your stories, sources, settings, topics, feedback, bookmarks, read markers, `auth.json`, and saved model API keys are used as they are; nothing is migrated or rewritten. Older ZIP backups, including schema-2/3 exports, still restore. `NEWSREVIEW_PASSWORD` and `NEWSREVIEW_SECURE_COOKIE` are no longer read, and the unused `/api/collect`, `/api/login`, `/api/session`, `/api/logout`, and `/api/history/clear` aliases were removed.

## Browser-blocked requests

If DevTools reports `net::ERR_BLOCKED_BY_CLIENT`, the request was blocked on the browser side, commonly by a content blocker or a browser request-blocking rule. In the reported case, GET requests worked but a POST whose path ended in `collect` was blocked immediately; NewsReview starts jobs through `/api/jobs/start` to avoid that pattern. If blocking persists, allow the NewsReview origin in the responsible extension or remove the matching DevTools blocking rule. A generic fetch failure cannot reliably identify the extension or prove whether a write reached the server, so NewsReview reports it as unconfirmed, checks current status, and never automatically resends the write.

## Troubleshooting

| Symptom | What to check |
| --- | --- |
| Check New Sources is disabled | All enabled sources have a recorded check, or a collection/review job is running. Enable a new source or use Refresh Sources to retry checked sources. |
| Source preview works but no new tiles appear | Preview never saves history. Collect afterward; lookback/per-source limits, known URLs, the selected edition, topics/search, or hidden filtered/duplicate coverage may explain the result. |
| Stories show excerpts and gray squares | AI is paused, not yet run, or queued for reassessment. Save a model, test it, then use Review Pending. Check Recent runs for failures and retry limits. |
| Reviews stop with HTTP 400 | The model server rejected the requests. Check the JSON explanation in Recent runs or `logs/console.log`, and the model server console if only the status appears. Confirm provider/model/settings, test with a sample, then retry. Previously recorded generic errors cannot recover a discarded response body. |
| A visible story does not match a time search | Times use the tile timestamp and browser timezone; dates use the publication day, falling back to collection day. Clear other filters and include filtered/duplicate coverage if necessary. |
| A model change did not replace existing summaries/scores | Model/interest changes affect future reviews. Use Reassess History to queue the archive explicitly. |
| Connection or storage error in the footer | Check the app process, address, logs, and data directory. A deleted running database requires stopping and restarting or restoring a backup; write actions are not replayed automatically. |

### Database startup errors and pooled drives

A database connection can open initially and then fail during SQLite setup when it accesses its journal/shared-memory files. NewsReview now handles `SQLITE_CANTOPEN` throughout connection setup and use, closes the connection, and reports the database path and recovery checks. Startup exits with that explanation instead of an unhandled exception. Other SQLite errors retain their original meaning; the app does not recreate the database, delete sidecar files, change journal mode, or silently relocate data.

For **unable to open database file**, confirm the data directory and existing database are accessible, the account running NewsReview can write there and create/open `newsreview.db-wal` and `newsreview.db-shm`, and another NewsReview process is not running against the same folder. The error alone does not prove corruption or identify a specific DrivePool fault.

DrivePool is a local pooled filesystem when accessed directly on its host; it is not inherently an SMB share. If the failure occurs on a pooled drive, isolate that path by testing the complete data folder on a regular local disk on the same machine:

1. Stop every NewsReview process and retain a backup of the original folder.
2. Copy the entire `data` folder to an unused local location, for example `C:\NewsReviewData`, including the database, any `-wal`/`-shm` files, `auth.json`, and `model-credentials.json`. Keep the original intact. Do not copy only a database main file from an active application or delete its sidecars.
3. Run from the existing application directory:

   ```powershell
   dotnet run --urls "http://0.0.0.0:8714" --data-dir "C:\NewsReviewData" --no-scheduler
   ```

4. Verify the existing history/settings load. If this works, it isolates the original storage path/filesystem/access conditions; it does not prove which DrivePool setting caused the failure. Keep using the same `--data-dir` (or set `NEWSREVIEW_DATA_DIR`) for later launches, and omit `--no-scheduler` to resume automatic collection. Do not start another instance against the original folder.

The code may remain on the pooled drive. Moving the data folder also moves login credentials and saved model keys; ZIP exports exclude those files. There is no automatic copy or change to your storage. For an actual SMB/NFS share, SQLite WAL requires local storage; see [SQLite's WAL requirements](https://sqlite.org/wal.html).

## Development checks

The automated suite needs only the .NET SDK:

```bash
dotnet test tests/NewsReview.Tests
```

It uses real temporary SQLite databases, the real application hosted in memory, and local HTTP fixtures for RSS, article pages, and Ollama/compatible/OpenRouter model responses. Tests cover parsing, extraction/excerpt fallback, settings/model validation, duplicate references, queue retries, idempotent/full/new-source collection, isolated review queues, interval scheduling, source failures, history/search/timezones, sorting and query plans, bookmarks, feedback, topics, model keys, ZIP restore validation and recovery, selective resets, authentication and sessions, origin checks, and static-file boundaries. `PythonCompatibility.json` holds outputs of the earlier Python release for URL canonicalization, URL joining, feed dates, and HTML text, so stored story URLs keep deduplicating. No tests contact live sources or require a real model.

Optional real-browser checks drive the actual frontend with Chromium through Playwright:

```bash
dotnet build tests/NewsReview.BrowserTests
pwsh tests/NewsReview.BrowserTests/bin/Debug/net10.0/playwright.ps1 install chromium   # first time only
dotnet test tests/NewsReview.BrowserTests
```

Browsers already installed by Playwright 1.60 for Python or Node are reused, so the install step can be skipped on such machines. They cover first-run setup and login, desktop/mobile reading, bookmarks, read markers, day navigation, keyword/date/time search, topics and relevance filters, sorting, interests, feed discovery, website preview, source health, new-source refresh, offline/reconnect handling and idle polling (with Playwright's test clock), AI providers, model keys, feedback and learned preferences, Stop review, duplicate purging, ZIP transfer, selective reset, and both Windows 95 themes. They write screenshots to the system temp folder and use temporary data. Playwright is a development-only tool, not a runtime dependency.

GitHub Actions builds the app and runs `tests/NewsReview.Tests` on every push. A real installed user model still needs its own connection and quality check.

## Project layout

```text
NewsReview.csproj         project file and authoritative version
src/
  Program.cs              startup, options, authentication, security headers, and API routes
  PasswordAuth.cs         setup token, password file, and sign-in cookie (the same file in all four apps)
  Hosting/
    Auth.cs               keeps the auth.json path and this run's setup token for the endpoints
    SingleFileLogger.cs   single-file logging with in-place 10 MiB truncation (shared with TaskList)
  Storage/
    Store.cs              SQLite schema, queries, queue and settings persistence
    Defaults.cs           first-run interests and collection/model settings
    Search.cs             TaskList-compatible date/time range parsing
    Transfer.cs           validated ZIP snapshots, transactional restoration, and the backup command
  Collection/
    Collector.cs          collection/review worker and interval scheduler
    Sources.cs            Automatic/feed/page collection strategy and source health
    Feeds.cs              RSS/Atom parsing/discovery and article extraction
    Listings.cs           website article-card/headline/structured-list extraction
    HtmlTokenizer.cs      tolerant HTML tokenizer used by the extractors
    Network.cs            bounded HTTP transport and public-address checks for news sources
    UrlTools.cs           URL splitting, joining, and canonical forms matching stored story URLs
  Review/
    Reviewer.cs           model adapters, editorial prompt, output validation
    Feedback.cs           bounded ratings, profile validation, and compact model context
    ModelKeys.cs          provider/server credentials and separate atomic key-file persistence
  Common/
    Validation.cs         strict JSON typing and source/settings input validation
    PyText.cs             code-point lengths and ISO timestamps matching stored records
wwwroot/
  login.html              TaskList-style setup/login page
  login.js                setup token, password creation, and sign-in
  theme.css               light/dark color tokens shared by every stylesheet
  auth.css                shared sibling-app login layout
  index.html              reader, settings panels, dialogs
  style.css               responsive Windows 95 layout and components
  app.js                  startup, navigation and collection status
  shared.js               API, DOM, notification and formatting helpers
  feed.js                 tiles, search, dates, pagination and bookmarks
  settings.js             interests, topics, sources, AI and scheduling controls
  transfer.js             browser ZIP download/upload and restore confirmation
  dropdowns.js            shared Win95 single-select and editable category menus
  icons/                  favicon.svg, favicon.ico, and touch-180/192/512.png, the same names in every app
tests/
  NewsReview.Tests/       unit and integration tests
  NewsReview.BrowserTests/ optional Playwright browser checks
deploy/                   example systemd unit
appsettings.json          listen address and log level
```

## Maintenance and cleanup

Cleanup is behavior-preserving work in the owning files, not a rewrite or a line-count target. Keep compatibility code unless it is demonstrably obsolete, avoid runtime overrides and release-specific shims, and do not add dependencies just for cleanup. Acceptance of older source markers and schema-2/3 layouts in ZIP restores is retained for existing backups.

The application owns dataset reloads after reset/restore, invalidating pending feed reads before replacing cached tiles and loading settings/sources once. Fresh-start checkbox defaults live in the HTML form; opening the dialog resets that form rather than maintaining a second JavaScript default list. `Store` owns the current schema version and archive validation uses it. ZIP and standalone SQLite backup paths remain separate because their output formats and recovery/error handling differ.

For verification, run `dotnet build` (no warnings expected), the unit/integration suite, and the relevant browser checks. Review the final diff, release version, README headings, and compatibility behavior before making a versioned commit with a detailed body.

## Release notes

### v1.0.38

- Every dialog is a `<form method="dialog">`, and its Cancel/Close/OK button is marked `data-close="dialogId"`; one shared handler closes it, the same code in every app.
- Keyboard focus draws the same dotted Windows 95 focus rectangle in every app; mouse clicks draw none.
- Screen readers get the same structure in every app: a hidden page heading with the app name and a **Skip to content** link that appears when you press Tab. (inputs used a solid outline before)
- Every button has an explicit type.

### v1.0.37

- The login and main pages share one set of page tags, the same in every app: a description, the iPhone home-screen and status-bar settings, the manifest, and icons.
- Icons live in `/icons/` with the same names in every app: `favicon.svg`, `favicon.ico`, and `touch-180/192/512.png`. The manifest has an app ID, scope, and description.
- The window and home-screen colors are navy and gray (#000080 / #c0c0c0) like the other apps; the icons moved from the site root to `/icons/`.

### v1.0.36

- SQL uses named parameters (`$id`, `$name`) like the other apps instead of `?` placeholders.
- The database has CHECK rules like TaskList's and SpendTracker's (source flags, mode and inactivity days, story status, relevance score, bookmark, attempts, and run kind/status). They are added once on startup without changing the table layout backups compare; if older rows break a rule, that table keeps none and the log says why.
- Success replies match the other apps: changes with nothing to return answer **204 No Content**, sign-in and setup answer `{ authenticated: true }`, and a missing story or source answers **404** instead of 400.

### v1.0.35

- Sign-in uses `src/PasswordAuth.cs`, the same file as the other apps. Existing passwords keep working and are re-saved in the shared format on the next re-hash; a malformed `auth.json` now signs nobody in instead of stopping startup.
- New `appsettings.json` with the listen address and log level like the other apps; `.env.example` is removed (the variables are listed in this manual).
- Like the other apps, switching back to the tab or window no longer reloads; use **File > Refresh**. Job-progress and reconnect checks still resume when a hidden tab is shown.

### v1.0.34

- Wording matches the other apps: menu paths are written **File > Import/Export...**, only "..." and straight quotes are used.
- Buttons, dialog titles and section headings use Title Case like the other apps (**Check Sources**, **Reset Feedback**, **Collection & History**), and dialog titles are no longer questions.
- The status bar shows **Loading...** until the first status arrives, then **Ready** like the other apps.
- An expired session shows **Your session has expired.** while going to the login page, like TaskList.
- "Update NewsReview first" replaces "upgrade the application" for newer databases and backups.

### v1.0.33

- The View menu has a **Tabs** heading over its tab list, like TaskList's **Tasks** heading.
- The About box points to File > Import/Export... for backups.

### v1.0.32

- Fresh Start works like TaskList's and SpendTracker's: each option shows a count, the dialog shows the database size, the database is compacted afterward, and a new **Pre-restore recovery backups** option clears data/backups. `GET /api/fresh-start` returns the counts.
- A query-plan test no longer flakes when a pooled connection kept an earlier trace.

### v1.0.31

- A brand-new database shows **Restore a Backup Before Starting** after the first login, the same box as TaskList's: **Start Fresh** or **Import/Export...**. Updating an existing installation does not show it.

### v1.0.30

- Import/Export works like the other apps: **Download Backup** and **Choose File to Restore...** as full-width buttons, and choosing a file asks for confirmation in a **Restore Backup** dialog (formerly a checkbox and a separate Restore backup button).
- Download names follow one pattern in every app, in local time: `newsreview-backup-YYYYMMDD-HHMMSS.zip`.

### v1.0.29

- One `theme.js` shared word for word with the other three apps: the page marks its theme with `data-theme`, and it loads at the top of every page, login included, so it never flashes the wrong theme.

### v1.0.28

- **View > Use Retro Font**: the whole app, login page and chart labels included, can use W95FA, an open-source recreation of the Windows 95 font bundled with the app (SIL Open Font License, `wwwroot/fonts/OFL.txt`), so it looks the same on Windows, Linux, macOS, Android, and iOS. Off by default; saved per browser.

### v1.0.27

- A File / View / Help menu bar like the other apps: **File** has Refresh, Import/Export..., Log Out (formerly Sign out), and Fresh Start... (moved from Settings); **View** switches tabs and chooses the Light or Dark theme; **Help** shows About.
- Daily Feed, Bookmarks, and Settings are tabs below a toolbar that holds **Check Sources** and **Review Pending**.
- The large Daily Feed and Settings page headings are gone, like the other apps.
- Day arrows are drawn triangles like SpendTracker's; dialogs have no × in their title bar.
- The theme loads from `theme.js` before the page draws, on the login page too, and the phone status bar is navy in the light theme like the other apps.

### v1.0.26

- The JSON API speaks camelCase like the other apps (`feedUrl`, `uncheckedOnly`, `timeZone`); snake_case request names are still accepted. The database and saved settings keep their names.
- Fixed a test that failed in CI after connections became pooled.

### v1.0.25

- Addresses match the other apps: `GET /api/backup` downloads a backup ZIP, `POST /api/restore` restores one, and `POST /api/fresh-start` runs Fresh start (formerly `/api/export`, `/api/import/backup`, and `/api/reset`).

### v1.0.24

- Database connections are pooled with foreign keys switched on in the connection string, like the other apps.

### v1.0.23

- Startup logs the version and data folder, the database settings, and where the sign-in keys are, in the same three lines as the other apps.
- The login page words wrong-password and too-many-attempts messages itself; setup conflicts say "A password is already configured."
- Unknown addresses load the app (or the login page) like the other apps; unknown API addresses still return 404.
- The version comes from `wwwroot/version.json`, like the other apps.
- `/api/auth/status` no longer includes `password_required`, which was always true.
- Database busy timeout is 5 seconds, like the other apps.
- Wrong setup tokens say "Invalid setup token." like the other apps.

### v1.0.22

- Pages and scripts are now revalidated with the server on each load instead of downloaded in full every time; API replies are still never stored.

### v1.0.21

- Restarting no longer signs you out on Windows: when NewsReview runs from a signed-in Windows account, its sign-in keys stay in that profile's ASP.NET key ring, like TaskList's, TaskList Stats', and SpendTracker's. The startup log names where the keys are. You are signed out once after this update.

### v1.0.20

- The login page's password box matches TaskList's, TaskList Stats', and SpendTracker's (no inner shadow, the same height), and the page uses their normal line height.
- The login page shows the NewsReview icon in the browser tab.

### v1.0.19

- The Home Screen icons have new file names (home-180.png, home-192.png, home-512.png), so iOS downloads the current icon instead of reusing one it saved earlier. To update an app already on your Home Screen, remove it and add it again from Safari's Share menu.

### v1.0.18

- On phones (650px wide or less) the title bar leaves out the version number, which was often cut off; desktops and the About box still show it.

### v1.0.17

- On phones the title bar starts the same distance from the top as TaskList's (`--ios-top-offset`, at least 20px below the status bar).

### v1.0.16

- The history reassessment and Fresh start dialogs put **Cancel** first and the confirming button on the right, the same order as every other dialog in NewsReview, TaskList, TaskList Stats, and SpendTracker.
- On touch screens, text boxes and dropdowns use 16px text so phones do not zoom in when one is focused.

### v1.0.15

- The server compresses its responses (Brotli or gzip), so pages, data, scripts, and styles cross the network several times smaller.
- The app icons (icon-512.png, icon-192.png, apple-touch-icon.png) are redrawn crisply from icon.svg as 8-bit PNGs: about 1.9 KB together instead of 216 KB.
- Removed an unused style and a repeated time-zone parameter in history searches.

### v1.0.14

- The Fresh start dialog's choices use Win95 check boxes like SpendTracker's: a sunken white square holding a check mark.

### v1.0.13

- The status bar sits 5px in from the window's sides and bottom, like TaskList's.

### v1.0.12

- The status bar is the same size as TaskList's: body-size text in the normal text color, in panels with a 1px bevel. **Dismiss** no longer makes it taller.

### v1.0.11

- **Help** opens a short **About NewsReview**, about as long as TaskList Stats' About: what NewsReview does and a few tips. The longer explanations it used to hold are in this manual.

### v1.0.10

- Write backup manifest keys in camelCase (`formatVersion`, `applicationVersion`, `schemaVersion`, `exportedAt`), matching TaskList and SpendTracker. Existing ZIPs and recovery copies with the earlier snake_case keys still restore; a manifest that spells a field both ways is rejected.

### v1.0.9

- Move every color into `wwwroot/theme.css` as light/dark tokens, using the token names shared with TaskList, TaskList Stats, and SpendTracker. `style.css` and `auth.css` no longer contain color values; the teal login background is now `--desktop`, and fixed meaning colors (status light, relevance swatches, shadows, dialog backdrop) are named tokens.
- Fix the dark-theme login and setup fields, which stayed white; they now use the dark field colors like the rest of the app. Every other screen renders exactly as before in both themes.

### v1.0.8

- Move the C# sources into `src/`, grouped into `Hosting`, `Storage`, `Collection`, `Review`, and `Common` folders. The project file, `wwwroot/`, tests, and every command stay where they were; behavior is unchanged.

### v1.0.7

- Say why an article had no readable text: review errors now include the download failure (for example a Cloudflare browser check, HTTP status, or timeout) or state that the page had no readable text, instead of a generic "No readable article or source excerpt".

### v1.0.6

- Add a **Check** button to each source that collects only that source and reviews its new stories (`source_id` on `POST /api/jobs/start`). One-source checks do not restart the scheduled collection interval.
- Replace the per-topic rows, Rename/Delete buttons, and dialogs with one Win95 text box, one topic per line. Editing a line in place renames it on saved stories; deleted lines are removed after a confirmation listing them. `PUT /api/topics` replaces the former add/rename/delete `POST`.

### v1.0.5

- Explain Cloudflare browser-check refusals ("the site requires a Cloudflare browser check that NewsReview cannot complete") instead of a bare HTTP 403, for both the HTTP/2 `cf-mitigated` header and the HTTP/1.1 challenge page.
- Raise the feed/page download limit from 3 MB to 10 MB so full-content feeds such as This Week in GNOME (about 3.8 MB) are read instead of falling back to the website; the error now states the limit.
- Show a stale or undated feed once in source health: the age warning is omitted when the collection note already reports it.

### v1.0.4

- Suggest the current Settings > Topics list for a source's Default category instead of a fixed News/Technology/Security/Standards/Hardware/Science list, so a chosen category matches a topic and labels new stories. The list refreshes after adding, renaming, or deleting a topic; custom values can still be typed.
- Start new sources with the first topic (or News when there are none) instead of always News.

### v1.0.3

- Fix keyword, topic, and all-time searches that timed out and kept the disk busy on large archives. They read every full story row, including stored article bodies and review snapshots; a covering `stories_search` index now answers them without touching those rows. In a 3,000-story test, a cold search read about 3 MB instead of about 78 MB per query, from one contiguous index instead of scattered pages.
- Interrupt the feed query when the browser abandons the request, so a slow search no longer keeps running after the page has given up.
- The index is built once on the first start after upgrading, which reads the stories table one time.

### v1.0.2

- Draw the titlebar minimize, maximize, and close symbols with CSS in equal-size boxes so they line up; the old `_` sat on the text baseline below the other two.
- End Help with about.lehighradio.com and the application version, matching TaskList and TaskList Stats.

### v1.0.1

- Fix sign-in failing with a DPAPI error on Windows when NewsReview runs from Task Scheduler without a loaded user profile. Cookie keys in `data/keys/` are now protected with machine-scope DPAPI. Nothing to migrate; sign in again after updating.

### v1.0.0

- Rewrite the backend in ASP.NET Core on .NET 10, matching TaskList and TaskList Stats. The frontend, API routes and responses, database schema, `auth.json`, `model-credentials.json`, and ZIP format are unchanged, so existing installations and backups keep working. Python is no longer required.
- Start with `dotnet run`; choose the listening address with `--urls` (default `http://127.0.0.1:8714`). `--data-dir` and `--no-scheduler` are unchanged; the backup command is `NewsReview backup`.
- Use 30-day sliding sign-in cookies whose keys live in `data/keys/`, so restarts no longer sign you out. Mark cookies Secure automatically over HTTPS and accept existing password hashes, re-hashing older ones once at 600,000 iterations.
- Check that news sources resolve to public addresses on the connection actually made, closing a DNS-rebinding gap. Keep canonical story URLs byte-identical to the Python release so archived stories still deduplicate.
- Remove `NEWSREVIEW_PASSWORD`, `NEWSREVIEW_SECURE_COOKIE`, the first-name `tech-news.db` import, the unused API aliases, and the `tests.check_sources` script (use Preview stories instead).
- Port the test suite to xUnit and the browser checks to Playwright for .NET, and verify the same API responses as the Python release on a large existing dataset.

### v0.13.0

- Ship without bundled sources or personal interest presets. Fresh installs start with no sources, no interest or article topics, neutral summary guidance, and the existing deal/sponsored/buying-guide exclusions.
- Remove first-run source seeding and the one-time source additions. Existing installations keep their sources, interests, topics, and history unchanged.
- Keep restoring ZIP backups that contain the older source markers; new backups no longer write them.
- Limit `tests.check_sources` to live website-listing previews.

### v0.12.1

- Place Sort immediately after Filter, followed by Date/time range, on Daily Feed and Bookmarks. Keep the same control order when the toolbar wraps on mobile.

### v0.12.0

- Use indexed UTC publication bounds for day/date-range browsing, preserving original dates, microsecond precision, browser timezones, daylight-saving behavior, and undated-story fallback.
- Materialize only the selected page IDs before loading display fields. Keep full article bodies and review snapshots out of feed queries, and use compact covering indexes for publication/bookmark filtering.
- Add a custom Win95 Sort dropdown for publication newest/oldest and relevance rank highest/lowest across Daily Feed and Bookmarks. Remember the browser preference, sort before paging, and keep deterministic tie ordering.
- Upgrade existing databases once to schema 4 without resetting history or preferences. Retain schema-2/3 ZIP restore support and validate derived timestamp integrity.
- Verify real query plans and bounded SQLite work against a larger archive, migration/backup roundtrips, offset/microsecond/DST boundaries, sorting/filtering/pagination, API validation, and desktop/mobile dropdown behavior.

### v0.11.1

- Skip individual articles without readable text without calling the model or tripping the consecutive model-failure cutoff. Preserve per-article errors, bounded attempts, and explicit retry controls.
- Discard unknown model topic suggestions while keeping configured labels and valid summaries. Allow untagged reviews without growing the owned topic list or repeating paid requests; retain strict validation for other fields.
- Catch database-open failures during SQLite setup as well as initial connection, always close connections, and show actionable startup diagnostics. Document safe data-folder testing outside pooled drives without changing storage or journal mode automatically.
- Verify queue continuation, failure-cutoff behavior, all model transports, topic-list preservation, retry limits, and real SQLite WAL-open failures.

### v0.11.0

- Add masked API-key entry, Save/Replace, configured status, and confirmed Remove controls to AI settings. Apply changes to the next request without restarting, including during active review batches.
- Save keys per provider/server in a separate credentials file with atomic writes, restrictive POSIX permissions, and no hard-link dependency. Keep keys out of SQLite, API responses, application backups, source control, and logs.
- Preserve the legacy environment variable as a visible fallback behind saved keys. Protect authenticated model requests against cross-origin redirects and redact Authorization values from model errors.
- Cover persistence/failure behavior, concurrent updates, authenticated APIs, all generation paths, active-batch replacement, backup/reset isolation, and desktop/mobile workflows.

### v0.10.1

- Rename the Local AI tab to AI and update current UI/help/setup wording for local or cloud review.
- Add an explicit OpenRouter server type that disables thinking for article reviews, model tests, and feedback-profile generation. Omit unsupported temperature controls while preserving other server request formats.
- Document server-side API-key setup, data flow, and model selection; verify provider settings, request contracts, and the browser workflow using local fixtures.

### v0.10.0

- Add a separate Topics settings tab with add, rename, and confirmed delete controls. Preserve every existing topic on upgrade and keep the list independent of interests and source categories.
- Update saved article labels atomically on rename/delete while preserving articles and reading data. Show new topics in feed filters immediately and retain active selection through renames.
- Use only managed topic names for AI and initial source labels; support an intentionally empty list without reintroducing source categories.
- Include topics in ZIP backup validation/restore while importing all labels from older backups without the new list. Verify preservation, model vocabulary, API guards, and desktop/mobile editing.

### v0.9.8

- Remove the one-time Clear generated topics button, confirmation dialog, API endpoint, persistence operation, and obsolete cleanup tests.
- Keep AI topic choices restricted to configured source categories, including canonicalization and validation for both model transports. Update current documentation to match.

### v0.9.7

- Add Clear generated topics in Collection & history with Cancel/confirm controls. Reset stored tags to each story's source category while preserving stories and all other reading/preferences data.
- Constrain AI topic choices to configured source categories in the structured schema and validate membership for both model transports, preventing uncontrolled topic growth.
- Cover cleanup preservation/idempotency, archived/missing sources, authenticated confirmation and job locking, model vocabulary enforcement, and the desktop/mobile workflow.

### v0.9.6

- Remove the fixed 70-character cutoff from review progress titles so the status bar uses its existing width-based ellipsis and full-message tooltip.

### v0.9.5

- Preserve bounded JSON explanations from rejected model requests in sample tests, run history, and server logs; retain status-only errors for unreadable or HTML bodies.
- Direct stopped batches to their run errors and Local AI settings instead of assuming a connection failure. Preserve queued stories, the three-failure stop, and Ollama unloading.
- Test both model API error formats, malformed/oversized bodies, message limits, and failure/queue persistence with real local HTTP fixtures.

### v0.9.4

- Add spacing above the learned-preferences note using the same settings-note spacing as Local AI.

### v0.9.3

- Track article openings and show a small Read label beneath the publisher on Daily Feed and Bookmarks. Save first-open timestamps in SQLite, retain them through reviews/bookmark changes, and include them in ZIP backups.
- Migrate to schema 3 while accepting schema-2 backup imports with empty read markers.

### v0.9.2

- Place the relevance square after the bookmark icon on story tiles.

### v0.9.1

- Replace feedback emoji with the supplied thumb-up/thumb-down PNG icons, retaining accessible labels and contrast in dark and selected states.

### v0.9.0

- Add thumbs-up/down ratings with optional reasons, cached learned-preference generation, manual editing/disable/reset controls, and selective Fresh start reset. Include feedback in SQLite/ZIP backups while retaining old archive compatibility.
- Bound profile generation to the 10 latest ratings once per changed set before review batches; send only the cached profile to normal reviews and give explicit interests/guidance priority.
- Remove Why it matters completely from current tiles, APIs, and model output for all relevance levels; keep its legacy storage column solely for compatibility.

### v0.8.1

- Request concise English significance text with 3–4 short sentences, capped at 90 words/650 characters. Reject unfinished or excessive output instead of saving clipped-looking prose.
- Add Stop review during active AI batches. Finish the current article, preserve queued stories, run model cleanup, and allow later resume while keeping collection scheduling enabled.

### v0.8.0

- Add native Ollama unloading after tests/batches, with warm reuse between articles and a setting to retain server defaults. Keep sample tests from interrupting active jobs.
- Add configurable response token limits, schema string bounds, and specific diagnostics for truncated, empty/thinking-only, malformed, and schema-invalid responses. Preserve strict review validation.

### v0.7.6

- Match TaskList runtime logging at logs/console.log: retain normal console output and keep one file, truncating in place at 10 MiB instead of rotating backup logs. Keep setup tokens console-only and allow console logging to continue if file output fails.

### v0.7.5

- Send top-level `think: false` on every Ollama review request, including the sample-article test, to request direct structured reviews without a separate thinking phase on supported models.
- Preserve the compatible local-server request format and verify both adapter contracts through real local HTTP fixtures.

### v0.7.4

- Avoid repeating Not checked in new-source warnings; keep one status label and the Refresh sources instruction.

### v0.7.3

- Remove the redundant Needs attention source heading and shorten Broken to a plain status label without an em dash. Keep detailed red warnings and failure counts below the source name.

### v0.7.2

- Move Include filtered / duplicates to the top of Filter..., above Relevance and Topics.

### v0.7.1

- Make relevance and Include filtered / duplicates rows use the same full-row checkbox interaction as topics, including clicks in blank row space.

### v0.7.0

- Combine topics, Include filtered / duplicates, and relevance-level checkboxes in one Filter... dropdown on Daily Feed and Bookmarks. Apply relevance filters before result counting and pagination.

### v0.6.4

- Show disabled sources with one red Disabled label, removing the redundant collection-disabled warning.

### v0.6.3

- Add a counted confirmation dialog for purging AI-marked duplicate stories near Fresh start. Preserve bookmarks and referenced stories, block concurrent background work, and compact the database after deletion.

### v0.6.2

- Add a vulnerability-advisory source to the bundled defaults, once for new and existing installations without overwriting source choices.

### v0.6.1

- Add a security news source to the bundled defaults, once on startup for both fresh and existing installations without overwriting existing source settings.

### v0.6.0

- Replace fixed daily collection times with configurable elapsed-hour intervals, persistent across restarts and safe across midnight/daylight-saving changes.
- Keep Daily Feed keyword/time-only searches within the selected publication day; explicit date ranges widen search.

### v0.5.6

- Add four bundled sources with verified feeds through the non-overwriting one-time source update.

### v0.5.5

- Add a web app manifest, 192/512px app icons, and an Apple touch icon for home-screen installation. No offline article caching or service worker is added; the server connection is still required.
- Keep theme and Sign out controls together on the right when mobile navigation wraps. Respect device safe areas and prevent version-label wrapping in the bottom bar.

### v0.5.4

- Filter Daily Feed days and archive date ranges by publication date in the browser timezone, with collection-date fallback for undated stories. Apply the rule to existing history without rewriting records.
- Order history by the same calendar date, then relevance.

### v0.5.3

- Give Review pending and Check sources matching widths and heights in the Daily Feed toolbar.

### v0.5.2

- Add horizontal navigation-bar padding so Sign out and its hover highlight stay inset from the window edge on desktop and mobile.

### v0.5.1

- Fix first-run password creation on Windows drives that reject hard links (WinError 50). Use exclusive credential-file creation, retaining owner-only POSIX permissions and overwrite protection.
- Flush credentials before completing setup; clean up failed writes and fail closed on files left incomplete by abrupt shutdown.

### v0.5.0

- Replace environment-password requirements with TaskList-style console-token setup and a matching standalone login screen.
- Persist salted password hashes outside the reading database; protect loopback and LAN APIs, rate-limit setup/login, and preserve credentials through imports and resets.
- Keep legacy login/session/logout API routes and support one-time migration from NEWSREVIEW_PASSWORD.

### v0.4.15

- Consolidate reset/restore dataset reloads in the app, invalidate pending feed reads, and remove duplicate source/settings requests.
- Use HTML form defaults and controls for selective reset instead of repeating checkbox/default lists in JavaScript.
- Share the authoritative schema-version constant between persistence and ZIP validation, retaining historical migration thresholds.
- Consolidate footer CSS, remove unused source-state/mobile rules, and scope form margins directly instead of overriding them with `!important`.
- Preserve migration/API compatibility, separate backup paths, existing titlebar styling, polling behavior, and source configuration. Add no dependencies, monkey patches, or replacement controls.

### v0.4.14

- Match TaskList's title-bar height, spacing, typography, centered caption, and decorative window symbols on desktop and mobile.
- Show NewsReview's server version beside its application name, remove the extra title-bar icon, and truncate long title labels without wrapping or overlap.

### v0.4.13

- Apply edited keyword searches when the field loses focus, matching date/time range behavior; clearing the field also applies on leaving it.
- Keep Enter and the Search button available; verify applying and clearing searches by clicking outside in browser QA.

### v0.4.12

- Stop idle overview polling; refresh when returning focus/visibility or changing the main view.
- Poll known active jobs every 2.5 seconds and retry failed connections every five seconds while visible; pause checks for hidden tabs and ZIP transfers.
- Stop checks again after job completion/recovery, combine focus events, prevent overlapping refreshes, and preserve unsaved settings on return.
- Keep server-side scheduled collection independent of the browser and document when idle tabs catch up.
- Add dedicated browser clock coverage for idle silence, external-job discovery on return, progress/completion, and connection recovery.

### v0.4.11

- Match keyword search against source names as well as headline/summary/excerpt/tag text, including historical source records.
- Check website listings in Automatic mode when feeds are stale or empty, merge usable results, and retain feed entries with an explicit warning if the website cannot be read.
- Explain staleness thresholds, strict feed-only behavior, combined preview methods, and JavaScript-only limitations in the UI and README.
- Add a bundled source with its verified RSS feed, preserving existing source settings through a one-time upgrade.
- Expand regression coverage for stale/fresh/undated/empty feeds, merged URL deduplication, fallback failures, source search, and source persistence.

### v0.4.10

- Move browser job starts to `/api/jobs/start` to avoid the `/api/collect` name being mistaken for tracking traffic; preserve the old API alias.
- Keep failed-action messages visible across successful background reads, wrap long errors in the footer, and add Dismiss.
- Explain browser/extension blocking without claiming an unconfirmed write failed on the server; keep checking status without replaying it.
- Test selective request blocking, persistent notices, dismissal, legacy-route blocking, and new-route authentication/origin checks.

### v0.4.9

- Add Check new sources with an eligible count, selecting enabled sources with no previous collection attempt.
- Scope AI review to selected sources, preserve the unrelated queue, and keep new-source runs independent of the daily full-collection schedule.
- Record selection scope and attempted source count in run history; disable the action when busy or no sources qualify.
- Audit README behavior, button labels, ordering, source upgrades, interface help, backup/restore, search, and operational guidance.

### v0.4.8

- Add seven bundled sources once to existing installations without overwriting matching source configuration; preserve later removal/disable decisions.
- Verify their feeds and HTML news-listing collection; update source documentation and backup marker validation.
- Leave existing interests and source configuration unchanged.

### v0.4.7

- Add a square AI relevance indicator immediately left of each bookmark, matching its size on both feeds.
- Show green for 75–100, yellow for 40–74, red for 0–39, and gray while awaiting review, with a key below the story count.
- Provide score tooltips and accessible text; reuse existing AI scores without new tracking, model calls, or changes to filtering.
- Verify colors, pending state, indicator/bookmark dimensions, and legend placement in browser QA.

### v0.4.6

- Match time searches to tile publication times in the browser timezone, falling back to collection time for undated stories; date ranges still use edition dates.
- Show collection time on undated tiles so their time-search behavior is visible.
- Cover noon boundaries, historical daylight-saving offsets, differing collection/publication times, undated fallback, invalid zones, and browser time-only search.

### v0.4.5

- Make the full topic row, including its text and unused space, toggle its checkbox once; keep direct checkbox activation working.
- Add row hover/pointer feedback and browser checks for text, row-space, and checkbox selection.

### v0.4.4

- Fix nested topic-menu scrollbars by keeping the outer popup unconstrained and scrolling only the checkbox list.
- Increase the Personal news reader caption and story count from 12px to 14px.
- Exercise a populated topic list in browser QA and verify it has one scrolling region with All topics still visible.

### v0.4.3

- Match the Daily Feed topic control to the shared Win95 dropdown styling, retaining multiple checkboxes and adding keyboard navigation/dismissal.
- Place story counts below the edition heading and capitalize Daily Feed and Today's Edition consistently.
- Keep story content selectable on daily/bookmark feeds while preventing page chrome, settings labels, and bookmark controls from being highlighted; form text stays editable and copyable.
- Add the decorative titlebar minimize/maximize/close symbols used by TaskList and TaskList Stats.
- Verify count positioning, menu keyboard controls, text-selection scope, and titlebar symbols in browser QA.

### v0.4.2

- Consolidate collection/review messages and the activity light in the bottom-left status bar; keep Review pending beside Check sources.
- Preserve bookmark icons during requests and show saving/removing progress in the status bar.
- Use shared Win95 dropdowns for source methods, local-model providers, interest ranks, and editable source categories, with centered arrow buttons and keyboard navigation.
- Retain checkboxes for the multi-topic filter; match disabled Restore backup text to Fresh start and space AI subnotes below its buttons.
- Verify custom dropdown selection, keyboard navigation, arbitrary categories, bookmark progress, and the existing backup/recovery workflows in Chromium.

### v0.4.1

- Move ZIP import/export into Settings, with body actions and an OK footer matching the companion apps.
- Use the bottom-left status bar for notifications and persistent connection errors; keep the latest collection state as the idle status.
- Compact feed-topic controls and the source category field, place Cancel before Save, and use Save throughout settings.
- Remove the redundant Read story link from tiles; headlines continue opening the original stories.
- Verify the revised settings, backup dialog, status recovery, and compact topic menu in real Chromium QA.

### v0.4.0

- Add Help after Settings, explaining AI filtering, summaries, listing/feed collection, and forum scope.
- Add validated ZIP export/restore with automatic recovery backups and stale-tab write protection.
- Add selective fresh starts with bookmarked-story preservation by default.
- Fill the viewport, pin application/dialog chrome, and scroll content internally in both themes.
- Combine date/time search, restore the blinking collection indicator, and refine labels/dropdown spacing.
- Expand backup/reset/search/browser tests and restore detailed release commit bodies.


### v0.3.0

- Remove two bundled sources from fresh and existing active source lists, preserving archived stories.
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
- Document RSS discovery versus article webpage fetching.

### v0.1.0

- Initial daily feed, SQLite history, bookmarks, keyword/date/tag search and calendar navigation.
- Seed a bundled source list; support UI-based RSS/Atom discovery and source editing.
- Add editable interests, exclusions, collection schedule and bounded processing controls.
- Support local Ollama and compatible model servers with validated summaries, significance, tags and conservative duplicate grouping.
- Preserve readable source excerpts while AI is unavailable; expose retry/reassessment and run history.
- Add password protection for LAN serving, light/dark themes, online backups, deployment guidance and automated coverage.
