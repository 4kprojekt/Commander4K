using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Commander4K.Core.Configuration;
using Commander4K.Core.Localization;
using Commander4K.Core.Theming;

namespace Commander4K.Core.Mcp;

/// <param name="Settings">Every setting the settings window shows (<c>AppHost.SettingsCatalog</c>).</param>
/// <param name="OnUi">Runs a change where the settings live (the UI thread in the app; directly in tests).</param>
/// <param name="SettingChanged">After a value was set: apply it (skin, density, columns…) and save settings.json.</param>
/// <param name="SaveKeyBindings">Validates and activates the bindings; throws <see cref="FormatException"/> on a bad sequence.</param>
/// <param name="CommandDescription">Description of a registered command, <c>null</c> when there is no such command.</param>
public sealed record McpConfigContext(
    Func<IReadOnlyList<SettingDescriptor>> Settings,
    AppSettings Values,
    Func<Func<Task>, Task> OnUi,
    Func<string, Task> SettingChanged,
    Func<IReadOnlyDictionary<string, string>> KeyBindings,
    Func<Dictionary<string, string>, Task> SaveKeyBindings,
    Func<string, string?> CommandDescription,
    string ConfigDirectory,
    Func<SkinCatalog> Skins,
    Func<string> CurrentSkinId);

/// <summary>
/// UI-free MCP tools for configuration, key bindings, translations and skins. The client is the model, so these tools
/// hand out what the clipboard prompts contain, check the answer and save it. They write only settings.json,
/// keymap.json, <c>config/i18n/&lt;code&gt;.json</c> (and, in the app, <c>config/skins/&lt;id&gt;.json</c>), with file
/// names built from validated codes, never from a path. Settings that widen MCP's own reach or replace where code
/// comes from are read-only here, whatever the tool mode. Messages are for the model, hence English.
/// </summary>
public static partial class McpConfigTools
{
    public const int DefaultKitPage = 300;
    public const int MaxKitPage = 500;

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// Settings MCP never changes: the MCP server itself (enabled, port, allowed folders, per-tool modes) and the URLs
    /// plugins, AI providers and updates are downloaded from. Otherwise "yes for this session" on set_setting would let
    /// the model grant itself folders or switch delete to "allow".
    /// </summary>
    public static bool IsReadOnly(string key) =>
        key.StartsWith("mcp", StringComparison.OrdinalIgnoreCase)
        || key.Equals("pluginCatalogUrl", StringComparison.OrdinalIgnoreCase)
        || key.Equals("aiCatalogUrl", StringComparison.OrdinalIgnoreCase)
        || key.Equals("updateRepository", StringComparison.OrdinalIgnoreCase);

    /// <summary>A language code usable as a file name: <c>de</c>, <c>pt-BR</c>; not <c>pl</c> (the source language).</summary>
    public static bool IsLanguageCode(string? code) =>
        code is not null && LanguageCode().IsMatch(code) && !code.Equals(Loc.SourceLanguage, StringComparison.OrdinalIgnoreCase)
        && !IsDeviceName(code);

    /// <summary>A skin id usable as a file name (lower-case kebab-case, as <see cref="SkinValidator"/> asks).</summary>
    public static bool IsSkinId(string? id) => id is not null && SkinId().IsMatch(id) && !IsDeviceName(id);

    // Windows device names (con.json can still mean the console); the patterns already exclude everything else.
    private static bool IsDeviceName(string name) =>
        name.ToLowerInvariant() is "con" or "prn" or "aux" or "nul"
        || (name.Length == 4 && (name.StartsWith("com", StringComparison.OrdinalIgnoreCase) || name.StartsWith("lpt", StringComparison.OrdinalIgnoreCase)) && char.IsAsciiDigit(name[3]));

    /// <summary>Parses a skin for check_skin/save_skin: the skin and its validator warnings, or an error for the model.</summary>
    public static (Skin? Skin, IReadOnlyList<string> Warnings, string? Error) ParseSkin(string json)
    {
        Skin skin;
        try
        {
            skin = SkinJson.Deserialize(json);
        }
        catch (JsonException ex)
        {
            return (null, [], "The skin JSON is not valid: " + ex.Message + " Call get_skin_guide for the schema.");
        }

        if (!IsSkinId(skin.Id))
        {
            return (null, [], $"The skin id '{skin.Id}' must be lower-case letters, digits and hyphens (max 64), e.g. \"solarized-dark\".");
        }

        return (skin, SkinValidator.Validate(skin), null);
    }

