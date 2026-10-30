using System.Text.Json;
using System.Text.Json.Serialization;

namespace McpForICloud;

/// <summary>
/// User settings loaded from <c>%APPDATA%\mcp-for-icloud\config.json</c> (or the path in <c>MCP_FOR_ICLOUD_CONFIG</c>).
/// Secrets never live here; the app-specific password comes from <see cref="CredentialProvider"/>.
/// </summary>
public sealed class McpForICloudOptions
{
    /// <summary>Apple ID used to sign in to iCloud CalDAV (and IMAP unless <see cref="MailUser"/> is set).</summary>
    public string AppleId { get; set; } = "";

    /// <summary>IMAP login when it differs from the Apple ID, e.g. your @icloud.com address when your Apple ID is a Gmail address.</summary>
    public string? MailUser { get; set; }

    /// <summary>Name shown on drafts you create.</summary>
    public string? DisplayName { get; set; }

    /// <summary>Your iCloud addresses and custom-domain aliases. Drafts may only be sent from one of these.</summary>
    public List<string> Aliases { get; set; } = [];

    /// <summary>From address for new drafts that aren't replies. Defaults to the first alias.</summary>
    public string? DefaultFrom { get; set; }

    /// <summary>IANA or Windows time zone id. Defaults to the machine's local zone.</summary>
    public string? TimeZone { get; set; }

    /// <summary>Calendar name used when a tool call doesn't specify one.</summary>
    public string? DefaultCalendar { get; set; }

    public WorkingHoursOptions WorkingHours { get; set; } = new();

    public PriorityOptions Priority { get; set; } = new();

    public const string DefaultCredentialTarget = "mcp-for-icloud";

    // Names from before the project was renamed from "AI VA".
    // They are still read so existing installs keep working; drop them once nobody depends on them.
    public const string LegacyCredentialTarget = "AiVa-iCloud";
    private const string LegacyConfigFolder = "AiVa";

    /// <summary>Windows Credential Manager target holding the Apple ID and app-specific password.</summary>
    public string CredentialTarget { get; set; } = DefaultCredentialTarget;

    public string ImapHost { get; set; } = "imap.mail.me.com";

    public int ImapPort { get; set; } = 993;

    public string CalDavUrl { get; set; } = "https://caldav.icloud.com/";

    [JsonIgnore]
    public string ConfigPath { get; private set; } = "";

    [JsonIgnore]
    public bool ConfigFound { get; private set; }

    [JsonIgnore]
    public TimeZoneInfo Zone => string.IsNullOrWhiteSpace(TimeZone) ? TimeZoneInfo.Local : TimeZoneInfo.FindSystemTimeZoneById(TimeZone);

    [JsonIgnore]
    public string ImapLogin => string.IsNullOrWhiteSpace(MailUser) ? AppleId : MailUser;

    /// <summary>Every address that counts as "you" when deciding who a message was sent to.</summary>
    [JsonIgnore]
    public IEnumerable<string> OwnAddresses =>
        Aliases.Append(AppleId).Append(MailUser).Append(DefaultFrom)
            .Where(a => !string.IsNullOrWhiteSpace(a))
            .Select(a => a!.Trim().ToLowerInvariant())
            .Distinct();

    public static McpForICloudOptions Load()
    {
        var path = GetEnvironmentVariable("MCP_FOR_ICLOUD_CONFIG", "AIVA_CONFIG");
        if (string.IsNullOrWhiteSpace(path))
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            path = Path.Combine(appData, "mcp-for-icloud", "config.json");

            // The project used to be called "AI VA"; keep reading its config folder until the user moves it.
            var legacyPath = Path.Combine(appData, LegacyConfigFolder, "config.json");
            if (!File.Exists(path) && File.Exists(legacyPath))
            {
                path = legacyPath;
            }
        }

        var options = new McpForICloudOptions();
        if (File.Exists(path))
        {
            var json = File.ReadAllText(path);
            options = JsonSerializer.Deserialize<McpForICloudOptions>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
                Converters = { new JsonStringEnumConverter() },
            }) ?? new McpForICloudOptions();
            options.ConfigFound = true;
        }

        options.ConfigPath = path;
        return options;
    }

    /// <summary>Reads an environment variable, falling back to its pre-rename name.</summary>
    public static string? GetEnvironmentVariable(string name, string legacyName) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : Environment.GetEnvironmentVariable(legacyName);
}

public sealed class WorkingHoursOptions
{
    public TimeOnly Start { get; set; } = new(8, 0);

    public TimeOnly End { get; set; } = new(18, 0);

    public List<DayOfWeek> Days { get; set; } =
        [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday];
}

/// <summary>
/// Sender patterns are either a full address (<c>pat@client.com</c>) or a domain starting with <c>@</c> (<c>@client.com</c>).
/// </summary>
public sealed class PriorityOptions
{
    /// <summary>People and domains whose mail should always rise to the top and be rescued from Junk.</summary>
    public List<string> VipSenders { get; set; } = [];

    /// <summary>Senders that are rarely urgent (social networks, receipts, newsletters you keep).</summary>
    public List<string> LowPrioritySenders { get; set; } = [];

    /// <summary>Domains you trust enough that their messages in Junk are probably misfiled.</summary>
    public List<string> TrustedDomains { get; set; } = [];

    /// <summary>Extra weight for mail sent to a particular alias, e.g. your business domain.</summary>
    public Dictionary<string, int> AliasWeights { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public List<string> UrgentKeywords { get; set; } =
        ["urgent", "asap", "today", "deadline", "invoice", "payment", "contract", "overdue", "action required", "time sensitive"];
}
