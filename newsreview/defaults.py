"""First-run settings. No sources are bundled; users add their own in Settings."""

INTERESTS = {
    "guidance": "Summarize what changed factually and concisely. Favor substantive reporting over rumors, marketing, and promotional content.",
    "topics": [],
    "exclusions": ["Deals and coupons", "Sponsored content", "Buying guides"],
}

SETTINGS = {
    "ai": {"provider": "ollama", "base_url": "http://127.0.0.1:11434", "model": "",
           "timeout_seconds": 180, "max_input_chars": 12000, "max_output_tokens": 2400, "unload_after_use": True},
    "collection": {"enabled": True, "interval_hours": 24, "hour": 7, "minute": 0, "items_per_source": 8,
                   "max_reviews": 40, "lookback_days": 3},
    "interests": INTERESTS,
}
