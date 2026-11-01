using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Commander4K.Core.Folders;
using Commander4K.Core.Localization;
using Commander4K.Core.Security;
using Commander4K.PluginSdk;

namespace Commander4K.Plugins.BuiltIn.Dashboard;

/// <summary>
/// Cards and the view for an HTTP API request from a <c>c4k.dir</c>: the request goes through the host's HTTP client
/// (net permission, audit), <c>{secret:name}</c> placeholders are filled from the <see cref="SecretStore"/> only for
/// the host the secret belongs to, and the chosen fields are shown as values, chart points or status colours.
/// </summary>
public sealed class ApiCards(SecretStore secrets)
{
    private const int MaxExcerpt = 200;
    private static readonly string[] GoodValues = ["true", "ok", "up", "healthy"];

    /// <summary>Target of the button that asks for a missing secret: <c>c4k:secret/&lt;name&gt;</c>.</summary>
    public const string SecretActionPrefix = "c4k:secret/";

    public PluginManifest Manifest { get; } = BuiltInManifest.Create("builtin.preview.api", Loc.T("Podgląd API"),
        Loc.T("Wybrane pola z odpowiedzi API HTTP na kartach dashboardu."), PluginPermissions.Network);

    public async Task<PreviewCard> LoadCardAsync(ApiRequest request, IPluginHost host, PreviewCard? previous, CancellationToken cancellationToken)
    {
        var (response, elapsed, failure) = await SendAsync(request, host, cancellationToken).ConfigureAwait(false);
        if (failure is not null)
        {
            return failure;
        }

        var items = new List<CardItem>();
        var status = CardStatus.Ok;
        if (response!.Status is < 200 or > 299)
        {
            status = CardStatus.Error;
            items.Add(new CardValue("HTTP", response.Status.ToString(CultureInfo.InvariantCulture), CardStatus.Error));
        }

        using var document = TryParse(response.Body);
        if (document is null || request.Fields.Count == 0)
        {
            // Fields were asked for but the answer is a page (a sign-in redirect, a portal): not healthy.
            if (request.Fields.Count > 0)
            {
                items.Add(new CardValue(Loc.T("Odpowiedź"), Loc.T("nie jest JSON-em"), CardStatus.Warning));
                status = status == CardStatus.Ok ? CardStatus.Warning : status;
            }

            items.Add(new CardText(Excerpt(response.Body)));
        }
        else
        {
            var chart = previous?.Items.OfType<CardChart>().FirstOrDefault()?.Chart ?? new ChartData(ChartKind.Line, []);
            var charted = false;
            var chartPosition = -1;
            foreach (var field in request.Fields)
            {
                var label = field.Label ?? LastSegment(field.Path);
                if (!JsonFieldPath.TryGet(document.RootElement, field.Path, out var value))
                {
                    // A missing status field is not "fine": the API changed or answered something else.
                    var missing = field.As == ApiFieldKind.Status ? CardStatus.Warning : CardStatus.None;
                    items.Add(new CardValue(label, "—", missing));
                    status = missing == CardStatus.Warning && status == CardStatus.Ok ? CardStatus.Warning : status;
                    continue;
                }

                var text = JsonFieldPath.Text(value) + (field.Unit is { } unit ? " " + unit : string.Empty);
                switch (field.As)
                {
                    case ApiFieldKind.Status:
                        var good = field.Ok.Count > 0
                            ? field.Ok.Contains(JsonFieldPath.Text(value), StringComparer.OrdinalIgnoreCase)
                            : GoodValues.Contains(JsonFieldPath.Text(value), StringComparer.OrdinalIgnoreCase);
                        items.Add(new CardValue(label, text, good ? CardStatus.Ok : CardStatus.Error));
                        if (!good && status == CardStatus.Ok)
                        {
                            status = CardStatus.Warning;
                        }

                        break;
                    case ApiFieldKind.Chart:
                        items.Add(new CardValue(label, text));
                        if (Number(value) is { } number)
                        {
                            chart = chart.Append(label, new ChartPoint(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0, number));
                            charted = true;
                            chart = chart with { Unit = field.Unit ?? chart.Unit };
                        }

                        chartPosition = chartPosition < 0 ? items.Count : chartPosition;
                        break;
                    default:
                        items.Add(new CardValue(label, text));
                        break;
                }
            }

            if (charted)
            {
                items.Insert(chartPosition, new CardChart(chart));
            }
        }

        items.Add(new CardValue(Loc.T("Czas"), $"{elapsed.TotalMilliseconds:N0} ms"));
        return new PreviewCard(null, null, "🌐", status, items, [new CardAction(Loc.T("Odśwież"), CardActions.Refresh)]);
    }

