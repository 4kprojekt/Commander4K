namespace Commander4K.Core.Configuration;

/// <summary>Content of <c>config/keymap.json</c>: key sequence (e.g. <c>Ctrl+K Ctrl+C</c>) to command id.</summary>
public sealed class KeymapFile
{
    public Dictionary<string, string> Bindings { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Command ids that existed when the file was last written. Default bindings are added on load only for commands not
    /// listed here, so new features get shortcuts while bindings the user removed stay removed.
    /// </summary>
    public List<string>? KnownCommands { get; set; }
}
