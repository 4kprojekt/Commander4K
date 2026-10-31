using System.Collections.Concurrent;
using System.Diagnostics;
using Commander4K.Core.Localization;

namespace Commander4K.Core.Ai;

public enum AiProviderStatus : byte
{
    NotSignedIn,
    Ready,

    /// <summary>Rate limit or quota hit; usable again at <see cref="AiProviderState.Until"/>.</summary>
    Waiting,

    /// <summary>The key was refused; sign in again.</summary>
    InvalidKey,
    Disabled,
}

public sealed record AiProviderState(AiProviderInfo Provider, AiProviderStatus Status, DateTimeOffset? Until, string? LastError, string? KeySource, int Order);

/// <summary>One try of one model during a request (shown when everything failed, and in the chat footer).</summary>
public sealed record AiAttempt(string ProviderId, string Model, AiFailureKind? Failure, string? Message);

public sealed record AiAnswer(string Text, AiProviderInfo Provider, string Model, IReadOnlyList<AiAttempt> Attempts, TimeSpan Duration)
{
    /// <summary>Tools the assistant wants called before it answers (empty when it answered in text).</summary>
    public IReadOnlyList<AiToolCall> ToolCalls { get; init; } = [];
}

public sealed class AiUnavailableException(string message, IReadOnlyList<AiAttempt> attempts) : Exception(message)
{
    public IReadOnlyList<AiAttempt> Attempts { get; } = attempts;
}

/// <summary>
/// Sends a chat to the signed-in providers in turn. A provider or model that is rate limited, out of quota, refuses
/// the key or fails is put on a cool-down (Retry-After when the server gives one) and the next one answers instead, so
/// free tiers of several services add up. State is per process; keys live in <see cref="AiCredentialStore"/>.
/// </summary>
public sealed class AiRouter
{
    /// <summary>Models tried per provider before moving on.</summary>
    public const int ModelsPerProvider = 4;

    private static readonly TimeSpan RateLimitWait = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan QuotaWait = TimeSpan.FromHours(1);
    private static readonly TimeSpan TransientWait = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MissingModelWait = TimeSpan.FromHours(6);

    private readonly AiCatalogService _catalog;
    private readonly AiCredentialStore _credentials;
    private readonly OpenAiCompatibleClient _client;
    private readonly Func<string?> _gitHubCliToken;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _cooldowns = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _errors = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, bool> _invalid = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, IReadOnlyList<string>> _discovered = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, IReadOnlyList<string>> _candidates = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, bool> _noTools = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lazy<string?> _ghToken;

    public AiRouter(AiCatalogService catalog, AiCredentialStore credentials, OpenAiCompatibleClient client, Func<string?>? gitHubCliToken = null)
    {
        _catalog = catalog;
        _credentials = credentials;
        _client = client;
        _gitHubCliToken = gitHubCliToken ?? (() => null);
        _ghToken = new Lazy<string?>(() => _gitHubCliToken(), LazyThreadSafetyMode.ExecutionAndPublication);
        credentials.Changed += () =>
        {
            // A new key or a re-enabled provider deserves a fresh chance.
            _invalid.Clear();
            _discovered.Clear();
            StateChanged?.Invoke();
        };
        catalog.Changed += () => StateChanged?.Invoke();
    }

    /// <summary>Clock (replaceable in tests).</summary>
    public TimeProvider Time { get; init; } = TimeProvider.System;

    /// <summary>Raised (on any thread) when a provider's state changed.</summary>
    public event Action? StateChanged;

    /// <summary>At least one provider is signed in and enabled (it may be waiting for its limit).</summary>
    public bool IsConfigured => States().Any(s => s.Status is AiProviderStatus.Ready or AiProviderStatus.Waiting);

    /// <summary>Every catalogue provider with its status, in rotation order.</summary>
    public IReadOnlyList<AiProviderState> States()
    {
        var now = Time.GetUtcNow();
        var credentials = _credentials.All();
        return [.. _catalog.Providers
            .Select((provider, index) =>
            {
                var credential = credentials.GetValueOrDefault(provider.Id);
                var key = ResolveKey(provider, out var source);
                var order = credential?.Priority ?? 1000 + index;
                var error = _errors.GetValueOrDefault(provider.Id);
                if (provider.Auth == AiAuthKinds.None ? credential is null : key is null)
                {
                    return new AiProviderState(provider, AiProviderStatus.NotSignedIn, null, null, null, order);
                }

                if (credential is { Enabled: false })
                {
                    return new AiProviderState(provider, AiProviderStatus.Disabled, null, error, source, order);
                }

                if (_invalid.ContainsKey(provider.Id))
                {
                    return new AiProviderState(provider, AiProviderStatus.InvalidKey, null, error, source, order);
                }

                return _cooldowns.TryGetValue(provider.Id, out var until) && until > now
                    ? new AiProviderState(provider, AiProviderStatus.Waiting, until, error, source, order)
                    : new AiProviderState(provider, AiProviderStatus.Ready, null, error, source, order);
            })
            .OrderBy(s => s.Order)];
    }

