using System.Numerics;
using System.Text.Json;

namespace Hotshot.Editor.Model;

/// <summary>
/// The editable annotation document: ordered annotations (z-order = list order), an optional crop,
/// and snapshot-based undo/redo covering every mutation.
/// </summary>
internal sealed class AnnotationDocument
{
    private readonly List<Annotation> _items = [];
    private readonly List<Snapshot> _undo = [];
    private readonly Stack<Snapshot> _redo = new();
    private long _versionCounter;
    private Snapshot? _transaction;

    public AnnotationDocument(int imageWidth, int imageHeight)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(imageWidth, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(imageHeight, 1);
        ImageWidth = imageWidth;
        ImageHeight = imageHeight;
    }

    public event EventHandler? Changed;

    public int ImageWidth { get; }

    public int ImageHeight { get; }

    public IReadOnlyList<Annotation> Annotations => _items;

    /// <summary>Normalized crop rectangle (whole pixels inside the image) or null when not cropped.</summary>
    public RectF? Crop { get; private set; }

    public int MaxHistory { get; set; } = 500;

    /// <summary>Identifies the current content; restored by undo/redo so "back to the saved state" is not dirty.</summary>
    public long Version { get; private set; }

    public long SavedVersion { get; private set; }

    public bool IsDirty => Version != SavedVersion;

    public bool InTransaction => _transaction is not null;

    public bool CanUndo => _undo.Count > 0 && _transaction is null;

    public bool CanRedo => _redo.Count > 0 && _transaction is null;

    public int UndoCount => _undo.Count;

    public int RedoCount => _redo.Count;

    public int NextStepNumber => _items.Count(a => a is StepAnnotation) + 1;

    public void MarkSaved() => SavedVersion = Version;

    public Annotation? Find(Guid? id) => id is { } g ? _items.Find(a => a.Id == g) : null;

    public int IndexOf(Guid id) => _items.FindIndex(a => a.Id == id);

    /// <summary>Topmost annotation under the point.</summary>
    public Annotation? HitTest(Vector2 point, float tolerance)
    {
        for (var i = _items.Count - 1; i >= 0; i--)
        {
            if (_items[i].HitTest(point, tolerance))
            {
                return _items[i];
            }
        }

        return null;
    }

    public void Add(Annotation annotation)
    {
        ArgumentNullException.ThrowIfNull(annotation);
        Execute(() => _items.Add(annotation));
    }

    public bool Remove(Guid id)
    {
        var index = IndexOf(id);
        if (index < 0)
        {
            return false;
        }

        return Execute(() => _items.RemoveAt(index));
    }

    /// <summary>Mutates an annotation in place (with undo). Returns false when nothing changed.</summary>
    public bool Update(Guid id, Action<Annotation> mutate)
    {
        var item = Find(id);
        if (item is null)
        {
            return false;
        }

        return Execute(() => mutate(item));
    }

    /// <summary>Replaces the annotation with the same id (with undo).</summary>
    public bool Replace(Annotation updated)
    {
        var index = IndexOf(updated.Id);
        if (index < 0)
        {
            return false;
        }

        return Execute(() => _items[index] = updated);
    }

    public bool SetCrop(RectF? crop)
    {
        var normalized = CropMath.Normalize(crop, ImageWidth, ImageHeight);
        if (normalized == Crop)
        {
            return false;
        }

        return Execute(() => Crop = normalized);
    }

    public void Clear() => Execute(() =>
    {
        _items.Clear();
        Crop = null;
    });

    // ---- Interactive transactions (drag to draw/move/resize): one undo entry for the whole gesture. ----

    public void BeginTransaction()
    {
        if (_transaction is not null)
        {
            CommitTransaction();
        }

        _transaction = Capture();
    }

    public void AddTransient(Annotation annotation)
    {
        _items.Add(annotation);
        RenumberSteps();
        OnChanged();
    }

    public void ReplaceTransient(Annotation updated)
    {
        var index = IndexOf(updated.Id);
        if (index >= 0)
        {
            _items[index] = updated;
            OnChanged();
        }
    }

    /// <summary>Sets the crop without normalizing or recording history (live preview while dragging).</summary>
    public void SetCropTransient(RectF? crop)
    {
        Crop = crop;
        OnChanged();
    }

