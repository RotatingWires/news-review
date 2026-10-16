"""TaskList-compatible date/time range syntax, evaluated on the server."""

from datetime import date
import re


def split_range(value, name):
    if not value.strip():
        return None
    match = re.fullmatch(r"\s*(.+?)\s*[-–—]\s*(.+?)\s*", value)
    if not match:
        raise ValueError(f"Use {name} as a range, for example " + ("10/3 - 10/8" if name == "date range" else "9am - 9pm"))
    return match.groups()


def parse_date(value, year):
    match = re.fullmatch(r"\s*(\d{1,2})/(\d{1,2})(?:/(\d{2}|\d{4}))?\s*", value)
    if not match:
        raise ValueError("Dates must use m/d, m/d/yy, or m/d/yyyy")
    month, day, specified_year = match.groups()
    if specified_year:
        year = int(specified_year) + (2000 if len(specified_year) == 2 else 0)
    try:
        return date(year, int(month), int(day)).isoformat()
    except ValueError as exc:
        raise ValueError("Enter a valid calendar date") from exc


def date_range(value, year=None):
    parts = split_range(value, "date range")
    if parts is None:
        return "", ""
    start, end = [parse_date(part, year or date.today().year) for part in parts]
    if start > end:
        raise ValueError("Start date must be before end date; include years for ranges crossing New Year")
    return start, end


def parse_time(value):
    normalized = re.sub(r"\s+", "", value.lower().replace(".", ""))
    match = re.fullmatch(r"(\d{1,2})(?::(\d{1,2}))?(am|pm)?", normalized)
    if not match:
        raise ValueError("Use a 12-hour time such as 9am or 9:30pm")
    hour, minute = int(match[1]), int(match[2] or 0)
    if not 1 <= hour <= 12 or minute > 59:
        raise ValueError("Use a valid 12-hour time")
    return (hour % 12) * 60 + minute + (720 if match[3] == "pm" else 0)


def time_range(value):
    parts = split_range(value, "time range")
    return (None, None) if parts is None else tuple(parse_time(part) for part in parts)


def date_time_range(value, year=None):
    """Date-only, time-only, or date range followed by comma and time range."""
    parts = value.strip().split(',')
    if len(parts) > 2:
        raise ValueError('Use one comma between the date and time ranges')
    if len(parts) == 2:
        dates, times = (part.strip() for part in parts)
    elif '/' in parts[0]:
        dates, times = parts[0], ''
    else:
        dates, times = '', parts[0]
    start, end = date_range(dates, year)
    time_start, time_end = time_range(times)
    return {'start': start, 'end': end, 'time_start': time_start, 'time_end': time_end}
