using System.Numerics;
using Hotshot.Editor.Model;

namespace Hotshot.Editor.Tests;

public sealed class DocumentTests
{
    private static AnnotationDocument NewDoc() => new(800, 600);

    private static RectangleAnnotation Rect(float x, float y, float w, float h) => new() { Rect = new RectF(x, y, w, h) };

    [Fact]
    public void Add_and_remove_record_history()
    {
        var doc = NewDoc();
        var r = Rect(10, 10, 100, 50);

        doc.Add(r);
        Assert.Single(doc.Annotations);
        Assert.True(doc.CanUndo);
        Assert.True(doc.IsDirty);

        Assert.True(doc.Remove(r.Id));
        Assert.Empty(doc.Annotations);
        Assert.Equal(2, doc.UndoCount);

        Assert.False(doc.Remove(Guid.NewGuid()));
        Assert.Equal(2, doc.UndoCount);
    }

    [Fact]
    public void Undo_redo_walks_the_stacks()
    {
        var doc = NewDoc();
        var a = Rect(0, 0, 10, 10);
        var b = Rect(20, 20, 10, 10);
        doc.Add(a);
        doc.Add(b);

        Assert.True(doc.Undo());
        Assert.Single(doc.Annotations);
        Assert.Equal(a.Id, doc.Annotations[0].Id);
        Assert.True(doc.CanRedo);

        Assert.True(doc.Undo());
        Assert.Empty(doc.Annotations);
        Assert.False(doc.CanUndo);
        Assert.False(doc.Undo());

        Assert.True(doc.Redo());
        Assert.True(doc.Redo());
        Assert.Equal(2, doc.Annotations.Count);
        Assert.False(doc.Redo());

        // A new edit clears the redo stack.
        doc.Undo();
        doc.Add(Rect(5, 5, 5, 5));
        Assert.False(doc.CanRedo);
    }

    [Fact]
    public void Undo_restores_independent_copies()
    {
        var doc = NewDoc();
        var r = Rect(10, 10, 100, 50);
        doc.Add(r);
        doc.Update(r.Id, a => a.Translate(new Vector2(5, 5)));
        Assert.Equal(new RectF(15, 15, 100, 50), ((RectangleAnnotation)doc.Annotations[0]).Rect);

        doc.Undo();
        Assert.Equal(new RectF(10, 10, 100, 50), ((RectangleAnnotation)doc.Annotations[0]).Rect);
        doc.Redo();
        Assert.Equal(new RectF(15, 15, 100, 50), ((RectangleAnnotation)doc.Annotations[0]).Rect);
    }

    [Fact]
    public void Update_without_change_is_not_recorded()
    {
        var doc = NewDoc();
        var r = Rect(10, 10, 100, 50);
        doc.Add(r);
        Assert.False(doc.Update(r.Id, a => a.Color = a.Color));
        Assert.Equal(1, doc.UndoCount);
    }

    [Fact]
    public void Move_transaction_is_a_single_undo_step()
    {
        var doc = NewDoc();
        var r = Rect(10, 10, 100, 50);
        doc.Add(r);

        doc.BeginTransaction();
        var original = doc.Find(r.Id)!.Clone();
        foreach (var dx in new[] { 1f, 5f, 12f, 30f })
        {
            var moved = original.Clone();
            moved.Translate(new Vector2(dx, dx / 2));
            doc.ReplaceTransient(moved);
        }

        Assert.True(doc.CommitTransaction());
        Assert.Equal(2, doc.UndoCount);
        Assert.Equal(new RectF(40, 25, 100, 50), ((RectangleAnnotation)doc.Annotations[0]).Rect);

        doc.Undo();
        Assert.Equal(new RectF(10, 10, 100, 50), ((RectangleAnnotation)doc.Annotations[0]).Rect);
    }

    [Fact]
    public void Transaction_without_change_records_nothing_and_cancel_restores()
    {
        var doc = NewDoc();
        var r = Rect(10, 10, 100, 50);
        doc.Add(r);

        doc.BeginTransaction();
        Assert.False(doc.CommitTransaction());
        Assert.Equal(1, doc.UndoCount);

        doc.BeginTransaction();
        var moved = doc.Find(r.Id)!.Clone();
        moved.Translate(new Vector2(100, 100));
        doc.ReplaceTransient(moved);
        doc.CancelTransaction();
        Assert.Equal(new RectF(10, 10, 100, 50), ((RectangleAnnotation)doc.Annotations[0]).Rect);
        Assert.Equal(1, doc.UndoCount);
    }

    [Fact]
    public void Draw_transaction_adds_with_one_undo_step()
    {
        var doc = NewDoc();
        doc.BeginTransaction();
        var arrow = new ArrowAnnotation { Start = new Vector2(10, 10), End = new Vector2(10, 10) };
        doc.AddTransient(arrow);
        var updated = (ArrowAnnotation)arrow.Clone();
        updated.End = new Vector2(200, 100);
        doc.ReplaceTransient(updated);
        doc.CommitTransaction();

        Assert.Single(doc.Annotations);
        Assert.Equal(1, doc.UndoCount);
        doc.Undo();
        Assert.Empty(doc.Annotations);
    }

