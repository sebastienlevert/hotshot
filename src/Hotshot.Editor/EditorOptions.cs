namespace Hotshot.Editor;

/// <summary>Options for opening an image in the <see cref="EditorView"/>.</summary>
public sealed class EditorOptions
{
    /// <summary>File being edited. Save overwrites it with the rendered image (PNG).</summary>
    public required string ImagePath { get; init; }

    /// <summary>Where the pristine original lives (created on the first save).</summary>
    public required string OriginalBackupPath { get; init; }

    /// <summary>JSON sidecar with the annotation document, used to re-edit annotations later.</summary>
    public required string AnnotationsPath { get; init; }

    /// <summary>Host clipboard implementation (receives PNG bytes). The Copy command is hidden when null.</summary>
    public Func<byte[], Task>? CopyPngToClipboard { get; init; }

    /// <summary>Optional .ico used for the window.</summary>
    public string? IconPath { get; init; }
}
