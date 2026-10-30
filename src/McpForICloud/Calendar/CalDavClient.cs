using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Xml.Linq;
using ModelContextProtocol;

namespace McpForICloud.Calendar;

public sealed record CalendarInfo(string Name, string Id, string? Color);

public sealed record CalendarResource(string Href, string? ETag, string CalendarName, string Ics);

/// <summary>
/// Minimal CalDAV client for iCloud: discovery, time-range queries, and PUT/DELETE of single events.
/// </summary>
public sealed class CalDavClient(McpForICloudOptions options, CredentialProvider credentials) : IDisposable
{
    private static readonly XNamespace s_dav = "DAV:";
    private static readonly XNamespace s_cal = "urn:ietf:params:xml:ns:caldav";
    private static readonly XNamespace s_apple = "http://apple.com/ns/ical/";
    private static readonly TimeSpan s_calendarCacheTtl = TimeSpan.FromMinutes(10);

    // Redirects are followed by hand because HttpClient drops the Authorization header when iCloud
    // bounces requests from caldav.icloud.com to a per-account pNN-caldav.icloud.com host.
    private readonly HttpClient _http = new(new SocketsHttpHandler { AllowAutoRedirect = false });
    private readonly SemaphoreSlim _discoveryGate = new(1, 1);
    private List<CalendarInfo>? _calendars;
    private DateTimeOffset _calendarsLoadedAt;

    public async Task<IReadOnlyList<CalendarInfo>> GetCalendarsAsync(CancellationToken ct)
    {
        await _discoveryGate.WaitAsync(ct);
        try
        {
            if (_calendars is not null && DateTimeOffset.UtcNow - _calendarsLoadedAt < s_calendarCacheTtl)
            {
                return _calendars;
            }

            var root = new Uri(options.CalDavUrl);
            var principalDoc = await PropFindAsync(root, 0, new XElement(s_dav + "current-user-principal"), ct);
            var principalHref = principalDoc.Descendants(s_dav + "current-user-principal").Elements(s_dav + "href").FirstOrDefault()?.Value
                ?? throw new McpException("iCloud CalDAV did not return a principal URL.");
            var principal = new Uri(root, principalHref);

            var homeDoc = await PropFindAsync(principal, 0, new XElement(s_cal + "calendar-home-set"), ct);
            var homeHref = homeDoc.Descendants(s_cal + "calendar-home-set").Elements(s_dav + "href").FirstOrDefault()?.Value
                ?? throw new McpException("iCloud CalDAV did not return a calendar home.");
            var home = new Uri(principal, homeHref);

            var listDoc = await PropFindAsync(home, 1, [
                new XElement(s_dav + "displayname"),
                new XElement(s_dav + "resourcetype"),
                new XElement(s_cal + "supported-calendar-component-set"),
                new XElement(s_apple + "calendar-color"),
            ], ct);

            _calendars = listDoc.Root!.Elements(s_dav + "response")
                .Where(r => r.Descendants(s_dav + "resourcetype").Elements(s_cal + "calendar").Any())
                .Where(r =>
                {
                    var components = r.Descendants(s_cal + "comp").Select(c => (string?)c.Attribute("name")).ToList();
                    return components.Count == 0 || components.Contains("VEVENT");
                })
                .Select(r => new CalendarInfo(
                    r.Descendants(s_dav + "displayname").FirstOrDefault()?.Value ?? "(unnamed)",
                    new Uri(home, r.Element(s_dav + "href")!.Value).ToString(),
                    r.Descendants(s_apple + "calendar-color").FirstOrDefault()?.Value))
                .ToList();
            _calendarsLoadedAt = DateTimeOffset.UtcNow;
            return _calendars;
        }
        finally
        {
            _discoveryGate.Release();
        }
    }

    /// <summary>Finds a calendar by name or id, falling back to the configured default.</summary>
    public async Task<CalendarInfo> ResolveCalendarAsync(string? nameOrId, CancellationToken ct)
    {
        var calendars = await GetCalendarsAsync(ct);
        var wanted = string.IsNullOrWhiteSpace(nameOrId) ? options.DefaultCalendar : nameOrId;
        if (string.IsNullOrWhiteSpace(wanted))
        {
            return calendars.FirstOrDefault(c => c.Name is "Calendar" or "Home") ?? calendars.FirstOrDefault()
                ?? throw new McpException("No iCloud calendars found.");
        }

        return calendars.FirstOrDefault(c => c.Name.Equals(wanted, StringComparison.OrdinalIgnoreCase) || c.Id == wanted)
            ?? throw new McpException($"Calendar '{wanted}' not found. Available: {string.Join(", ", calendars.Select(c => c.Name))}.");
    }

    public async Task<List<CalendarResource>> QueryAsync(CalendarInfo calendar, DateTime startUtc, DateTime endUtc, CancellationToken ct)
    {
        var body = new XDocument(
            new XElement(s_cal + "calendar-query",
                new XAttribute(XNamespace.Xmlns + "d", s_dav),
                new XAttribute(XNamespace.Xmlns + "c", s_cal),
                new XElement(s_dav + "prop", new XElement(s_dav + "getetag"), new XElement(s_cal + "calendar-data")),
                new XElement(s_cal + "filter",
                    new XElement(s_cal + "comp-filter", new XAttribute("name", "VCALENDAR"),
                        new XElement(s_cal + "comp-filter", new XAttribute("name", "VEVENT"),
                            new XElement(s_cal + "time-range",
                                new XAttribute("start", startUtc.ToString("yyyyMMdd'T'HHmmss'Z'")),
                                new XAttribute("end", endUtc.ToString("yyyyMMdd'T'HHmmss'Z'"))))))));

        var calendarUri = new Uri(calendar.Id);
        var doc = await SendXmlAsync(new HttpMethod("REPORT"), calendarUri, body, 1, ct);
        return doc.Root!.Elements(s_dav + "response")
            .Select(r => new
            {
                Href = r.Element(s_dav + "href")?.Value,
                ETag = r.Descendants(s_dav + "getetag").FirstOrDefault()?.Value,
                Ics = r.Descendants(s_cal + "calendar-data").FirstOrDefault()?.Value,
            })
            .Where(r => r.Href is not null && !string.IsNullOrWhiteSpace(r.Ics))
            .Select(r => new CalendarResource(new Uri(calendarUri, r.Href).ToString(), r.ETag, calendar.Name, r.Ics!))
            .ToList();
    }

