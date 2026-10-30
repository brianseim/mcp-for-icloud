using System.Net.Sockets;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MailKit.Security;
using ModelContextProtocol;

namespace McpForICloud.Mail;

/// <summary>
/// Owns one long-lived IMAP connection to iCloud and serializes access to it,
/// because an IMAP connection can only have one folder selected at a time.
/// </summary>
public sealed class MailService(McpForICloudOptions options, CredentialProvider credentials) : IAsyncDisposable
{
    private static readonly TimeSpan s_knownSendersTtl = TimeSpan.FromMinutes(30);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private ImapClient? _client;
    private HashSet<string>? _knownSenders;
    private DateTimeOffset _knownSendersLoadedAt;

    public McpForICloudOptions Options => options;

    public async Task<T> RunAsync<T>(Func<ImapClient, Task<T>> action, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    var client = await GetClientAsync(ct);
                    return await action(client);
                }
                catch (Exception ex) when (attempt == 0 && IsConnectionFailure(ex))
                {
                    // iCloud drops idle connections; reconnect once and retry.
                    await DropClientAsync();
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IMailFolder> GetFolderAsync(ImapClient client, string name, CancellationToken ct)
    {
        switch (name.Trim().ToLowerInvariant())
        {
            case "inbox":
                return client.Inbox;
            case "junk":
            case "spam":
                return await SpecialAsync(SpecialFolder.Junk, "Junk");
            case "sent":
                return await SpecialAsync(SpecialFolder.Sent, "Sent Messages");
            case "drafts":
                return await SpecialAsync(SpecialFolder.Drafts, "Drafts");
            case "trash":
            case "deleted":
                return await SpecialAsync(SpecialFolder.Trash, "Deleted Messages");
            case "archive":
                return await SpecialAsync(SpecialFolder.Archive, "Archive");
        }

        try
        {
            return await client.GetFolderAsync(name, ct);
        }
        catch (FolderNotFoundException)
        {
            throw new McpException($"Mail folder '{name}' was not found. Use mail_list_folders to see available folders.");
        }

        async Task<IMailFolder> SpecialAsync(SpecialFolder special, string fallbackName)
        {
            if (client.Capabilities.HasFlag(ImapCapabilities.SpecialUse) && client.GetFolder(special) is { } folder)
            {
                return folder;
            }

            return await client.GetFolderAsync(fallbackName, ct);
        }
    }

    /// <summary>
    /// Addresses you've written to in the last year.
    /// Someone you've emailed before is the strongest signal that a message in Junk is misfiled.
    /// </summary>
    public async Task<HashSet<string>> GetKnownCorrespondentsAsync(ImapClient client, CancellationToken ct)
    {
        if (_knownSenders is not null && DateTimeOffset.UtcNow - _knownSendersLoadedAt < s_knownSendersTtl)
        {
            return _knownSenders;
        }

        var sent = await GetFolderAsync(client, "sent", ct);
        await sent.OpenAsync(FolderAccess.ReadOnly, ct);
        var uids = await sent.SearchAsync(SearchQuery.DeliveredAfter(DateTime.Today.AddDays(-365)), ct);
        var recent = uids.OrderByDescending(u => u.Id).Take(3000).ToList();

        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (recent.Count > 0)
        {
            var summaries = await sent.FetchAsync(recent, new FetchRequest(MessageSummaryItems.Envelope), ct);
            foreach (var envelope in summaries.Select(s => s.Envelope).OfType<Envelope>())
            {
                foreach (var address in envelope.To.Mailboxes.Concat(envelope.Cc.Mailboxes))
                {
                    known.Add(address.Address);
                }
            }
        }

        _knownSenders = known;
        _knownSendersLoadedAt = DateTimeOffset.UtcNow;
        return known;
    }

    public async ValueTask DisposeAsync()
    {
        await DropClientAsync();
        _gate.Dispose();
    }

    private async Task<ImapClient> GetClientAsync(CancellationToken ct)
    {
        if (_client is { IsConnected: true, IsAuthenticated: true })
        {
            return _client;
        }

        await DropClientAsync();
        var credential = credentials.Get();
        var client = new ImapClient();
        try
        {
            await client.ConnectAsync(options.ImapHost, options.ImapPort, SecureSocketOptions.SslOnConnect, ct);
            await client.AuthenticateAsync(options.ImapLogin is { Length: > 0 } login ? login : credential.UserName, credential.Password, ct);
        }
        catch (AuthenticationException)
        {
            client.Dispose();
            throw new McpException(
                "iCloud Mail rejected the login. Use an app-specific password (not your Apple ID password), " +
                "and set MailUser in config.json to your @icloud.com/@me.com address if your Apple ID is a non-Apple email.");
        }
        catch
        {
            client.Dispose();
            throw;
        }

        _client = client;
        return client;
    }

    private async Task DropClientAsync()
    {
        if (_client is null)
        {
            return;
        }

        try
        {
            if (_client.IsConnected)
            {
                await _client.DisconnectAsync(true);
            }
        }
        catch
        {
            // The connection is already broken; nothing to clean up on the server.
        }

        _client.Dispose();
        _client = null;
    }

    private static bool IsConnectionFailure(Exception ex) =>
        ex is IOException or SocketException or ServiceNotConnectedException or ImapProtocolException;
}
