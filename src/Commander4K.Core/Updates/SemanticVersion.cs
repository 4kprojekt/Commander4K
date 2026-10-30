using System.Globalization;
using System.Reflection;

namespace Commander4K.Core.Updates;

/// <summary>SemVer 2.0 version (build metadata after '+' is kept for display but ignored when comparing).</summary>
public readonly record struct SemanticVersion(int Major, int Minor, int Patch, string? Prerelease = null, string? Build = null)
    : IComparable<SemanticVersion>
{
    public bool IsPrerelease => !string.IsNullOrEmpty(Prerelease);

    public static bool operator <(SemanticVersion a, SemanticVersion b) => a.CompareTo(b) < 0;

    public static bool operator >(SemanticVersion a, SemanticVersion b) => a.CompareTo(b) > 0;

    public static bool operator <=(SemanticVersion a, SemanticVersion b) => a.CompareTo(b) <= 0;

    public static bool operator >=(SemanticVersion a, SemanticVersion b) => a.CompareTo(b) >= 0;

    /// <summary>Accepts an optional leading "v" (git tags) and a missing patch number.</summary>
    public static bool TryParse(string? text, out SemanticVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var value = text.Trim().TrimStart('v', 'V');
        string? build = null;
        var plus = value.IndexOf('+');
        if (plus >= 0)
        {
            build = value[(plus + 1)..];
            value = value[..plus];
        }

        string? prerelease = null;
        var dash = value.IndexOf('-');
        if (dash >= 0)
        {
            prerelease = value[(dash + 1)..];
            value = value[..dash];
        }

        var parts = value.Split('.');
        if (parts.Length is < 2 or > 3
            || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var major)
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var minor))
        {
            return false;
        }

        var patch = 0;
        if (parts.Length == 3 && !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out patch))
        {
            return false;
        }

        version = new SemanticVersion(major, minor, patch, string.IsNullOrEmpty(prerelease) ? null : prerelease, build);
        return true;
    }

    public static SemanticVersion Parse(string text) =>
        TryParse(text, out var version) ? version : throw new FormatException($"'{text}' is not a semantic version.");

    public int CompareTo(SemanticVersion other)
    {
        var result = Major.CompareTo(other.Major);
        if (result == 0) result = Minor.CompareTo(other.Minor);
        if (result == 0) result = Patch.CompareTo(other.Patch);
        if (result != 0)
        {
            return result;
        }

        // A pre-release sorts before its release; identifiers compare numerically when both are numbers.
        if (IsPrerelease != other.IsPrerelease)
        {
            return IsPrerelease ? -1 : 1;
        }

        if (!IsPrerelease)
        {
            return 0;
        }

        var mine = Prerelease!.Split('.');
        var theirs = other.Prerelease!.Split('.');
        for (var i = 0; i < Math.Min(mine.Length, theirs.Length); i++)
        {
            var bothNumeric = int.TryParse(mine[i], out var a) & int.TryParse(theirs[i], out var b);
            result = bothNumeric ? a.CompareTo(b) : string.CompareOrdinal(mine[i], theirs[i]);
            if (result != 0)
            {
                return result;
            }
        }

        return mine.Length.CompareTo(theirs.Length);
    }

    /// <summary>Without build metadata, e.g. <c>0.2.0-beta.1</c>.</summary>
    public override string ToString() =>
        $"{Major}.{Minor}.{Patch}{(IsPrerelease ? "-" + Prerelease : string.Empty)}";
}

/// <summary>Version of the running application, from the entry assembly's informational version.</summary>
public static class AppVersion
{
    private static readonly Lazy<SemanticVersion> Lazy = new(() =>
    {
        var assembly = Assembly.GetEntryAssembly() ?? typeof(AppVersion).Assembly;
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        return SemanticVersion.TryParse(informational, out var version) ? version : new SemanticVersion(0, 0, 0);
    });

    public static SemanticVersion Current => Lazy.Value;

    /// <summary>e.g. <c>0.2.0 (a1b2c3d)</c>: version plus the short commit it was built from, when known.</summary>
    public static string Display
    {
        get
        {
            var commit = Current.Build is { Length: > 0 } build ? build.Split('.')[0] : null;
            return commit is null ? Current.ToString() : $"{Current} ({commit[..Math.Min(7, commit.Length)]})";
        }
    }
}
