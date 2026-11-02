using System.Globalization;
using System.Text.RegularExpressions;

namespace NewsReview;

/// <summary>TaskList-compatible date/time range syntax, evaluated on the server.</summary>
static class Search
{
    static readonly Regex RangePattern = new(@"\A\s*(.+?)\s*[-–—]\s*(.+?)\s*\z", RegexOptions.CultureInvariant);
    static readonly Regex DatePattern = new(@"\A\s*(\d{1,2})/(\d{1,2})(?:/(\d{2}|\d{4}))?\s*\z", RegexOptions.CultureInvariant);
    static readonly Regex TimePattern = new(@"\A(\d{1,2})(?::(\d{1,2}))?(am|pm)?\z", RegexOptions.CultureInvariant);

    public static DateOnly ParseIsoDate(string value) =>
        DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
            ? day : throw new ValidationException($"Invalid isoformat string: '{value}'");

    static (string, string)? SplitRange(string value, string name)
    {
        if (value.Trim().Length == 0) return null;
        var match = RangePattern.Match(value);
        if (!match.Success)
            throw new ValidationException($"Use {name} as a range, for example " + (name == "date range" ? "10/3 - 10/8" : "9am - 9pm"));
        return (match.Groups[1].Value, match.Groups[2].Value);
    }

    static string ParseDate(string value, int year)
    {
        var match = DatePattern.Match(value);
        if (!match.Success) throw new ValidationException("Dates must use m/d, m/d/yy, or m/d/yyyy");
        if (match.Groups[3].Success)
        {
            var specified = match.Groups[3].Value;
            year = int.Parse(specified, CultureInfo.InvariantCulture) + (specified.Length == 2 ? 2000 : 0);
        }
        try
        {
            return new DateOnly(year, int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture), int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture))
                .ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }
        catch (ArgumentOutOfRangeException) { throw new ValidationException("Enter a valid calendar date"); }
    }

    public static (string Start, string End) DateRange(string value, int? year = null)
    {
        if (SplitRange(value, "date range") is not var (first, second)) return ("", "");
        var currentYear = year ?? DateTime.Now.Year;
        var start = ParseDate(first, currentYear);
        var end = ParseDate(second, currentYear);
        if (string.CompareOrdinal(start, end) > 0)
            throw new ValidationException("Start date must be before end date; include years for ranges crossing New Year");
        return (start, end);
    }

    static int ParseTime(string value)
    {
        var normalized = Regex.Replace(value.ToLowerInvariant().Replace(".", ""), @"\s+", "");
        var match = TimePattern.Match(normalized);
        if (!match.Success) throw new ValidationException("Use a 12-hour time such as 9am or 9:30pm");
        var hour = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        var minute = match.Groups[2].Success ? int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture) : 0;
        if (hour is < 1 or > 12 || minute > 59) throw new ValidationException("Use a valid 12-hour time");
        return hour % 12 * 60 + minute + (match.Groups[3].Value == "pm" ? 720 : 0);
    }

    public static (int? Start, int? End) TimeRange(string value) =>
        SplitRange(value, "time range") is var (first, second) ? (ParseTime(first), ParseTime(second)) : (null, null);

    /// <summary>Date-only, time-only, or a date range followed by a comma and a time range.</summary>
    public static (string Start, string End, int? TimeStart, int? TimeEnd) DateTimeRange(string value, int? year = null)
    {
        var parts = value.Trim().Split(',');
        if (parts.Length > 2) throw new ValidationException("Use one comma between the date and time ranges");
        string dates, times;
        if (parts.Length == 2) (dates, times) = (parts[0].Trim(), parts[1].Trim());
        else if (parts[0].Contains('/')) (dates, times) = (parts[0], "");
        else (dates, times) = ("", parts[0]);
        var (start, end) = DateRange(dates, year);
        var (timeStart, timeEnd) = TimeRange(times);
        return (start, end, timeStart, timeEnd);
    }
}
