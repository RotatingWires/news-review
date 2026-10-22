# Approved source additions

These are the 27 current approved additions, excluding the original 12 technology websites. Daniel Stenberg and LWN were removed in v0.3.0.

## Standards, engineering, and open source

- [IETF Blog](https://www.ietf.org/blog/)
- [W3C News](https://www.w3.org/news/)
- [web.dev](https://web.dev/discover/)
- [Mozilla Hacks](https://hacks.mozilla.org/)
- [Cloudflare Blog](https://blog.cloudflare.com/)
- [Phoronix](https://www.phoronix.com/)
- [Hackaday — general engineering](https://hackaday.com/) (RSS: `https://hackaday.com/blog/feed/`, added in v0.4.11)

## Radio, SDR, and ham radio

- [RTL-SDR Blog](https://www.rtl-sdr.com/)
- [RadioReference Forums](https://forums.radioreference.com/)
- [GNU Radio News](https://www.gnuradio.org/news/)
- [Hackaday — Ham Radio](https://hackaday.com/tag/ham-radio/)

## PBX and VoIP

- [FreePBX Blog](https://www.freepbx.org/blog/)
- [FreePBX Community announcements](https://community.freepbx.org/c/blog/50)
- [Asterisk Blog](https://www.asterisk.org/blog/)

## Homelab and self-hosting (v0.4.8)

- [selfh.st](https://selfh.st/)
- [ServeTheHome](https://www.servethehome.com/)
- [VirtualizationHowto](https://www.virtualizationhowto.com/)

## Additional cybersecurity sources (v0.4.8)

- [PortSwigger Research](https://portswigger.net/research)
- [SANS Internet Storm Center](https://isc.sans.edu/)
- [KrebsOnSecurity](https://krebsonsecurity.com/)
- [Cybernews](https://cybernews.com/news/)

These seven sources are added once on upgrade as well as on fresh installs. Matching configured URLs are kept as-is, including disabled/removed sources. Six have verified RSS feeds; Cybernews uses Automatic collection and currently falls back to its news listing (21 candidates during the v0.4.8 audit). MDN Blog and WebKit were not added; web.dev was already part of the earlier approved source list.

## Google, Apple, and security additions (v0.5.6)

- [9to5Google](https://9to5google.com/) — RSS: `https://9to5google.com/feed/`
- [9to5Mac](https://9to5mac.com/) — RSS: `https://9to5mac.com/feed/`
- [HackerNoon Cybersecurity](https://hackernoon.com/c/cybersecurity) — RSS: `https://hackernoon.com/tagged/cybersecurity/feed`
- [TechRepublic Security](https://www.techrepublic.com/topic/security/) — RSS: `https://www.techrepublic.com/rssfeeds/topic/security/`

Added once on restart after upgrading, without overwriting existing source settings or re-enabling removed/disabled sources. Use **Check new sources** to collect these feeds.

## The Hacker News (v0.6.1)

- [The Hacker News](https://thehackernews.com/) — RSS: `https://feeds.feedburner.com/TheHackersNews`

Added once on restart after updating. Existing matching sources and disabled/removed choices remain unchanged; use **Check new sources** to collect it.

## Zero Day Initiative (v0.6.2)

- [Zero Day Initiative published advisories](https://www.zerodayinitiative.com/advisories/published/) — RSS: `https://www.zerodayinitiative.com/rss/published/`

Collects published vulnerability disclosures, including technical details and vendor remediation links. Added once on restart without overwriting existing source settings or disabled/removed choices.
