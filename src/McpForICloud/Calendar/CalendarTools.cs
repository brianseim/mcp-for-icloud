using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Ical.Net;
using Ical.Net.CalendarComponents;
using Ical.Net.DataTypes;
using Ical.Net.Serialization;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace McpForICloud.Calendar;

public sealed record EventInfo(
    string Id,
    string? ETag,
    string Calendar,
    string Title,
    string Start,
    string End,
    bool AllDay,
    string? Location,
    string? Notes,
    bool Recurring,
    bool Busy,
    string? Status);

public sealed record FreeSlot(string Start, string End, int Minutes);

[McpServerToolType]
public sealed class CalendarTools(CalDavClient calDav, McpForICloudOptions options)
{
    private const string TimeFormat = "yyyy-MM-dd'T'HH:mm:sszzz";
    private const string DateFormat = "yyyy-MM-dd";
    private static readonly Regex s_rruleChars = new("^[A-Z0-9=;,+-]+$", RegexOptions.Compiled);

    [McpServerTool(Name = "calendar_list_calendars", ReadOnly = true, OpenWorld = true)]
    [Description("Lists your iCloud calendars, plus the current local time and time zone used to interpret dates.")]
    public async Task<object> ListCalendars(CancellationToken ct)
    {
        var calendars = await calDav.GetCalendarsAsync(ct);
        var zone = options.Zone;
        return new
        {
            Now = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, zone).ToString(TimeFormat, CultureInfo.InvariantCulture),
            TimeZone = zone.Id,
            DefaultCalendar = options.DefaultCalendar,
            Calendars = calendars.Select(c => new { c.Name, c.Color }),
        };
    }

    [McpServerTool(Name = "calendar_list_events", ReadOnly = true, OpenWorld = true)]
    [Description("Lists events (with recurring events expanded) between two times across all calendars or one. " +
                 "Times without an offset are interpreted in the user's time zone. Defaults to the next 7 days.")]
    public async Task<List<EventInfo>> ListEvents(
        [Description("Range start, ISO 8601 (e.g. 2026-10-20 or 2026-10-20T09:00). Defaults to now.")] string? start = null,
        [Description("Range end, ISO 8601. Defaults to 7 days after start.")] string? end = null,
        [Description("Calendar name; omit for all calendars.")] string? calendar = null,
        CancellationToken ct = default)
    {
        var rangeStart = start is null ? DateTimeOffset.UtcNow : ParseTime(start);
        var rangeEnd = end is null ? rangeStart.AddDays(7) : ParseTime(end);
        return await GetEventsAsync(rangeStart, rangeEnd, calendar, ct);
    }

    [McpServerTool(Name = "calendar_find_free_time", ReadOnly = true, OpenWorld = true)]
    [Description("Finds open slots of at least the given length between two times, inside configured working hours unless anyHour is true. " +
                 "Busy time is any timed, non-cancelled event not marked free; all-day events don't block time.")]
    public async Task<List<FreeSlot>> FindFreeTime(
        [Description("Range start, ISO 8601.")] string start,
        [Description("Range end, ISO 8601.")] string end,
        [Description("Minimum slot length in minutes.")] int durationMinutes = 30,
        [Description("Ignore working hours and days.")] bool anyHour = false,
        CancellationToken ct = default)
    {
        var zone = options.Zone;
        var rangeStart = ParseTime(start);
        var rangeEnd = ParseTime(end);
        var busy = (await GetEventsAsync(rangeStart, rangeEnd, null, ct))
            .Where(e => e.Busy && !e.AllDay && !string.Equals(e.Status, "CANCELLED", StringComparison.OrdinalIgnoreCase))
            .Select(e => (Start: DateTimeOffset.Parse(e.Start, CultureInfo.InvariantCulture), End: DateTimeOffset.Parse(e.End, CultureInfo.InvariantCulture)))
            .OrderBy(b => b.Start)
            .ToList();

        var windows = new List<(DateTimeOffset Start, DateTimeOffset End)>();
        if (anyHour)
        {
            windows.Add((rangeStart, rangeEnd));
        }
        else
        {
            var hours = options.WorkingHours;
            var firstDay = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(rangeStart, zone).DateTime);
            var lastDay = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(rangeEnd, zone).DateTime);
            for (var day = firstDay; day <= lastDay; day = day.AddDays(1))
            {
                if (!hours.Days.Contains(day.DayOfWeek))
                {
                    continue;
                }

                var windowStart = Max(ToZoned(day.ToDateTime(hours.Start), zone), rangeStart);
                var windowEnd = Min(ToZoned(day.ToDateTime(hours.End), zone), rangeEnd);
                if (windowEnd > windowStart)
                {
                    windows.Add((windowStart, windowEnd));
                }
            }
        }

        var slots = new List<FreeSlot>();
        foreach (var (windowStart, windowEnd) in windows)
        {
            var cursor = windowStart;
            foreach (var block in busy.Where(b => b.End > windowStart && b.Start < windowEnd))
            {
                AddSlot(cursor, Min(block.Start, windowEnd));
                cursor = Max(cursor, block.End);
            }

            AddSlot(cursor, windowEnd);
        }

        return slots;

        void AddSlot(DateTimeOffset from, DateTimeOffset to)
        {
            var minutes = (int)(to - from).TotalMinutes;
            if (minutes >= durationMinutes)
            {
                slots.Add(new FreeSlot(Format(from), Format(to), minutes));
            }
        }
    }

    [McpServerTool(Name = "calendar_create_event", ReadOnly = false, Destructive = false, OpenWorld = true)]
    [Description("Creates an event without inviting anyone, optionally repeating (e.g. a birthday as one yearly all-day event). " +
                 "Confirm the details with the user first.")]
    public async Task<EventInfo> CreateEvent(
        [Description("Event title.")] string title,
        [Description("Start, ISO 8601. A date alone (2026-10-20) with allDay=true makes an all-day event. For a repeating event this is the first occurrence.")] string start,
        [Description("End, ISO 8601. Defaults to 1 hour after start (or the same day for all-day events). For a repeating event this is the end of the first occurrence, not of the series.")] string? end = null,
        [Description("All-day event.")] bool allDay = false,
        [Description("Calendar name; omit for the default calendar.")] string? calendar = null,
        [Description("Location.")] string? location = null,
        [Description("Notes.")] string? notes = null,
        [Description("Alert this many minutes before the start.")] int? alertMinutesBefore = null,
        [Description("Repeat forever on the start's pattern: daily, weekly, monthly or yearly. Omit for a one-off event. Use rrule instead for anything more specific.")] string? repeat = null,
        [Description("RFC 5545 recurrence rule for patterns repeat can't express, e.g. FREQ=WEEKLY;BYDAY=MO,WE;COUNT=10. " +
                     "UNTIL must be a UTC time (20271231T235959Z) for timed events or a date (20271231) for all-day events. Don't combine with repeat.")] string? rrule = null,
        CancellationToken ct = default)
    {
        var recurrence = ResolveRecurrence(repeat, rrule);
        var target = await calDav.ResolveCalendarAsync(calendar, ct);
        var uid = Guid.NewGuid().ToString("D").ToUpperInvariant();

        var lines = new List<string>
        {
            "BEGIN:VCALENDAR",
            "VERSION:2.0",
            "PRODID:-//mcp-for-icloud//EN",
            "CALSCALE:GREGORIAN",
            "BEGIN:VEVENT",
            $"UID:{uid}",
            $"DTSTAMP:{UtcStamp(DateTime.UtcNow)}",
        };

        if (allDay)
        {
            var startDay = DateOnly.FromDateTime(ParseTime(start).DateTime);
            var endDay = end is null ? startDay : DateOnly.FromDateTime(ParseTime(end).DateTime);

            // iCalendar all-day DTEND is exclusive, so a one-day event ends the following day.
            lines.Add($"DTSTART;VALUE=DATE:{startDay:yyyyMMdd}");
            lines.Add($"DTEND;VALUE=DATE:{endDay.AddDays(1):yyyyMMdd}");
        }
        else
        {
            var startTime = ParseTime(start);
            var endTime = end is null ? startTime.AddHours(1) : ParseTime(end);
            if (endTime <= startTime)
            {
                throw new McpException("The event must end after it starts.");
            }

            if (recurrence is null)
            {
                lines.Add($"DTSTART:{UtcStamp(startTime.UtcDateTime)}");
                lines.Add($"DTEND:{UtcStamp(endTime.UtcDateTime)}");
            }
            else
            {
                // A UTC start would drift an hour against local time across DST changes, so recurring events are anchored to the user's zone.
                var tzid = IanaZoneId(options.Zone);
                lines.InsertRange(lines.IndexOf("BEGIN:VEVENT"), TimeZoneLines(tzid, startTime));
                lines.Add($"DTSTART;TZID={tzid}:{LocalStamp(startTime)}");
                lines.Add($"DTEND;TZID={tzid}:{LocalStamp(endTime)}");
            }
        }

        if (recurrence is not null)
        {
            lines.Add($"RRULE:{recurrence}");
        }

        lines.Add($"SUMMARY:{Escape(title)}");
        if (!string.IsNullOrWhiteSpace(location))
        {
            lines.Add($"LOCATION:{Escape(location)}");
        }

        if (!string.IsNullOrWhiteSpace(notes))
        {
            lines.Add($"DESCRIPTION:{Escape(notes)}");
        }

        if (alertMinutesBefore is { } alert)
        {
            lines.AddRange(["BEGIN:VALARM", "ACTION:DISPLAY", "DESCRIPTION:Reminder", $"TRIGGER:-PT{alert}M", "END:VALARM"]);
        }

        lines.AddRange(["END:VEVENT", "END:VCALENDAR"]);

        var href = new Uri(new Uri(target.Id), $"{uid}.ics").ToString();
        var etag = await calDav.PutAsync(href, string.Join("\r\n", lines.Select(Fold)) + "\r\n", null, ct);
        var created = await calDav.GetAsync(href, ct);
        return ToEventInfos(created with { ETag = etag ?? created.ETag }, DateTimeOffset.MinValue, DateTimeOffset.MaxValue).First();
    }

    [McpServerTool(Name = "calendar_update_event", ReadOnly = false, Destructive = false, OpenWorld = true)]
    [Description("Updates an event's title, time, location or notes. Only fields you pass change. " +
                 "For recurring events only title, location and notes can change, and the change applies to the whole series. Confirm with the user first.")]
    public async Task<EventInfo> UpdateEvent(
        [Description("Event id from calendar_list_events.")] string id,
        [Description("New title.")] string? title = null,
        [Description("New start, ISO 8601. The duration is kept unless end is also given.")] string? start = null,
        [Description("New end, ISO 8601.")] string? end = null,
        [Description("New location (empty string clears it).")] string? location = null,
        [Description("New notes (empty string clears them).")] string? notes = null,
        CancellationToken ct = default)
    {
        var resource = await calDav.GetAsync(id, ct);
        var ical = Ical.Net.Calendar.Load(resource.Ics) ?? throw new McpException("Couldn't parse the event.");
        var master = ical.Events.FirstOrDefault(e => e.RecurrenceIdentifier is null) ?? ical.Events.First();
        var recurring = master.RecurrenceRule is not null || ical.Events.Count > 1;

        if (title is not null)
        {
            master.Summary = title;
        }

        if (location is not null)
        {
            master.Location = location.Length == 0 ? null : location;
        }

        if (notes is not null)
        {
            master.Description = notes.Length == 0 ? null : notes;
        }

        if (start is not null || end is not null)
        {
            if (recurring)
            {
                throw new McpException("Changing the time of a recurring event isn't supported here; change it in the Calendar app.");
            }

            if (master.IsAllDay)
            {
                throw new McpException("Changing the date of an all-day event isn't supported yet; delete and recreate it, or change it in the Calendar app.");
            }

            var oldStart = ToOffset(master.DtStart!);
            var oldEnd = master.DtEnd is { } dtEnd ? ToOffset(dtEnd) : oldStart.Add(master.Duration?.ToTimeSpanUnspecified() ?? TimeSpan.FromHours(1));
            var newStart = start is null ? oldStart : ParseTime(start);
            var newEnd = end is null ? newStart + (oldEnd - oldStart) : ParseTime(end);
            if (newEnd <= newStart)
            {
                throw new McpException("The event must end after it starts.");
            }

            master.DtStart = new CalDateTime(newStart.UtcDateTime, "UTC", true);
            master.Duration = null;
            master.DtEnd = new CalDateTime(newEnd.UtcDateTime, "UTC", true);
        }

        master.Sequence++;
        master.DtStamp = new CalDateTime(DateTime.UtcNow, "UTC", true);
        master.LastModified = new CalDateTime(DateTime.UtcNow, "UTC", true);

        var ics = new CalendarSerializer().SerializeToString(ical) ?? throw new McpException("Couldn't serialize the event.");
        var etag = await calDav.PutAsync(id, ics, resource.ETag, ct);
        var updated = await calDav.GetAsync(id, ct);
        return ToEventInfos(updated with { ETag = etag ?? updated.ETag }, DateTimeOffset.MinValue, DateTimeOffset.MaxValue).First();
    }

    [McpServerTool(Name = "calendar_delete_event", ReadOnly = false, Destructive = true, OpenWorld = true)]
    [Description("Deletes an event. For a recurring event this deletes the whole series. Always confirm with the user first, naming the event and date.")]
    public async Task<object> DeleteEvent(
        [Description("Event id from calendar_list_events.")] string id,
        [Description("ETag from calendar_list_events; prevents deleting an event that changed since it was listed.")] string? etag = null,
        CancellationToken ct = default)
    {
        await calDav.DeleteAsync(id, etag, ct);
        return new { Deleted = id };
    }

    private async Task<List<EventInfo>> GetEventsAsync(DateTimeOffset rangeStart, DateTimeOffset rangeEnd, string? calendar, CancellationToken ct)
    {
        if (rangeEnd <= rangeStart)
        {
            throw new McpException("The range end must be after its start.");
        }

        var calendars = calendar is null
            ? await calDav.GetCalendarsAsync(ct)
            : [await calDav.ResolveCalendarAsync(calendar, ct)];

        var events = new List<EventInfo>();
        foreach (var cal in calendars)
        {
            foreach (var resource in await calDav.QueryAsync(cal, rangeStart.UtcDateTime, rangeEnd.UtcDateTime, ct))
            {
                events.AddRange(ToEventInfos(resource, rangeStart, rangeEnd));
            }
        }

        return events.OrderBy(e => e.Start, StringComparer.Ordinal).ToList();
    }

    private IEnumerable<EventInfo> ToEventInfos(CalendarResource resource, DateTimeOffset rangeStart, DateTimeOffset rangeEnd)
    {
        var ical = Ical.Net.Calendar.Load(resource.Ics);
        if (ical is null)
        {
            yield break;
        }

        var recurring = ical.Events.Any(e => e.RecurrenceRule is not null);
        var master = ical.Events.FirstOrDefault(e => e.RecurrenceIdentifier is null) ?? ical.Events.FirstOrDefault();
        if (master?.DtStart is null)
        {
            yield break;
        }

        // Start a little early so multi-day events that began before the range still show up.
        var searchFrom = rangeStart == DateTimeOffset.MinValue ? ToOffset(master.DtStart) : rangeStart.AddDays(-14);
        var occurrences = ical.GetOccurrences<CalendarEvent>(new CalDateTime(searchFrom.UtcDateTime, "UTC", true));
        if (rangeEnd != DateTimeOffset.MaxValue)
        {
            occurrences = occurrences.TakeWhileBefore(new CalDateTime(rangeEnd.UtcDateTime, "UTC", true));
        }
        else
        {
            occurrences = occurrences.Take(1);
        }

        foreach (var occurrence in occurrences)
        {
            if (occurrence.Source is not CalendarEvent ev)
            {
                continue;
            }

            var allDay = !occurrence.Period.StartTime.HasTime;
            var occStart = occurrence.Period.StartTime;
            var occEnd = occurrence.Period.EffectiveEndTime ?? occStart;
            var startOffset = ToOffset(occStart);
            var endOffset = ToOffset(occEnd);
            if (rangeStart != DateTimeOffset.MinValue && (endOffset <= rangeStart || startOffset >= rangeEnd) && !(allDay && startOffset == endOffset))
            {
                continue;
            }

            yield return new EventInfo(
                resource.Href,
                resource.ETag,
                resource.CalendarName,
                ev.Summary ?? "(no title)",
                allDay ? occStart.Date.ToString(DateFormat, CultureInfo.InvariantCulture) : Format(startOffset),
                // All-day DTEND is exclusive in iCalendar; show the last day the event actually covers.
                allDay ? occEnd.Date.AddDays(-1).ToString(DateFormat, CultureInfo.InvariantCulture) : Format(endOffset),
                allDay,
                ev.Location,
                ev.Description is { Length: > 500 } d ? d[..500] + "…" : ev.Description,
                recurring,
                !string.Equals(ev.Transparency, "TRANSPARENT", StringComparison.OrdinalIgnoreCase),
                ev.Status);
        }
    }

    private DateTimeOffset ParseTime(string value)
    {
        var zone = options.Zone;
        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed) &&
            (value.EndsWith('Z') || value.EndsWith('z') || HasExplicitOffset(value)))
        {
            return parsed;
        }

        if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
        {
            return ToZoned(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), zone);
        }

        throw new McpException($"Couldn't understand the time '{value}'. Use ISO 8601, e.g. 2026-10-20T14:30.");

        static bool HasExplicitOffset(string s)
        {
            var t = s.IndexOf('T');
            return t > 0 && (s.IndexOf('+', t) > 0 || s.IndexOf('-', t) > 0);
        }
    }

    private DateTimeOffset ToOffset(CalDateTime value)
    {
        if (!value.HasTime)
        {
            return ToZoned(value.Date.ToDateTime(TimeOnly.MinValue), options.Zone);
        }

        return TimeZoneInfo.ConvertTime(new DateTimeOffset(value.AsUtc, TimeSpan.Zero), options.Zone);
    }

    private static DateTimeOffset ToZoned(DateTime local, TimeZoneInfo zone) =>
        new(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), zone.GetUtcOffset(local));

    private string Format(DateTimeOffset value) =>
        TimeZoneInfo.ConvertTime(value, options.Zone).ToString(TimeFormat, CultureInfo.InvariantCulture);

    private static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;

    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;

    private static string UtcStamp(DateTime utc) => utc.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);

    private string LocalStamp(DateTimeOffset value) =>
        TimeZoneInfo.ConvertTime(value, options.Zone).ToString("yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture);

    /// <summary>Turns a repeat preset or a raw RRULE into a validated RRULE value, or null for a one-off event.</summary>
    private static string? ResolveRecurrence(string? repeat, string? rrule)
    {
        var hasRepeat = !string.IsNullOrWhiteSpace(repeat);
        var hasRule = !string.IsNullOrWhiteSpace(rrule);
        if (hasRepeat && hasRule)
        {
            throw new McpException("Pass either repeat or rrule, not both.");
        }

        if (hasRepeat)
        {
            return repeat!.Trim().ToLowerInvariant() switch
            {
                "daily" => "FREQ=DAILY",
                "weekly" => "FREQ=WEEKLY",
                "monthly" => "FREQ=MONTHLY",
                "yearly" or "annually" => "FREQ=YEARLY",
                _ => throw new McpException($"Unknown repeat '{repeat}'. Use daily, weekly, monthly or yearly, or pass an rrule."),
            };
        }

        if (!hasRule)
        {
            return null;
        }

        var value = rrule!.Trim();
        if (value.StartsWith("RRULE:", StringComparison.OrdinalIgnoreCase))
        {
            value = value["RRULE:".Length..];
        }

        value = value.ToUpperInvariant();

        // The rule is written into the ICS verbatim, so anything outside RRULE's alphabet (notably line breaks) is rejected outright.
        if (!s_rruleChars.IsMatch(value) || !value.Split(';').Any(part => part.StartsWith("FREQ=", StringComparison.Ordinal)))
        {
            throw new McpException($"'{rrule}' isn't a valid recurrence rule. Expected something like FREQ=WEEKLY;BYDAY=MO,WE;COUNT=10.");
        }

        try
        {
            _ = new RecurrencePattern(value);
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or InvalidOperationException)
        {
            throw new McpException($"'{rrule}' isn't a valid recurrence rule: {ex.Message}");
        }

        return value;
    }

    private static string IanaZoneId(TimeZoneInfo zone)
    {
        if (zone.HasIanaId)
        {
            return zone.Id;
        }

        return TimeZoneInfo.TryConvertWindowsIdToIanaId(zone.Id, out var iana)
            ? iana
            : throw new McpException($"Couldn't map the time zone '{zone.Id}' to an IANA id; set TimeZone in config.json to one like America/Denver.");
    }

    /// <summary>Builds the VTIMEZONE block that RFC 5545 requires for every TZID an event references.</summary>
    private static IEnumerable<string> TimeZoneLines(string tzid, DateTimeOffset earliest)
    {
        var calendar = new Ical.Net.Calendar();
        calendar.AddTimeZone(VTimeZone.FromDateTimeZone(tzid, earliest.UtcDateTime, includeHistoricalData: false));
        var ics = new CalendarSerializer().SerializeToString(calendar) ?? throw new McpException("Couldn't serialize the time zone.");

        // Unfold first so Fold re-wraps each property cleanly when the event is written.
        var unfolded = ics.Replace("\r\n ", "").Replace("\r\n\t", "").Split("\r\n");
        var begin = Array.IndexOf(unfolded, "BEGIN:VTIMEZONE");
        var end = Array.LastIndexOf(unfolded, "END:VTIMEZONE");
        return begin < 0 || end < begin ? throw new McpException("Couldn't build the time zone definition.") : unfolded[begin..(end + 1)];
    }

    private static string Escape(string text) =>
        text.Replace("\\", "\\\\").Replace(";", "\\;").Replace(",", "\\,").Replace("\r\n", "\\n").Replace("\n", "\\n");

    /// <summary>Folds a content line at 75 octets as RFC 5545 requires, without splitting a UTF-8 character.</summary>
    private static string Fold(string line)
    {
        if (Encoding.UTF8.GetByteCount(line) <= 75)
        {
            return line;
        }

        var result = new StringBuilder();
        var octets = 0;
        foreach (var rune in line.EnumerateRunes())
        {
            var size = rune.Utf8SequenceLength;
            if (octets + size > 75)
            {
                result.Append("\r\n ");
                octets = 1;
            }

            result.Append(rune.ToString());
            octets += size;
        }

        return result.ToString();
    }
}