    public static IReadOnlyList<McpTool> Create(McpConfigContext context) =>
    [
        new McpTool(
            new McpToolDefinition(
                "list_settings",
                "Lists Commander4K settings (the same as the settings window, Ctrl+,): key, category, title, description, kind (toggle/text/choice), "
                + "current value, allowed values for choices, whether a restart is needed, and whether it is read-only over MCP. "
                + "query searches titles, descriptions and keywords (Polish or English words, accents optional).",
                McpSchema.Object(("query", "string", "Words to search for; empty = all settings.", false)),
                McpToolMode.Allow),
            (call, _) => Task.FromResult(ListSettings(context, call.Optional("query"))),
            args => "list_settings " + args["query"]),
        new McpTool(
            new McpToolDefinition(
                "set_setting",
                "Changes one setting (key from list_settings) and applies it at once (skin, density, columns…), then saves it. "
                + "toggle: true/false; choice: one of the allowed values; text: the text. Settings marked read-only (the MCP server, catalog and update URLs) "
                + "can only be changed by the user in the settings window.",
                McpSchema.Object(("key", "string", "Setting key.", true), ("value", "string", "New value.", true)),
                McpToolMode.Ask),
            (call, _) => SetSettingAsync(context, call.Required("key"), call.Optional("value") ?? string.Empty),
            args => $"set_setting {args["key"]} = {args["value"]?.ToJsonString()}"),
        new McpTool(
            new McpToolDefinition(
                "list_key_bindings",
                "Lists keyboard shortcuts: keys → command id (description). Key names follow Avalonia's Key enum, e.g. \"Ctrl+Shift+P\", \"Alt+F7\"; "
                + "chords are separated by a space (\"Ctrl+K Ctrl+S\").",
                McpSchema.Object(("filter", "string", "Only bindings whose keys, command or description contain this text.", false)),
                McpToolMode.Allow),
            (call, _) => Task.FromResult(ListKeyBindings(context, call.Optional("filter"))),
            args => "list_key_bindings " + args["filter"]),
        new McpTool(
            new McpToolDefinition(
                "set_key_binding",
                "Binds keys to a command (id from list_commands), replacing what those keys did; without command the binding is removed. "
                + "Active at once and saved to keymap.json.",
                McpSchema.Object(
                    ("keys", "string", "Key sequence, e.g. \"Ctrl+Alt+N\".", true),
                    ("command", "string", "Command id, e.g. \"cm_OpenNetwork\"; omit to remove the binding.", false)),
                McpToolMode.Ask),
            (call, _) => SetKeyBindingAsync(context, call.Required("keys"), call.Optional("command")),
            args => $"set_key_binding {args["keys"]} → {args["command"]?.ToString() ?? "(none)"}"),
        new McpTool(
            new McpToolDefinition(
                "get_translation_kit",
                "Material for translating the Commander4K user interface: the rules, the number of plural forms, and one page of entries "
                + "(Polish key → English value) from the reference file. Translate the entries, check them with check_translation and "
                + "save them with save_translation in batches; call again with the next offset until there is no 'next offset'. "
                + "onlyMissing lists only the entries the language file does not have yet (to complete or update a language).",
                McpSchema.Object(
                    ("language", "string", "Language code, e.g. \"de\", \"uk\", \"pt-BR\".", true),
                    ("name", "string", "Language name in English (default: from the code).", false),
                    ("onlyMissing", "boolean", "Only entries missing from config/i18n/<language>.json.", false),
                    ("offset", "integer", "First entry of the page (default 0).", false),
                    ("limit", "integer", $"Entries per page (default {DefaultKitPage}, max {MaxKitPage}).", false)),
                McpToolMode.Allow),
            (call, _) => Task.Run(() => Guarded(() => TranslationKitPage(context, call)), CancellationToken.None),
            args => "get_translation_kit " + args["language"]),
        new McpTool(
            new McpToolDefinition(
                "check_translation",
                "Checks translated entries as they would be after merging into config/i18n/<language>.json, without saving: unknown keys, "
                + "placeholders ({0}, {1:N0}…) that differ from the key, wrong number of plural forms, and how many entries are still missing.",
                TranslationSchema(),
                McpToolMode.Allow),
            (call, _) => Task.Run(() => Guarded(() => CheckTranslation(context, call, save: false)), CancellationToken.None),
            args => $"check_translation {args["language"]} ({(args["entries"] as JsonObject)?.Count ?? 0} entries)"),
        new McpTool(
            new McpToolDefinition(
                "save_translation",
                "Merges translated entries into config/i18n/<language>.json (new values replace old ones) and saves it. Refused when an entry has "
                + "an unknown key or wrong placeholders/plural forms (nothing is saved); missing entries are only counted (the app shows English for them). "
                + "To use the language: set_setting language = <code>, then the user restarts Commander4K.",
                TranslationSchema(),
                McpToolMode.Ask),
            (call, _) => Task.Run(() => Guarded(() => CheckTranslation(context, call, save: true)), CancellationToken.None),
            args => $"save_translation {args["language"]} ({(args["entries"] as JsonObject)?.Count ?? 0} entries)"),
        new McpTool(
            new McpToolDefinition(
                "get_skin_guide",
                "How to design a Commander4K skin: the design brief, the JSON Schema of a skin and the current skin as a starting point. "
                + "Then check_skin your JSON, fix the warnings and save_skin it.",
                McpSchema.Object(),
                McpToolMode.Allow),
            (_, _) => Task.FromResult(SkinGuide(context)),
            _ => "get_skin_guide"),
        new McpTool(
            new McpToolDefinition(
                "list_skins",
                "Lists the skins: id, name, built-in or the user's, which one is current, and contrast warnings.",
                McpSchema.Object(),
                McpToolMode.Allow),
            (_, _) => Task.FromResult(ListSkins(context)),
            _ => "list_skins"),
        new McpTool(
            new McpToolDefinition(
                "get_skin",
                "Returns one skin as JSON (to change it and save it under a new id).",
                McpSchema.Object(("id", "string", "Skin id from list_skins.", true)),
                McpToolMode.Allow),
            (call, _) => Task.FromResult(GetSkin(context, call.Required("id"))),
            args => "get_skin " + args["id"]),
        new McpTool(
            new McpToolDefinition(
                "check_skin",
                "Validates skin JSON without changing anything: schema, id, and WCAG contrast of text against its background.",
                McpSchema.Object(("json", "string", "The complete skin JSON.", true)),
                McpToolMode.Allow),
            (call, _) => Task.FromResult(CheckSkin(call.Required("json"))),
            _ => "check_skin"),
    ];

