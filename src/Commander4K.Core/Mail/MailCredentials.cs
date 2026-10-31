using System.Collections.Concurrent;
using Commander4K.Core.Security;
using Commander4K.PluginSdk;

namespace Commander4K.Core.Mail;

/// <summary>
/// Mail passwords in <see cref="SecretStore"/>, per plugin, user and host: <c>mail:&lt;id&gt;:&lt;user&gt;@&lt;imap host&gt;</c> bound to
/// the IMAP host and <c>mail-smtp:&lt;id&gt;:&lt;user&gt;@&lt;smtp host&gt;</c> bound to the SMTP host. A changed host is a missing secret.
/// The password never leaves the host process.
/// </summary>
/// <param name="askPassword">(realm, user) → password or <c>null</c>; called only for interactive accounts. The realm text names
/// both hosts when the account has an SMTP server.</param>
public sealed class MailCredentials(SecretStore secrets, Func<string, string, Task<string?>> askPassword)
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _prompts = new(StringComparer.OrdinalIgnoreCase);

    public static string ImapSecretName(string pluginId, MailAccount a) => $"mail:{pluginId}:{a.User}@{ImapHost(a)}";

    public static string SmtpSecretName(string pluginId, MailAccount a) => $"mail-smtp:{pluginId}:{a.User}@{SmtpHost(a)}";

    /// <summary><c>&lt;user&gt;@&lt;imap host&gt;</c>: the account's identity in <see cref="MailAuthenticationRequiredException"/>.</summary>
    public static string Realm(MailAccount a) => $"{a.User}@{ImapHost(a)}";

    internal static string ImapHost(MailAccount a) => MailEndpoints.Parse(a.Imap, 993).Host.ToLowerInvariant();

    internal static string SmtpHost(MailAccount a) =>
        MailEndpoints.Parse(a.Smtp ?? throw new InvalidOperationException("The mail account has no SMTP server."), 465).Host.ToLowerInvariant();

    /// <param name="afterRefusal">The server refused the stored password: ask again (interactive) or fail (background).</param>
    public Task<string> GetImapPasswordAsync(string pluginId, MailAccount a, bool afterRefusal, CancellationToken ct) =>
        GetAsync(pluginId, a, smtp: false, afterRefusal, ct);

    public Task<string> GetSmtpPasswordAsync(string pluginId, MailAccount a, bool afterRefusal, CancellationToken ct) =>
        GetAsync(pluginId, a, smtp: true, afterRefusal, ct);

    private async Task<string> GetAsync(string pluginId, MailAccount a, bool smtp, bool afterRefusal, CancellationToken ct)
    {
        var name = smtp ? SmtpSecretName(pluginId, a) : ImapSecretName(pluginId, a);
        var host = smtp ? SmtpHost(a) : ImapHost(a);
        if (!afterRefusal && secrets.Get(name, host) is { } stored)
        {
            return stored;
        }

        if (!a.Interactive)
        {
            throw new MailAuthenticationRequiredException(Realm(a));
        }

        // One dialog per account at a time; a parallel call waiting here uses the password typed meanwhile.
        var gate = _prompts.GetOrAdd($"{pluginId}\n{Realm(a)}", _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!afterRefusal && secrets.Get(name, host) is { } meanwhile)
            {
                return meanwhile;
            }

            var label = a.Smtp is null || string.Equals(SmtpHost(a), ImapHost(a), StringComparison.Ordinal)
                ? Realm(a)
                : $"{Realm(a)}, {SmtpHost(a)}";
            var typed = await askPassword(label, a.User).WaitAsync(ct).ConfigureAwait(false);
            if (typed is null)
            {
                throw new MailAuthenticationRequiredException(Realm(a));
            }

            // An interactive login fills both secrets; a second SMTP-only prompt (different SMTP password) keeps the IMAP one.
            if (!smtp || secrets.Get(ImapSecretName(pluginId, a), ImapHost(a)) is null)
            {
                secrets.Set(ImapSecretName(pluginId, a), typed, ImapHost(a));
            }

            if (a.Smtp is not null)
            {
                secrets.Set(SmtpSecretName(pluginId, a), typed, SmtpHost(a));
            }

            return typed;
        }
        finally
        {
            gate.Release();
        }
    }
}
