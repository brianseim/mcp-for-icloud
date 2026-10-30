using System.ComponentModel;
using System.Net;
using System.Text.RegularExpressions;
using MailKit;
using MailKit.Search;
using MimeKit;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace McpForICloud.Mail;

public sealed record MailSummary(
    string Folder,
    uint Uid,
    DateTimeOffset? Date,
    string From,
    string FromAddress,
    string To,
    string? Cc,
    string? DeliveredToAlias,
    string Subject,
    bool Unread,
    bool Flagged,
    string? Preview,
    int? Score = null,
    IReadOnlyList<string>? Reasons = null);

public sealed record MailMessage(
    string Folder,
    uint Uid,
    DateTimeOffset Date,
    string From,
    string? ReplyTo,
    string To,
    string? Cc,
    string? DeliveredToAlias,
    string Subject,
    string? MessageId,
    IReadOnlyList<string> Attachments,
    string Body,
    bool Truncated);

public sealed record MailRef(
    [property: Description("Folder the message is in.")] string Folder,
    [property: Description("Message UID.")] uint Uid);

[McpServerToolType]
public sealed partial class MailTools(MailService mail)
{
    private static readonly string[] s_scoringHeaders = ["List-Unsubscribe", "Precedence", "Authentication-Results"];

    [McpServerTool(Name = "mail_list_folders", ReadOnly = true, OpenWorld = true)]
    [Description("Lists the iCloud Mail folders with total and unread counts.")]
    public Task<List<object>> ListFolders(CancellationToken ct) =>
        mail.RunAsync(async client =>
        {
            var result = new List<object>();
            foreach (var folder in await client.GetFoldersAsync(client.PersonalNamespaces[0], StatusItems.Count | StatusItems.Unread, cancellationToken: ct))
            {
                if (!folder.Attributes.HasFlag(FolderAttributes.NonExistent) && !folder.Attributes.HasFlag(FolderAttributes.NoSelect))
                {
                    result.Add(new { folder.FullName, Total = folder.Count, folder.Unread });
                }
            }

            return result;
        }, ct);

    [McpServerTool(Name = "mail_search", ReadOnly = true, OpenWorld = true)]
    [Description("Searches a mail folder and returns message summaries, newest first. Does not mark anything as read.")]
    public Task<List<MailSummary>> Search(
        [Description("Folder: inbox, junk, sent, drafts, archive, trash, or a full folder name.")] string folder = "inbox",
        [Description("Sender address or name contains this text.")] string? from = null,
        [Description("Recipient contains this text, e.g. one of your aliases.")] string? to = null,
        [Description("Subject contains this text.")] string? subject = null,
        [Description("Body contains this text (slower).")] string? text = null,
        [Description("Only messages from the last N days.")] int? sinceDays = null,
        [Description("Only unread messages.")] bool unreadOnly = false,
        [Description("Maximum results (1-100).")] int limit = 25,
        CancellationToken ct = default) =>
        mail.RunAsync(async client =>
        {
            var query = BuildQuery(from, to, subject, text, sinceDays, unreadOnly);
            var (_, items) = await FetchSummariesAsync(client, folder, query, limit, ct);
            return items.Select(i => i.Summary).ToList();
        }, ct);

    [McpServerTool(Name = "mail_read", ReadOnly = true, OpenWorld = true)]
    [Description("Reads one message's headers, attachment names and plain-text body. Does not mark it as read.")]
    public Task<MailMessage> Read(
        [Description("Folder the message is in.")] string folder,
        [Description("Message UID from mail_search, mail_triage or mail_review_spam.")] uint uid,
        [Description("Maximum body characters to return.")] int maxChars = 8000,
        CancellationToken ct = default) =>
        mail.RunAsync(async client =>
        {
            var mailFolder = await mail.GetFolderAsync(client, folder, ct);
            await mailFolder.OpenAsync(FolderAccess.ReadOnly, ct);
            var message = await GetMessageAsync(mailFolder, uid, ct);

            var body = message.TextBody ?? HtmlToText(message.HtmlBody ?? "");
            var truncated = body.Length > maxChars;
            return new MailMessage(
                mailFolder.FullName,
                uid,
                message.Date,
                message.From.ToString(),
                message.ReplyTo.Count > 0 ? message.ReplyTo.ToString() : null,
                message.To.ToString(),
                message.Cc.Count > 0 ? message.Cc.ToString() : null,
                FindOwnAddress(message.To.Mailboxes.Concat(message.Cc.Mailboxes)),
                message.Subject ?? "",
                message.MessageId,
                message.Attachments.Select(a => a.ContentDisposition?.FileName ?? a.ContentType.Name ?? "attachment").ToList(),
                truncated ? body[..maxChars] : body,
                truncated);
        }, ct);

