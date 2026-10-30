using MailKit;

namespace McpForICloud.Mail;

/// <summary>
/// Cheap, explainable signals that pre-rank messages.
/// The scores are a starting point for the assistant, not a verdict; every point comes with a reason it can show you.
/// </summary>
public static class PriorityScorer
{
    public static (int Score, List<string> Reasons) ScoreInbox(MailSummary mail, IMessageSummary raw, McpForICloudOptions options, HashSet<string> known)
    {
        var score = 0;
        var reasons = new List<string>();
        var priority = options.Priority;
        void Add(int points, string reason)
        {
            score += points;
            reasons.Add($"{(points >= 0 ? "+" : "")}{points} {reason}");
        }

        if (Matches(mail.FromAddress, priority.VipSenders))
        {
            Add(5, "VIP sender");
        }

        if (known.Contains(mail.FromAddress))
        {
            Add(2, "you've emailed this sender before");
        }

        if (mail.DeliveredToAlias is { } alias)
        {
            Add(1, $"addressed directly to you ({alias})");
            if (priority.AliasWeights.TryGetValue(alias, out var weight) && weight != 0)
            {
                Add(weight, $"alias weight for {alias}");
            }
        }

        var text = $"{mail.Subject} {mail.Preview}";
        var keyword = priority.UrgentKeywords.FirstOrDefault(k => text.Contains(k, StringComparison.OrdinalIgnoreCase));
        if (keyword is not null)
        {
            Add(2, $"mentions \"{keyword}\"");
        }

        if (mail.Flagged)
        {
            Add(1, "already flagged");
        }

        if (Matches(mail.FromAddress, priority.LowPrioritySenders))
        {
            Add(-3, "low-priority sender");
        }

        if (IsBulk(raw))
        {
            Add(-3, "bulk/newsletter mail");
        }

        if (mail.FromAddress.Contains("noreply", StringComparison.OrdinalIgnoreCase) ||
            mail.FromAddress.Contains("no-reply", StringComparison.OrdinalIgnoreCase))
        {
            Add(-2, "no-reply sender");
        }

        return (score, reasons);
    }

    /// <summary>Scores how likely a Junk message is legitimate mail that was misfiled.</summary>
    public static (int Score, List<string> Reasons) ScoreSpamRescue(MailSummary mail, IMessageSummary raw, McpForICloudOptions options, HashSet<string> known)
    {
        var score = 0;
        var reasons = new List<string>();
        var priority = options.Priority;
        void Add(int points, string reason)
        {
            score += points;
            reasons.Add($"{(points >= 0 ? "+" : "")}{points} {reason}");
        }

        if (Matches(mail.FromAddress, priority.VipSenders))
        {
            Add(5, "VIP sender");
        }

        if (known.Contains(mail.FromAddress))
        {
            Add(4, "you've emailed this sender before");
        }

        if (Matches(mail.FromAddress, priority.TrustedDomains.Select(d => d.StartsWith('@') ? d : "@" + d)))
        {
            Add(3, "trusted domain");
        }

        if (raw.Envelope?.InReplyTo is { Length: > 0 } && mail.Subject.StartsWith("Re:", StringComparison.OrdinalIgnoreCase))
        {
            Add(3, "reply in an existing thread");
        }

        if (mail.DeliveredToAlias is { } alias)
        {
            Add(1, $"addressed directly to you ({alias})");
        }

        var auth = raw.Headers?["Authentication-Results"] ?? "";
        if (auth.Contains("dmarc=pass", StringComparison.OrdinalIgnoreCase))
        {
            Add(1, "sender passed DMARC");
        }

        if (auth.Contains("spf=fail", StringComparison.OrdinalIgnoreCase) ||
            auth.Contains("dkim=fail", StringComparison.OrdinalIgnoreCase) ||
            auth.Contains("dmarc=fail", StringComparison.OrdinalIgnoreCase))
        {
            Add(-4, "failed sender authentication (SPF/DKIM/DMARC)");
        }

        if (IsBulk(raw))
        {
            Add(-1, "bulk/newsletter mail");
        }

        return (score, reasons);
    }

    public static bool Matches(string address, IEnumerable<string> patterns)
    {
        foreach (var pattern in patterns)
        {
            var p = pattern.Trim();
            if (p.Length == 0)
            {
                continue;
            }

            if (p.StartsWith('@')
                ? address.EndsWith(p, StringComparison.OrdinalIgnoreCase) || address.EndsWith("." + p[1..], StringComparison.OrdinalIgnoreCase)
                : address.Equals(p, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsBulk(IMessageSummary raw)
    {
        var headers = raw.Headers;
        if (headers is null)
        {
            return false;
        }

        var precedence = headers["Precedence"] ?? "";
        return headers.Contains("List-Unsubscribe") ||
               precedence.Contains("bulk", StringComparison.OrdinalIgnoreCase) ||
               precedence.Contains("list", StringComparison.OrdinalIgnoreCase);
    }
}
