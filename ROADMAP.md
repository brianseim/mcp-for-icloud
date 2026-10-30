# Roadmap

What's planned next, roughly in priority order.
Suggestions and pull requests are welcome.

## Next

- [ ] **Automated tests.** Unit tests for triage and spam scoring, unsubscribe-header parsing, iCalendar line folding and escaping, and time-range parsing.
- [ ] **CI.** A GitHub Actions workflow that builds and runs the tests on every pull request, required by branch protection.
- [ ] **Keychain / libsecret credentials.** Read the app-specific password from the macOS Keychain and Linux Secret Service instead of plain-text environment variables.
- [ ] **Configurable scoring weights.** Move the hard-coded triage and spam-rescue points into `config.json`.

## Later

- [ ] **Event invitations.** Create events with attendees, with an explicit confirmation step because iCloud emails the invites.
- [ ] **Single-occurrence edits.** Change or cancel one instance of a recurring event, and move recurring series in time.
- [ ] **Contacts (CardDAV).** Look up people and use contact groups as VIP lists.
- [ ] **Remote MCP server.** An optional HTTP transport with OAuth so web and mobile MCP clients can use it.
- [ ] **Packaging.** Published release binaries and a `dotnet tool` package so setup doesn't require building from source.

## Housekeeping

- [ ] **Drop pre-release name fallbacks** (`AiVa-iCloud` credential, `%APPDATA%\AiVa`, `AIVA_*` variables) in 2.0.
