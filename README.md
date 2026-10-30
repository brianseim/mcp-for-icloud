# MCP for iCloud — iCloud Mail & Calendar MCP server

A local [Model Context Protocol](https://modelcontextprotocol.io) (MCP) server that gives any MCP-capable AI assistant access to your iCloud Mail and Calendar: triage your inbox (including custom-domain aliases), rescue misfiled Junk, save reply drafts, and manage your calendar.

It talks to iCloud directly over IMAP (`imap.mail.me.com`) and CalDAV (`caldav.icloud.com`) using an **app-specific password**, and runs on your own computer as a stdio MCP server launched by your MCP client. Nothing is hosted anywhere.

> **Independent project.** MCP for iCloud is not affiliated with, endorsed by, or sponsored by Apple Inc. Use it at your own risk; see [Disclaimer](#disclaimer).

## Who it's for

People who live in iCloud Mail and Calendar, often with custom-domain aliases, and want an AI assistant that can actually work in them: sorting the inbox each morning, catching real mail that landed in Junk, drafting replies in their voice, and planning the week around their calendar.
iCloud has no official MCP integration, so this fills that gap without handing your account to a hosted service: everything runs locally.

It works with any MCP client that supports local (stdio) servers, such as Claude Desktop, VS Code with GitHub Copilot, Cursor, Windsurf, Cline or Continue.

## Built with

- **C# / .NET 10**, built as a single command-line executable.
- **[ModelContextProtocol](https://github.com/modelcontextprotocol/csharp-sdk)**, the official MCP C# SDK, over the stdio transport.
- **[MailKit](https://github.com/jstedfast/MailKit)** for iCloud Mail over IMAP.
- **[Ical.Net](https://github.com/ical-org/ical.net)** plus a small hand-written CalDAV client for iCloud Calendar.
- **Windows Credential Manager** for the app-specific password (environment variables elsewhere).

```text
src/McpForICloud/
  Program.cs                 host + MCP server registration
  McpForICloudOptions.cs     config.json loading, priority rules
  CredentialProvider.cs      Credential Manager / env var lookup
  Mail/                      IMAP connection, tools, triage and spam scoring, unsubscribe parsing
  Calendar/                  CalDAV client and calendar tools
```

## What it can do

| Tool | What it does | Changes anything? |
| --- | --- | --- |
| `mail_triage` | Pre-ranks recent inbox mail with reasons (VIPs, people you've emailed, alias weights, urgent keywords, bulk mail) | No |
| `mail_review_spam` | Scores Junk by how likely it's legitimate (known correspondents, VIPs, trusted domains, thread replies, SPF/DKIM/DMARC) | No |
| `mail_search`, `mail_read`, `mail_list_folders` | Find and read mail without marking it read | No |
| `mail_create_draft` | Saves a draft to iCloud Drafts, threaded and from the right alias | Adds a draft |
| `mail_mark_not_spam` | Moves Junk → Inbox and tags it Not Junk | Yes |
| `mail_move`, `mail_set_flags` | Archive/move (never to Trash), read/unread, flag | Yes |
| `mail_delete` | Moves a batch of messages, from any folders, to Trash in one call. Never empties Trash | Yes (marked destructive) |
| `mail_unsubscribe_info` | Reads List-Unsubscribe headers and returns the one-click URL, web link or mailto. Never unsubscribes | No |
| `calendar_list_calendars`, `calendar_list_events`, `calendar_find_free_time` | Read your schedule, expand recurring events, find open slots in working hours | No |
| `calendar_create_event`, `calendar_update_event` | Create/edit events (no invitations are sent); new events can repeat daily/weekly/monthly/yearly or follow an RRULE | Yes |
| `calendar_delete_event` | Delete an event (whole series if recurring) | Yes (marked destructive) |

**There is deliberately no send tool and no permanent delete.** Drafts land in iCloud Drafts and you send them from Mail on any device. `mail_delete` only moves mail to Trash, where it stays recoverable until iCloud empties it.

Every tool carries MCP annotations (`readOnlyHint`, `destructiveHint`), so clients that support them can ask before running anything that changes your mail or calendar.

## Setup (about 10 minutes)

### Prerequisites
- Windows (credentials are read from Credential Manager). macOS and Linux work too; see [macOS and Linux](#macos-and-linux).
- [.NET 10 SDK](https://dotnet.microsoft.com/download).
- An MCP client that supports local stdio servers.

### 0. Build it

```bash
git clone https://github.com/brianseim/mcp-for-icloud.git
```

```bash
dotnet publish mcp-for-icloud/src/McpForICloud/McpForICloud.csproj -c Release -o mcp-for-icloud/publish
```

### 1. Create an app-specific password
1. Go to <https://account.apple.com> → **Sign-In and Security** → **App-Specific Passwords**.
2. Generate one named `MCP for iCloud`. Keep the page open.

### 2. Store it in Windows Credential Manager
In a terminal, run this and paste the password when prompted (it isn't echoed or saved to any file):

```bash
cmdkey /generic:mcp-for-icloud /user:you@icloud.com /pass
```

Use the Apple ID you sign in to iCloud with as `/user`.

### 3. Create your config
Copy `config.example.json` to `%APPDATA%\mcp-for-icloud\config.json` and fill in your Apple ID, every alias you receive mail at, time zone, working hours and VIPs.

If your Apple ID is not an @icloud.com/@me.com address (e.g. it's a Gmail address), also set `MailUser` to your @icloud.com address — that's the IMAP login.

### 4. Register it with your MCP client
The server is a plain stdio executable with no arguments: point your client at the full path of `publish\mcp-for-icloud.exe`.
Restart the client after changing its configuration.

**Claude Desktop, Cursor, Windsurf, Cline** and most other clients use an `mcpServers` object (Claude Desktop: **Settings → Developer → Edit Config**; Cursor: `~/.cursor/mcp.json`):

```json
{
  "mcpServers": {
    "mcp-for-icloud": {
      "command": "C:\\path\\to\\mcp-for-icloud\\publish\\mcp-for-icloud.exe"
    }
  }
}
```

**VS Code (GitHub Copilot agent mode)** uses `.vscode/mcp.json` in a workspace, or your user `mcp.json`:

```json
{
  "servers": {
    "mcp-for-icloud": {
      "type": "stdio",
      "command": "C:\\path\\to\\mcp-for-icloud\\publish\\mcp-for-icloud.exe"
    }
  }
}
```

### 5. Give your assistant working rules
Paste [`docs/example-assistant-instructions.md`](docs/example-assistant-instructions.md) into your client's custom instructions (a project, custom mode, rules file or system prompt, depending on the client) and edit the priorities section.
If your client lets you, require approval before tools that change things run.

### 6. Try it
- "What needs my attention in email today?"
- "Check my spam for anything real from the last two weeks."
- "Draft a reply to the latest email from Dana saying Thursday works."
- "Find me a free hour next Tuesday afternoon and hold it for proposal review."

## Adding another computer

Each machine runs its own copy of the server; they all talk to the same iCloud account.

1. Install the [prerequisites](#prerequisites), then clone and build ([step 0](#0-build-it)).
2. Create a **separate app-specific password** for this machine (e.g. `MCP for iCloud – laptop`), so you can revoke one device without breaking the others, and store it as in [step 2](#2-store-it-in-windows-credential-manager).
3. Copy `%APPDATA%\mcp-for-icloud\config.json` from your existing machine instead of writing a new one.
4. Register the server with your MCP client ([step 4](#4-register-it-with-your-mcp-client)) using this machine's `publish` path, then restart the client.
5. Ask your assistant to "list my calendars" to confirm the connection.
6. If you'll change code on this machine, enable the [pre-push hook](#contributing-workflow).

### macOS and Linux

Credential Manager is Windows-only, so pass the login through environment variables in your client's server entry instead.
This stores the app-specific password in plain text in that file; it's limited to mail, calendar and contacts and can be revoked at any time.

```json
"mcp-for-icloud": {
  "command": "/Users/you/source/mcp-for-icloud/publish/mcp-for-icloud",
  "env": {
    "MCP_FOR_ICLOUD_USER": "you@icloud.com",
    "MCP_FOR_ICLOUD_APP_PASSWORD": "xxxx-xxxx-xxxx-xxxx"
  }
}
```

The config file lives at `~/.config/mcp-for-icloud/config.json`.

## Updating after code changes

Stop your MCP client first (fully quit it), or the running server will lock the files. Then, from the repo root:

```bash
git pull
```

```bash
dotnet publish src/McpForICloud/McpForICloud.csproj -c Release -o publish
```

Start the client again to load the new version.

### Upgrading from pre-release builds

Early builds used different names.
They are still read when the new ones don't exist, so an existing setup keeps working: the `AiVa-iCloud` credential, `%APPDATA%\AiVa\config.json` and `AIVA_*` environment variables.
The old `AiVa.Mcp.exe` is no longer built, so point your client at `publish\mcp-for-icloud.exe`.
To move to the new names, run the `cmdkey` command from [step 2](#2-store-it-in-windows-credential-manager), move the config to `%APPDATA%\mcp-for-icloud\config.json`, then remove the old credential:

```bash
cmdkey /delete:AiVa-iCloud
```

## Contributing workflow

`main` is protected on GitHub and only changes through pull requests.
A versioned pre-push hook also stops direct pushes to `main` locally, before GitHub would reject them.
Enable it once per clone:

```bash
git config core.hooksPath .githooks
```

Then work on a branch, push it, and open a PR with `gh pr create`; merge with `gh pr merge` or on GitHub.

Planned work is tracked in [ROADMAP.md](ROADMAP.md).

## Notes and limits
- This is a local stdio server. Web and mobile chat apps that can only reach remote MCP servers can't use it; that would need it hosted as a remote MCP server with auth (see the [roadmap](ROADMAP.md)).
- Recurring events: titles/locations/notes can be edited (whole series); time changes are left to the Calendar app.
- Creating events with invitees isn't supported yet; your assistant can draft the invite email instead.
- Priority scores are hints with reasons, not verdicts. Tune them in `config.json` → `Priority`.
- Overrides: `MCP_FOR_ICLOUD_CONFIG` (config path), `MCP_FOR_ICLOUD_USER` / `MCP_FOR_ICLOUD_APP_PASSWORD` (if you'd rather not use Credential Manager).

## Disclaimer

**Use at your own risk.**
This software is provided "as is", without warranty of any kind, as stated in the [license](LICENSE).
It reads and changes your real email and calendar on your behalf, driven by an AI model that can misunderstand instructions or make mistakes.

- Review every draft before you send it, and every proposed move, flag or calendar change before you approve it.
- Keep your MCP client set to ask before running tools that change things, if it supports that.
- Mail moved between folders and calendar events that are edited or deleted may not be recoverable; keep the backups you'd want anyway (for example iCloud's own data recovery).
- Your app-specific password grants access to your iCloud Mail, Contacts and Calendars. Store it only as described above, and revoke it at <https://account.apple.com> if a device is lost or you stop using this tool.
- Message and calendar content the server returns is passed to whichever AI model your MCP client uses, under that provider's terms and privacy policy.
- Apple may change or restrict iCloud's IMAP or CalDAV access at any time, which can break this tool without notice.

The author is not liable for lost or altered data, missed messages or appointments, or any other damages arising from use of this software.

### Trademarks

Apple, iCloud, and iCloud Mail are trademarks of Apple Inc., registered in the U.S. and other countries.
They are used here only to describe the service this software works with.
MCP for iCloud is an independent project and is not affiliated with, endorsed by, or sponsored by Apple Inc.
Other product names mentioned here are trademarks of their respective owners and are used only to identify compatible clients.

## License

Copyright (c) 2026 Brian Seim.
Released under the [MIT License](LICENSE).