    private static JsonObject TranslationSchema()
    {
        var schema = McpSchema.Object(
            ("language", "string", "Language code, e.g. \"de\".", true),
            ("entries", "object", "Polish key → translated text, keys exactly as in get_translation_kit.", true));
        schema["properties"]!["entries"]!["additionalProperties"] = new JsonObject { ["type"] = "string" };
        return schema;
    }

    private static McpToolResult ListSettings(McpConfigContext context, string? query)
    {
        var settings = SettingsCatalog.Search(context.Settings(), query, s => s.Title, s => [s.Key, s.Description, s.Category, .. s.Keywords ?? []]);
        if (settings.Count == 0)
        {
            return new McpToolResult($"No setting matches '{query}'.");
        }

        var text = new StringBuilder();
        foreach (var setting in settings)
        {
            text.Append("key: ").Append(setting.Key).Append(" (").Append(setting.Kind.ToString().ToLowerInvariant()).Append(')');
            if (IsReadOnly(setting.Key))
            {
                text.Append(", read-only over MCP");
            }

            if (setting.RequiresRestart)
            {
                text.Append(", needs a restart");
            }

            text.AppendLine();
            text.Append("  ").Append(setting.Category).Append(" › ").AppendLine(setting.Title);
            text.Append("  ").AppendLine(setting.Description);
            text.Append("  value: ").AppendLine(Format(setting.Get(context.Values)));
            if (setting.Kind == SettingKind.Choice && setting.Choices?.Invoke() is { Count: > 0 } choices)
            {
                text.Append("  values: ").AppendLine(string.Join(", ", choices.Select(c => c.Value == c.Label ? c.Value : $"{c.Value} ({c.Label})")));
            }
        }

        return new McpToolResult(text.ToString());
    }

