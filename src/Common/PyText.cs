using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace NewsReview;

/// <summary>
/// String and timestamp behavior that must match records written by the earlier Python release:
/// lengths count Unicode code points, and ISO timestamps keep their exact text forms.
/// </summary>
static class PyText
{
    public static int Length(string value)
    {
        var count = 0;
        foreach (var _ in value.EnumerateRunes()) count++;
        return count;
    }

    public static string Truncate(string value, int limit)
    {
        if (value.Length <= limit) return value;
        var builder = new StringBuilder();
        var count = 0;
        foreach (var rune in value.EnumerateRunes())
        {
            if (count++ == limit) break;
            builder.Append(rune.ToString());
        }
        return builder.ToString();
    }

    /// <summary>Python's " ".join(value.split()).</summary>
    public static string CollapseWhitespace(string value) =>
        string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    public static string Fold(string value) => value.ToLowerInvariant();

    public static bool FoldEquals(string left, string right) => Fold(left) == Fold(right);

    /// <summary>Sort like Python's sorted(names, key=str.casefold).</summary>
    public static List<string> SortFolded(IEnumerable<string> values) =>
        values.OrderBy(Fold, StringComparer.Ordinal).ToList();

    public static string UtcNow() => FormatSeconds(DateTimeOffset.UtcNow);

    /// <summary>datetime.isoformat(timespec="seconds") for an aware value.</summary>
    public static string FormatSeconds(DateTimeOffset value) =>
        value.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture) + FormatOffset(value.Offset);

    /// <summary>datetime.isoformat() with microseconds only when present.</summary>
    public static string Format(DateTimeOffset value)
    {
        var micro = (value.Ticks % TimeSpan.TicksPerSecond) / 10;
        var text = value.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
        if (micro != 0) text += "." + micro.ToString("D6", CultureInfo.InvariantCulture);
        return text + FormatOffset(value.Offset);
    }

    static string FormatOffset(TimeSpan offset) =>
        (offset < TimeSpan.Zero ? "-" : "+") + offset.Duration().ToString(@"hh\:mm", CultureInfo.InvariantCulture);

    static readonly Regex IsoPattern = new(
        @"\A(?<y>\d{4})-?(?<mo>\d{2})-?(?<d>\d{2})" +
        @"(?:[T ](?<h>\d{2})(?::?(?<mi>\d{2})(?::?(?<s>\d{2})(?:[.,](?<f>\d{1,9}))?)?)?" +
        @"(?<z>Z|[+-]\d{2}(?::?\d{2}(?::?\d{2})?)?)?)?\z",
        RegexOptions.CultureInvariant);

    /// <summary>
    /// datetime.fromisoformat for the forms used by feeds and stored records.
    /// Returns null when the text is not ISO 8601. Naive values report HasOffset=false.
    /// </summary>
    public static (DateTime Local, TimeSpan? Offset)? ParseIso(string? text)
    {
        if (text is null) return null;
        var match = IsoPattern.Match(text);
        if (!match.Success) return null;
        try
        {
            int Part(string name) => match.Groups[name].Success ? int.Parse(match.Groups[name].Value, CultureInfo.InvariantCulture) : 0;
            var fraction = match.Groups["f"].Success ? match.Groups["f"].Value.PadRight(7, '0')[..7] : "0";
            var hour = Part("h");
            var local = new DateTime(Part("y"), Part("mo"), Part("d"), 0, 0, 0, DateTimeKind.Unspecified);
            if (hour == 24 && Part("mi") == 0 && Part("s") == 0 && fraction.Trim('0').Length == 0) local = local.AddDays(1);
            else local = local.Add(new TimeSpan(hour, Part("mi"), Part("s"))).AddTicks(long.Parse(fraction, CultureInfo.InvariantCulture));
            TimeSpan? offset = null;
            if (match.Groups["z"].Success)
            {
                var zone = match.Groups["z"].Value;
                if (zone == "Z") offset = TimeSpan.Zero;
                else
                {
                    var digits = zone[1..].Replace(":", "");
                    var span = new TimeSpan(int.Parse(digits[..2], CultureInfo.InvariantCulture),
                        digits.Length >= 4 ? int.Parse(digits[2..4], CultureInfo.InvariantCulture) : 0,
                        digits.Length >= 6 ? int.Parse(digits[4..6], CultureInfo.InvariantCulture) : 0);
                    if (span >= TimeSpan.FromDays(1)) return null;
                    offset = zone[0] == '-' ? -span : span;
                }
            }
            return (local, offset);
        }
        catch (ArgumentOutOfRangeException) { return null; }
        catch (OverflowException) { return null; }
    }

    /// <summary>An aware timestamp; naive values are interpreted as server-local time, like astimezone().</summary>
    public static DateTimeOffset? ParseInstant(string? text)
    {
        var parsed = ParseIso(text);
        if (parsed is null) return null;
        var (local, offset) = parsed.Value;
        try
        {
            if (offset is not { } value) return Zones.FromLocal(local, TimeZoneInfo.Local);
            // DateTimeOffset holds whole-minute offsets within ±14 hours; others keep their exact UTC instant.
            if (value.Seconds == 0 && value.Duration() <= TimeSpan.FromHours(14)) return new DateTimeOffset(local, value);
            return new DateTimeOffset(local - value, TimeSpan.Zero);
        }
        catch (ArgumentException) { return null; }
    }

    /// <summary>A timestamp that must include a UTC offset (stored records require one).</summary>
    public static DateTimeOffset? ParseAware(string? text)
    {
        var parsed = ParseIso(text);
        return parsed?.Offset is null ? null : ParseInstant(text);
    }

    static readonly DateTimeOffset Epoch = new(1970, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>Exact UTC microseconds since the epoch, as stored in stories.sort_timestamp.</summary>
    public static long Microseconds(DateTimeOffset value) => (value.UtcTicks - Epoch.UtcTicks) / 10;

    public static DateTimeOffset FromMicroseconds(long value) => Epoch.AddTicks(value * 10);
}

/// <summary>Local-time conversion matching Python zoneinfo with fold=0.</summary>
static class Zones
{
    public static DateTimeOffset FromLocal(DateTime local, TimeZoneInfo zone)
    {
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        TimeSpan offset;
        if (zone.IsAmbiguousTime(local))
            // fold=0 selects the first occurrence, which uses the offset in effect before the transition.
            offset = zone.GetAmbiguousTimeOffsets(local).Max();
        else if (zone.IsInvalidTime(local))
            // A skipped wall time also uses the pre-transition offset, as zoneinfo does for fold=0.
            offset = zone.GetUtcOffset(local.AddDays(-1));
        else
            offset = zone.GetUtcOffset(local);
        return new DateTimeOffset(local, offset);
    }

    public static TimeZoneInfo Find(string name)
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(name); }
        catch (Exception error) when (error is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            throw new ValidationException("Search timezone is unavailable on this server");
        }
    }
}

/// <summary>An input or data problem reported to the client as HTTP 400.</summary>
class ValidationException(string message) : Exception(message);

/// <summary>A story or source that does not exist; answered with 404 like TaskList and SpendTracker.</summary>
sealed class NotFoundException(string message) : ValidationException(message);
