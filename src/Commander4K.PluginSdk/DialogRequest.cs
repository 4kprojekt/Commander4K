namespace Commander4K.PluginSdk;

/// <summary>Kinds of <see cref="DialogField"/>. <c>Heading</c> starts a section and has no value.</summary>
public enum DialogFieldType : byte
{
    Text,
    Multiline,
    Password,
    Number,
    Select,
    Segmented,
    Checkbox,
    File,
    Folder,
    Heading,
}

public sealed record DialogOption(string Value, string Label);

/// <summary>One field of a <see cref="DialogRequest"/>.</summary>
/// <param name="Value">Initial value as text: a number in invariant culture, <c>true</c>/<c>false</c> for a checkbox, an option's value for a select.</param>
/// <param name="VisibleWhen">Shown only while every named field has one of the listed values (modes: password vs key).</param>
/// <param name="Button">A button beside a text field: it closes the dialog without checks and the answers carry
/// <c>"$button": "&lt;field id&gt;"</c>; the plugin does its work and shows the dialog again with new values.</param>
public sealed record DialogField(
    string Id,
    DialogFieldType Type,
    string? Label = null,
    string? Value = null,
    string? Placeholder = null,
    string? Help = null,
    bool Required = false,
    IReadOnlyList<DialogOption>? Options = null,
    IReadOnlyDictionary<string, IReadOnlyList<string>>? VisibleWhen = null,
    double? Min = null,
    double? Max = null,
    string? Button = null);

public static class DialogAnswers
{
    /// <summary>The answer key naming the field whose <see cref="DialogField.Button"/> closed the dialog.</summary>
    public const string ButtonKey = "$button";
}

/// <summary>
/// A modal window with several fields (<see cref="IPluginHost.ShowDialogAsync"/>). To report a check the plugin made
/// itself (a refused login), it shows the dialog again with the answers as <see cref="DialogField.Value"/>s and
/// <see cref="Errors"/> per field or a <see cref="Message"/>.
/// </summary>
public sealed record DialogRequest(
    string Title,
    IReadOnlyList<DialogField> Fields,
    string? Description = null,
    string? Submit = null,
    string? Message = null,
    IReadOnlyDictionary<string, string>? Errors = null);