    [McpServerTool(Name = "mail_triage", ReadOnly = true, OpenWorld = true)]
    [Description("Pre-ranks recent inbox mail by explainable priority signals (VIP senders, people you've emailed, alias weights, urgent keywords, bulk mail). " +
                 "Use the score and reasons as a starting point, then apply judgment and the user's priorities.")]
    public Task<List<MailSummary>> Triage(
        [Description("Look back this many days.")] int sinceDays = 2,
        [Description("Only unread messages.")] bool unreadOnly = true,
        [Description("Maximum messages to score (1-100).")] int limit = 50,
        CancellationToken ct = default) =>
        mail.RunAsync(async client =>
        {
            var known = await mail.GetKnownCorrespondentsAsync(client, ct);
            var query = BuildQuery(null, null, null, null, sinceDays, unreadOnly);
            var (_, items) = await FetchSummariesAsync(client, "inbox", query, limit, ct);
            return items
                .Select(i =>
                {
                    var (score, reasons) = PriorityScorer.ScoreInbox(i.Summary, i.Raw, mail.Options, known);
                    return i.Summary with { Score = score, Reasons = reasons };
                })
                .OrderByDescending(s => s.Score)
                .ThenByDescending(s => s.Date)
                .ToList();
        }, ct);

    [McpServerTool(Name = "mail_review_spam", ReadOnly = true, OpenWorld = true)]
    [Description("Scores recent Junk messages by how likely they are legitimate mail that was misfiled " +
                 "(known correspondents, VIPs, trusted domains, replies to your threads, sender authentication). " +
                 "Present likely-legitimate ones to the user; only move them with mail_mark_not_spam after the user agrees.")]
    public Task<List<MailSummary>> ReviewSpam(
        [Description("Look back this many days.")] int sinceDays = 14,
        [Description("Maximum messages to score (1-100).")] int limit = 50,
        CancellationToken ct = default) =>
        mail.RunAsync(async client =>
        {
            var known = await mail.GetKnownCorrespondentsAsync(client, ct);
            var query = BuildQuery(null, null, null, null, sinceDays, false);
            var (_, items) = await FetchSummariesAsync(client, "junk", query, limit, ct);
            return items
                .Select(i =>
                {
                    var (score, reasons) = PriorityScorer.ScoreSpamRescue(i.Summary, i.Raw, mail.Options, known);
                    return i.Summary with { Score = score, Reasons = reasons };
                })
                .OrderByDescending(s => s.Score)
                .ThenByDescending(s => s.Date)
                .ToList();
        }, ct);

