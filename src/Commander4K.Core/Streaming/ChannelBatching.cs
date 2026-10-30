using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace Commander4K.Core.Streaming;

public static class ChannelBatching
{
    /// <summary>One frame at 60 Hz, the default cadence for pushing listing snapshots to the UI.</summary>
    public static readonly TimeSpan FrameInterval = TimeSpan.FromMilliseconds(16);

    /// <summary>
    /// Groups items into batches emitted at most once per <paramref name="interval"/>. The first batch is emitted as
    /// soon as anything is available, so the first rows of a 500 000-entry listing appear immediately.
    /// </summary>
    public static async IAsyncEnumerable<IReadOnlyList<T>> ReadBatchesAsync<T>(
        this ChannelReader<T> reader,
        TimeSpan interval,
        int maxBatchSize = 8192,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBatchSize);

        using var timer = new PeriodicTimer(interval);
        while (await reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var batch = new List<T>(Math.Min(maxBatchSize, 256));
            while (batch.Count < maxBatchSize && reader.TryRead(out var item))
            {
                batch.Add(item);
            }

            if (batch.Count > 0)
            {
                yield return batch;
            }

            await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