    /// <summary>Ends the gesture; records an undo step only when the state actually changed.</summary>
    public bool CommitTransaction()
    {
        if (_transaction is not { } before)
        {
            return false;
        }

        _transaction = null;
        Crop = CropMath.Normalize(Crop, ImageWidth, ImageHeight);
        RenumberSteps();
        var changed = !StateEquals(before);
        if (changed)
        {
            PushUndo(before);
            Version = ++_versionCounter;
        }

        OnChanged();
        return changed;
    }

    public void CancelTransaction()
    {
        if (_transaction is not { } before)
        {
            return;
        }

        _transaction = null;
        Restore(before);
        OnChanged();
    }

    public bool Undo()
    {
        if (!CanUndo)
        {
            return false;
        }

        var snapshot = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        _redo.Push(Capture());
        Restore(snapshot);
        OnChanged();
        return true;
    }

    public bool Redo()
    {
        if (!CanRedo)
        {
            return false;
        }

        var snapshot = _redo.Pop();
        _undo.Add(Capture());
        Restore(snapshot);
        OnChanged();
        return true;
    }

    /// <summary>Assigns 1..n to step badges in z-order.</summary>
    public void RenumberSteps()
    {
        var n = 1;
        foreach (var step in _items.OfType<StepAnnotation>())
        {
            step.Number = n++;
        }
    }

    // ---- Serialization ----

    public string ToJson()
    {
        var file = new AnnotationFile
        {
            ImageWidth = ImageWidth,
            ImageHeight = ImageHeight,
            Crop = Crop,
            Annotations = [.. _items],
        };
        return JsonSerializer.Serialize(file, EditorJsonContext.Default.AnnotationFile);
    }

    public static AnnotationDocument FromJson(string json)
    {
        var file = JsonSerializer.Deserialize(json, EditorJsonContext.Default.AnnotationFile)
            ?? throw new JsonException("Empty annotation document.");
        if (file.Version > AnnotationFile.CurrentVersion)
        {
            throw new JsonException($"Unsupported annotation document version {file.Version}.");
        }

        var doc = new AnnotationDocument(file.ImageWidth, file.ImageHeight);
        foreach (var a in file.Annotations)
        {
            if (a is not null)
            {
                doc._items.Add(a);
            }
        }

        doc.Crop = CropMath.Normalize(file.Crop, file.ImageWidth, file.ImageHeight);
        doc.RenumberSteps();
        return doc;
    }

    public static bool TryFromJson(string json, out AnnotationDocument? document)
    {
        try
        {
            document = FromJson(json);
            return true;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or ArgumentException or InvalidOperationException)
        {
            document = null;
            return false;
        }
    }

    public static string SerializeAnnotation(Annotation annotation) =>
        JsonSerializer.Serialize(annotation, EditorJsonContext.Default.Annotation);

    // ---- internals ----

    private bool Execute(Action action)
    {
        if (_transaction is not null)
        {
            CommitTransaction();
        }

        var before = Capture();
        action();
        RenumberSteps();
        if (StateEquals(before))
        {
            return false;
        }

        PushUndo(before);
        Version = ++_versionCounter;
        OnChanged();
        return true;
    }

    private void PushUndo(Snapshot snapshot)
    {
        _undo.Add(snapshot);
        if (_undo.Count > MaxHistory)
        {
            _undo.RemoveAt(0);
        }

        _redo.Clear();
    }

    private Snapshot Capture() => new([.. _items.Select(a => a.Clone())], Crop, Version);

    private void Restore(Snapshot snapshot)
    {
        _items.Clear();
        _items.AddRange(snapshot.Items.Select(a => a.Clone()));
        Crop = snapshot.Crop;
        Version = snapshot.Version;
    }

    private bool StateEquals(Snapshot snapshot)
    {
        if (snapshot.Crop != Crop || snapshot.Items.Length != _items.Count)
        {
            return false;
        }

        for (var i = 0; i < _items.Count; i++)
        {
            var a = snapshot.Items[i];
            var b = _items[i];
            if (a.Id != b.Id || a.GetType() != b.GetType() || SerializeAnnotation(a) != SerializeAnnotation(b))
            {
                return false;
            }
        }

        return true;
    }

    private void OnChanged() => Changed?.Invoke(this, EventArgs.Empty);

    private sealed record Snapshot(Annotation[] Items, RectF? Crop, long Version);
}
