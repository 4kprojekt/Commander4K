using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Commander4K.Core.Configuration;

namespace Commander4K.Core.Mail;

/// <summary>
/// Mail server certificates that failed validation but the user trusted (<c>config/mail_certificates.json</c>,
/// <c>host:port</c> → SHA-256 fingerprint), like <c>KnownHostsStore</c>. Thread-safe.
/// </summary>
/// <param name="confirm">(host:port, SHA-256 hex) → trust; called only for interactive calls.</param>
public sealed class MailCertificateStore(string path, Func<string, string, Task<bool>> confirm)
{
    private readonly Lock _gate = new();
    private Dictionary<string, string>? _trusted;

    public static string Fingerprint(X509Certificate certificate) =>
        Convert.ToHexString(SHA256.HashData(certificate.GetRawCertData()));

    public bool IsTrusted(string host, int port, string fingerprint)
    {
        lock (_gate)
        {
            return Load().TryGetValue(Key(host, port), out var known) && string.Equals(known, fingerprint, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>Trusted already, or (interactive only) confirmed by the user now and remembered.</summary>
    public async Task<bool> ValidateAsync(string host, int port, string fingerprint, bool interactive)
    {
        if (IsTrusted(host, port, fingerprint))
        {
            return true;
        }

        if (!interactive || !await confirm($"{host}:{port}", fingerprint).ConfigureAwait(false))
        {
            return false;
        }

        Trust(host, port, fingerprint);
        return true;
    }

    private void Trust(string host, int port, string fingerprint)
    {
        lock (_gate)
        {
            var trusted = Load();
            trusted[Key(host, port)] = fingerprint;
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(trusted, ConfigJsonContext.Default.DictionaryStringString));
            File.Move(temporary, path, overwrite: true);
        }
    }

    private static string Key(string host, int port) => $"{host.ToLowerInvariant()}:{port}";

    private Dictionary<string, string> Load()
    {
        if (_trusted is not null)
        {
            return _trusted;
        }

        _trusted = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (File.Exists(path))
            {
                foreach (var (key, value) in JsonSerializer.Deserialize(File.ReadAllText(path), ConfigJsonContext.Default.DictionaryStringString) ?? [])
                {
                    _trusted[key] = value;
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            // An unreadable file trusts nothing; the user is asked again.
        }

        return _trusted;
    }
}