    private static async Task<McpToolResult> SetSettingAsync(McpConfigContext context, string key, string value)
    {
        // Before anything else: a session grant for set_setting must not reach these.
        if (IsReadOnly(key))
        {
            return McpToolResult.Error($"'{key}' can only be changed in the Commander4K settings window.");
        }

        if (context.Settings().FirstOrDefault(s => s.Key.Equals(key, StringComparison.OrdinalIgnoreCase)) is not { } setting)
        {
            return McpToolResult.Error($"No setting '{key}'. Call list_settings to find the key.");
        }

        object? parsed;
        switch (setting.Kind)
        {
            case SettingKind.Toggle when bool.TryParse(value.Trim(), out var flag):
                parsed = flag;
                break;
            case SettingKind.Toggle:
                return McpToolResult.Error($"'{setting.Key}' is a toggle: use true or false.");
            case SettingKind.Choice:
                var choices = setting.Choices?.Invoke() ?? [];
                if (choices.FirstOrDefault(c => c.Value.Equals(value.Trim(), StringComparison.OrdinalIgnoreCase)) is not { } choice)
                {
                    return McpToolResult.Error($"'{value}' is not allowed for '{setting.Key}'. Allowed: {string.Join(", ", choices.Select(c => c.Value))}.");
                }

                parsed = choice.Value;
                break;
            default:
                parsed = value;
                break;
        }

        string? before = null;
        string? after = null;
        try
        {
            await context.OnUi(async () =>
            {
                before = Format(setting.Get(context.Values));
                setting.Set(context.Values, parsed);
                after = Format(setting.Get(context.Values));
                await context.SettingChanged(setting.Key).ConfigureAwait(false);
            }).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or InvalidCastException)
        {
            return McpToolResult.Error($"'{value}' is not a valid value for '{setting.Key}': {ex.Message}");
        }

        var restart = setting.RequiresRestart ? " It takes effect after the user restarts Commander4K." : string.Empty;
        return new McpToolResult($"{setting.Key}: {before} → {after}.{restart}");
    }

