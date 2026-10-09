using Hotshot.Capture;
using Hotshot.Interop;
using Hotshot.Shell;

namespace Hotshot.CaptureFlow;

internal enum OverlayMode
{
    Screenshot,
    Recording,
}

internal sealed record OverlayOptions(OverlayMode Mode, bool SnapToWindows, bool ShowMagnifier, bool ShowCrosshair);

internal abstract record OverlaySelection(PixelRect Rect);

internal sealed record RegionSelection(PixelRect Rect) : OverlaySelection(Rect);

internal sealed record WindowSelection(WindowInfo Window, PixelRect Rect) : OverlaySelection(Rect);

internal sealed record MonitorSelection(MonitorInfo Monitor) : OverlaySelection(Monitor.Bounds);

/// <summary>
/// Full-virtual-screen Win32 overlay drawn with GDI over a frozen screenshot. Pure Win32 keeps the
/// hotkey-to-overlay latency to a few milliseconds.
/// </summary>
internal sealed unsafe class CaptureOverlay : NativeWindow
{
    private const string ClassName = "Hotshot.CaptureOverlay";
    private const int MagnifierCells = 15;

    private static readonly uint Accent = Win32.Rgb(255, 84, 72);
    private static readonly uint PillColor = Win32.Rgb(24, 24, 27);

    private readonly ScreenSnapshot _snapshot;
    private readonly OverlayOptions _options;
    private readonly GdiBitmap _dim;
    private readonly GdiBitmap _back;
    private readonly IReadOnlyList<MonitorInfo> _monitors;
    private readonly IReadOnlyList<WindowInfo> _windows;
    private readonly TaskCompletionSource<OverlaySelection?> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Dictionary<int, nint> _fonts = [];
    private readonly nint _accentBrush;
    private readonly nint _pillBrush;
    private readonly nint _whiteBrush;
    private readonly nint _blackBrush;

    private int _cx;
    private int _cy;
    private bool _mouseDown;
    private bool _dragging;
    private int _anchorX;
    private int _anchorY;
    private WindowInfo? _hoverWindow;
    private Frame _frame;

    private CaptureOverlay(ScreenSnapshot snapshot, OverlayOptions options)
    {
        _snapshot = snapshot;
        _options = options;
        _monitors = Monitors.GetAll();
        _windows = options.SnapToWindows ? WindowEnumerator.GetVisibleWindows((uint)Environment.ProcessId) : [];
        _dim = snapshot.Bitmap.CreateDimmedCopy(118);
        _back = new GdiBitmap(snapshot.Bounds.Width, snapshot.Bounds.Height);
        _accentBrush = Win32.CreateSolidBrush(Accent);
        _pillBrush = Win32.CreateSolidBrush(PillColor);
        _whiteBrush = Win32.CreateSolidBrush(Win32.Rgb(255, 255, 255));
        _blackBrush = Win32.CreateSolidBrush(Win32.Rgb(0, 0, 0));

        Native.GetCursorPos(out var pt);
        UpdateHover(pt.X - snapshot.Bounds.X, pt.Y - snapshot.Bounds.Y);
        _frame = BuildFrame();

        var b = snapshot.Bounds;
        CreateHandle(ClassName, Win32.WS_EX_TOPMOST | Win32.WS_EX_TOOLWINDOW, Win32.WS_POPUP, b.X, b.Y, b.Width, b.Height,
            "Hotshot capture", Win32.IDC_CROSS);
    }

    public static Task<OverlaySelection?> SelectAsync(ScreenSnapshot snapshot, OverlayOptions options)
    {
        var overlay = new CaptureOverlay(snapshot, options);
        overlay.Show();
        return overlay._result.Task;
    }

    private PixelRect Origin => _snapshot.Bounds;

    private void Show()
    {
        Win32.ShowWindow(Handle, Win32.SW_SHOW);
        Win32.SetWindowPos(Handle, Win32.HWND_TOPMOST, 0, 0, 0, 0, Win32.SWP_NOMOVE | Win32.SWP_NOSIZE);
        Win32.SetForegroundWindow(Handle);
        Win32.BringWindowToTop(Handle);
        Win32.SetFocus(Handle);
        Win32.UpdateWindow(Handle);
    }

