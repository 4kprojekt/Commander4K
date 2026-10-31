using System.Runtime.CompilerServices;

namespace Commander4K.Core.Execution;

/// <summary>
/// Runs potentially blocking I/O (UNC/SMB calls that can hang for ~30 s and ignore cancellation) so that the caller
/// can walk away from it the moment its token is cancelled or a timeout elapses. The hung operation keeps running in
/// the background until the OS gives up; its result or exception is observed and discarded.
/// </summary>
public sealed class ResilientAsyncExecutor
{
    private int _abandonedOperations;

    public ResilientAsyncExecutor(TimeSpan? defaultTimeout = null)
    {
        DefaultTimeout = defaultTimeout ?? Timeout.InfiniteTimeSpan;
    }

    /// <summary>Timeout applied when a call does not pass its own; infinite by default.</summary>
    public TimeSpan DefaultTimeout { get; }

    /// <summary>Operations that were detached and have not finished yet (diagnostics).</summary>
    public int AbandonedOperations => Volatile.Read(ref _abandonedOperations);

    /// <summary>Runs a synchronous blocking call on a dedicated thread.</summary>
    public Task<T> RunAsync<T>(Func<CancellationToken, T> operation, CancellationToken cancellationToken, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(operation);
        cancellationToken.ThrowIfCancellationRequested();

        // The result goes through a completion source so that an exception from the operation is caught here, in our
        // code, rather than by the task machinery: a debugger with "Just My Code" would otherwise stop on every
        // expected failure (access denied, offline share) as "user-unhandled". The operation observes the token
        // itself, so the thread is started with None and always completes the source.
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task.Factory.StartNew(
            () =>
            {
                try
                {
                    completion.TrySetResult(operation(cancellationToken));
                }
                catch (OperationCanceledException ex)
                {
                    completion.TrySetCanceled(ex.CancellationToken);
                }
                catch (Exception ex)
                {
                    completion.TrySetException(ex);
                }
            },
            CancellationToken.None,
            TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach,
            TaskScheduler.Default);

        return DetachOnCancelAsync(completion.Task, timeout ?? DefaultTimeout, cancellationToken);
    }

    /// <summary>Starts <paramref name="operation"/>, turning a synchronous throw into a faulted task (see the sync overload).</summary>
    private static Task<T> Invoke<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken)
    {
        try
        {
            return operation(cancellationToken);
        }
        catch (OperationCanceledException ex)
        {
            return Task.FromCanceled<T>(ex.CancellationToken.IsCancellationRequested ? ex.CancellationToken : new CancellationToken(canceled: true));
        }
        catch (Exception ex)
        {
            return Task.FromException<T>(ex);
        }
    }

    /// <summary>Runs an asynchronous call whose synchronous prefix may block (e.g. the first FindFirstFile).</summary>
    public Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(operation);
        cancellationToken.ThrowIfCancellationRequested();

        var task = Task.Run(() => Invoke(operation, cancellationToken), cancellationToken);
        return DetachOnCancelAsync(task, timeout ?? DefaultTimeout, cancellationToken);
    }

    /// <summary>Asynchronous call without a result; see <see cref="RunAsync{T}(Func{CancellationToken, Task{T}}, CancellationToken, TimeSpan?)"/>.</summary>
    public Task RunAsync(Func<CancellationToken, Task> operation, CancellationToken cancellationToken, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(operation);
        return RunAsync<bool>(
            async ct =>
            {
                await operation(ct).ConfigureAwait(false);
                return true;
            },
            cancellationToken,
            timeout);
    }

    /// <summary>
    /// Re-streams <paramref name="source"/> so that a stalled <c>MoveNextAsync</c> is detached on cancellation or
    /// when no item arrives within <paramref name="stallTimeout"/>. The first step runs on the thread pool because
    /// iterator prologues (opening a handle on a dead share) are the usual place where Windows blocks.
    /// </summary>
    public async IAsyncEnumerable<T> StreamAsync<T>(
        IAsyncEnumerable<T> source,
        [EnumeratorCancellation] CancellationToken cancellationToken = default,
        TimeSpan? stallTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        var timeout = stallTimeout ?? DefaultTimeout;

        // Not disposed via 'using': an abandoned enumerator may still observe the token after we return.
        var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var enumerator = source.GetAsyncEnumerator(linked.Token);
        Task<bool>? pending = Task.Run(() => StepAsync(enumerator), CancellationToken.None);

        try
        {
            while (true)
            {
                bool hasItem;
                if (pending is not null)
                {
                    hasItem = await pending.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
                    pending = null;
                }
                else
                {
                    var step = enumerator.MoveNextAsync();
                    if (step.IsCompletedSuccessfully)
                    {
                        hasItem = step.Result;
                    }
                    else
                    {
                        pending = AwaitStepAsync(step);
                        hasItem = await pending.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
                        pending = null;
                    }
                }

                if (!hasItem)
                {
                    yield break;
                }

                yield return enumerator.Current;
            }
        }
        finally
        {
            if (pending is { IsCompleted: false })
            {
                linked.Cancel();
                Abandon(pending, () => DisposeQuietlyAsync(enumerator, linked));
            }
            else
            {
                _ = pending?.Exception;
                await DisposeQuietlyAsync(enumerator, linked).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// One step of a provider's iterator, awaited in our own async method. <c>ValueTask.AsTask()</c> on an async
    /// iterator's step reads its result inside .NET's adapter, so a failed step (login refused, access denied) was
    /// thrown from our code into .NET code — and a debugger with "Just My Code" stopped on every such expected error
    /// as user-unhandled. Here the failure surfaces in this method and is stored in the task like any other.
    /// </summary>
    private static async Task<bool> StepAsync<T>(IAsyncEnumerator<T> enumerator) => await enumerator.MoveNextAsync().ConfigureAwait(false);

    /// <inheritdoc cref="StepAsync{T}(IAsyncEnumerator{T})"/>
    private static async Task<bool> AwaitStepAsync(ValueTask<bool> step) => await step.ConfigureAwait(false);

    private async Task<T> DetachOnCancelAsync<T>(Task<T> task, TimeSpan timeout, CancellationToken cancellationToken)
    {
        try
        {
            return await task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException && !task.IsCompleted)
        {
            Abandon(task, onCompleted: null);
            throw;
        }
    }

    private void Abandon(Task task, Func<ValueTask>? onCompleted)
    {
        Interlocked.Increment(ref _abandonedOperations);
        _ = task.ContinueWith(
            async t =>
            {
                _ = t.Exception;
                try
                {
                    if (onCompleted is not null)
                    {
                        await onCompleted().ConfigureAwait(false);
                    }
                }
                finally
                {
                    Interlocked.Decrement(ref _abandonedOperations);
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private static async ValueTask DisposeQuietlyAsync<T>(IAsyncEnumerator<T> enumerator, CancellationTokenSource linked)
    {
        try
        {
            await enumerator.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // The consumer is gone; a failure while tearing down the abandoned source has nobody to report to.
        }
        finally
        {
            linked.Dispose();
        }
    }
}