    private static McpToolResult ListKeyBindings(McpConfigContext context, string? filter)
    {
        var lines = context.KeyBindings()
            .Select(b => $"{b.Key} → {b.Value} ({context.CommandDescription(b.Value) ?? "unknown command"})")
            .Where(line => string.IsNullOrWhiteSpace(filter) || line.Contains(filter.Trim(), StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new McpToolResult(lines.Count == 0 ? "No key bindings match." : string.Join('\n', lines));
    }

    private static async Task<McpToolResult> SetKeyBindingAsync(McpConfigContext context, string keys, string? command)
    {
        keys = keys.Trim();
        if (command is not null && context.CommandDescription(command) is null)
        {
            return McpToolResult.Error($"No command '{command}'. Call list_commands for the ids.");
        }

        string? replaced = null;
        try
        {
            await context.OnUi(async () =>
            {
                var bindings = new Dictionary<string, string>(context.KeyBindings(), StringComparer.OrdinalIgnoreCase);
                replaced = bindings.GetValueOrDefault(keys);
                if (command is null)
                {
                    bindings.Remove(keys);
                }
                else
                {
                    bindings[keys] = command;
                }

                await context.SaveKeyBindings(bindings).ConfigureAwait(false);
            }).ConfigureAwait(false);
        }
        catch (FormatException ex)
        {
            return McpToolResult.Error($"'{keys}' is not a valid key sequence: {ex.Message}");
        }

        var was = replaced is null ? "it was not bound before" : $"it was bound to {replaced}";
        return new McpToolResult(command is null
            ? (replaced is null ? $"{keys} was not bound." : $"Removed {keys} (was {replaced}).")
            : $"{keys} → {command}; {was}.");
    }

    private static McpToolResult TranslationKitPage(McpConfigContext context, McpCall call)
    {
        var code = call.Required("language").Trim();
        if (!IsLanguageCode(code))
        {
            return McpToolResult.Error(BadLanguage(code));
        }

        var name = call.Optional("name") is { Length: > 0 } given ? given.Trim() : LanguageName(code);
        var onlyMissing = call.Optional("onlyMissing") is { } flag && bool.TryParse(flag, out var only) && only;
        var offset = Math.Max(0, ReadInt(call, "offset") ?? 0);
        var limit = Math.Clamp(ReadInt(call, "limit") ?? DefaultKitPage, 1, MaxKitPage);

        var reference = Reference();
        IEnumerable<KeyValuePair<string, string>> entries = reference;
        if (onlyMissing)
        {
            var existing = ReadLanguageFile(context, code) ?? [];
            entries = reference.Where(e => !existing.ContainsKey(e.Key));
        }

        var all = entries.ToList();
        var page = all.Skip(offset).Take(limit).ToDictionary(e => e.Key, e => e.Value);

        // The rules of the clipboard prompt, without the pasted file and the "answer with one code block" ending.
        var prompt = TranslationKit.PromptFor(code, name);
        var cut = prompt.IndexOf("Answer with one", StringComparison.Ordinal);
        var rules = (cut > 0 ? prompt[..cut] : prompt).TrimEnd();

        var text = new StringBuilder()
            .AppendLine(rules)
            .AppendLine()
            .AppendLine("Send the translated entries (same keys, translated values) to save_translation in batches; check_translation checks without saving.")
            .Append(CultureInfo.InvariantCulture, $"plural forms: {TranslationKit.PluralForms(code)}").AppendLine()
            .Append(CultureInfo.InvariantCulture, $"{all.Count} entries{(onlyMissing ? " missing" : string.Empty)}; this page: {offset}..{offset + page.Count - 1}").AppendLine();
        if (offset + page.Count < all.Count)
        {
            text.Append(CultureInfo.InvariantCulture, $"next offset: {offset + page.Count}").AppendLine();
        }

        text.AppendLine().Append(JsonSerializer.Serialize(page, WriteOptions));
        return new McpToolResult(text.ToString());
    }

    private static McpToolResult CheckTranslation(McpConfigContext context, McpCall call, bool save)
    {
        var code = call.Required("language").Trim();
        if (!IsLanguageCode(code))
        {
            return McpToolResult.Error(BadLanguage(code));
        }

        if (call.Arguments["entries"] is not JsonObject given)
        {
            return McpToolResult.Error("'entries' must be an object: Polish key → translated text.");
        }

        var entries = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, node) in given)
        {
            if (node is not JsonValue value || !value.TryGetValue<string>(out var text))
            {
                return McpToolResult.Error($"The value of \"{key}\" must be a string.");
            }

            entries[key] = text;
        }

        Dictionary<string, string> merged;
        try
        {
            merged = ReadLanguageFile(context, code) ?? [];
        }
        catch (JsonException ex)
        {
            return McpToolResult.Error($"The existing config/i18n/{code}.json is not valid JSON ({ex.Message}); the user has to fix or delete it first.");
        }

        foreach (var (key, value) in entries)
        {
            merged[key] = value;
        }

        var reference = Reference();
        var issues = TranslationKit.Check(JsonSerializer.Serialize(merged), code);

        // Only the entries of this call can block a save; problems already in the file are reported, not held against it.
        var problems = issues.Where(i => entries.ContainsKey(i.Key)).ToList();
        var older = issues.Count(i => merged.ContainsKey(i.Key) && !entries.ContainsKey(i.Key));
        var missing = reference.Keys.Count(k => !merged.ContainsKey(k));
        var report = new StringBuilder();
        foreach (var issue in problems.Take(50))
        {
            report.Append(JsonSerializer.Serialize(issue.Key, WriteOptions)).Append(": ").AppendLine(issue.Problem);
        }

        if (problems.Count > 50)
        {
            report.Append(CultureInfo.InvariantCulture, $"… and {problems.Count - 50} more.").AppendLine();
        }

        var summary = $"{entries.Count} entries in this call, {problems.Count} with problems; after merging: {merged.Count} entries, {missing} still missing"
            + (older > 0 ? $", {older} older entries in the file have problems" : string.Empty) + ".";
        if (problems.Count > 0)
        {
            return save
                ? McpToolResult.Error("Nothing saved. Fix these entries:\n" + report + summary)
                : new McpToolResult("Problems:\n" + report + summary);
        }

        if (!save)
        {
            return new McpToolResult("OK. " + summary);
        }

        WriteLanguageFile(context, code, merged, reference);
        return new McpToolResult($"Saved config/i18n/{code}.json. {summary} To use it: set_setting language = {code}, then the user restarts Commander4K.");
    }

    private static McpToolResult SkinGuide(McpConfigContext context)
    {
        var current = context.Skins().Get(context.CurrentSkinId());
        return new McpToolResult(new StringBuilder()
            .AppendLine(SkinPrompt.DesignBrief)
            .AppendLine("The skin JSON must validate against this JSON Schema:")
            .AppendLine(SkinSchema.CreateJson())
            .AppendLine()
            .AppendLine("The current skin, as a starting point (give yours a new id):")
            .AppendLine(SkinJson.Serialize(current))
            .AppendLine()
            .Append("Check it with check_skin, fix the warnings, then save_skin (the user sees it live before accepting).")
            .ToString());
    }

