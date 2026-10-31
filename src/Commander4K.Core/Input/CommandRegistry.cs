using System.Collections.Frozen;

namespace Commander4K.Core.Input;

public readonly record struct CommandInvocation(string CommandId, object? Parameter, CancellationToken CancellationToken);

public sealed record CommandDescriptor(string Id, string Description, Func<CommandInvocation, ValueTask> Handler);

/// <summary>
/// Command id to handler table. Handlers are registered during start-up, then <see cref="Freeze"/> switches lookups
/// to a <see cref="FrozenDictionary{TKey,TValue}"/>.
/// </summary>
public sealed class CommandRegistry
{
    private readonly Dictionary<string, CommandDescriptor> _pending = new(StringComparer.OrdinalIgnoreCase);
    private FrozenDictionary<string, CommandDescriptor>? _frozen;

    public IEnumerable<CommandDescriptor> Commands => (IEnumerable<CommandDescriptor>?)_frozen?.Values ?? _pending.Values;

    public CommandRegistry Register(string id, string description, Func<CommandInvocation, ValueTask> handler)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(handler);
        if (_frozen is not null)
        {
            throw new InvalidOperationException("The command registry is frozen.");
        }

        _pending[id] = new CommandDescriptor(id, description, handler);
        return this;
    }

    public CommandRegistry Register(string id, string description, Action handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return Register(id, description, _ =>
        {
            handler();
            return ValueTask.CompletedTask;
        });
    }

    public void Freeze() => _frozen ??= _pending.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    public bool Contains(string id) => _frozen?.ContainsKey(id) ?? _pending.ContainsKey(id);

    /// <returns><c>false</c> when no handler is registered for <paramref name="id"/>.</returns>
    public async ValueTask<bool> ExecuteAsync(string id, object? parameter = null, CancellationToken cancellationToken = default)
    {
        var found = _frozen is not null ? _frozen.TryGetValue(id, out var descriptor) : _pending.TryGetValue(id, out descriptor);
        if (!found)
        {
            return false;
        }

        await descriptor!.Handler(new CommandInvocation(id, parameter, cancellationToken)).ConfigureAwait(false);
        return true;
    }
}