    /// <summary>The key for a provider: stored (signed in here) → environment variable → GitHub CLI.</summary>
    public string? ResolveKey(AiProviderInfo provider, out string? source)
    {
        if (_credentials.GetKey(provider.Id) is { Length: > 0 } stored)
        {
            source = _credentials.Get(provider.Id)?.Source ?? "pasted";
            return stored;
        }

        if (provider.EnvVar is { Length: > 0 } variable && Environment.GetEnvironmentVariable(variable) is { Length: > 0 } fromEnvironment)
        {
            source = "env:" + variable;
            return fromEnvironment;
        }

        if (provider.GitHubCli && _ghToken.Value is { Length: > 0 } gh)
        {
            source = "gh";
            return gh;
        }

        source = null;
        return null;
    }

    /// <summary>Clears a provider's cool-down and "invalid key" mark (the user asked to retry).</summary>
    public void Reset(string providerId)
    {
        _invalid.TryRemove(providerId, out _);
        _errors.TryRemove(providerId, out _);
        foreach (var key in _cooldowns.Keys.Where(k => k.Equals(providerId, StringComparison.OrdinalIgnoreCase) || k.StartsWith(providerId + "|", StringComparison.OrdinalIgnoreCase)))
        {
            _cooldowns.TryRemove(key, out _);
        }

        _discovered.TryRemove(providerId, out _);
        StateChanged?.Invoke();
    }

    /// <summary>Asks the first provider that answers; <paramref name="onlyProvider"/> limits the rotation to one (testing a login).</summary>
    /// <remarks>
    /// With <paramref name="tools"/> the answer may carry <see cref="AiAnswer.ToolCalls"/>; a model that rejects tools is
    /// asked again without them (the conversation flattened by <see cref="AiMessage.WithoutTools"/>) and remembered.
    /// </remarks>
    public async Task<AiAnswer> CompleteAsync(IReadOnlyList<AiMessage> messages, IProgress<string>? progress = null,
        string? onlyProvider = null, CancellationToken cancellationToken = default, IReadOnlyList<AiTool>? tools = null)
    {
        var started = Stopwatch.StartNew();
        var attempts = new List<AiAttempt>();
        if (onlyProvider is not null)
        {
            Reset(onlyProvider); // A test ignores the cool-down.
        }

        var states = States().Where(s => onlyProvider is null || string.Equals(s.Provider.Id, onlyProvider, StringComparison.OrdinalIgnoreCase));
        foreach (var state in states.Where(s => s.Status == AiProviderStatus.Ready).ToList())
        {
            var provider = state.Provider;
            var key = ResolveKey(provider, out _);
            progress?.Report(Loc.T("Pytam: {0}…", provider.Name));
            var models = await ModelsAsync(provider, key, cancellationToken).ConfigureAwait(false);
            if (models.Count == 0)
            {
                WaitIfAllModelsWait(provider);
                attempts.Add(new AiAttempt(provider.Id, "-", AiFailureKind.ModelUnavailable, Loc.T("brak dostępnych modeli")));
                continue;
            }

            foreach (var model in models)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var completion = await CallAsync(provider, key, model, messages, tools, cancellationToken).ConfigureAwait(false);
                    _errors.TryRemove(provider.Id, out _);
                    attempts.Add(new AiAttempt(provider.Id, model, null, null));
                    StateChanged?.Invoke();
                    return new AiAnswer(completion.Text, provider, model, attempts, started.Elapsed) { ToolCalls = completion.ToolCalls };
                }
                catch (AiProviderException ex)
                {
                    attempts.Add(new AiAttempt(provider.Id, model, ex.Kind, ex.Message));
                    _errors[provider.Id] = ex.Message;
                    var next = Record(provider, model, ex);
                    StateChanged?.Invoke();
                    if (!next)
                    {
                        break;
                    }

                    progress?.Report(Loc.T("{0}: {1} — próbuję następny model…", provider.Name, ex.Message));
                }
            }