    [McpServerTool(Name = "mail_mark_not_spam", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = true)]
    [Description("Moves messages from Junk back to the Inbox and tags them as not junk so iCloud learns. Confirm with the user first.")]
    public Task<object> MarkNotSpam(
        [Description("UIDs of messages in the Junk folder.")] uint[] uids,
        CancellationToken ct = default) =>
        mail.RunAsync<object>(async client =>
        {
            var junk = await mail.GetFolderAsync(client, "junk", ct);
            await junk.OpenAsync(FolderAccess.ReadWrite, ct);
            var ids = ToUniqueIds(uids);

            // Apple Mail's own "Not Junk" button sets these keywords; servers that don't allow custom keywords just skip them.
            try
            {
                var notJunk = new StoreFlagsRequest(StoreAction.Add, MessageFlags.None) { Silent = true };
                notJunk.Keywords.Add("$NotJunk");
                notJunk.Keywords.Add("NotJunk");
                await junk.StoreAsync(ids, notJunk, ct);
                var junkTags = new StoreFlagsRequest(StoreAction.Remove, MessageFlags.None) { Silent = true };
                junkTags.Keywords.Add("$Junk");
                junkTags.Keywords.Add("Junk");
                await junk.StoreAsync(ids, junkTags, ct);
            }
            catch (MailKit.Net.Imap.ImapCommandException)
            {
            }

            var moved = await junk.MoveToAsync(ids, client.Inbox, ct);
            return new { Moved = ids.Count, NewInboxUids = moved?.Destination.Select(u => u.Id).ToArray() };
        }, ct);

    [McpServerTool(Name = "mail_move", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = true)]
    [Description("Moves messages between folders, e.g. inbox to archive. Never moves to trash; confirm bulk moves with the user first.")]
    public Task<object> Move(
        [Description("Folder the messages are in.")] string folder,
        [Description("Message UIDs.")] uint[] uids,
        [Description("Destination folder: archive, inbox, junk, or a full folder name.")] string destination,
        CancellationToken ct = default) =>
        mail.RunAsync<object>(async client =>
        {
            var target = await mail.GetFolderAsync(client, destination, ct);
            if (target.Attributes.HasFlag(FolderAttributes.Trash))
            {
                throw new McpException("mail_move doesn't move to trash. Use mail_delete after the user approves the deletion.");
            }

            var source = await mail.GetFolderAsync(client, folder, ct);
            await source.OpenAsync(FolderAccess.ReadWrite, ct);
            var ids = ToUniqueIds(uids);
            await source.MoveToAsync(ids, target, ct);
            return new { Moved = ids.Count, From = source.FullName, To = target.FullName };
        }, ct);

    [McpServerTool(Name = "mail_delete", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = true)]
    [Description("Moves messages to the trash folder (\"Deleted Messages\" on iCloud) in one call; they stay recoverable there until iCloud empties it. " +
                 "Messages can come from several folders. Never permanently deletes. Confirm the list with the user first.")]
    public Task<object> Delete(
        [Description("Messages to delete, each with the folder it's in and its UID.")] MailRef[] messages,
        CancellationToken ct = default) =>
        mail.RunAsync<object>(async client =>
        {
            if (messages is not { Length: > 0 })
            {
                throw new McpException("Pass at least one message to delete.");
            }

            var trash = await mail.GetFolderAsync(client, "trash", ct);

            // Resolve every folder before moving anything, so a typo can't leave the batch half done.
            var batches = new List<(IMailFolder Folder, List<UniqueId> Ids)>();
            foreach (var group in messages.GroupBy(m => m.Folder.Trim(), StringComparer.OrdinalIgnoreCase))
            {
                var source = await mail.GetFolderAsync(client, group.Key, ct);
                if (source.FullName == trash.FullName)
                {
                    throw new McpException("Those messages are already in Trash. Emptying Trash isn't available from the assistant.");
                }

                batches.Add((source, ToUniqueIds(group.Select(m => m.Uid).ToArray())));
            }

            var results = new List<object>();
            foreach (var (source, ids) in batches)
            {
                await source.OpenAsync(FolderAccess.ReadWrite, ct);
                await source.MoveToAsync(ids, trash, ct);
                results.Add(new { From = source.FullName, Moved = ids.Count });
            }

            return new { Deleted = batches.Sum(b => b.Ids.Count), To = trash.FullName, Folders = results };
        }, ct);

