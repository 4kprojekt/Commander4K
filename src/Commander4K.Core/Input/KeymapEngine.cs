using System.Collections.Frozen;

namespace Commander4K.Core.Input;

public enum KeymapResultKind : byte
{
    NotHandled,
    ChordPending,
    Command,
}

public readonly record struct KeymapResult(KeymapResultKind Kind, string? CommandId = null)
{
    public static KeymapResult NotHandled => default;

    public static KeymapResult Pending => new(KeymapResultKind.ChordPending);
}

/// <summary>
/// Maps key strokes and chords (<c>Ctrl+K Ctrl+C</c>) to command ids with O(1) frozen-dictionary lookups. Stateful
/// for chords; call <see cref="Process"/> from a single (UI) thread.
/// </summary>
public sealed class KeymapEngine
{
    private readonly FrozenDictionary<string, string> _bindings;
    private readonly FrozenSet<string> _chordPrefixes;
    private string? _pendingChord;

    /// <param name="bindings">Key sequence text (strokes separated by spaces) to command id.</param>
    public KeymapEngine(IEnumerable<KeyValuePair<string, string>> bindings)
    {
        ArgumentNullException.ThrowIfNull(bindings);

        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        var prefixes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (sequenceText, commandId) in bindings)
        {
            var strokes = ParseSequence(sequenceText);
            var canonical = string.Join(' ', strokes);
            map[canonical] = commandId;

            for (var i = 1; i < strokes.Length; i++)
            {
                prefixes.Add(string.Join(' ', strokes[..i]));
            }
        }

        _bindings = map.ToFrozenDictionary(StringComparer.Ordinal);
        _chordPrefixes = prefixes.ToFrozenSet(StringComparer.Ordinal);
    }

    public bool IsChordPending => _pendingChord is not null;

    /// <summary>Canonical sequence to command id, for display (menus, tooltips, key bar).</summary>
    public IReadOnlyDictionary<string, string> Bindings => _bindings;

    public KeymapResult Process(KeyStroke stroke)
    {
        var strokeText = stroke.ToString();
        if (_pendingChord is not null)
        {
            var candidate = _pendingChord + " " + strokeText;
            _pendingChord = null;
            var chordResult = Match(candidate);
            if (chordResult.Kind != KeymapResultKind.NotHandled)
            {
                return chordResult;
            }

            // The chord did not continue; give the stroke a chance on its own.
        }

        return Match(strokeText);
    }

    public void CancelChord() => _pendingChord = null;

    private KeymapResult Match(string sequence)
    {
        if (_bindings.TryGetValue(sequence, out var commandId))
        {
            return new KeymapResult(KeymapResultKind.Command, commandId);
        }

        if (_chordPrefixes.Contains(sequence))
        {
            _pendingChord = sequence;
            return KeymapResult.Pending;
        }

        return KeymapResult.NotHandled;
    }

    private static KeyStroke[] ParseSequence(string text)
    {
        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            throw new FormatException("Empty key sequence.");
        }

        return Array.ConvertAll(parts, KeyStroke.Parse);
    }
}