    private void Complete(OverlaySelection? selection)
    {
        if (_result.Task.IsCompleted)
        {
            return;
        }

        _result.TrySetResult(selection);
        Win32.ReleaseCapture();
        Win32.ShowWindow(Handle, Win32.SW_HIDE);
        Dispose();
    }

    protected override void OnDestroyed()
    {
        _result.TrySetResult(null);
        _dim.Dispose();
        _back.Dispose();
        Native.DeleteObject(_accentBrush);
        Native.DeleteObject(_pillBrush);
        Native.DeleteObject(_whiteBrush);
        Native.DeleteObject(_blackBrush);
        foreach (var font in _fonts.Values)
        {
            Native.DeleteObject(font);
        }

        _fonts.Clear();
    }

    protected override nint WndProc(uint msg, nint wParam, nint lParam)
    {
        switch (msg)
        {
            case Win32.WM_PAINT:
                Paint();
                return 0;

            case Win32.WM_ERASEBKGND:
                return 1;

            case Win32.WM_SETCURSOR:
                Win32.SetCursor(Win32.LoadCursor(0, Win32.IDC_CROSS));
                return 1;

            case Win32.WM_DPICHANGED:
                // The overlay spans monitors in physical pixels; never let Windows rescale it.
                return 0;

            case Win32.WM_MOUSEMOVE:
                OnMouseMove(Win32.LoWord(lParam), Win32.HiWord(lParam));
                return 0;

            case Win32.WM_LBUTTONDOWN:
                _mouseDown = true;
                _anchorX = Win32.LoWord(lParam);
                _anchorY = Win32.HiWord(lParam);
                Win32.SetCapture(Handle);
                return 0;

            case Win32.WM_LBUTTONUP:
                OnMouseUp(Win32.LoWord(lParam), Win32.HiWord(lParam));
                return 0;

            case Win32.WM_RBUTTONDOWN:
                if (_dragging)
                {
                    _dragging = _mouseDown = false;
                    Win32.ReleaseCapture();
                    Refresh(Win32.LoWord(lParam), Win32.HiWord(lParam));
                }
                else
                {
                    Complete(null);
                }

                return 0;

            case Win32.WM_CAPTURECHANGED:
                if (_mouseDown && lParam != Handle)
                {
                    _mouseDown = _dragging = false;
                }

                return 0;

            case Win32.WM_KEYDOWN:
            case Win32.WM_SYSKEYDOWN:
                OnKeyDown((int)wParam);
                return 0;

            case Win32.WM_CLOSE:
                Complete(null);
                return 0;
        }

        return base.WndProc(msg, wParam, lParam);
    }

    private void OnMouseMove(int x, int y)
    {
        if (_mouseDown && !_dragging && (Math.Abs(x - _anchorX) > 3 || Math.Abs(y - _anchorY) > 3))
        {
            _dragging = true;
        }

        Refresh(x, y);
    }

    private void OnMouseUp(int x, int y)
    {
        if (!_mouseDown)
        {
            return;
        }

        _mouseDown = false;
        Win32.ReleaseCapture();
        if (_dragging)
        {
            _dragging = false;
            _cx = x;
            _cy = y;
            var rect = DragRect();
            if (rect.Width >= 4 && rect.Height >= 4)
            {
                Complete(new RegionSelection(rect.Offset(Origin.X, Origin.Y)));
            }
            else
            {
                Refresh(x, y);
            }

            return;
        }

        CompleteWithHover(x, y);
    }

    private void CompleteWithHover(int x, int y)
    {
        UpdateHover(x, y);
        if (_hoverWindow is { } window)
        {
            var rect = window.Bounds.Intersect(Origin);
            if (!rect.IsEmpty)
            {
                Complete(new WindowSelection(window, rect));
                return;
            }
        }

        Complete(new MonitorSelection(MonitorAt(x, y)));
    }

    private void OnKeyDown(int vk)
    {
        switch (vk)
        {
            case Win32.VK_ESCAPE:
                Complete(null);
                break;
            case Win32.VK_SPACE:
            case 'F':
                Complete(new MonitorSelection(MonitorAt(_cx, _cy)));
                break;
            case Win32.VK_RETURN when !_dragging:
                CompleteWithHover(_cx, _cy);
                break;
            case Win32.VK_LEFT or Win32.VK_RIGHT or Win32.VK_UP or Win32.VK_DOWN:
                var step = Win32.IsKeyDown(Win32.VK_SHIFT) ? 10 : 1;
                var dx = vk == Win32.VK_LEFT ? -step : vk == Win32.VK_RIGHT ? step : 0;
                var dy = vk == Win32.VK_UP ? -step : vk == Win32.VK_DOWN ? step : 0;
                Native.GetCursorPos(out var pt);
                Win32.SetCursorPos(pt.X + dx, pt.Y + dy);
                break;
        }
    }