    [McpServerTool(Name = "mail_unsubscribe_info", ReadOnly = true, OpenWorld = true)]
    [Description("Reads the List-Unsubscribe headers of messages and returns how to unsubscribe: a one-click URL (RFC 8058), a web link, or a mailto. " +
                 "Returns the links only and never unsubscribes. Show them to the user; one-click URLs must be POSTed, never just opened.")]
    public Task<List<UnsubscribeInfo>> GetUnsubscribeInfo(
        [Description("Folder the messages are in.")] string folder,
        [Description("Message UIDs.")] uint[] uids,
        CancellationToken ct = default) =>
        mail.RunAsync(async client =>
        {
            var source = await mail.GetFolderAsync(client, folder, ct);
            await source.OpenAsync(FolderAccess.ReadOnly, ct);
            var ids = ToUniqueIds(uids);
            var request = new FetchRequest(MessageSummaryItems.UniqueId | MessageSummaryItems.Envelope)
            {
                Headers = new HeaderSet(["List-Unsubscribe", "List-Unsubscribe-Post"]),
            };
            var summaries = (await source.FetchAsync(ids, request, ct)).ToDictionary(s => s.UniqueId.Id);

            return ids.Select(id =>
            {
                if (!summaries.TryGetValue(id.Id, out var s))
                {
                    return new UnsubscribeInfo(source.FullName, id.Id, null, null, false, null, null, null, "Message not found; it may have been moved.");
                }

                var sender = s.Envelope?.From.Mailboxes.FirstOrDefault();
                return UnsubscribeParser.Parse(
                    source.FullName,
                    id.Id,
                    sender?.Address?.ToLowerInvariant(),
                    s.Envelope?.Subject,
                    s.Headers?["List-Unsubscribe"],
                    s.Headers?["List-Unsubscribe-Post"]);
            }).ToList();
        }, ct);

    [McpServerTool(Name = "mail_set_flags",ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = true)]
    [Description("Marks messages read/unread and/or flagged/unflagged.")]
    public Task<object> SetFlags(
        [Description("Folder the messages are in.")] string folder,
        [Description("Message UIDs.")] uint[] uids,
        [Description("true = mark read, false = mark unread, omit to leave unchanged.")] bool? read = null,
        [Description("true = flag, false = unflag, omit to leave unchanged.")] bool? flagged = null,
        CancellationToken ct = default) =>
        mail.RunAsync<object>(async client =>
        {
            var source = await mail.GetFolderAsync(client, folder, ct);
            await source.OpenAsync(FolderAccess.ReadWrite, ct);
            var ids = ToUniqueIds(uids);
            if (read is { } r)
            {
                await source.StoreAsync(ids, new StoreFlagsRequest(r ? StoreAction.Add : StoreAction.Remove, MessageFlags.Seen) { Silent = true }, ct);
            }

            if (flagged is { } f)
            {
                await source.StoreAsync(ids, new StoreFlagsRequest(f ? StoreAction.Add : StoreAction.Remove, MessageFlags.Flagged) { Silent = true }, ct);
            }

            return new { Updated = ids.Count };
        }, ct);

