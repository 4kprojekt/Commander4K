using System.IO.Enumeration;

namespace Commander4K.Plugins.BuiltIn.Remote;

/// <summary>What <c>~/.ssh/config</c> says about one host: the user and the key files that exist, in file order.</summary>
public sealed record SshHostConfig(string? User, IReadOnlyList<string> IdentityFiles);

/// <summary>
/// Reads <c>User</c> and <c>IdentityFile</c> from the user's OpenSSH config for a host, like <c>ssh</c>: every
/// <c>Host</c> block whose patterns match applies, the first <c>User</c> wins and key files add up.
/// </summary>
public static class SshConfig
{
    private static readonly string Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>Never throws: no file or an unreadable one means no settings.</summary>
    public static SshHostConfig For(string host)
    {
        try
        {
            var config = Parse(File.ReadAllText(Path.Combine(Home, ".ssh", "config")), host);
            return config with { IdentityFiles = [.. config.IdentityFiles.Where(File.Exists)] };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new SshHostConfig(null, []);
        }
    }

    // shortcut: Match, Include, HostName aliases and Port are ignored; add them when someone's config needs them.
    internal static SshHostConfig Parse(string text, string host)
    {
        string? user = null;
        var keys = new List<string>();
        var applies = true; // lines before the first Host block apply to every host
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            var split = line.IndexOfAny([' ', '\t', '=']);
            if (line.Length == 0 || line[0] == '#' || split < 0)
            {
                continue;
            }

            var keyword = line[..split];
            var value = line[split..].TrimStart(' ', '\t', '=').Trim();
            if (keyword.Equals("Host", StringComparison.OrdinalIgnoreCase))
            {
                var patterns = value.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
                applies = patterns.Any(p => p[0] != '!' && FileSystemName.MatchesSimpleExpression(p, host))
                    && !patterns.Any(p => p[0] == '!' && FileSystemName.MatchesSimpleExpression(p.AsSpan(1), host));
            }
            else if (keyword.Equals("Match", StringComparison.OrdinalIgnoreCase))
            {
                applies = false;
            }
            else if (applies && keyword.Equals("User", StringComparison.OrdinalIgnoreCase))
            {
                user ??= value.Trim('"');
            }
            else if (applies && keyword.Equals("IdentityFile", StringComparison.OrdinalIgnoreCase))
            {
                var path = value.Trim('"').Replace("%d", Home, StringComparison.Ordinal);
                keys.Add(Path.GetFullPath(path.StartsWith('~') ? Home + path[1..] : path, Path.Combine(Home, ".ssh")));
            }
        }

        return new SshHostConfig(user, keys);
    }
}
