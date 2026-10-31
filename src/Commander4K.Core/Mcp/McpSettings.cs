using System.Buffers.Text;
using System.Security.Cryptography;
using Commander4K.Core.Security;

namespace Commander4K.Core.Mcp;

/// <summary>The <c>mcp</c> section of <c>config/settings.json</c>: the local MCP server for Claude Code.</summary>
public sealed class McpSettings
{
    public const int DefaultPort = 47800;

    public bool Enabled { get; set; }

    /// <summary>Loopback port of the HTTP endpoint.</summary>
    public int Port { get; set; } = DefaultPort;

    /// <summary>Folders (any VFS path) that tools may touch; empty = none.</summary>
    public List<string> AllowedRoots { get; set => field = value ?? []; } = [];

    /// <summary>Tool name → <c>allow</c>, <c>ask</c> or <c>block</c>; missing = the tool's default.</summary>
    public Dictionary<string, string> ToolModes
    {
        get;
        set => field = CaseInsensitive(value);
    } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The bearer token, DPAPI-encrypted (<see cref="SecretProtector"/>).</summary>
    public string? Token { get; set; }

    /// <summary>A new random token: 32 bytes, base64url without padding.</summary>
    public static string NewToken() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

    /// <summary>The plain token, or <c>null</c> when none is set or it cannot be decrypted.</summary>
    public string? ReadToken() => SecretProtector.Unprotect(Token);

    public void SetToken(string token) => Token = SecretProtector.Protect(token);

    // The JSON reader hands over its own dictionary (default comparer) or null; keep lookups case-insensitive.
    private static Dictionary<string, string> CaseInsensitive(Dictionary<string, string>? value)
    {
        if (value is not null && ReferenceEquals(value.Comparer, StringComparer.OrdinalIgnoreCase))
        {
            return value;
        }

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, mode) in value ?? [])
        {
            result[key] = mode;
        }

        return result;
    }
}
