using System.Text.Json.Nodes;
using Commander4K.Core.Mcp;

namespace Commander4K.Core.Tests;

public sealed class McpClientConfigsTests
{
    [Fact]
    public void Every_client_gets_the_current_url_and_token()
    {
        var configs = McpClientConfigs.For("http://127.0.0.1:49000/mcp", "tok123");

        Assert.Equal(configs.Count, configs.Select(c => c.Name).Distinct().Count());
        foreach (var config in configs)
        {
            Assert.Contains("http://127.0.0.1:49000/mcp", config.Text, StringComparison.Ordinal);
            Assert.Contains("Bearer tok123", config.Text, StringComparison.Ordinal);
            if (config.Text.StartsWith('{'))
            {
                Assert.NotNull(JsonNode.Parse(config.Text)); // The JSON snippets must paste as valid JSON.
            }
        }
    }
}
