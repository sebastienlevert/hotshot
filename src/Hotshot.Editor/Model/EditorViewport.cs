using System.Numerics;

namespace Hotshot.Editor.Model;

public sealed class EditorViewport
{
    public bool Fit { get; private set; } = true;
    public float Scale { get; private set; } = 1;
    public Vector2 Offset { get; private set; }
    public void Layout(Vector2 canvas, Vector2 image)
    {
        if (Fit)
        {
            Scale = Math.Clamp(Math.Min((canvas.X - 32) / image.X, (canvas.Y - 32) / image.Y), 0.01f, 32);
            Offset = (canvas - image * Scale) / 2;
        }
    }
    public void Zoom(float scale, Vector2 anchor)
    {
        var next = Math.Clamp(scale, 0.01f, 32);
        Offset = anchor - (anchor - Offset) * (next / Scale);
        Scale = next;
        Fit = false;
    }
    public void Pan(Vector2 delta) { Offset += delta; Fit = false; }
    public void Reset() => Fit = true;
}
