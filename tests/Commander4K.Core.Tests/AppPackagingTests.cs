using System.Xml.Linq;

namespace Commander4K.Core.Tests;

/// <summary>Checks on the App project file; the test projects do not reference the App, so its resources are read from the csproj.</summary>
public sealed class AppPackagingTests
{
    [Fact]
    public void Pdf_viewer_example_is_not_embedded_as_a_template()
    {
        var project = XDocument.Load(Path.Combine(RepositoryRoot(), "src", "Commander4K.App", "Commander4K.App.csproj"));
        var examples = project.Descendants("EmbeddedResource")
            .Where(e => ((string?)e.Attribute("Include"))?.Contains(@"examples\plugins\", StringComparison.OrdinalIgnoreCase) == true)
            .ToList();

        Assert.NotEmpty(examples);
        Assert.All(examples, e =>
        {
            var excluded = ((string?)e.Attribute("Exclude") ?? string.Empty).Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            Assert.Contains(@"..\..\examples\plugins\pdf-viewer-node\**", excluded, StringComparer.OrdinalIgnoreCase);
        });
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
