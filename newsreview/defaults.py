"""First-run defaults. Existing installations are never overwritten on startup."""

SOURCES = [
    ("BetaNews", "https://betanews.com/", "https://betanews.com/feed/", "Technology"),
    ("Ars Technica", "https://arstechnica.com/", "https://feeds.arstechnica.com/arstechnica/index", "Technology"),
    ("BleepingComputer", "https://www.bleepingcomputer.com/", "https://www.bleepingcomputer.com/feed/", "Security"),
    ("TechCrunch", "https://techcrunch.com/", "https://techcrunch.com/feed/", "Technology"),
    ("MacRumors", "https://www.macrumors.com/", "https://feeds.macrumors.com/MacRumors-All", "Hardware"),
    ("Android Authority", "https://www.androidauthority.com/", "https://www.androidauthority.com/feed/", "Hardware"),
    ("Engadget", "https://www.engadget.com/", "https://www.engadget.com/rss.xml", "Technology"),
    ("MIT Technology Review", "https://www.technologyreview.com/", "https://www.technologyreview.com/feed/", "Research"),
    ("PCWorld", "https://www.pcworld.com/", "https://www.pcworld.com/feed", "Hardware"),
    ("The Register", "https://www.theregister.com/", "https://www.theregister.com/headlines.atom", "Infrastructure"),
    ("Tom's Hardware", "https://www.tomshardware.com/news", "https://www.tomshardware.com/feeds/all", "Hardware"),
    ("CNET", "https://www.cnet.com/news/", "https://www.cnet.com/rss/news/", "Technology"),
    ("IETF Blog", "https://www.ietf.org/blog/", "", "Standards"),
    ("W3C News", "https://www.w3.org/news/", "https://www.w3.org/news/feed/", "Standards"),
    ("web.dev", "https://web.dev/blog", "https://web.dev/feed.xml", "Web Platform"),
    ("Mozilla Hacks", "https://hacks.mozilla.org/", "https://hacks.mozilla.org/feed/", "Web Platform"),
    ("Daniel Stenberg", "https://daniel.haxx.se/blog/", "https://daniel.haxx.se/blog/feed/", "Protocols"),
    ("Cloudflare Blog", "https://blog.cloudflare.com/", "https://blog.cloudflare.com/rss/", "Infrastructure"),
    ("LWN", "https://lwn.net/", "https://lwn.net/headlines/rss", "Open Source"),
    ("Phoronix", "https://www.phoronix.com/", "https://www.phoronix.com/rss.php", "Open Source"),
    ("RTL-SDR Blog", "https://www.rtl-sdr.com/", "https://www.rtl-sdr.com/feed/", "Radio & SDR"),
    ("RadioReference Forums", "https://forums.radioreference.com/", "https://forums.radioreference.com/forums/-/index.rss", "Radio & SDR"),
    ("GNU Radio News", "https://www.gnuradio.org/news/", "https://www.gnuradio.org/news/index.xml", "Radio & SDR"),
    ("FreePBX Blog", "https://www.freepbx.org/blog/", "https://www.freepbx.org/feed/", "PBX & VoIP"),
    ("FreePBX Announcements", "https://community.freepbx.org/c/blog/50", "https://community.freepbx.org/c/blog/50.rss", "PBX & VoIP"),
    ("Asterisk Blog", "https://www.asterisk.org/blog/", "https://www.asterisk.org/feed/", "PBX & VoIP"),
    ("Hackaday · Ham Radio", "https://hackaday.com/tag/ham-radio/", "https://hackaday.com/tag/ham-radio/feed/", "Radio & SDR"),
]

INTERESTS = {
    "guidance": "Favor substantive technical developments, interesting engineering, useful projects, and niche news. Explain what changed and why it matters. Routine phone refreshes, rumors, funding announcements and marketing are low priority. Distinguish proposals, approved standards, experimental support and shipped features.",
    "topics": [
        {"name": name, "preference": "favor"} for name in [
            "Cybersecurity and privacy", "Protocols, HTTP and DNS", "Image/video formats and compression",
            "Web standards and browser APIs", "Open source and infrastructure", "New hardware capabilities",
            "SDR, scanners, P25 and DMR", "Ham radio, homebrew RF and antennas", "FreePBX, Asterisk, SIP and VoIP",
        ]
    ],
    "exclusions": ["Deals and coupons", "Sponsored content", "Buying guides"],
}

SETTINGS = {
    "ai": {"provider": "ollama", "base_url": "http://127.0.0.1:11434", "model": "",
           "timeout_seconds": 180, "max_input_chars": 12000},
    "collection": {"enabled": True, "hour": 7, "minute": 0, "items_per_source": 8,
                   "max_reviews": 40, "lookback_days": 3},
    "interests": INTERESTS,
}