    private void Refresh(int x, int y)
    {
        if (_dragging && _options.Mode == OverlayMode.Recording)
        {
            // A recording region must stay on one monitor.
            var m = MonitorAt(_anchorX, _anchorY).Bounds.Offset(-Origin.X, -Origin.Y);
            x = Math.Clamp(x, m.Left, m.Right - 1);
            y = Math.Clamp(y, m.Top, m.Bottom - 1);
        }

        UpdateHover(x, y);
        var next = BuildFrame();
        foreach (var rect in _frame.DirtyRects().Concat(next.DirtyRects()))
        {
            Invalidate(rect);
        }

        _frame = next;
    }

    private void UpdateHover(int x, int y)
    {
        _cx = x;
        _cy = y;
        _hoverWindow = null;
        if (_dragging || !_options.SnapToWindows)
        {
            return;
        }

        int sx = x + Origin.X, sy = y + Origin.Y;
        foreach (var window in _windows)
        {
            if (window.Bounds.Contains(sx, sy))
            {
                _hoverWindow = window;
                return;
            }
        }
    }

    private PixelRect DragRect() =>
        PixelRect.FromLTRB(Math.Min(_anchorX, _cx), Math.Min(_anchorY, _cy), Math.Max(_anchorX, _cx) + 1, Math.Max(_anchorY, _cy) + 1);

    private MonitorInfo MonitorAt(int x, int y)
    {
        int sx = x + Origin.X, sy = y + Origin.Y;
        return _monitors.FirstOrDefault(m => m.Bounds.Contains(sx, sy))
            ?? Monitors.FromPoint(sx, sy);
    }

    // ----------------------------------------------------------------- layout

    private readonly record struct Frame(
        PixelRect Selection, int Border, PixelRect Label, string LabelText, PixelRect Magnifier, PixelRect MagnifierInfo,
        string MagnifierText, PixelRect Hint, string HintText, int CursorX, int CursorY, bool Crosshair, int Width, int Height, double Scale)
    {
        public IEnumerable<PixelRect> DirtyRects()
        {
            if (!Selection.IsEmpty)
            {
                yield return Selection.Inflate(Border + 1);
            }

            if (!Label.IsEmpty) yield return Label.Inflate(2);
            if (!Magnifier.IsEmpty) yield return Magnifier.Inflate(Border + 1);
            if (!MagnifierInfo.IsEmpty) yield return MagnifierInfo.Inflate(2);
            if (!Hint.IsEmpty) yield return Hint.Inflate(2);
            if (Crosshair)
            {
                yield return new PixelRect(0, CursorY, Width, 1);
                yield return new PixelRect(CursorX, 0, 1, Height);
            }
        }
    }

