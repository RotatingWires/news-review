"""One bounded background queue shared by scheduled and manual collection."""

from datetime import datetime, timedelta, timezone
import json
import logging
import re
import threading

from .feedback import for_model
from .feeds import extract_article
from .sources import read_source
from .store import StorageUnavailable
from .network import Fetcher
from .reviewer import Reviewer, candidates_for

logger = logging.getLogger(__name__)
NOISE = re.compile(r"\b(sponsored|coupon|promo code|best deals|daily deals)\b", re.I)


class Collector:
    def __init__(self, store, fetcher=None, reviewer=None):
        self.store = store
        self.fetcher = fetcher or Fetcher()
        self.reviewer = reviewer or Reviewer()
        self.lock = threading.Lock()
        self.stop_event = threading.Event()
        self.review_stop_event = threading.Event()
        self.state_lock = threading.Lock()
        self.state = {"running": False, "message": "Ready", "kind": None, "reviewing": False, "review_stop_requested": False}
        self.worker = None

    def status(self):
        with self.state_lock:
            return dict(self.state)

    def update(self, **values):
        with self.state_lock:
            self.state.update(values)

    def start(self, kind="collect", *, scheduled=False, unchecked_only=False):
        if unchecked_only and (kind != "collect" or scheduled):
            raise ValueError("New-source checks must be a manual collection")
        # Validate storage synchronously so /api/collect cannot acknowledge a job
        # which can only fail because its data folder was removed.
        self.store.settings()
        if not self.lock.acquire(blocking=False):
            return False
        if scheduled and not self.schedule_due(datetime.now().astimezone()):
            self.lock.release()
            return False
        self.review_stop_event.clear()
        self.update(running=True, kind=kind, reviewing=False, review_stop_requested=False, message="Starting…")
        self.worker = threading.Thread(target=self._run, args=(kind, unchecked_only), daemon=True, name="news-collector")
        self.worker.start()
        return True

    def stop_review(self):
        with self.state_lock:
            if not self.state['running'] or not self.state['reviewing']:
                return False
            self.review_stop_event.set()
            self.state.update(review_stop_requested=True, message='Stopping AI review after the current article; remaining stories stay queued.')
            return True

    def _run(self, kind, unchecked_only=False):
        run_id = None
        model_used = False
        ai = None
        details = {"added": 0, "reviewed": 0, "duplicates": 0, "filtered": 0, "errors": [], "scope": "unchecked" if unchecked_only else "all", "sources_checked": 0}
        try:
            run_id = self.store.start_run(kind, details)
            settings = self.store.settings()
            config = settings["collection"]
            now = datetime.now().astimezone()
            cutoff = now.astimezone(timezone.utc) - timedelta(days=config["lookback_days"])
            if kind == "collect":
                sources = [s for s in self.store.sources() if s["enabled"] and (not unchecked_only or s["last_checked"] is None)]
                source_ids = [source["id"] for source in sources]
                for number, source in enumerate(sources, 1):
                    if self.stop_event.is_set():
                        break
                    details["sources_checked"] += 1
                    self.update(message=f"Checking {source['name']} ({number}/{len(sources)})")
                    try:
                        result = read_source(self.fetcher, source)
                        entries = result['entries']
                        # Sort newest first, while retaining source order for undated items.
                        entries.sort(key=lambda i: i["published_at"] or "", reverse=True)
                        eligible = [i for i in entries if not i["published_at"] or datetime.fromisoformat(i["published_at"]) >= cutoff]
                        added = 0
                        for item in eligible[:config["items_per_source"]]:
                            item["category"] = source["category"]
                            added += self.store.insert_story(source["id"], item, now.date().isoformat())
                        details['added'] += added
                        newest = max((i['published_at'] for i in entries if i['published_at']), default=None)
                        self.store.source_checked(source['id'], feed_url=result['feed_url'], count=len(entries), newest=newest,
                                                  added=added, method=result['method'], note=result['note'])
                    except Exception as exc:
                        logger.warning("Source %s failed: %s", source["name"], exc)
                        self.store.source_checked(source["id"], str(exc)[:500])
                        details["errors"].append(f"{source['name']}: {str(exc)[:300]}")
                    if self.stop_event.wait(0.25):
                        break
            ai = settings["ai"]
            if not ai["model"].strip():
                details["note"] = "No model configured. Stories are saved with source excerpts; configure AI and run Review pending."
            else:
                recent = self.store.recent()
                pending = self.store.pending(config["max_reviews"], source_ids=source_ids if unchecked_only else None)
                consecutive_failures = 0
                self.update(reviewing=bool(pending))
                feedback = self.store.feedback_state()
                if pending and feedback['enabled'] and feedback['auto_update'] and feedback['ratings'] and feedback['profile_revision'] != feedback['revision']:
                    self.update(message='Updating learned preferences from recent feedback')
                    model_used = True
                    try:
                        profile = self.reviewer.learn_preferences(ai, feedback, keep_loaded=True)
                        self.store.save_feedback_profile(profile, revision=feedback['revision'])
                    except Exception as exc:
                        logger.warning('Feedback profile update failed: %s', exc)
                        details['errors'].append('Feedback profile update failed; existing preferences kept: ' + str(exc)[:200])
                editorial_interests = for_model(self.store.settings()['interests'])
                topics = self.store.topics()
                for number, story in enumerate(pending, 1):
                    if self.stop_event.is_set() or self.review_stop_event.is_set():
                        break
                    self.update(message=f"Reviewing {number}/{len(pending)} · {story['headline']}")
                    text, basis = story["article_text"], story["content_basis"]
                    if not text:
                        try:
                            html, _, _ = self.fetcher.request(story["url"])
                            text = extract_article(html)
                            basis = "article" if text else "excerpt"
                        except ValueError:
                            basis = "excerpt"
                        text = text or story["excerpt"]
                    try:
                        if not text.strip():
                            raise ValueError("No readable article or source excerpt; showing headline and link only")
                        if NOISE.search(story["headline"]):
                            result = {"summary": "Excluded by the headline filter.", "tags": [topic for topic in topics if topic.casefold() == (story['category'] or '').casefold()][:1], "status": "filtered", "score": 0, "milestone": "not applicable"}
                        else:
                            candidates = candidates_for(story["headline"], recent)
                            model_used = True
                            result = self.reviewer.review(ai, editorial_interests, {
                                "headline": story["headline"], "text": text[:ai["max_input_chars"]],
                                "content_basis": basis, "published_at": story["published_at"], "category": story['category'],
                            }, candidates, topics=topics, keep_loaded=True)
                        self.store.review_result(story["id"], result, ai, editorial_interests, text[:100000], basis)
                        details[{"reviewed": "reviewed", "duplicate": "duplicates", "filtered": "filtered"}[result["status"]]] += 1
                        if result["status"] == "reviewed":
                            recent.insert(0, {"id": story["id"], "headline": story["headline"], "summary": result["summary"]})
                            recent = recent[:150]
                        consecutive_failures = 0
                    except Exception as exc:
                        self.store.review_failed(story["id"], str(exc)[:500], text[:100000], basis)
                        details["errors"].append(f"Review #{story['id']}: {str(exc)[:300]}")
                        consecutive_failures += 1
                        if consecutive_failures >= 3:
                            details["note"] = "Stopped review after three consecutive failures. Check the run errors and AI settings; remaining stories are queued."
                            break
            self.update(reviewing=False)
            if self.review_stop_event.is_set():
                details['review_stopped'] = True
                details['note'] = 'AI review stopped by the user. The current article finished; remaining stories are still queued.'
            status = "interrupted" if self.stop_event.is_set() else ("partial" if details["errors"] or details.get('review_stopped') else "complete")
            self.store.finish_run(run_id, status, details)
            self.update(message=(f"Checked {details['sources_checked']} new sources · " if unchecked_only else "") + f"Saved {details['added']} new stories · {details['reviewed']} reviewed" + (" · See run details for errors" if details["errors"] else "") + (" · AI review stopped; remaining stories are queued" if details.get("review_stopped") else ""))
        except Exception as exc:
            logger.exception("Collection failed")
            if run_id:
                details["errors"].append(str(exc)[:500])
                try:
                    self.store.finish_run(run_id, "failed", details)
                except StorageUnavailable:
                    pass
            self.update(message=str(exc) if isinstance(exc, StorageUnavailable) else "Collection failed. See run history and server log.")
        finally:
            if model_used:
                try:
                    self.reviewer.unload(ai)
                except Exception as exc:
                    logger.warning('Ollama model unload failed: %s', exc)
                    self.update(message=self.status()['message'] + ' · Model unload failed; see logs/console.log')
            self.update(running=False, reviewing=False, review_stop_requested=False)
            self.lock.release()

    def schedule_due(self, now):
        config = self.store.settings()["collection"]
        if not config["enabled"]:
            return False
        with self.store.connect() as db:
            rows = db.execute("SELECT started_at FROM runs WHERE kind='collect' AND status!='interrupted' AND COALESCE(json_extract(details,'$.scope'),'all')!='unchecked' ORDER BY id DESC LIMIT 1").fetchall()
            reset = db.execute("SELECT value FROM settings WHERE key='history_cleared_at'").fetchone()
        anchors = [datetime.fromisoformat(rows[0][0])] if rows else []
        if reset:
            anchors.append(datetime.fromisoformat(json.loads(reset[0])))
        if not anchors:
            return True
        elapsed = now.astimezone(timezone.utc) - max(value.astimezone(timezone.utc) for value in anchors)
        return elapsed >= timedelta(hours=config.get("interval_hours", 24))

    def schedule(self):
        # Persistent UTC elapsed time survives restarts and daylight-saving changes.
        # Missed intervals trigger one catch-up run, without replaying a backlog.
        while not self.stop_event.wait(20):
            try:
                now = datetime.now().astimezone()
                if self.schedule_due(now):
                    self.start(scheduled=True)
            except StorageUnavailable as exc:
                self.update(message=str(exc))

    def stop(self):
        self.stop_event.set()