    public async Task<CalendarResource> GetAsync(string href, CancellationToken ct)
    {
        var uri = new Uri(href);
        using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, uri), uri, ct);
        await EnsureSuccessAsync(response, "read event");
        var calendars = await GetCalendarsAsync(ct);
        var calendarName = calendars.FirstOrDefault(c => href.StartsWith(c.Id, StringComparison.Ordinal))?.Name ?? "";
        return new CalendarResource(href, response.Headers.ETag?.ToString(), calendarName, await response.Content.ReadAsStringAsync(ct));
    }

    /// <summary>Creates (etag null) or replaces (etag set, guarded by If-Match) an event resource.</summary>
    public async Task<string?> PutAsync(string href, string ics, string? etag, CancellationToken ct)
    {
        var uri = new Uri(href);
        using var response = await SendAsync(() =>
        {
            var request = new HttpRequestMessage(HttpMethod.Put, uri)
            {
                Content = new StringContent(ics, Encoding.UTF8, "text/calendar"),
            };
            if (etag is null)
            {
                request.Headers.IfNoneMatch.Add(EntityTagHeaderValue.Any);
            }
            else
            {
                request.Headers.TryAddWithoutValidation("If-Match", etag);
            }

            return request;
        }, uri, ct);

        if (response.StatusCode == HttpStatusCode.PreconditionFailed)
        {
            throw new McpException("The event changed on another device since it was read. List events again and retry.");
        }

        await EnsureSuccessAsync(response, "save event");
        return response.Headers.ETag?.ToString();
    }

    public async Task DeleteAsync(string href, string? etag, CancellationToken ct)
    {
        var uri = new Uri(href);
        using var response = await SendAsync(() =>
        {
            var request = new HttpRequestMessage(HttpMethod.Delete, uri);
            if (etag is not null)
            {
                request.Headers.TryAddWithoutValidation("If-Match", etag);
            }

            return request;
        }, uri, ct);

        if (response.StatusCode == HttpStatusCode.PreconditionFailed)
        {
            throw new McpException("The event changed on another device since it was read. List events again and retry.");
        }

        await EnsureSuccessAsync(response, "delete event");
    }

    public void Dispose()
    {
        _http.Dispose();
        _discoveryGate.Dispose();
    }

    private Task<XDocument> PropFindAsync(Uri uri, int depth, XElement prop, CancellationToken ct) =>
        PropFindAsync(uri, depth, [prop], ct);

    private Task<XDocument> PropFindAsync(Uri uri, int depth, XElement[] props, CancellationToken ct)
    {
        var body = new XDocument(new XElement(s_dav + "propfind",
            new XAttribute(XNamespace.Xmlns + "d", s_dav),
            new XAttribute(XNamespace.Xmlns + "c", s_cal),
            new XElement(s_dav + "prop", props)));
        return SendXmlAsync(new HttpMethod("PROPFIND"), uri, body, depth, ct);
    }

    private async Task<XDocument> SendXmlAsync(HttpMethod method, Uri uri, XDocument body, int depth, CancellationToken ct)
    {
        var xml = body.ToString(SaveOptions.DisableFormatting);
        using var response = await SendAsync(() =>
        {
            var request = new HttpRequestMessage(method, uri)
            {
                Content = new StringContent(xml, Encoding.UTF8, "application/xml"),
            };
            request.Headers.Add("Depth", depth.ToString());
            return request;
        }, uri, ct);

        await EnsureSuccessAsync(response, method.Method);
        return XDocument.Parse(await response.Content.ReadAsStringAsync(ct));
    }

    private async Task<HttpResponseMessage> SendAsync(Func<HttpRequestMessage> createRequest, Uri uri, CancellationToken ct)
    {
        var credential = credentials.Get();
        var auth = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"{credential.UserName}:{credential.Password}")));

        for (var hop = 0; hop < 5; hop++)
        {
            using var request = createRequest();
            request.RequestUri = uri;
            request.Headers.Authorization = auth;
            var response = await _http.SendAsync(request, ct);

            if ((int)response.StatusCode is 301 or 302 or 307 or 308 && response.Headers.Location is { } location)
            {
                uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
                response.Dispose();
                continue;
            }

            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                response.Dispose();
                throw new McpException("iCloud Calendar rejected the login. Check the Apple ID and app-specific password in Credential Manager.");
            }

            return response;
        }

        throw new McpException("iCloud Calendar redirected too many times.");
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, string action)
    {
        if (!response.IsSuccessStatusCode)
        {
            var detail = await response.Content.ReadAsStringAsync();
            throw new McpException($"iCloud Calendar failed to {action}: {(int)response.StatusCode} {response.ReasonPhrase}. {detail[..Math.Min(detail.Length, 300)]}");
        }
    }
}