    private Frame BuildFrame()
    {
        var monitor = MonitorAt(_cx, _cy);
        var scale = Math.Max(1.0, monitor.Scale);
        var mon = monitor.Bounds.Offset(-Origin.X, -Origin.Y);
        var border = Math.Max(2, (int)Math.Round(2 * scale));

        PixelRect selection;
        if (_dragging)
        {
            selection = DragRect();
        }
        else if (_hoverWindow is { } window)
        {
            selection = window.Bounds.Intersect(Origin).Offset(-Origin.X, -Origin.Y);
        }
        else
        {
            selection = mon;
        }

        // Size label
        var labelText = $"{selection.Width} × {selection.Height}";
        if (!_dragging && _hoverWindow is { Title.Length: > 0 } hovered)
        {
            var title = hovered.Title.Length > 48 ? hovered.Title[..47] + "…" : hovered.Title;
            labelText = $"{title}  ·  {labelText}";
        }

        var labelSize = MeasureText(labelText, scale, out _);
        var pad = (int)(8 * scale);
        var labelW = labelSize.Width + pad * 2;
        var labelH = labelSize.Height + (int)(6 * scale);
        var gap = (int)(6 * scale);
        var lx = Math.Clamp(selection.X, mon.Left + gap, Math.Max(mon.Left + gap, mon.Right - labelW - gap));
        var ly = selection.Bottom + gap + labelH <= mon.Bottom - gap
            ? selection.Bottom + gap
            : selection.Top - gap - labelH >= mon.Top + gap
                ? selection.Top - gap - labelH
                : selection.Top + gap;
        var label = new PixelRect(lx, ly, labelW, labelH);

        // Magnifier
        PixelRect magnifier = default, magInfo = default;
        var magText = string.Empty;
        if (_options.ShowMagnifier)
        {
            var zoom = Math.Max(6, (int)Math.Round(8 * scale));
            var size = MagnifierCells * zoom;
            var offset = (int)(24 * scale);
            var mx = _cx + offset + size <= mon.Right ? _cx + offset : _cx - offset - size;
            var my = _cy + offset + size + labelH + gap <= mon.Bottom ? _cy + offset : _cy - offset - size - labelH - gap;
            magnifier = new PixelRect(mx, my, size, size);

            var color = PixelAt(_cx, _cy);
            magText = $"{_cx + Origin.X}, {_cy + Origin.Y}   #{color.R:X2}{color.G:X2}{color.B:X2}";
            var infoSize = MeasureText(magText, scale, out _);
            var infoW = Math.Max(size, infoSize.Width + pad * 2);
            magInfo = new PixelRect(mx + (size - infoW) / 2, my + size + gap, infoW, labelH);
        }

        // Hint
        PixelRect hint = default;
        var hintText = string.Empty;
        if (!_mouseDown)
        {
            hintText = _options.Mode == OverlayMode.Recording
                ? "Record: drag a region  ·  click a window  ·  Space = full screen  ·  Esc = cancel"
                : "Drag a region  ·  click a window  ·  Space = full screen  ·  Esc = cancel";
            var hintSize = MeasureText(hintText, scale, out _);
            var hw = hintSize.Width + pad * 3;
            var hh = hintSize.Height + (int)(12 * scale);
            hint = new PixelRect(mon.X + (mon.Width - hw) / 2, mon.Y + (int)(24 * scale), hw, hh);
        }

        return new Frame(selection, border, label, labelText, magnifier, magInfo, magText, hint, hintText,
            _cx, _cy, _options.ShowCrosshair, Origin.Width, Origin.Height, scale);
    }

    private (byte R, byte G, byte B) PixelAt(int x, int y)
    {
        var bmp = _snapshot.Bitmap;
        if (x < 0 || y < 0 || x >= bmp.Width || y >= bmp.Height)
        {
            return default;
        }

        var p = bmp.Bits + (long)y * bmp.Stride + x * 4;
        return (p[2], p[1], p[0]);
    }

    // ---------------------------------------------------------------- drawing

    private void Invalidate(PixelRect rect)
    {
        var r = rect.ToRect();
        Win32.InvalidateRect(Handle, &r, false);
    }

    private void Paint()
    {
        var region = Win32.CreateRectRgn(0, 0, 0, 0);
        var hasRegion = Win32.GetUpdateRgn(Handle, region, false) > 1;
        PAINTSTRUCT ps;
        var hdc = Win32.BeginPaint(Handle, &ps);
        try
        {
            var back = _back.Dc;
            Win32.SelectClipRgn(back, hasRegion ? region : 0);
            Render(back, _frame);
            Win32.SelectClipRgn(back, 0);
            var r = ps.rcPaint;
            Native.BitBlt(hdc, r.Left, r.Top, r.Width, r.Height, back, r.Left, r.Top, Native.SRCCOPY);
        }
        finally
        {
            Win32.EndPaint(Handle, &ps);
            Native.DeleteObject(region);
        }
    }

