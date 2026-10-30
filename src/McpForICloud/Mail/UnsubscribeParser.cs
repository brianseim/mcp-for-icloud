using System.Text.RegularExpressions;

namespace McpForICloud.Mail;

public sealed record UnsubscribeInfo(
    string Folder,
    uint Uid,
    string? FromAddress,
    string? Subject,
    bool OneClick,
    string? OneClickUrl,
    string? WebUrl,
    string? Mailto,
    string? Note);

/// <summary>
/// Reads RFC 2369 List-Unsubscribe and RFC 8058 List-Unsubscribe-Post headers.
/// It only reports the links; acting on them is left to the user.
/// </summary>
public static partial class UnsubscribeParser
{
    public static UnsubscribeInfo Parse(string folder, uint uid, string? fromAddress, string? subject, string? listUnsubscribe, string? listUnsubscribePost)
    {
        var links = string.IsNullOrWhiteSpace(listUnsubscribe)
            ? []
            : AngleBracketed().Matches(listUnsubscribe).Select(m => Unfold().Replace(m.Groups[1].Value, "")).ToList();

        var https = links.FirstOrDefault(l => Uri.TryCreate(l, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps);
        var http = https ?? links.FirstOrDefault(l => Uri.TryCreate(l, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttp);
        var mailto = links.FirstOrDefault(l => l.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase));

        // RFC 8058 one-click needs both the POST header and an HTTPS link; a plain http link is only a web page.
        var oneClick = https is not null
            && listUnsubscribePost?.Contains("List-Unsubscribe=One-Click", StringComparison.OrdinalIgnoreCase) == true;

        var note = (oneClick, http, mailto) switch
        {
            (true, _, _) => "One-click: POST 'List-Unsubscribe=One-Click' to OneClickUrl. Opening it in a browser may not unsubscribe.",
            (false, not null, _) => "Opening WebUrl shows the sender's unsubscribe page.",
            (false, null, not null) => "Unsubscribe by sending an email to the mailto address.",
            _ => "No List-Unsubscribe header. Look for an unsubscribe link in the message body, or block the sender.",
        };

        return new UnsubscribeInfo(folder, uid, fromAddress, subject, oneClick, oneClick ? https : null, oneClick ? null : http, mailto, note);
    }

    [GeneratedRegex("<([^>]+)>")]
    private static partial Regex AngleBracketed();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Unfold();
}