    private static McpToolResult ListSkins(McpConfigContext context)
    {
        var current = context.CurrentSkinId();
        var text = new StringBuilder();
        foreach (var skin in context.Skins().Skins)
        {
            var builtIn = BuiltInSkins.All.Any(b => b.Id.Equals(skin.Id, StringComparison.OrdinalIgnoreCase) && ReferenceEquals(b, skin));
            var tags = new List<string>();
            if (skin.Id.Equals(current, StringComparison.OrdinalIgnoreCase))
            {
                tags.Add("current");
            }

            tags.Add(builtIn ? "built-in" : "user");
            var warnings = SkinValidator.Validate(skin).Count;
            text.Append(skin.Id).Append(" (").Append(string.Join(", ", tags)).Append("): ").Append(skin.DisplayName);
            text.AppendLine(warnings > 0 ? $" — {warnings} contrast warnings" : string.Empty);
        }

        return new McpToolResult(text.ToString());
    }

    private static McpToolResult GetSkin(McpConfigContext context, string id) =>
        context.Skins().Skins.FirstOrDefault(s => s.Id.Equals(id.Trim(), StringComparison.OrdinalIgnoreCase)) is { } skin
            ? new McpToolResult(SkinJson.Serialize(skin))
            : McpToolResult.Error($"No skin '{id}'. Call list_skins for the ids.");

    private static McpToolResult CheckSkin(string json)
    {
        var (skin, warnings, error) = ParseSkin(json);
        if (error is not null)
        {
            return McpToolResult.Error(error);
        }

        return warnings.Count == 0
            ? new McpToolResult($"OK: {skin!.Id} ({skin.DisplayName}) is valid and readable.")
            : new McpToolResult("Warnings (fix them before save_skin):\n- " + string.Join("\n- ", warnings));
    }

    /// <summary>
    /// Expected failures (missing argument, unreadable or broken language file, disk errors) become tool errors here,
    /// inside the Task.Run lambda, so they never leave it (debugger rule in CLAUDE.md).
    /// </summary>
    private static McpToolResult Guarded(Func<McpToolResult> run)
    {
        try
        {
            return run();
        }
        catch (JsonException ex)
        {
            return McpToolResult.Error("The existing language file is not valid JSON; the user has to fix or delete it first. " + ex.Message);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return McpToolResult.Error(ex.Message);
        }
    }

    private static Dictionary<string, string> Reference() => Loc.BuiltInFile("en") is { } en ? Loc.Parse(en) : [];

    private static string LanguageFile(McpConfigContext context, string code) =>
        Path.Combine(context.ConfigDirectory, Loc.FolderName, code + ".json");

    /// <summary>The user's language file, or <c>null</c> when there is none.</summary>
    private static Dictionary<string, string>? ReadLanguageFile(McpConfigContext context, string code)
    {
        var path = LanguageFile(context, code);
        return File.Exists(path) ? Loc.Parse(File.ReadAllText(path)) : null;
    }

    /// <summary>Keys in the reference order (so diffs stay readable), anything else after them; temp file + move.</summary>
    private static void WriteLanguageFile(McpConfigContext context, string code, Dictionary<string, string> entries, Dictionary<string, string> reference)
    {
        var ordered = new JsonObject();
        foreach (var key in reference.Keys.Where(entries.ContainsKey).Concat(entries.Keys.Where(k => !reference.ContainsKey(k))))
        {
            ordered[key] = entries[key];
        }

        var path = LanguageFile(context, code);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, ordered.ToJsonString(WriteOptions) + "\n");
        File.Move(temp, path, overwrite: true);
    }

    private static string LanguageName(string code)
    {
        try
        {
            return CultureInfo.GetCultureInfo(code).EnglishName;
        }
        catch (CultureNotFoundException)
        {
            return code;
        }
    }

    private static string BadLanguage(string code) => code.Equals(Loc.SourceLanguage, StringComparison.OrdinalIgnoreCase)
        ? "Polish is the source language of Commander4K; it has no translation file."
        : $"'{code}' is not a language code. Use a code like \"de\", \"uk\" or \"pt-BR\".";

    private static int? ReadInt(McpCall call, string name) =>
        call.Optional(name) is { } text && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;

    private static string Format(object? value) => value switch
    {
        null => "(empty)",
        bool flag => flag ? "true" : "false",
        string { Length: 0 } => "(empty)",
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty,
    };

    // \z, not $: $ also matches before a final newline, which would end up in the file name.
    [GeneratedRegex(@"^[a-z]{2,3}(-[A-Za-z0-9]{2,8})?\z")]
    private static partial Regex LanguageCode();

    [GeneratedRegex(@"^[a-z0-9][a-z0-9-]{0,63}\z")]
    private static partial Regex SkinId();
}
