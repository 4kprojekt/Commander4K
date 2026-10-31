# Mail Plugin Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** An official catalog plugin `official.mail` (PowerShell) keeps own-IMAP mailboxes as `.eml` files in folders that mirror the server. It syncs them in the background and shows each message with Reply / Reply all / Forward / Archive / Delete actions. You write replies in the preview and send them over SMTP. Each account is a card on the `Poczta/` dashboard.

**Architecture:**
- The host does all mail protocol and MIME work. `Core/Mail` uses MailKit/MimeKit and exposes it to plugins as `IPluginHost.Mail` (`IMailHost`): gated by a new `mail` permission, audited, and the plugin never sees a password.
- The rest is generic, not mail-specific, so other plugins can use it:
  - a background tick service (`background` permission, `contributes.background`, `background.tick`);
  - preview actions and forms (`ActionsPreview`, `FormPreview`, `preview.action`, `preview.formChanged`);
  - Enter-opens-pinned-preview for files (`contributes.previews[].enter`).
- The plugin stays thin. It decides what to sync and when, renames files for read state, writes `.mail/state.json`, and drives the UI.

**Tech Stack:** .NET 10, MailKit (brings MimeKit), Avalonia 12 + Avalonia.Markup.Declarative (C# only), System.Text.Json, xunit.v3, PowerShell 7 for the plugin, docker `greenmail/standalone` for integration tests.

**Spec:** `docs/superpowers/specs/2026-10-31-mail-plugin-design.md` (Polish). Read it before every task; this plan does not repeat its rationale.

**Prerequisite:** `docs/superpowers/plans/2026-10-31-dashboard-cards.md` is fully implemented: `IPreviewCardPlugin`, external `preview.card`, `CardResolver`, `SecretStore`, `DashboardView`, `FolderConfigWriter`. Task 9 builds on it. Tasks 1–8 do not.

**Commits:** the working tree may hold unrelated uncommitted work in files this plan touches. Treat "Commit" steps as checkpoints: run the build and tests, and commit only this task's files (`git commit -- <paths>`).

## Global Constraints

- The password never reaches `c4k.dir`, plugin `storage`, the plugin process or the audit log.
  - Secret names: `mail:<plugin id>:<user>@<imap host>`, bound to the IMAP host, and `mail-smtp:<plugin id>:<user>@<smtp host>`, bound to the SMTP host. Both are stored in `SecretStore`.
  - An interactive login stores both at once.
  - A changed host means a missing secret, so the app asks again.
- TLS is mandatory:
  - port 993 or 465 = implicit TLS (`SslOnConnect`);
  - any other port = `StartTls` (required, not "when available");
  - plain-text authentication is never attempted.
  - A certificate that fails validation is trusted only after the user confirms its SHA-256 fingerprint. Store the trust in `config/mail_certificates.json`, host:port → fingerprint, like `KnownHostsStore`.
- Interactive vs background:
  - `MailAccount.Interactive = false` never shows a dialog. A missing password, refused login or untrusted certificate throws `MailAuthenticationRequiredException` / `MailCertificateUntrustedException`.
  - After an auth failure the plugin stops background syncs for that account until an interactive call succeeds.
- Limits:
  - at most **2** IMAP connections per account, idle-closed after **5 min**;
  - **30 s** timeout per operation (fetch/send: 30 s + 1 s per 100 KB);
  - every operation can be cancelled.
- Background ticks:
  - minimum `interval` **1 min**;
  - one tick per plugin at a time;
  - `{ "more": true }` → next tick in **2 s**;
  - tick timeout **60 s**;
  - none while the setting `PluginBackground` is off or the plugin lacks the approved `background` permission;
  - shutdown waits at most **2 s**.
- Plugin batching: one tick does at most **50** fetches or **15 s** of work, then returns `more: true`.
- Account `refresh` minimum **1 min**; missing/`false` = only on open + „Sprawdź teraz”.
- `since`:
  - `"<n>d" | "<n>m" | "<n>y" | "YYYY-MM-DD"`;
  - empty or missing = **3 months**.
  - Files never get deleted for falling outside `since`.
- Only explicit preview actions (Delete, Archive, Move, Mark unread, Send, opening with Enter → `\Seen`) change the server. Sync never infers a server operation from a missing file; it downloads the file again.
- Viewing a message never loads anything from the network. HTML becomes plain text with links. Links go through the existing tile-target confirmation path.
- File names:
  - read message: `yyyy-MM-dd HHmm <sender> - <subject> [uid <N>].eml`;
  - unread: the prefix `● ` (U+25CF + space);
  - flagged state lives only in `state.json`;
  - folder `.mail/` is Hidden.
- Special folders get localized names through `Loc.T`: INBOX → „Odebrane”, `\Sent` → „Wysłane”, `\Drafts` → „Szkice”, `\Trash` → „Kosz”, `\Archive` → „Archiwum”, `\Junk` → „Spam”. Without SPECIAL-USE, fall back by name: Sent / Sent Items / Sent Messages, Drafts, Trash / Deleted Items, Archive, Junk / Spam (case-insensitive).
- One computer per account folder: `.mail/lock` holds `{ machine, lastTick }`. Another machine sees a lock younger than **10 min** and skips sync; its card shows „synchronizuje <komputer>”.
- UI text goes through `Loc.T`/`Loc.P` with `en.json` entries. Not translated: the plugin's own text (the SDK cannot reference Core) and audit entries.
- Colours come only from `SkinBrushes`; layouts work in modern and retro skins. No `.axaml`. Public events take a single argument.
- New protocol surface goes into:
  - `IPluginHost` + `ScopedPluginHost`, gated and audited;
  - `ExternalPlugin.HandleRequestAsync` (plugin → host) or the adapters (host → plugin);
  - all three SDK clients, then copies in every example and `plugins-catalog` plugin (identity test);
  - the `docs/plugins.md` SDK table;
  - `docs/plugin.schema.json`.
  After changing a catalog plugin: `pwsh ./plugins-catalog/Update-Catalog.ps1`.
- New setting → `AppSettings` + `SettingsCatalog.Create`. New command → `CommandIds` + `RegisterCommands`.
- Expected exceptions (cancellation, timeout, auth, socket errors) are caught in our code and never escape `Task.Run` lambdas or the background thread.
- Package versions are pinned in `Directory.Packages.props`. `PackageReference` items carry no `Version`.
- Build: `dotnet build Commander4K.sln` (warnings are errors). Tests: `dotnet test --solution Commander4K.sln`. Single test: `dotnet test --solution Commander4K.sln -- --filter-method "*Name*"`.
- Integration server:
  - `docker run -d --name c4k-mail -p 3025:3025 -p 3143:3143 -p 3465:3465 -p 3993:3993 -e GREENMAIL_OPTS="-Dgreenmail.setup.test.all -Dgreenmail.hostname=0.0.0.0 -Dgreenmail.users=test:test@localhost" greenmail/standalone` (pin a current 2.x tag);
  - then `C4K_TEST_IMAP=imaps://test:test@127.0.0.1:3993` and `C4K_TEST_SMTP=smtps://127.0.0.1:3465`;
  - GreenMail's certificate is self-signed, so tests trust it through the certificate callback.

## Review Focus

1. **A server that hangs or drops mid-tick.** The tick times out, the UI never freezes, and the account card shows the error and the last successful check. Next tick retries. Test: Task 5 `Tick_that_hangs_is_abandoned_and_next_tick_runs`, and Task 3 `Operation_times_out_on_silent_server`.
2. **The first sync of a large mailbox.** Previews, cards and Send stay responsive while there is a backlog, because each tick is bounded and returns `more`. Test: Task 7 `First_sync_is_batched_and_resumes`.
3. **Hostile HTML mail:** remote images, `<script>`, tracking pixels, `javascript:` and `data:` links, CSS. The text output contains no fetchable resource and no non-http(s)/mailto link. Test: Task 2 `HtmlText_drops_scripts_images_and_unsafe_links`.
4. **Awkward subjects and senders:** illegal characters, encoded words, emoji, empty subject, 500-character subject, two messages in the same minute from the same sender. Names must be valid on Windows, unique and short enough. Test: Task 2 `FileName_is_valid_unique_and_bounded`.
5. **A password changed on the server while the app runs in the background.** No dialog pops up, there is no retry loop every minute that could lock the account, and the card says „wymaga logowania”. Test: Task 3 `Background_call_without_password_throws_without_prompt`, and Task 7 `Auth_failure_pauses_account_until_interactive_login`.

---

### Task 1: SDK contracts (mail host, background, preview actions/forms, Enter-to-preview)

**Files:**
- Modify: `src/Commander4K.PluginSdk/PluginManifest.cs` (permissions `Mail = "mail"`, `Background = "background"`, both in `All` and `Describe`: „poczta (IMAP/SMTP) przez aplikację”, „praca w tle”; `BackgroundContribution`; `PreviewContribution.Enter`; `PluginContributions.Background`)
- Create: `src/Commander4K.PluginSdk/IMailHost.cs`
- Modify: `src/Commander4K.PluginSdk/IPluginHost.cs` (add `IMailHost Mail { get; }`; add `string? target = null` to `Notify`)
- Modify: `src/Commander4K.PluginSdk/IPreviewPlugin.cs` (`PreviewAction`, `ActionsPreview`, `FormField`, `FormFile`, `FormPreview`, `IInteractivePreviewPlugin`)
- Modify: `src/Commander4K.Core/Plugins/ManifestReader.cs`, `docs/plugin.schema.json`
- Test: `tests/Commander4K.Core.Tests/ManifestReaderTests.cs` (create if absent; otherwise extend the existing manifest tests)

**Interfaces:**
- Produces (all in namespace `Commander4K.PluginSdk`):
  ```csharp
  public sealed record BackgroundContribution(string Interval);           // "1m", "5m"…; host clamps to ≥ 1 min
  public sealed record PreviewContribution(..., bool Card = false, bool Enter = false); // Enter: Enter on a matching file opens the pinned preview; Shift+Enter keeps the shell open
  public sealed record PluginContributions(..., BackgroundContribution? Background = null);

  /// "imap.host:993", "smtp.host:465" (null = cannot send), user; Interactive=false never shows dialogs.
  public sealed record MailAccount(string Imap, string? Smtp, string User, bool Interactive = true);
  [Flags] public enum MailFlags { None = 0, Seen = 1, Flagged = 2, Answered = 4, Draft = 8 }
  public enum MailSpecialFolder { None, Inbox, Sent, Drafts, Trash, Archive, Junk }
  public sealed record MailFolderInfo(string ServerName, string LocalName, char Separator, MailSpecialFolder Special, uint UidValidity, int Count, int Unread);
  public sealed record MailSyncRequest(string Folder, uint UidValidity, uint LastUid, ulong ModSeq, DateTimeOffset Since, IReadOnlyList<uint> KnownUids);
  public sealed record MailHeader(uint Uid, DateTimeOffset Date, string From, string Subject, MailFlags Flags, long Size, string FileName);
  public sealed record MailSyncResult(uint UidValidity, bool UidValidityChanged, ulong ModSeq, IReadOnlyList<MailHeader> New, IReadOnlyList<(uint Uid, MailFlags Flags)> FlagChanges, IReadOnlyList<uint> Removed);
  public sealed record MailAddress(string? Name, string Address);
  public sealed record MailAttachment(int Index, string FileName, string ContentType, long Size);
  public sealed record MailMessageView(MailAddress? From, IReadOnlyList<MailAddress> To, IReadOnlyList<MailAddress> Cc, string Subject, DateTimeOffset? Date,
      string? MessageId, IReadOnlyList<string> References, string BodyText, IReadOnlyList<MailAttachment> Attachments);
  public enum MailReplyMode { Reply, ReplyAll, Forward }
  public sealed record MailDraft(IReadOnlyList<MailAddress> To, IReadOnlyList<MailAddress> Cc, IReadOnlyList<MailAddress> Bcc, string Subject, string Body,
      string? InReplyTo, IReadOnlyList<string> References, IReadOnlyList<string> AttachmentPaths, MailAddress? From = null);

  public interface IMailHost   // every member requires PluginPermissions.Mail (+ the fs permission noted)
  {
      Task<IReadOnlyList<MailFolderInfo>> FoldersAsync(MailAccount account, CancellationToken ct = default);
      Task<MailSyncResult> SyncAsync(MailAccount account, MailSyncRequest request, CancellationToken ct = default);
      Task<long> FetchAsync(MailAccount account, string folder, uint uid, string targetPath, CancellationToken ct = default);          // + fs.write; bytes written
      Task SetFlagsAsync(MailAccount account, string folder, IReadOnlyList<uint> uids, MailFlags add, MailFlags remove, CancellationToken ct = default);
      Task MoveAsync(MailAccount account, string folder, IReadOnlyList<uint> uids, string targetFolder, CancellationToken ct = default);
      Task DeleteAsync(MailAccount account, string folder, IReadOnlyList<uint> uids, CancellationToken ct = default);                   // to Trash; in Trash = expunge
      Task<uint?> AppendAsync(MailAccount account, string folder, string path, MailFlags flags, CancellationToken ct = default);       // + fs.read; new UID when the server reports it
      Task<MailMessageView> ParseAsync(string path, CancellationToken ct = default);                                                   // + fs.read
      Task SaveAttachmentAsync(string path, int index, string targetPath, CancellationToken ct = default);                             // + fs.read, fs.write
      Task<MailDraft> CreateReplyAsync(string path, MailReplyMode mode, string selfAddress, CancellationToken ct = default);           // + fs.read
      Task ComposeAsync(string path, MailDraft draft, CancellationToken ct = default);                                                 // + fs.write (+ fs.read for attachments)
      Task<string> SendAsync(MailAccount account, string draftPath, CancellationToken ct = default);                                   // + fs.read; returns Message-ID; appends to Sent
  }
  public sealed class MailAuthenticationRequiredException(string realm) : UnauthorizedAccessException(...) { public string Realm { get; } }
  public sealed class MailCertificateUntrustedException(string host, string fingerprint) : System.Security.Authentication.AuthenticationException(...) { ... }

  public sealed record PreviewAction(string Label, string Target, string? Shortcut = null);   // Target: like CardAction, plus "c4k:plugin/<action>[?k=v&…]"
  public sealed record ActionsPreview(PreviewContent Content, IReadOnlyList<PreviewAction> Actions) : PreviewContent;
  public sealed record FormField(string Id, string Label, string Value, bool Multiline = false);
  public sealed record FormFile(string Name, string Path, long Size);
  public sealed record FormPreview(string Title, IReadOnlyList<FormField> Fields, IReadOnlyList<FormFile> Files, IReadOnlyList<PreviewAction> Buttons, string? Message = null, bool MessageIsError = false) : PreviewContent;
  public sealed record PreviewActionRequest(VfsEntry Entry, string? OptionsJson, string Action, IReadOnlyDictionary<string, string> Arguments, IReadOnlyDictionary<string, string>? FormValues);
  public interface IInteractivePreviewPlugin : IPreviewPlugin
  {
      /// Result: new content to show, or null = render the entry again.
      Task<PreviewContent?> InvokeActionAsync(PreviewActionRequest request, IPluginHost host, CancellationToken ct);
      Task FormChangedAsync(VfsEntry entry, string? optionsJson, IReadOnlyDictionary<string, string> values, IPluginHost host, CancellationToken ct);
  }
  public static class PreviewActionTargets { public const string PluginPrefix = "c4k:plugin/"; public static bool TryParse(string target, out string action, out IReadOnlyDictionary<string, string> arguments); }
  ```

- [ ] **Step 1: Write failing tests**
  - `Manifest_reads_background_and_preview_enter`: a manifest with `"contributes": { "background": { "interval": "5m" }, "previews": [{ "id": "eml", "title": "Mail", "extensions": [".eml"], "enter": true }] }` and `"permissions": ["mail","background"]` parses. Expected values: `Background.Interval == "5m"`, `Previews[0].Enter == true`, `Allows("mail") && Allows("background")`.
  - `Manifest_rejects_unknown_permission_still`: `"permissions": ["mail.raw"]` is still refused, as today.
  - `PreviewActionTargets_parses_action_and_arguments`: `"c4k:plugin/check?account=C%3A%5CPoczta%5CFirma&x=1"` gives action `check` and arguments `{ account = @"C:\Poczta\Firma", x = "1" }`. `"c4k:refresh"` gives false.
- [ ] **Step 2: Run `dotnet test --solution Commander4K.sln -- --filter-method "*Manifest_reads_background*"`**. Expected: FAIL to compile.
- [ ] **Step 3: Implement the records, the interfaces, `ManifestReader` parsing and the schema.** Schema details:
  - add `mail` and `background` to the permissions enum;
  - add the object `contributes.background` with a string `interval`;
  - add the boolean `enter` to preview entries.
  `ScopedPluginHost` gets a temporary `Mail => throw new NotSupportedException()` so the build stays green; Task 4 replaces it. `Notify(message, isError, target)` passes `target` through `IPluginUi.Notify(string message, bool isError, string? target)`. Update every `IPluginUi` implementation. The App implementation makes a notification with a target clickable the same way the update notice at `MainWindow.cs:373` is, and the click goes through the tile-target path.
- [ ] **Step 4: Run the three tests and the full suite.** Expected: PASS.
- [ ] **Step 5: Commit** `feat(sdk): mail host, background and interactive preview contracts`.

### Task 2: Core mail helpers (pure, no network)

**Files:**
- Modify: `Directory.Packages.props` (`MailKit`, latest stable 4.x; check `dotnet package search MailKit`), `src/Commander4K.Core/Commander4K.Core.csproj`
- Create: `src/Commander4K.Core/Mail/MailEndpoints.cs`, `MailFileNames.cs`, `HtmlText.cs`, `MailDrafts.cs`, `MailSince.cs`
- Test: `tests/Commander4K.Core.Tests/MailHelpersTests.cs`

**Interfaces:**
- Produces:
  - `MailEndpoint MailEndpoints.Parse(string hostPort, int defaultPort)`, where `record MailEndpoint(string Host, int Port, MailKit.Security.SecureSocketOptions Security)`. Ports 993 and 465 give `SslOnConnect`; anything else gives `StartTls`. A missing port uses `defaultPort`. Spaces, a scheme or an empty host throw `FormatException`.
  - `string MailFileNames.For(DateTimeOffset date, string from, string subject, uint uid)`. The read form, without `● `. The sender part is the display name, or the address local part when there is no name. Invalid filename characters become `_`. Subject and sender are collapsed to single spaces, the sender is trimmed to 40 characters and the subject to 80. An empty subject becomes `(bez tematu)` via `Loc.T`.
  - `const string MailFileNames.UnreadPrefix = "● "`. `bool MailFileNames.TryParseUid(string fileName, out uint uid)` reads the `[uid N]` suffix.
  - `string MailFileNames.LocalFolderName(string serverName, char separator, MailSpecialFolder special)` returns the localized special name or the server leaf, with invalid characters replaced. The nested path joins the leaves with `\`.
  - `MailSpecialFolder MailFileNames.Classify(string serverName, MailKit.FolderAttributes attributes)` applies the SPECIAL-USE flags, then the name fallback from Global Constraints.
  - `string HtmlText.ToText(string html)`, built on `MimeKit.Text.HtmlTokenizer`. Block tags become newlines. A link becomes `text (url)` only for `http`/`https`/`mailto`; any other scheme keeps the text alone. `script`/`style`/`head` content is dropped. An image becomes `[obraz: alt]` and its `src` is never emitted. Entities are decoded.
  - `MailDraft MailDrafts.CreateReply(MimeKit.MimeMessage original, MailReplyMode mode, string selfAddress)`:
    - Re:/Fwd: prefixes are not doubled (`Re: Re: x` → `Re: x`);
    - ReplyAll takes To+Cc minus `selfAddress` (case-insensitive), with `Reply-To` winning over `From`;
    - the quote is `\n\n<header line>\n> ` per line; the header line is `Loc.T("{0}, {1} napisał(a):", date, from)`;
    - Forward has empty To and carries the original's attachments: they are written next to the draft by `ComposeAsync`, so here they are listed as `AttachmentPaths` of the original `.eml` with `#<index>` suffixes;
    - `In-Reply-To`/`References` are set for Reply/ReplyAll only.
  - `MimeKit.MimeMessage MailDrafts.Build(MailDraft draft, Func<string, Stream> openAttachment)`: plain-text body, UTF-8, attachments with their file names; an `AttachmentPath` ending in `#<n>` reads attachment n of that `.eml`.
  - `MailMessageView MailDrafts.View(MimeKit.MimeMessage message)`: prefers the text/plain part, otherwise `HtmlText.ToText(html)`.
  - `DateTimeOffset MailSince.Resolve(string? since, DateTimeOffset now)`: empty → `now.AddMonths(-3)`; `"10d"`, `"6m"`, `"1y"`; `"2026-01-01"`; anything else → `FormatException`.

- [ ] **Step 1: Write failing tests** (in `MailHelpersTests`):
  - `Endpoint_picks_tls_mode_by_port`: `"imap.x:993"` gives SslOnConnect; `"imap.x:143"` and `"smtp.x:587"` give StartTls; `Parse("imap.x", 993).Port == 993`; `"imaps://x"` throws.
  - `FileName_is_valid_unique_and_bounded` (Review Focus 4):
    - the subject `Re: <oferta>/"pilne"?*|: 🎉` gives a name with no `Path.GetInvalidFileNameChars()`;
    - an empty subject contains `(bez tematu)`;
    - a 500-character subject gives a name of at most 160 characters;
    - two messages with the same date, sender and subject but UIDs 1 and 2 get different names;
    - `TryParseUid` round-trips the UID.
  - `Special_folders_get_local_names`: `INBOX` → `Odebrane`; `Sent Items` without attributes → `Sent` → `Wysłane`; `Projekty/Klient A` with `/` → `Projekty\Klient A`.
  - `HtmlText_drops_scripts_images_and_unsafe_links` (Review Focus 3). Input:
    ```html
    <p>Hej&nbsp;<b>Jan</b></p><script>alert(1)</script><img src="https://t.example/p.gif" alt="logo"><a href="javascript:x()">klik</a><a href="https://ok.example/a">tu</a><style>p{}</style>
    ```
    The output contains `Hej Jan`, `[obraz: logo]`, `klik` and `tu (https://ok.example/a)`. It does not contain `alert`, `t.example`, `javascript:` or `p{}`.
  - `Reply_all_excludes_self_and_quotes`: original From `a@x`, To `me@x, b@x`, Cc `c@x`, subject `Re: Plan`. `ReplyAll` with `me@x` gives To `[a@x]`, Cc `[b@x, c@x]`, subject `Re: Plan`, `InReplyTo` = the original Message-ID, and every original body line appears in the body prefixed with `> `.
  - `Since_defaults_to_three_months`: `Resolve(null, 2026-10-31)` = 2026-07-31; `"10d"` = 2026-10-21; `"2026-01-01"` parses; `"abc"` throws.
- [ ] **Step 2: Run `dotnet test --solution Commander4K.sln -- --filter-method "*MailHelpers*"`**. Expected: FAIL to compile.
- [ ] **Step 3: Implement the five helpers.** Add `en.json` entries for the new `Loc.T` keys.
- [ ] **Step 4: Run the tests and `LocalizationTests`.** Expected: PASS.
- [ ] **Step 5: Commit** `feat(core): mail helpers – endpoints, file names, html to text, drafts`.

### Task 3: `MailService` (MailKit) with credentials, certificates, pool

**Files:**
- Create: `src/Commander4K.Core/Mail/MailService.cs`, `MailCredentials.cs`, `MailCertificateStore.cs`
- Test: `tests/Commander4K.Core.Tests/MailServiceTests.cs` (unit), `tests/Commander4K.Plugins.Tests/MailServerTests.cs` (integration, skipped without `C4K_TEST_IMAP`)

**Interfaces:**
- Consumes: Task 1 records, Task 2 helpers, `Core.Security.SecretStore`.
- Produces:
  ```csharp
  public interface IMailBackend   // unscoped; ScopedMailHost (Task 4) gates and audits
  {
      Task<IReadOnlyList<MailFolderInfo>> FoldersAsync(string pluginId, MailAccount account, CancellationToken ct);
      Task<MailSyncResult> SyncAsync(string pluginId, MailAccount account, MailSyncRequest request, CancellationToken ct);
      Task<long> FetchAsync(string pluginId, MailAccount account, string folder, uint uid, Stream destination, CancellationToken ct);
      Task SetFlagsAsync(string pluginId, MailAccount account, string folder, IReadOnlyList<uint> uids, MailFlags add, MailFlags remove, CancellationToken ct);
      Task MoveAsync(string pluginId, MailAccount account, string folder, IReadOnlyList<uint> uids, string targetFolder, CancellationToken ct);
      Task DeleteAsync(string pluginId, MailAccount account, string folder, IReadOnlyList<uint> uids, CancellationToken ct);
      Task<uint?> AppendAsync(string pluginId, MailAccount account, string folder, Stream message, MailFlags flags, CancellationToken ct);
      Task<string> SendAsync(string pluginId, MailAccount account, Stream message, CancellationToken ct);
  }
  public sealed class MailService(MailCredentials credentials, MailCertificateStore certificates, TimeProvider? time = null) : IMailBackend, IAsyncDisposable;
  /// askPassword(realm, user) → password or null; called only when account.Interactive.
  public sealed class MailCredentials(SecretStore secrets, Func<string, string, Task<string?>> askPassword)
  {
      public static string ImapSecretName(string pluginId, MailAccount a);   // "mail:<id>:<user>@<imap host>"
      public static string SmtpSecretName(string pluginId, MailAccount a);   // "mail-smtp:<id>:<user>@<smtp host>"
      public Task<string> GetImapPasswordAsync(string pluginId, MailAccount a, bool afterRefusal, CancellationToken ct);
      public Task<string> GetSmtpPasswordAsync(string pluginId, MailAccount a, bool afterRefusal, CancellationToken ct);
  }
  /// confirm(host:port, sha256 hex) → trust; called only when interactive.
  public sealed class MailCertificateStore(string path, Func<string, string, Task<bool>> confirm);
  ```
  - `SyncAsync` behaviour:
    - With CONDSTORE/QRESYNC: changes since `ModSeq`.
    - Without them: search `SINCE <Since>`, compare with `KnownUids`. `Removed` = known UIDs no longer present, among those ≥ the lowest UID in range. `FlagChanges` = fetched flags that differ.
    - `New` = UIDs > `LastUid` within `Since`, with `MailFileNames.For(...)` as `FileName`.
    - `UidValidityChanged` when the server value differs from a non-zero request value.
  - `SendAsync`: SMTP auth. Afterwards the message is APPENDed to the folder classified `Sent`, with `\Seen`.
  - The refusal path: `AuthenticationException` from MailKit. Interactive calls ask once again (`afterRefusal: true`) and retry once; background calls throw `MailAuthenticationRequiredException(realm)`. Realm = `"<user>@<imap host>"`, and the dialog shows both hosts.
  - An interactive login stores the password under both secret names (bound to their hosts).

- [ ] **Step 1: Write failing unit tests** (no server):
  - `Background_call_without_password_throws_without_prompt` (Review Focus 5): `askPassword` counts calls. `FoldersAsync` with `Interactive=false` on an empty `SecretStore` throws `MailAuthenticationRequiredException`, and the call count is 0.
  - `Secrets_are_bound_per_plugin_and_host`: store a password for plugin `a`. `GetImapPasswordAsync` for plugin `b` with `Interactive=false` throws. The same account with the IMAP host changed also throws.
  - `Interactive_login_stores_both_secrets`: `askPassword` returns `"pw"`. Afterwards `secrets.Get(ImapSecretName(..), imapHost) == "pw"` and `secrets.Get(SmtpSecretName(..), smtpHost) == "pw"`.
  - `Certificate_trust_is_remembered`: `confirm` returns true once. A second validation of the same host:port and fingerprint does not call it; a different fingerprint calls it again.
  - `Operation_times_out_on_silent_server` (Review Focus 1): a `TcpListener` on loopback accepts and never writes. `FoldersAsync` against it (port ≠ 993 → StartTls) fails within 35 s with `TimeoutException` (or `OperationCanceledException` from the timeout, not from the caller's token). Use `TimeProvider`/a shortened timeout seam if needed to keep the test under 5 s.
- [ ] **Step 2: Run `dotnet test --solution Commander4K.sln -- --filter-method "*MailService*"`**. Expected: FAIL.
- [ ] **Step 3: Implement.**
  - Pool: a `ConcurrentDictionary<(pluginId, imap, user), Pool>` with ≤ 2 `ImapClient`s per key, each guarded by a `SemaphoreSlim`. Idle clients are closed by a timer after 5 min. `DisposeAsync` closes all.
  - Timeouts: set `client.Timeout`, plus a linked CTS for the operation limit.
  - Certificates: the certificate callback goes through `MailCertificateStore`. In a background call, an untrusted certificate throws `MailCertificateUntrustedException`.
- [ ] **Step 4: Write integration tests** in `MailServerTests` (skipped when `C4K_TEST_IMAP` is unset, like `RemoteServerTests`):
  - `Full_cycle_against_real_server`:
    1. send 3 messages to `test@localhost` over SMTP;
    2. `FoldersAsync` contains Inbox with Count ≥ 3;
    3. a first `SyncAsync` returns 3 `New`;
    4. `FetchAsync` writes a parseable message;
    5. `SetFlagsAsync(+Seen)`, then `SyncAsync` reports the flag change;
    6. `MoveAsync` to a created folder `Archive`, then `SyncAsync` on Inbox lists the UID in `Removed`;
    7. `SendAsync` returns a Message-ID and the message appears in `Sent`.
  - `Sync_without_condstore_uses_known_uids`: force the fallback path and assert the same results.
- [ ] **Step 5: Run with the docker server up.** Expected: PASS; without it the tests are skipped.
- [ ] **Step 6: Commit** `feat(core): MailKit mail service with per-plugin secrets and certificate trust`.

### Task 4: Gated `IMailHost`, RPC methods, SDK clients, docs

**Files:**
- Create: `src/Commander4K.Core/Plugins/ScopedMailHost.cs`
- Modify: `src/Commander4K.Core/Plugins/ScopedPluginHost.cs` (ctor parameter `IMailBackend? mail = null`; `Mail => new ScopedMailHost(...)`), `src/Commander4K.Core/Plugins/ExternalPlugin.cs` (`HandleRequestAsync` cases), `src/Commander4K.App/Composition/AppHost.cs` (construct `MailService` with `LoginDialog` / confirm dialog callbacks and pass it into `HostFor`; dispose on exit)
- Modify: `plugins-sdk/{python/c4k.py,node/c4k.js,powershell/c4k.ps1}`, then copy into every `examples/plugins/*` and `plugins-catalog/*` plugin; `docs/plugins.md` (SDK table, permissions list, the `mail` and `background` sections)
- Test: `tests/Commander4K.Core.Tests/ScopedMailHostTests.cs`

**Interfaces:**
- Consumes: `IMailBackend` (Task 3), `IMailHost` (Task 1).
- Produces:
  - RPC methods (params are camelCase JSON):
    - `mail.folders {account}`;
    - `mail.sync {account, folder, uidValidity, lastUid, modSeq, since (ISO), knownUids}`;
    - `mail.fetch {account, folder, uid, path}` → `{bytes}`;
    - `mail.setFlags {account, folder, uids, add[], remove[]}`;
    - `mail.move {account, folder, uids, target}`;
    - `mail.delete {account, folder, uids}`;
    - `mail.append {account, folder, path, flags[]}` → `{uid?}`;
    - `mail.parse {path}`;
    - `mail.saveAttachment {path, index, target}`;
    - `mail.reply {path, mode: reply|replyAll|forward, self}` → draft;
    - `mail.compose {path, draft}`;
    - `mail.send {account, path}` → `{messageId}`;
    - `background.wake {}`, handled in Task 5.
    - `account = { imap, smtp?, user, interactive? }`; flags are strings `seen|flagged|answered|draft`.
    - Errors: `MailAuthenticationRequiredException` becomes JSON-RPC error code `-32010` with `data: { authRequired: true, realm }`; an untrusted certificate becomes `-32011`.
  - SDK helpers:
    - PowerShell: `Get-C4KMailFolders`, `Sync-C4KMail`, `Save-C4KMail`, `Set-C4KMailFlags`, `Move-C4KMail`, `Remove-C4KMail`, `Add-C4KMail`, `Read-C4KMail`, `Save-C4KMailAttachment`, `New-C4KMailReply`, `Write-C4KMailDraft`, `Send-C4KMail`;
    - Python and Node: the same names in snake_case and camelCase.
    - Each client also gets `notify(message, error, target)`, extended with `target`.

- [ ] **Step 1: Write failing tests**:
  - `Mail_requires_mail_permission`: a manifest without `mail` → `Mail.FoldersAsync` throws `PluginPermissionException`, and the audit has a `Denied` entry for `mail.folders`.
  - `Fetch_requires_fs_write_for_target`: `mail` without `fs.write` → `FetchAsync` throws `PluginPermissionException(…, "fs.write")`.
  - `Audit_has_no_content_or_password`: with a fake `IMailBackend`, `SendAsync` audits one `Allowed` entry. The entry contains the account user and host but not the body text, the subject or `"pw"`.
  - `Fetch_writes_through_router`: with a fake backend writing `"Subject: x\r\n\r\nhi"`, the target file under a temp folder holds exactly those bytes.
- [ ] **Step 2: Run `dotnet test --solution Commander4K.sln -- --filter-method "*ScopedMailHost*"`**. Expected: FAIL.
- [ ] **Step 3: Implement `ScopedMailHost`**:
  - `Gate(PluginPermissions.Mail, "mail.<op>", "<user>@<imap> <folder> <n> uid", …)`, the same `Gate` pattern as `ScopedPluginHost`;
  - fs permission checks for the paths involved;
  - streams opened through `VfsRouter`.
  Then add the RPC cases and the three SDK clients, and copy them into the examples and catalog plugins. Run `pwsh ./plugins-catalog/Update-Catalog.ps1` and update `docs/plugins.md`.
- [ ] **Step 4: Run the tests, the SDK-copy identity test, `PluginCatalogTests`, `PluginSourceCheckTests` and `ExternalPluginTests`.** Expected: PASS.
- [ ] **Step 5: Commit** `feat(plugins): mail.* protocol methods behind the mail permission`.

### Task 5: Background tick service + setting

**Files:**
- Create: `src/Commander4K.Core/Plugins/BackgroundTickLoop.cs` (UI-free logic), `src/Commander4K.App/Composition/PluginBackgroundService.cs` (dedicated thread, wiring)
- Modify: `src/Commander4K.Core/Plugins/ExternalPlugin.cs` (`TickAsync`, `background.wake`), `src/Commander4K.Core/Settings/AppSettings.cs` (or wherever `AppSettings` lives: `PluginBackground` bool, default `true`), `SettingsCatalog.Create` (title „Wtyczki mogą działać w tle”, keywords „tło, synchronizacja, poczta, wtyczki”), `AppHost.cs` (start after plugins load, stop on exit), `en.json`
- Test: `tests/Commander4K.Core.Tests/BackgroundTickLoopTests.cs`

**Interfaces:**
- Consumes: `BackgroundContribution` (Task 1), `PluginApprovalStore`.
- Produces:
  ```csharp
  public sealed class BackgroundTickLoop(TimeProvider time, Func<bool> enabled)
  {
      /// tick returns true when the plugin reported more work.
      public void Register(string pluginId, TimeSpan interval, Func<CancellationToken, Task<bool>> tick);   // interval clamped to ≥ 1 min
      public void Wake(string pluginId);                     // run as soon as the current tick (if any) ends
      public Task RunAsync(CancellationToken stop);          // the loop; tick timeout 60 s; more → 2 s
  }
  // ExternalPlugin
  public Task<bool> TickAsync(CancellationToken ct);         // background.tick → result.more == true
  public event Action<string>? WakeRequested;                // raised by background.wake from the plugin (single-argument event: plugin id)
  ```
  `PluginBackgroundService` registers every external plugin that is enabled and approved, has `contributes.background` and is allowed `background`. It runs `RunAsync` on a `new Thread { IsBackground = true, Name = "C4K plugin background" }` that blocks on the task. On exit it cancels and joins for at most 2 s.

- [ ] **Step 1: Write failing tests** (with `FakeTimeProvider` from `Microsoft.Extensions.TimeProvider.Testing`, or the existing test time helper if there is one):
  - `Ticks_follow_interval_and_more`: interval 1 min; tick returns `true` then `false`. Ticks fire at t=0, 2 s and 62 s.
  - `Interval_below_one_minute_is_clamped`: an interval of 10 s fires at 0 s and 60 s.
  - `No_overlap_while_tick_runs`: a tick blocked on a TCS for 3 min starts no second tick.
  - `Tick_that_hangs_is_abandoned_and_next_tick_runs` (Review Focus 1): a tick that ignores its token is abandoned at 60 s, the next one fires on schedule, and no exception escapes `RunAsync`.
  - `Disabled_setting_stops_ticks`: with `enabled()` false there are 0 ticks; when it flips to true, ticks start.
  - `Wake_runs_now`: `Wake` during idle triggers a tick within the next loop step.
- [ ] **Step 2: Run `dotnet test --solution Commander4K.sln -- --filter-method "*BackgroundTickLoop*"`**. Expected: FAIL.
- [ ] **Step 3: Implement the loop, `ExternalPlugin.TickAsync`/`background.wake`, the service, the setting and the docs** (`docs/plugins.md`: `contributes.background`, `background.tick`, `background.wake`).
- [ ] **Step 4: Run the tests and `LocalizationTests`.** Expected: PASS.
- [ ] **Step 5: Commit** `feat(plugins): background ticks on a dedicated thread`.

### Task 6: Preview actions, forms, Enter-to-preview, `pinned`

**Files:**
- Modify: `src/Commander4K.Core/Plugins/ExternalPluginAdapters.cs`:
  - `ExternalPreviewPlugin : IPreviewCardPlugin, IInteractivePreviewPlugin`;
  - parse `actions` on any render result into `ActionsPreview`;
  - `kind: "form"` → `FormPreview`;
  - `pinned` in `preview.render` params;
  - `preview.action`;
  - `preview.formChanged`.
- Modify: `src/Commander4K.App/Preview/PreviewPanelView.cs`:
  - render `ActionsPreview` as a top action bar plus its inner content, and `FormPreview` as fields, files and buttons;
  - shortcuts are active only while the preview has focus;
  - `c4k:plugin/` targets go to `IInteractivePreviewPlugin`, other targets keep the existing tile path;
  - form edits are debounced **800 ms** into `FormChangedAsync`, and the last edit is flushed before any action runs;
  - pinned file previews.
- Modify: `src/Commander4K.App/Panels/FilePanelView.cs` and `MainWindow.cs`: Enter on a file matched by a preview with `Enter == true` opens the pinned preview of that file over the panel; Shift+Enter keeps the old shell open; Esc returns to the files.
- Modify: `src/Commander4K.Plugins.BuiltIn/Dashboard/CardResolver.cs` / `DashboardView.cs`: a card action `c4k:plugin/…` invokes `InvokeActionAsync` with the card's target entry, then refreshes that card.
- Modify: SDK clients, examples, catalog copies, `docs/plugins.md`.
- Test: `tests/Commander4K.Core.Tests/PreviewActionParsingTests.cs`; extend `tests/Commander4K.Plugins.Tests/ExternalPluginTests.cs`

**Interfaces:**
- Consumes: Task 1 records.
- Produces:
  - Protocol:
    - any `preview.render` result may carry `actions: [{label, target, shortcut?}]`;
    - `kind: "form"` has the shape `{ title, fields: [{id,label,value,multiline?}], files: [{name,path,size}], buttons: [{label,target,shortcut?}], message?, messageError? }`;
    - `preview.render` params gain `pinned: bool`;
    - `preview.action { preview, entry, options, action, args, values? }` → `null`, or a render result (same shape as `preview.render`);
    - `preview.formChanged { preview, entry, options, values }` → `null`.
  - `ExternalPreviewPlugin.LoadWithOptionsAsync(VfsEntry entry, IPluginHost host, string? options, bool pinned, CancellationToken ct)`. The existing overload forwards `pinned: false`.

- [ ] **Step 1: Write failing tests**:
  - `Render_with_actions_becomes_ActionsPreview`: a JSON result `{kind:"markdown",content:"x",actions:[{label:"Odpowiedz",target:"c4k:plugin/reply",shortcut:"R"}]}` parses to `ActionsPreview(MarkdownPreview("x",false), [PreviewAction("Odpowiedz","c4k:plugin/reply","R")])`.
  - `Form_result_parses_fields_files_buttons`: `Multiline` is kept, and file sizes are parsed as longs.
  - `ExternalPluginTests.Preview_action_round_trip`: extend an example plugin (e.g. `folder-stats-python`) with an action `c4k:plugin/echo?x=1` that returns markdown `"echo 1"` and a `preview.formChanged` handler that stores `values` in storage. `InvokeActionAsync` returns `MarkdownPreview("echo 1")`, and `FormChangedAsync` makes storage hold the values.
  - `Unknown_kind_still_falls_back_to_text`: the existing behaviour is kept.
- [ ] **Step 2: Run the new tests.** Expected: FAIL.
- [ ] **Step 3: Implement parsing and protocol, then the App rendering and routing.** Build the action bar and the form layout in the same style as the existing banner buttons (`BannerButton`) and the text boxes, using skin tokens only. The Enter rule: if the folder's `c4k.dir` has an `enter` action, it still wins for folders; for files, check `PreviewContribution.Enter` on the highest-priority matching preview.
- [ ] **Step 4: Run the full suite. Then verify headless**, as described in CLAUDE.md under "UI verification": render a `FormPreview` and an `ActionsPreview` in `modern-dark` and in a retro skin, and save both PNGs to the scratchpad. Expected: tests PASS, and the PNGs show the bar, fields and buttons in skin colours.
- [ ] **Step 5: Commit** `feat(preview): actions, forms and Enter-to-preview for plugin previews`.

### Task 7: Mail plugin — account and sync

**Files:**
- Create: `plugins-catalog/mail-powershell/plugin.json` (id `official.mail`, runtime `powershell >=7.2`, permissions `mail, fs.read, fs.write, background`, `contributes.background.interval = "1m"`, preview `account` for `/` with `card: true`, preview `message` for `.eml` with `enter: true`, `priority: 60`), `main.ps1`, `sync.ps1`, a copy of `c4k.ps1`
- Modify: `plugins-catalog/catalog.json` (via `Update-Catalog.ps1`)
- Test: `tests/Commander4K.Plugins.Tests/MailPluginTests.cs` (server-backed; skipped without `C4K_TEST_IMAP`) plus offline tests using a fake host

**Interfaces:**
- Consumes: `mail.*` (Task 4), `background.tick`/`background.wake` (Task 5), storage, fs.*.
- Produces:
  - Storage key `accounts`: an array of account folder paths. A path is added on `preview.render`/`preview.card` of an account folder and by „Dodaj konto”.
  - `<account>/.mail/state.json`:
    ```jsonc
    { "folders": { "<serverName>": { "local": "Odebrane", "special": "inbox", "uidValidity": 1, "lastUid": 4711, "modSeq": 0, "known": [/* uids */] } },
      "lastCheck": "ISO", "lastSuccess": "ISO", "error": null | "text", "authRequired": false, "progress": null | { "done": 120, "total": 3400 },
      "flagged": [/* "<server>/<uid>" */], "drafts": { "<local file>": <server uid> } }
    ```
  - `background.tick`:
    1. For each account that is due (by `refresh` and `lastCheck`), not `authRequired`, and not locked by another machine: refresh the lock, then run `mail.folders`.
    2. Create, rename or map local folders (Hidden `.mail`).
    3. Per folder, run `mail.sync`. On a UIDVALIDITY change, rename the existing files to `… [stary].eml` and reset the state.
    4. `mail.fetch` new messages into `<local>/<●? FileName>`.
    5. Apply flag changes by renaming; delete files of removed UIDs; APPEND changed drafts.
    6. Write `state.json` after each folder.
    7. Stop at 50 fetches or 15 s and return `{ more: true }`.
    8. Raise one `ui.notify` per tick when new unread messages arrived: `"<account>: <n> nowe wiadomości"` (plugin text, not localized), with the account's Inbox as target.
  - `-32010` (auth required) sets `authRequired: true`, and background ticks skip the account. An interactive `Sprawdź teraz` clears the flag, then retries.

- [ ] **Step 1: Write failing tests**:
  - `First_sync_is_batched_and_resumes` (Review Focus 2, server-backed):
    1. seed 120 messages;
    2. call the plugin's `background.tick` through `ExternalPlugin.TickAsync` until `more` is false;
    3. the first tick returns `more: true` with ≤ 50 files;
    4. at the end the Inbox has 120 `.eml` files, all prefixed `● `;
    5. `state.json` `lastUid` equals the highest UID;
    6. between two ticks, a `preview.card` call answers in < 2 s.
  - `Read_state_follows_server_flags`: mark 1 message `\Seen` server-side and tick. The file loses `● `.
  - `Removed_on_server_removes_file_but_missing_file_is_refetched`: delete 1 message on the server and its file disappears after a tick. Delete another file locally: after a tick it is back, and the server still has it.
  - `Outside_since_is_kept`: a local file older than `since` stays after ticks.
  - `Auth_failure_pauses_account_until_interactive_login` (Review Focus 5, offline):
    1. a fake mail backend throws auth-required;
    2. after a tick `state.json` has `authRequired: true`;
    3. the next 3 ticks make 0 backend calls;
    4. `preview.action check` with `interactive` makes one call.
  - `Lock_held_by_other_machine_skips_sync` (offline): `.mail/lock` = `{ machine: "OTHER", lastTick: now-1min }` gives 0 backend calls; `now-11min` syncs.
- [ ] **Step 2: Run `dotnet test --solution Commander4K.sln -- --filter-method "*MailPlugin*"`**. Expected: FAIL.
- [ ] **Step 3: Implement `sync.ps1` and its wiring in `main.ps1`.** All system access goes through the SDK, so `PluginSourceCheck` must stay clean.
- [ ] **Step 4: Run the tests, `PluginSourceCheckTests` and `PluginCatalogTests`.** Expected: PASS.
- [ ] **Step 5: Commit** `feat(catalog): mail plugin – background IMAP sync to .eml folders`.

### Task 8: Mail plugin — reading, actions, composing, sending

**Files:**
- Modify: `plugins-catalog/mail-powershell/main.ps1`; create `compose.ps1`
- Test: extend `tests/Commander4K.Plugins.Tests/MailPluginTests.cs`

**Interfaces:**
- Consumes: `mail.parse/reply/compose/send/setFlags/move/delete/saveAttachment` (Task 4), the `ActionsPreview`/`FormPreview` protocol and `pinned` (Task 6).
- Produces:
  - `.eml` render:
    - markdown with a header block (Od / Do / DW / Temat / Data), the body as text, and a list of attachments;
    - actions:
      - `reply` (R), `replyAll` (Shift+R), `forward` (F);
      - `archive` (A), `delete` (Del), `unread` (U);
      - `saveAttachment?i=<n>`, which targets the other panel's path (`context.otherPath`).
  - `pinned: true` on an unread file → `mail.setFlags +seen` and a rename to the read name. The render returns content for the renamed entry, and the host follows the new path.
  - Archive and delete:
    - **Archive:** `mail.move` to the folder classified Archive. If there is none, the action is hidden. The local file moves to the matching local folder.
    - **Delete:** `mail.delete`, then the local file moves to Kosz, or is removed when it was already in Kosz.
  - `reply|replyAll|forward`:
    1. `mail.reply`, then `mail.compose` into `Szkice/<yyyy-MM-dd HHmm> <subject>.eml`;
    2. the result is a form (Do, DW, UDW, Temat, Treść multiline) with buttons:
       - `send` (Ctrl+Enter);
       - `attachSelected`, which adds `context.selection` file paths;
       - `removeAttachment?i=`;
       - `discard`.
  - `preview.formChanged` → `mail.compose` overwrites the draft.
  - `send`: `mail.send`, then `+answered` on the original, then delete the draft locally and on the server. The result is the original message's render.
  - Send failure: the same form with `message` = the server error and `messageError: true`.
  - Opening a draft file in `Szkice/` renders it directly as the form.

- [ ] **Step 1: Write failing tests** (server-backed):
  - `Reply_flow_sends_and_marks_answered`:
    1. render a seeded message with `pinned: true`; the file loses `● `;
    2. invoke `reply`; a form comes back with Do = the original sender and a Temat starting `Re: `;
    3. `formChanged` with body `"Dzięki"`, then the draft file contains `Dzięki`;
    4. invoke `send`; the SMTP server received a message with the matching `In-Reply-To`;
    5. Sent holds a copy, the original has `\Answered`, and the draft file is gone.
  - `Cursor_preview_does_not_mark_read`: a render with `pinned: false` keeps `● ` and leaves the server `\Seen` unset.
  - `Delete_moves_to_trash_on_server_and_locally`.
  - `Send_failure_keeps_draft_and_shows_error`: the SMTP endpoint points at a closed port; the form comes back with `messageError` and the draft still exists.
  - `Html_message_renders_without_remote_content`: seed the HTML from Task 2's test; the rendered markdown contains no `t.example`.
- [ ] **Step 2: Run them.** Expected: FAIL.
- [ ] **Step 3: Implement `compose.ps1` and the render/action handlers.**
- [ ] **Step 4: Run the tests. Then do a manual check in a real window** against GreenMail: Enter on a mail, reply, attach a file from the other panel, send. Expected: PASS, and the flow works with the keyboard alone.
- [ ] **Step 5: Commit** `feat(catalog): mail plugin – read, reply, forward, send`.

### Task 9: Account view, card, „Dodaj konto pocztowe”, catalog release

**Files:**
- Modify: `plugins-catalog/mail-powershell/main.ps1`, `plugin.json` (command `add-account`, „Dodaj konto pocztowe”, category „Poczta”), the catalog via `Update-Catalog.ps1`; `docs/plugins.md` (a short "Poczta" example in the `c4k.dir` guide)
- Test: extend `MailPluginTests.cs`

**Interfaces:**
- Consumes: `preview.card`, `CardAction`, `c4k:plugin/` card actions (Task 6), `FolderConfigWriter` behaviour (the dashboard plan; for the plugin this means writing `c4k.dir` JSON through `fs.writeText` while preserving the other keys).
- Produces:
  - Account folder render (`preview: plugin official.mail`, and `enter` too):
    - a markdown table of folders with unread counts (counted from `● ` files);
    - `lastSuccess`, error or `authRequired`, and progress;
    - actions `check` („Sprawdź teraz”, F5) and `new` („Nowa wiadomość”, N), which opens an empty form in Szkice.
  - Card:
    - the Inbox unread count as `CardValue("Nieprzeczytane", n)`;
    - the other folders' unread counts, excluding Kosz/Spam, as small values;
    - `CardText("sprawdzono HH:mm")`, or the error, `wymaga logowania`, or `synchronizuje <komputer>`;
    - status Ok, Warning (auth/lock) or Error;
    - actions `Otwórz` (Inbox path) and `Sprawdź teraz` (`c4k:plugin/check`).
    - It reads files and `state.json` only, with no `mail.*` calls.
  - `check`: an interactive bounded sync of that account (≤ 15 s), then `background.wake` when `more`.
  - `add-account`:
    1. `ui.prompt` for the address, IMAP (suggest `imap.<domain>:993`), SMTP (suggest `smtp.<domain>:465`), display name and folder name (suggest the domain);
    2. `mail.folders` interactive, which validates the login and stores the password;
    3. create `<active path>/<name>/c4k.dir` with `preview`/`enter` = `official.mail` and the options;
    4. add or extend the `<active path>/c4k.dir` dashboard tiles with `{ title, open: "<name>", card: "official.mail/account", refresh: "30s" }`;
    5. add the path to `accounts` and call `background.wake`.
    - The command refuses to run outside `file`/`smb` paths.

- [ ] **Step 1: Write failing tests**:
  - `Card_reads_files_only`: with a fake backend that counts calls, `preview.card` on a synced account returns the Inbox unread count equal to the number of `● ` files, and the backend count is 0.
  - `Card_shows_auth_required_as_warning`.
  - `Add_account_writes_both_c4k_dir_files` (offline, prompts answered by a scripted `IPluginUi`):
    - the account `c4k.dir` has the options and no password field anywhere;
    - the dashboard `c4k.dir` keeps a pre-existing unrelated tile and gains the account tile;
    - storage `accounts` contains the new path.
- [ ] **Step 2: Run them.** Expected: FAIL.
- [ ] **Step 3: Implement. Then run `pwsh ./plugins-catalog/Update-Catalog.ps1`.**
- [ ] **Step 4: Run the full suite. Then do a manual check in a real window**: install from the catalog (the plugin is disabled until approved), approve, add an account, see the card on the `Poczta/` dashboard update after the background tick, and get a notification for a new mail. Expected: PASS and the flow works.
- [ ] **Step 5: Commit** `feat(catalog): mail account view, dashboard card and add-account command`.