    [McpServerTool(Name = "mail_create_draft", ReadOnly = false, Destructive = false, OpenWorld = true)]
    [Description("Saves a draft to iCloud Drafts. It is NOT sent; the user reviews and sends it from Mail on any device. " +
                 "When replying, pass replyToFolder and replyToUid: threading headers are set, and the From address defaults to the alias the original was sent to.")]
    public Task<object> CreateDraft(
        [Description("Plain-text body.")] string body,
        [Description("Recipients, comma-separated. Optional for replies (defaults to the original sender).")] string? to = null,
        [Description("Subject. Optional for replies (defaults to Re: original subject).")] string? subject = null,
        [Description("CC recipients, comma-separated.")] string? cc = null,
        [Description("From address; must be one of your configured aliases.")] string? from = null,
        [Description("Folder of the message being replied to.")] string? replyToFolder = null,
        [Description("UID of the message being replied to.")] uint? replyToUid = null,
        CancellationToken ct = default) =>
        mail.RunAsync<object>(async client =>
        {
            var options = mail.Options;
            var draft = new MimeMessage();
            MimeMessage? original = null;

            if (replyToUid is { } uid)
            {
                var folder = await mail.GetFolderAsync(client, replyToFolder ?? "inbox", ct);
                await folder.OpenAsync(FolderAccess.ReadOnly, ct);
                original = await GetMessageAsync(folder, uid, ct);
            }

            var fromAddress = from
                ?? (original is null ? null : FindOwnAddress(original.To.Mailboxes.Concat(original.Cc.Mailboxes)))
                ?? options.DefaultFrom
                ?? options.Aliases.FirstOrDefault()
                ?? options.ImapLogin;
            if (options.OwnAddresses.Any() && !options.OwnAddresses.Contains(fromAddress.ToLowerInvariant()))
            {
                throw new McpException($"'{fromAddress}' isn't one of your configured addresses ({string.Join(", ", options.OwnAddresses)}).");
            }

            draft.From.Add(new MailboxAddress(options.DisplayName ?? "", fromAddress));

            if (!string.IsNullOrWhiteSpace(to))
            {
                draft.To.AddRange(InternetAddressList.Parse(to));
            }
            else if (original is not null)
            {
                draft.To.AddRange(original.ReplyTo.Count > 0 ? original.ReplyTo : original.From);
            }
            else
            {
                throw new McpException("A draft needs at least one recipient.");
            }

            if (!string.IsNullOrWhiteSpace(cc))
            {
                draft.Cc.AddRange(InternetAddressList.Parse(cc));
            }

            var originalSubject = original?.Subject ?? "";
            draft.Subject = subject
                ?? (originalSubject.StartsWith("Re:", StringComparison.OrdinalIgnoreCase) ? originalSubject : $"Re: {originalSubject}");

            if (original?.MessageId is { } messageId)
            {
                draft.InReplyTo = messageId;
                foreach (var reference in original.References)
                {
                    draft.References.Add(reference);
                }

                draft.References.Add(messageId);
            }

            // Spark's editor drops line breaks from text/plain drafts, so include an HTML alternative too.
            var text = body.ReplaceLineEndings("\r\n");
            draft.Body = new BodyBuilder { TextBody = text, HtmlBody = ToHtml(text) }.ToMessageBody();

            var drafts = await mail.GetFolderAsync(client, "drafts", ct);
            var newUid = await drafts.AppendAsync(new AppendRequest(draft, MessageFlags.Draft | MessageFlags.Seen), ct);
            return new
            {
                Saved = true,
                Folder = drafts.FullName,
                Uid = newUid?.Id,
                From = draft.From.ToString(),
                To = draft.To.ToString(),
                draft.Subject,
                Note = "Draft saved to iCloud Drafts. Review and send it from Mail.",
            };
        }, ct);

    private async Task<(IMailFolder Folder, List<(MailSummary Summary, IMessageSummary Raw)> Items)> FetchSummariesAsync(
        MailKit.Net.Imap.ImapClient client, string folderName, SearchQuery query, int limit, CancellationToken ct)
    {
        var folder = await mail.GetFolderAsync(client, folderName, ct);
        await folder.OpenAsync(FolderAccess.ReadOnly, ct);
        var uids = await folder.SearchAsync(query, ct);
        var newest = uids.OrderByDescending(u => u.Id).Take(Math.Clamp(limit, 1, 100)).ToList();
        if (newest.Count == 0)
        {
            return (folder, []);
        }

        var request = new FetchRequest(MessageSummaryItems.UniqueId | MessageSummaryItems.Envelope | MessageSummaryItems.Flags | MessageSummaryItems.PreviewText)
        {
            Headers = new HeaderSet(s_scoringHeaders),
        };
        var summaries = await folder.FetchAsync(newest, request, ct);

        var items = summaries
            .Where(s => s.Envelope is not null)
            .Select(s =>
            {
                var envelope = s.Envelope!;
                var sender = envelope.From.Mailboxes.FirstOrDefault();
                var summary = new MailSummary(
                    folder.FullName,
                    s.UniqueId.Id,
                    envelope.Date,
                    envelope.From.ToString(),
                    sender?.Address?.ToLowerInvariant() ?? "",
                    envelope.To.ToString(),
                    envelope.Cc.Count > 0 ? envelope.Cc.ToString() : null,
                    FindOwnAddress(envelope.To.Mailboxes.Concat(envelope.Cc.Mailboxes)),
                    envelope.Subject ?? "",
                    !(s.Flags ?? MessageFlags.None).HasFlag(MessageFlags.Seen),
                    (s.Flags ?? MessageFlags.None).HasFlag(MessageFlags.Flagged),
                    s.PreviewText);
                return (summary, (IMessageSummary)s);
            })
            .OrderByDescending(i => i.summary.Date)
            .ToList();

        return (folder, items);
    }

