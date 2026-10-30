namespace Commander4K.PluginSdk;

/// <summary>Optional provider capability: notifies the host when a listed container changes.</summary>
public interface IVfsChangeNotifier
{
    /// <summary>
    /// Starts watching <paramref name="directoryPath"/>. <paramref name="changed"/> is invoked on a background thread,
    /// already debounced, so a burst of changes produces one call. Starting a watch can block on network paths;
    /// call it off the UI thread. Dispose the result to stop watching.
    /// </summary>
    IDisposable Watch(string directoryPath, Action changed);
}
