using System.Text.Json;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Beta.Messages;
using Commander4K.Core.Localization;
using Commander4K.Core.Theming;

namespace Commander4K.App.Theming;

public sealed record SkinGenerationResult(Skin? Skin, string? Json, string? Error)
{
    public static SkinGenerationResult Failed(string error) => new(null, null, error);
}

/// <summary>
/// Designs a skin from a free-text description with Claude. Structured outputs constrain the reply to the skin
/// schema, so the response parses straight into <see cref="Skin"/>. Credentials come from the standard SDK chain
/// (<c>ANTHROPIC_API_KEY</c>, or a profile created with <c>ant auth login</c>).
/// </summary>
public sealed class ClaudeSkinGenerator
{
    public const string Model = "claude-opus-5-5";

    private static readonly Lazy<Dictionary<string, JsonElement>> Schema = new(() =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(SkinSchema.CreateForStructuredOutput().ToJsonString())!);

    public async Task<SkinGenerationResult> GenerateAsync(string description, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(description);

        BetaMessage response;
        try
        {
            AnthropicClient client = new();
            response = await client.Beta.Messages.Create(
                new MessageCreateParams
                {
                    Model = Model,
                    MaxTokens = 16000,
                    // A refused request is re-served by the fallback model inside the same call.
                    Betas = ["server-side-fallback-2026-06-01"],
                    Fallbacks = new List<BetaFallbackParam> { new() { Model = "claude-opus-4-8" } },
                    System = SkinPrompt.DesignBrief,
                    OutputConfig = new BetaOutputConfig
                    {
                        Effort = Effort.Medium,
                        Format = new BetaJsonOutputFormat { Schema = Schema.Value },
                    },
                    Messages = [new BetaUserMessageParam { Content = SkinPrompt.ForApi(description) }],
                },
                cancellationToken);
        }
        catch (AnthropicUnauthorizedException)
        {
            return SkinGenerationResult.Failed(
                Loc.T("Brak lub nieprawidłowy klucz API Claude. Ustaw zmienną środowiskową ANTHROPIC_API_KEY (albo zaloguj się poleceniem „ant auth login”) i uruchom Commander4K ponownie. Bez klucza możesz użyć przycisku „Kopiuj prompt” i wygenerować skórkę w Claude.ai lub Claude Design."));
        }
        catch (AnthropicRateLimitException)
        {
            return SkinGenerationResult.Failed(Loc.T("Przekroczono limit zapytań do Claude API. Spróbuj ponownie za chwilę."));
        }
        catch (AnthropicApiException ex)
        {
            return SkinGenerationResult.Failed(Loc.T("Claude API zwróciło błąd: {0}", ex.Message));
        }
        catch (HttpRequestException ex)
        {
            return SkinGenerationResult.Failed(Loc.T("Brak połączenia z Claude API: {0}", ex.Message));
        }

        if (response.StopReason == "refusal")
        {
            return SkinGenerationResult.Failed(Loc.T("Claude odmówił wygenerowania tej skórki. Spróbuj innego opisu."));
        }

        if (response.StopReason == "max_tokens")
        {
            return SkinGenerationResult.Failed(Loc.T("Odpowiedź została ucięta (limit tokenów). Spróbuj krótszego opisu."));
        }

        var json = string.Concat(response.Content
            .Select(block => block.TryPickText(out var text) ? text.Text : null)
            .Where(text => text is not null));
        try
        {
            return new SkinGenerationResult(SkinJson.Deserialize(json), json, null);
        }
        catch (JsonException ex)
        {
            // Colours are validated here rather than by the schema (structured outputs reject "pattern").
            return new SkinGenerationResult(null, json, Loc.T("Wygenerowany JSON jest niepoprawny: {0}", ex.Message));
        }
    }
}
