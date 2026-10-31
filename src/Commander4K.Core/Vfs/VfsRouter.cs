using System.Collections.Frozen;
using System.Threading.Channels;
using Commander4K.Core.Execution;
using Commander4K.PluginSdk;

namespace Commander4K.Core.Vfs;

/// <summary>
/// Routes paths to <see cref="IVfsProviderPlugin"/>s by scheme and exposes their listings as resilient streams. Every
/// call is safe to make from the UI thread: provider code only ever runs on background threads.
/// </summary>
public sealed class VfsRouter
{
    private const int ListingChannelCapacity = 16_384;

    private readonly ResilientAsyncExecutor _executor;
    private readonly Lock _registrationGate = new();
    private readonly List<IVfsProviderPlugin> _registered = [];
    private FrozenDictionary<string, IVfsProviderPlugin> _byScheme = FrozenDictionary<string, IVfsProviderPlugin>.Empty;

    public VfsRouter(ResilientAsyncExecutor executor, IEnumerable<IVfsProviderPlugin>? providers = null)
    {
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        if (providers is not null)
        {
            foreach (var provider in providers)
            {
                Register(provider);
            }
        }
    }

    /// <summary>Applied between two consecutive entries of a listing; a stall longer than this aborts it.</summary>
    public TimeSpan ListingStallTimeout { get; init; } = TimeSpan.FromSeconds(45);

    public IReadOnlyCollection<IVfsProviderPlugin> Providers => _byScheme.Values;

    /// <summary>Registers a provider; later registrations win for schemes claimed twice.</summary>
    public void Register(IVfsProviderPlugin provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        lock (_registrationGate)
        {
            _registered.Add(provider);
            Rebuild();
        }
    }

    public bool TryResolve(string path, out IVfsProviderPlugin provider)
    {
        var scheme = VfsPath.GetScheme(path);
        if (scheme is not null && Volatile.Read(ref _byScheme).TryGetValue(scheme, out var found))
        {
            provider = found;
            return true;
        }

        provider = null!;
        return false;
    }

    public IVfsProviderPlugin Resolve(string path) =>
        TryResolve(path, out var provider) ? provider : throw new VfsProviderNotFoundException(path);

    public IAsyncEnumerable<VfsEntry> EnumerateAsync(string path, CancellationToken cancellationToken)
    {
        var provider = Resolve(path);
        return _executor.StreamAsync(provider.EnumerateAsync(path, cancellationToken), cancellationToken, ListingStallTimeout);
    }

    /// <summary>
    /// Listing for tree walks: providers with <see cref="IVfsTreeListing"/> report unreadable containers through
    /// <paramref name="inaccessible"/> instead of throwing; the rest behave like <see cref="EnumerateAsync"/>.
    /// </summary>
    public IAsyncEnumerable<VfsEntry> EnumerateForWalkAsync(string path, Action inaccessible, CancellationToken cancellationToken)
    {
        var provider = Resolve(path);
        var source = provider is IVfsTreeListing walk
            ? walk.EnumerateForWalkAsync(path, inaccessible, cancellationToken)
            : provider.EnumerateAsync(path, cancellationToken);
        return _executor.StreamAsync(source, cancellationToken, ListingStallTimeout);
    }

    /// <summary>
    /// Starts streaming a listing into a bounded channel on a background producer. The channel completes with the
    /// provider's exception (if any); cancelling the token detaches the consumer immediately, even if the provider
    /// itself is stuck in a blocking call.
    /// </summary>
    public ChannelReader<VfsEntry> OpenListing(string path, CancellationToken cancellationToken)
    {
        var channel = Channel.CreateBounded<VfsEntry>(new BoundedChannelOptions(ListingChannelCapacity)
        {
            SingleWriter = true,
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait,
        });

        _ = Task.Run(
            async () =>
            {
                try
                {
                    await foreach (var entry in EnumerateAsync(path, cancellationToken).ConfigureAwait(false))
                    {
                        await channel.Writer.WriteAsync(entry, cancellationToken).ConfigureAwait(false);
                    }

                    channel.Writer.TryComplete();
                }
                catch (Exception ex)
                {
                    channel.Writer.TryComplete(ex);
                }
            },
            CancellationToken.None);

        return channel.Reader;
    }

    public Task<VfsEntry?> GetEntryAsync(string path, CancellationToken cancellationToken)
    {
        var provider = Resolve(path);
        return _executor.RunAsync(ct => provider.GetEntryAsync(path, ct).AsTask(), cancellationToken);
    }

    public Task<Stream> OpenReadAsync(string path, CancellationToken cancellationToken)
    {
        var provider = Resolve(path);
        return _executor.RunAsync(ct => provider.OpenReadAsync(path, ct).AsTask(), cancellationToken);
    }

    public Task<Stream> OpenWriteAsync(string path, bool overwrite, CancellationToken cancellationToken)
    {
        var provider = Resolve(path);
        return _executor.RunAsync(ct => provider.OpenWriteAsync(path, overwrite, ct).AsTask(), cancellationToken);
    }

    public Task CreateDirectoryAsync(string path, CancellationToken cancellationToken)
    {
        var provider = Resolve(path);
        return _executor.RunAsync(ct => provider.CreateDirectoryAsync(path, ct).AsTask(), cancellationToken);
    }

    public Task DeleteAsync(string path, bool recursive, CancellationToken cancellationToken)
    {
        var provider = Resolve(path);
        return _executor.RunAsync(ct => provider.DeleteAsync(path, recursive, ct).AsTask(), cancellationToken);
    }

    /// <summary>Provider-native move; both paths must belong to the same provider.</summary>
    public Task MoveAsync(string sourcePath, string destinationPath, bool overwrite, CancellationToken cancellationToken)
    {
        var provider = Resolve(sourcePath);
        if (!ReferenceEquals(provider, Resolve(destinationPath)))
        {
            throw new NotSupportedException("Native move requires both paths to be on the same provider.");
        }

        return _executor.RunAsync(ct => provider.MoveAsync(sourcePath, destinationPath, overwrite, ct).AsTask(), cancellationToken);
    }

    public bool AreOnSameProvider(string first, string second) =>
        TryResolve(first, out var a) && TryResolve(second, out var b) && ReferenceEquals(a, b);

    public string? GetParentPath(string path) => Resolve(path).GetParentPath(path);

    public string Combine(string directoryPath, string name) => Resolve(directoryPath).Combine(directoryPath, name);

    private void Rebuild()
    {
        var map = new Dictionary<string, IVfsProviderPlugin>(StringComparer.OrdinalIgnoreCase);
        foreach (var provider in _registered)
        {
            foreach (var scheme in provider.Schemes)
            {
                map[scheme] = provider;
            }
        }

        Volatile.Write(ref _byScheme, map.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase));
    }
}