    [Fact]
    public void Resize_box_handles()
    {
        var doc = NewDoc();
        var r = Rect(100, 100, 100, 50);
        doc.Add(r);

        var resized = (RectangleAnnotation)r.Clone();
        resized.MoveHandle(HandleKind.BottomRight, new Vector2(300, 400), constrain: false);
        doc.Replace(resized);
        Assert.Equal(new RectF(100, 100, 200, 300), ((RectangleAnnotation)doc.Annotations[0]).Rect);

        // Dragging a corner past the opposite one flips and stays normalized.
        var flipped = (RectangleAnnotation)doc.Annotations[0].Clone();
        flipped.MoveHandle(HandleKind.TopLeft, new Vector2(400, 500), constrain: false);
        Assert.Equal(new RectF(300, 400, 100, 100), flipped.Rect);

        // Shift keeps it square.
        var square = (RectangleAnnotation)r.Clone();
        square.MoveHandle(HandleKind.BottomRight, new Vector2(150, 260), constrain: true);
        Assert.Equal(160, square.Rect.Width);
        Assert.Equal(160, square.Rect.Height);

        doc.Undo();
        Assert.Equal(new RectF(100, 100, 100, 50), ((RectangleAnnotation)doc.Annotations[0]).Rect);
    }

    [Fact]
    public void Segment_handles_and_angle_snap()
    {
        var line = new LineAnnotation { Start = new Vector2(0, 0), End = new Vector2(100, 0) };
        line.MoveHandle(HandleKind.End, new Vector2(100, 8), constrain: true);
        Assert.Equal(0, line.End.Y, 3);
        Assert.Equal(100f, line.End.X, 2);

        line.MoveHandle(HandleKind.End, new Vector2(50, 47), constrain: true);
        Assert.Equal(line.End.X, line.End.Y, 3);

        line.MoveHandle(HandleKind.Start, new Vector2(-10, -10), constrain: false);
        Assert.Equal(new Vector2(-10, -10), line.Start);
    }

    [Fact]
    public void Pen_resize_scales_points()
    {
        var pen = new PenAnnotation { Points = [new(0, 0), new(10, 5), new(20, 10)] };
        pen.MoveHandle(HandleKind.BottomRight, new Vector2(40, 20), constrain: false);
        Assert.Equal(new Vector2(0, 0), pen.Points[0]);
        Assert.Equal(new Vector2(20, 10), pen.Points[1]);
        Assert.Equal(new Vector2(40, 20), pen.Points[2]);

        var clone = (PenAnnotation)pen.Clone();
        clone.Translate(new Vector2(1, 1));
        Assert.Equal(new Vector2(0, 0), pen.Points[0]);
    }

    [Fact]
    public void Steps_are_numbered_and_renumbered_on_delete()
    {
        var doc = NewDoc();
        var steps = Enumerable.Range(0, 4).Select(i => new StepAnnotation { Center = new Vector2(50 * i, 50) }).ToList();
        foreach (var s in steps)
        {
            Assert.Equal(doc.NextStepNumber, doc.Annotations.OfType<StepAnnotation>().Count() + 1);
            doc.Add(s);
        }

        doc.Add(Rect(0, 0, 10, 10));
        Assert.Equal(new[] { 1, 2, 3, 4 }, doc.Annotations.OfType<StepAnnotation>().Select(s => s.Number));
        Assert.Equal(5, doc.NextStepNumber);

        doc.Remove(steps[1].Id);
        var remaining = doc.Annotations.OfType<StepAnnotation>().ToList();
        Assert.Equal(new[] { 1, 2, 3 }, remaining.Select(s => s.Number));
        Assert.Equal(steps[2].Id, remaining[1].Id);
        Assert.Equal(4, doc.NextStepNumber);

        doc.Undo();
        Assert.Equal(new[] { 1, 2, 3, 4 }, doc.Annotations.OfType<StepAnnotation>().Select(s => s.Number));
        Assert.Equal(steps[1].Id, doc.Annotations.OfType<StepAnnotation>().ElementAt(1).Id);
    }

    [Fact]
    public void Dirty_tracking_follows_saved_version_through_undo()
    {
        var doc = NewDoc();
        Assert.False(doc.IsDirty);
        doc.Add(Rect(0, 0, 10, 10));
        doc.MarkSaved();
        Assert.False(doc.IsDirty);

        doc.Add(Rect(10, 10, 10, 10));
        Assert.True(doc.IsDirty);
        doc.Undo();
        Assert.False(doc.IsDirty);
        doc.Undo();
        Assert.True(doc.IsDirty);
        doc.Redo();
        Assert.False(doc.IsDirty);

        // A different edit from the saved state is dirty even with the same count.
        doc.Add(Rect(30, 30, 10, 10));
        Assert.True(doc.IsDirty);
    }

    [Fact]
    public void Crop_is_normalized_and_undoable()
    {
        var doc = NewDoc();
        Assert.True(doc.SetCrop(new RectF(500.4f, 400.6f, -300.2f, -200.3f)));
        Assert.Equal(new RectF(200, 200, 300, 201), doc.Crop);

        Assert.False(doc.SetCrop(new RectF(200, 200, 300, 201)));

        Assert.True(doc.SetCrop(null));
        Assert.Null(doc.Crop);

        doc.Undo();
        Assert.Equal(new RectF(200, 200, 300, 201), doc.Crop);
        doc.Undo();
        Assert.Null(doc.Crop);
    }

    [Fact]
    public void HitTest_returns_topmost()
    {
        var doc = NewDoc();
        var bottom = new RectangleAnnotation { Rect = new RectF(0, 0, 200, 200), Filled = true };
        var top = new EllipseAnnotation { Rect = new RectF(50, 50, 100, 100), Filled = true };
        doc.Add(bottom);
        doc.Add(top);

        Assert.Equal(top.Id, doc.HitTest(new Vector2(100, 100), 2)?.Id);
        Assert.Equal(bottom.Id, doc.HitTest(new Vector2(10, 10), 2)?.Id);
        Assert.Null(doc.HitTest(new Vector2(400, 400), 2));
    }
}
