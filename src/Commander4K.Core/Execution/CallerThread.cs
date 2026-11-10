using System.Runtime.CompilerServices;

namespace Commander4K.Core.Execution;

public static class CallerThread
{
    /// <summary>
    /// Awaited first in an async method whose synchronous part does real work (starting a process, file I/O, JSON for
    /// a big request): a caller on the UI thread (any thread with a <see cref="SynchronizationContext"/>) gets its
    /// thread back at once and the method continues on the pool. Other callers continue inline, so dedicated threads
    /// and the order of calls made from one pool thread stay as they were. Unlike <c>Task.Run</c>, failures stay inside
    /// the async method (see "Debugger-friendly background work").
    /// </summary>
    public static ConfiguredTaskAwaitable LeaveUi() =>
        Task.CompletedTask.ConfigureAwait(SynchronizationContext.Current is null ? ConfigureAwaitOptions.None : ConfigureAwaitOptions.ForceYielding);
}
