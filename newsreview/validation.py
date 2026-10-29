"""Validate UI inputs before they reach persistence or network adapters."""

from copy import deepcopy

from .network import valid_url
from . import feedback


def text(value, name, limit, *, empty=False):
    if not isinstance(value, str) or len(value) > limit or (not empty and not value.strip()):
        raise ValueError(f"{name} must be {'0' if empty else '1'}–{limit} characters")
    return value.strip()


def integer(value, name, low, high):
    if type(value) is not int or not low <= value <= high:
        raise ValueError(f"{name} must be an integer from {low} to {high}")
    return value


def boolean(value, name):
    if type(value) is not bool:
        raise ValueError(f"{name} must be true or false")
    return value


def topic_names(value):
    if not isinstance(value, list) or any(not isinstance(name, str) or not name.strip() or len(name) > 60 for name in value):
        raise ValueError('Topics must be a list of names, each 1–60 characters')
    if len(set(value)) != len(value):
        raise ValueError('Topic names must be unique')
    return value


def source(value):
    if not isinstance(value, dict):
        raise ValueError("Expected a source object")
    feed = text(value.get("feed_url", ""), "Feed URL", 2048, empty=True)
    mode = value.get('mode', 'auto')
    if mode not in ('auto', 'feed', 'page'):
        raise ValueError('Choose Automatic, RSS/Atom, or Website page')
    return {"name": text(value.get("name"), "Source name", 100),
            "url": valid_url(value.get("url")), "feed_url": valid_url(feed) if feed else "",
            "category": text(value.get("category"), "Category", 60),
            "enabled": boolean(value.get("enabled", True), "Enabled"), 'mode': mode,
            'link_pattern': text(value.get('link_pattern', ''), 'Article URL filter', 200, empty=True),
            'stale_days': integer(value.get('stale_days', 14), 'Inactivity warning days', 1, 365)}


def settings(value, current):
    if not isinstance(value, dict) or set(value) - {"ai", "collection", "interests"}:
        raise ValueError("Unknown settings section")
    result = deepcopy(current)
    if "ai" in value:
        ai = value["ai"]
        if not isinstance(ai, dict) or ai.get("provider") not in ("ollama", "compatible", "openrouter"):
            raise ValueError("Choose Ollama, a compatible server, or OpenRouter")
        result["ai"] = {
            "provider": ai["provider"], "base_url": valid_url(ai.get("base_url")).rstrip("/"),
            "model": text(ai.get("model"), "Model", 200, empty=True),
            "timeout_seconds": integer(ai.get("timeout_seconds"), "Model timeout", 10, 600),
            "max_input_chars": integer(ai.get("max_input_chars"), "Article input length", 2000, 40000),
            "max_output_tokens": integer(ai.get("max_output_tokens", 2400), "Response tokens", 256, 8192),
            "unload_after_use": boolean(ai.get("unload_after_use", True), "Unload after use"),
        }
    if "collection" in value:
        c = value["collection"]
        if not isinstance(c, dict):
            raise ValueError("Invalid collection settings")
        result["collection"] = {
            "enabled": boolean(c.get("enabled"), "Schedule enabled"),
            "interval_hours": integer(c.get("interval_hours", 24), "Collection interval (hours)", 1, 8760),
            "hour": integer(c.get("hour", 7), "Hour", 0, 23), "minute": integer(c.get("minute", 0), "Minute", 0, 59),
            "items_per_source": integer(c.get("items_per_source"), "Articles per source", 1, 50),
            "max_reviews": integer(c.get("max_reviews"), "Reviews per run", 1, 500),
            "lookback_days": integer(c.get("lookback_days"), "Lookback days", 1, 30),
        }
    if "interests" in value:
        i = value["interests"]
        if not isinstance(i, dict) or not isinstance(i.get("topics"), list) or len(i["topics"]) > 100:
            raise ValueError("Use at most 100 interests")
        topics = []
        for topic in i["topics"]:
            if not isinstance(topic, dict) or topic.get("preference") not in ("favor", "normal", "less", "exclude"):
                raise ValueError("Invalid interest preference")
            topics.append({"name": text(topic.get("name"), "Interest", 120), "preference": topic["preference"]})
        exclusions = i.get("exclusions")
        if not isinstance(exclusions, list) or len(exclusions) > 100:
            raise ValueError("Use at most 100 exclusions")
        result["interests"] = {"guidance": text(i.get("guidance"), "Editorial guidance", 6000, empty=True),
                               "topics": topics, "exclusions": [text(e, "Exclusion", 200) for e in exclusions]}
        if 'feedback' in i:
            result['interests']['feedback'] = feedback.validate(i['feedback'])
    return {k: result[k] for k in value}
