namespace Hotshot.Editor;

/// <summary>Raised after the editor wrote an image to disk.</summary>
public sealed class EditorSavedEventArgs : EventArgs
{
    /// <summary>Path of the written image.</summary>
    public required string Path { get; init; }

    /// <summary>True for "Save as" (a new file; the backup and annotations sidecar are untouched).</summary>
    public required bool IsSaveAs { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public long FileSize { get; init; }
}