    private void Render(nint dc, Frame f)
    {
        Native.BitBlt(dc, 0, 0, f.Width, f.Height, _dim.Dc, 0, 0, Native.SRCCOPY);

        var s = f.Selection;
        if (!s.IsEmpty)
        {
            Native.BitBlt(dc, s.X, s.Y, s.Width, s.Height, _snapshot.Bitmap.Dc, s.X, s.Y, Native.SRCCOPY);
        }

        if (f.Crosshair)
        {
            var h = new RECT(0, f.CursorY, f.Width, f.CursorY + 1);
            var v = new RECT(f.CursorX, 0, f.CursorX + 1, f.Height);
            Win32.FillRect(dc, &h, _whiteBrush);
            Win32.FillRect(dc, &v, _whiteBrush);
        }

        if (!s.IsEmpty)
        {
            DrawFrame(dc, s.Inflate(f.Border), f.Border, _accentBrush);
        }

        DrawPill(dc, f.Label, f.LabelText, f.Scale);

        if (!f.Magnifier.IsEmpty)
        {
            DrawMagnifier(dc, f);
            DrawPill(dc, f.MagnifierInfo, f.MagnifierText, f.Scale);
        }

        if (!f.Hint.IsEmpty)
        {
            DrawPill(dc, f.Hint, f.HintText, f.Scale);
        }
    }

    private void DrawMagnifier(nint dc, Frame f)
    {
        var m = f.Magnifier;
        var zoom = m.Width / MagnifierCells;
        Native.SetStretchBltMode(dc, Native.COLORONCOLOR);
        Native.StretchBlt(dc, m.X, m.Y, m.Width, m.Height, _snapshot.Bitmap.Dc,
            f.CursorX - MagnifierCells / 2, f.CursorY - MagnifierCells / 2, MagnifierCells, MagnifierCells, Native.SRCCOPY);

        var center = new PixelRect(m.X + MagnifierCells / 2 * zoom, m.Y + MagnifierCells / 2 * zoom, zoom, zoom);
        DrawFrame(dc, center.Inflate(1), 1, _blackBrush);
        DrawFrame(dc, center, 1, _whiteBrush);
        DrawFrame(dc, m.Inflate(f.Border), f.Border, _accentBrush);
    }

    private static void DrawFrame(nint dc, PixelRect r, int thickness, nint brush)
    {
        var top = new RECT(r.Left, r.Top, r.Right, r.Top + thickness);
        var bottom = new RECT(r.Left, r.Bottom - thickness, r.Right, r.Bottom);
        var left = new RECT(r.Left, r.Top, r.Left + thickness, r.Bottom);
        var right = new RECT(r.Right - thickness, r.Top, r.Right, r.Bottom);
        Win32.FillRect(dc, &top, brush);
        Win32.FillRect(dc, &bottom, brush);
        Win32.FillRect(dc, &left, brush);
        Win32.FillRect(dc, &right, brush);
    }

    private void DrawPill(nint dc, PixelRect rect, string text, double scale)
    {
        if (rect.IsEmpty || text.Length == 0)
        {
            return;
        }

        var radius = (int)(10 * scale);
        var oldBrush = Native.SelectObject(dc, _pillBrush);
        var oldPen = Native.SelectObject(dc, Win32.GetStockObject(8)); // NULL_PEN
        Win32.RoundRect(dc, rect.Left, rect.Top, rect.Right + 1, rect.Bottom + 1, radius, radius);
        Native.SelectObject(dc, oldPen);
        Native.SelectObject(dc, oldBrush);

        var oldFont = Native.SelectObject(dc, Font(scale));
        Win32.SetBkMode(dc, Win32.TRANSPARENT);
        Win32.SetTextColor(dc, Win32.Rgb(245, 245, 245));
        var r = rect.ToRect();
        Win32.DrawText(dc, text, text.Length, &r, Win32.DT_CENTER | Win32.DT_VCENTER | Win32.DT_SINGLELINE | Win32.DT_NOPREFIX);
        Native.SelectObject(dc, oldFont);
    }

    private (int Width, int Height) MeasureText(string text, double scale, out nint font)
    {
        font = Font(scale);
        var dc = _back.Dc;
        var old = Native.SelectObject(dc, font);
        var r = new RECT(0, 0, 0, 0);
        Win32.DrawText(dc, text, text.Length, &r, Win32.DT_CALCRECT | Win32.DT_SINGLELINE | Win32.DT_NOPREFIX);
        Native.SelectObject(dc, old);
        return (r.Width, r.Height);
    }

    private nint Font(double scale)
    {
        var height = -(int)Math.Round(13 * scale);
        if (!_fonts.TryGetValue(height, out var font))
        {
            font = Win32.CreateFont(height, 0, 0, 0, Win32.FW_SEMIBOLD, 0, 0, 0, 1, 0, 0, Win32.CLEARTYPE_QUALITY, 0, "Segoe UI");
            _fonts[height] = font;
        }

        return font;
    }
}
