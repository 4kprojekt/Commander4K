using System.Text.RegularExpressions;

namespace Commander4K.Plugins.Tests;

/// <summary>
/// The plugin protocol lives in five places: the host's dispatch, three SDK clients and docs/plugins.md. Every method
/// the host answers must be callable from each client and described in the docs.
/// </summary>
public sealed class ProtocolConsistencyTests
{
    [Theory]
    [InlineData("docs/plugins.md")]
    [InlineData("plugins-sdk/python/c4k.py")]
    [InlineData("plugins-sdk/node/c4k.js")]
    [InlineData("plugins-sdk/powershell/c4k.ps1")]
    public void Every_handled_method_is_in(string file)
    {
        var root = RepositoryRoot();
        var methods = new[] { "ExternalPlugin.cs", "MailRpc.cs" }
            .SelectMany(source => Regex.Matches(File.ReadAllText(Path.Combine(root, "src", "Commander4K.Core", "Plugins", source)), "case \"([a-z]+\\.[A-Za-z]+)\":"))
            .Select(match => match.Groups[1].Value)
            .ToList();
        Assert.True(methods.Count > 40, "The dispatch was not found; did the switch move?");

        var text = File.ReadAllText(Path.Combine(root, file));
        var missing = methods.Where(method => !Regex.IsMatch(text, "[`'\"]" + Regex.Escape(method) + "[`'\"]")).ToList();
        Assert.True(missing.Count == 0, $"{file} lacks: {string.Join(", ", missing)}");
    }

    private static string RepositoryRoot()
    {
        var directory = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(directory, "Commander4K.sln")))
        {
            directory = Path.GetDirectoryName(directory) ?? throw new InvalidOperationException("Repository root not found.");
        }

        return directory;
    }
}