            WaitIfAllModelsWait(provider);
        }

        var waiting = States().Where(s => s.Status == AiProviderStatus.Waiting && s.Until is not null).MinBy(s => s.Until);
        var message = attempts.Count == 0 && waiting is null ? Loc.T("Żaden dostawca AI nie jest zalogowany — otwórz „Dostawcy AI” i zaloguj się.")
            : waiting is not null ? Loc.T("Wszyscy dostawcy AI są chwilowo niedostępni (limity). Najwcześniej: {0} o {1:HH:mm}.", waiting.Provider.Name, waiting.Until!.Value.ToLocalTime())
            : Loc.T("Żaden dostawca AI nie odpowiedział.");
        throw new AiUnavailableException(message, attempts);
    }

    private async Task<AiCompletion> CallAsync(AiProviderInfo provider, string? key, string model, IReadOnlyList<AiMessage> messages,
        IReadOnlyList<AiTool>? tools, CancellationToken cancellationToken)
    {
        var modelKey = provider.Id + "|" + model;
        if (tools is { Count: > 0 } && !_noTools.ContainsKey(modelKey))
        {
            try
            {
                return await _client.CompleteAsync(provider, key, model, messages, tools, cancellationToken).ConfigureAwait(false);
            }
            catch (AiProviderException ex) when (ex.Kind == AiFailureKind.ToolsUnsupported)
            {
                _noTools[modelKey] = true;
            }
        }

        var plain = tools is { Count: > 0 } || messages.Any(m => m.ToolCalls.Count > 0 || m.Role == "tool") ? AiMessage.WithoutTools(messages) : messages;
        return await _client.CompleteAsync(provider, key, model, plain, null, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Puts the failed model/provider on a cool-down; returns whether another model of this provider is worth trying.</summary>
    private bool Record(AiProviderInfo provider, string model, AiProviderException ex)
    {
        var now = Time.GetUtcNow();
        var modelKey = provider.Id + "|" + model;
        switch (ex.Kind)
        {
            case AiFailureKind.RateLimited:
                _cooldowns[modelKey] = now + Clamp(ex.RetryAfter, RateLimitWait);
                return true;
            case AiFailureKind.ModelUnavailable:
                _cooldowns[modelKey] = now + MissingModelWait;
                return true;
            case AiFailureKind.QuotaExhausted:
                _cooldowns[provider.Id] = now + Clamp(ex.RetryAfter, QuotaWait);
                return false;
            case AiFailureKind.Unauthorized:
                _invalid[provider.Id] = true;
                return false;
            case AiFailureKind.Rejected:
                return false;
            default:
                _cooldowns[provider.Id] = now + Clamp(ex.RetryAfter, TransientWait);
                return false;
        }

        static TimeSpan Clamp(TimeSpan? requested, TimeSpan fallback) =>
            requested is { } value && value > TimeSpan.Zero ? (value > TimeSpan.FromDays(1) ? TimeSpan.FromDays(1) : value) : fallback;
    }

    /// <summary>Models to try now: the user's choice, then the catalogue's (or discovered) ones, minus those cooling down.</summary>
    private async Task<IReadOnlyList<string>> ModelsAsync(AiProviderInfo provider, string? key, CancellationToken cancellationToken)
    {
        var preferred = new List<string>();
        if (_credentials.Get(provider.Id)?.Model is { Length: > 0 } chosen)
        {
            preferred.Add(chosen);
        }

        preferred.AddRange(provider.Models);
        if (provider.DiscoverModels is "all" or "openrouter-free")
        {
            if (!_discovered.TryGetValue(provider.Id, out var discovered))
            {
                try
                {
                    discovered = await _client.ListModelsAsync(provider, key, provider.DiscoverModels == "openrouter-free", cancellationToken).ConfigureAwait(false);
                    _discovered[provider.Id] = discovered;
                }
                catch (AiProviderException)
                {
                    discovered = [];
                }
            }

            if (discovered.Count > 0)
            {
                // Keep the preferred order for models that still exist, then the rest of what the server offers.
                var available = discovered.ToHashSet(StringComparer.OrdinalIgnoreCase);
                preferred = [.. preferred.Where(available.Contains), .. discovered];
            }
        }

        var candidates = preferred.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        _candidates[provider.Id] = candidates;
        var now = Time.GetUtcNow();
        return [.. candidates.Where(m => !_cooldowns.TryGetValue(provider.Id + "|" + m, out var until) || until <= now).Take(ModelsPerProvider)];
    }

    /// <summary>When every model of a provider is cooling down, the provider as a whole waits for the first to come back.</summary>
    private void WaitIfAllModelsWait(AiProviderInfo provider)
    {
        if (!_candidates.TryGetValue(provider.Id, out var candidates) || candidates.Count == 0)
        {
            return;
        }

        var now = Time.GetUtcNow();
        var until = candidates.Select(m => _cooldowns.TryGetValue(provider.Id + "|" + m, out var time) ? time : now).Min();
        if (until > now && (!_cooldowns.TryGetValue(provider.Id, out var current) || current < until))
        {
            _cooldowns[provider.Id] = until;
            StateChanged?.Invoke();
        }
    }

    /// <summary>Discovered or catalogue models, for the "model" choice in the providers window.</summary>
    public async Task<IReadOnlyList<string>> AvailableModelsAsync(AiProviderInfo provider, CancellationToken cancellationToken)
    {
        var key = ResolveKey(provider, out _);
        try
        {
            var models = await _client.ListModelsAsync(provider, key, provider.DiscoverModels == "openrouter-free", cancellationToken).ConfigureAwait(false);
            return models.Count > 0 ? models : provider.Models;
        }
        catch (AiProviderException)
        {
            return provider.Models;
        }
    }
}