    /// <summary>The full preview of an API folder: the chosen fields as a table and the formatted response. GET and HEAD only.</summary>
    public async Task<PreviewContent> LoadViewAsync(ApiRequest request, IPluginHost host, CancellationToken cancellationToken)
    {
        if (request.HasSideEffects)
        {
            return new MessagePreview(Loc.T("Podgląd API wysyła tylko GET i HEAD. Zapytania {0} działają jako karta dashboardu, po Twojej zgodzie.", request.Method));
        }

        var (response, elapsed, failure) = await SendAsync(request, host, cancellationToken).ConfigureAwait(false);
        if (failure is not null)
        {
            return new MessagePreview("⚠ " + failure.Subtitle);
        }

        var markdown = new System.Text.StringBuilder();
        markdown.Append("**").Append(request.Method).Append("** `").Append(request.Url).Append("` → **")
            .Append(response!.Status.ToString(CultureInfo.InvariantCulture)).Append("** · ").Append($"{elapsed.TotalMilliseconds:N0} ms").AppendLine().AppendLine();
        using var document = TryParse(response.Body);
        if (document is not null && request.Fields.Count > 0)
        {
            markdown.AppendLine($"| {Loc.T("Pole")} | {Loc.T("Wartość")} |").AppendLine("|---|---|");
            foreach (var field in request.Fields)
            {
                var value = JsonFieldPath.TryGet(document.RootElement, field.Path, out var found) ? JsonFieldPath.Text(found) : "—";
                markdown.Append("| ").Append(field.Label ?? field.Path).Append(" | ").Append(value.Replace("|", "\\|", StringComparison.Ordinal))
                    .Append(field.Unit is { } unit ? " " + unit : string.Empty).AppendLine(" |");
            }

            markdown.AppendLine();
        }

        var body = document is null ? response.Body : JsonSerializer.Serialize(document.RootElement, Indented);
        markdown.AppendLine(document is null ? "```" : "```json").AppendLine(body.Length <= 50_000 ? body : body[..50_000] + "\n…").AppendLine("```");
        return new MarkdownPreview(markdown.ToString(), CanEdit: false);
    }

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>Sends the request with its secrets filled in; a problem before or during sending becomes an error card.</summary>
    private async Task<(HttpResponseData? Response, TimeSpan Elapsed, PreviewCard? Failure)> SendAsync(ApiRequest request, IPluginHost host, CancellationToken cancellationToken)
    {
        // Secrets go in headers or the body only: a URL ends up in logs (the audit log records every request).
        if (!Uri.TryCreate(request.Url, UriKind.Absolute, out var literal))
        {
            return (null, default, Failed(Loc.T("Nieprawidłowy adres „{0}”.", request.Url)));
        }

        if (SecretStore.NamesIn(request.Url).Any())
        {
            return (null, default, Failed(Loc.T("Sekret można wpisać tylko w nagłówku albo w treści zapytania, nie w adresie.")));
        }

        HttpRequestSpec spec;
        try
        {
            // A secret is sent only to the origin (scheme, host and port) it was saved for: no http downgrade, no other port.
            var target = Origin(literal);
            spec = new HttpRequestSpec(
                request.Method,
                literal,
                request.Headers.ToDictionary(h => h.Key, h => secrets.Expand(h.Value, target), StringComparer.OrdinalIgnoreCase),
                request.Body is { } body ? secrets.Expand(body, target) : null,
                request.ContentType);
        }
        catch (SecretMissingException missing)
        {
            return (null, default, Failed(Loc.T("Brak sekretu „{0}”.", missing.Name), new CardAction(Loc.T("Podaj sekret"), SecretActionPrefix + missing.Name)));
        }
        catch (SecretHostMismatchException mismatch)
        {
            return (null, default, Failed(Loc.T("Sekret „{0}” należy do {1} i nie zostanie wysłany gdzie indziej.", mismatch.Name, mismatch.BoundHost)));
        }

        var clock = Stopwatch.StartNew();
        try
        {
            var response = await host.HttpRequestAsync(spec, cancellationToken).ConfigureAwait(false);
            return (response, clock.Elapsed, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or PluginPermissionException or ArgumentException
            || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            return (null, clock.Elapsed, Failed(ex.Message));
        }
    }

    /// <summary>What a secret is bound to: <c>https://api.example.com:443</c>.</summary>
    public static string Origin(Uri uri) => $"{uri.Scheme}://{uri.Host.ToLowerInvariant()}:{uri.Port.ToString(CultureInfo.InvariantCulture)}";

    private static PreviewCard Failed(string message, params CardAction[] actions) =>
        new(null, message, "🌐", CardStatus.Error, [], actions.Length > 0 ? actions : [new CardAction(Loc.T("Odśwież"), CardActions.Refresh)]);

    private static JsonDocument? TryParse(string body)
    {
        try
        {
            return body.TrimStart() is ['{', ..] or ['[', ..] ? JsonDocument.Parse(body) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>A finite number, or null: "NaN", "Infinity" and values beyond double's range never reach a chart.</summary>
    private static double? Number(JsonElement value)
    {
        double? number = value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetDouble(out var d) => d,
            JsonValueKind.String when double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null,
        };
        return number is { } n && double.IsFinite(n) ? n : null;
    }

    private static string LastSegment(string path)
    {
        var cut = path.LastIndexOfAny(['.', ']']);
        return cut < 0 ? path : path[(cut + 1)..] is { Length: > 0 } tail ? tail : path;
    }

    private static string Excerpt(string body)
    {
        var text = body.ReplaceLineEndings(" ").Trim();
        return text.Length <= MaxExcerpt ? text : text[..MaxExcerpt] + "…";
    }
}