    private static async Task<MimeMessage> GetMessageAsync(IMailFolder folder, uint uid, CancellationToken ct)
    {
        try
        {
            // MailKit fetches with BODY.PEEK, so reading never flips the message to read.
            return await folder.GetMessageAsync(new UniqueId(uid), ct);
        }
        catch (MessageNotFoundException)
        {
            throw new McpException($"No message with UID {uid} in {folder.FullName}. It may have been moved; search again.");
        }
    }

    private string? FindOwnAddress(IEnumerable<MailboxAddress> recipients)
    {
        var own = mail.Options.OwnAddresses.ToHashSet();
        return recipients.Select(r => r.Address?.ToLowerInvariant()).FirstOrDefault(a => a is not null && own.Contains(a));
    }

    private static SearchQuery BuildQuery(string? from, string? to, string? subject, string? text, int? sinceDays, bool unreadOnly)
    {
        SearchQuery query = SearchQuery.All;
        if (!string.IsNullOrWhiteSpace(from))
        {
            query = query.And(SearchQuery.FromContains(from));
        }

        if (!string.IsNullOrWhiteSpace(to))
        {
            query = query.And(SearchQuery.ToContains(to).Or(SearchQuery.CcContains(to)));
        }

        if (!string.IsNullOrWhiteSpace(subject))
        {
            query = query.And(SearchQuery.SubjectContains(subject));
        }

        if (!string.IsNullOrWhiteSpace(text))
        {
            query = query.And(SearchQuery.BodyContains(text));
        }

        if (sinceDays is > 0)
        {
            query = query.And(SearchQuery.DeliveredAfter(DateTime.Today.AddDays(-sinceDays.Value)));
        }

        if (unreadOnly)
        {
            query = query.And(SearchQuery.NotSeen);
        }

        return query;
    }

    private static List<UniqueId> ToUniqueIds(uint[] uids)
    {
        if (uids is not { Length: > 0 })
        {
            throw new McpException("Pass at least one message UID.");
        }

        return uids.Distinct().Select(u => new UniqueId(u)).ToList();
    }

    private static string HtmlToText(string html)
    {
        var text = HiddenBlocks().Replace(html, " ");
        text = LineBreaks().Replace(text, "\n");
        text = Tags().Replace(text, "");
        text = WebUtility.HtmlDecode(text);
        text = SpaceRuns().Replace(text, " ");
        return BlankLines().Replace(text, "\n\n").Trim();
    }

    // One <div> per line, <div><br></div> for blank lines, the way Apple Mail composes; Spark's editor drops <p> spacing.
    private static string ToHtml(string text)
    {
        var lines = text.Trim().Split("\r\n")
            .Select(line => line.Length == 0 ? "<div><br></div>" : $"<div>{WebUtility.HtmlEncode(line)}</div>");
        return "<html><body>\r\n" + string.Join("\r\n", lines) + "\r\n</body></html>";
    }

    [GeneratedRegex(@"<(script|style|head)[^>]*>.*?</\1>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex HiddenBlocks();

    [GeneratedRegex(@"<(br|/p|/div|/tr|/li|/h\d)[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex LineBreaks();

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex Tags();

    [GeneratedRegex(@"[ \t ]+")]
    private static partial Regex SpaceRuns();

    [GeneratedRegex(@"\s*\n\s*\n\s*")]
    private static partial Regex BlankLines();
}
