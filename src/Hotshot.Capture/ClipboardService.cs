using System.Runtime.InteropServices;
using System.Text;
using Hotshot.Interop;

namespace Hotshot.Capture;

/// <summary>Win32 clipboard writer. Requires an owner window handle (SetClipboardData fails without one).</summary>
public static class ClipboardService
{
    private static readonly uint PngFormat = Native.RegisterClipboardFormat("PNG");
    private static readonly uint DropEffectFormat = Native.RegisterClipboardFormat("Preferred DropEffect");

    /// <summary>Puts an image on the clipboard as CF_DIB plus the registered "PNG" format.</summary>
    public static bool SetImage(nint owner, CapturedImage image, byte[]? png) =>
        SetData(owner, CreateImageData(image, png));

    public static Task<bool> SetImageAsync(nint owner, CapturedImage image, byte[]? png) =>
        SetDataAsync(owner, CreateImageData(image, png));

    private static (uint Format, nint Handle)[] CreateImageData(CapturedImage image, byte[]? png)
    {
        var dib = CreateDib(image);
        var pngHandle = png is { Length: > 0 } ? ToGlobal(png) : 0;
        return [(Native.CF_DIB, dib), (PngFormat, pngHandle)];
    }

    /// <summary>Puts files on the clipboard (paste into Explorer, Teams, Slack, Outlook…).</summary>
    public static bool SetFiles(nint owner, IReadOnlyList<string> paths) =>
        SetData(owner, CreateFileData(paths));

    public static Task<bool> SetFilesAsync(nint owner, IReadOnlyList<string> paths) =>
        SetDataAsync(owner, CreateFileData(paths));

    private static (uint Format, nint Handle)[] CreateFileData(IReadOnlyList<string> paths)
    {
        var drop = CreateDropFiles(paths);
        var effect = ToGlobal(BitConverter.GetBytes(1)); // DROPEFFECT_COPY
        return [(Native.CF_HDROP, drop), (DropEffectFormat, effect)];
    }

    public static bool SetText(nint owner, string text)
    {
        var bytes = Encoding.Unicode.GetBytes(text + "\0");
        return SetData(owner, [(Native.CF_UNICODETEXT, ToGlobal(bytes))]);
    }

    private static bool SetData(nint owner, (uint Format, nint Handle)[] items)
    {
        if (items[0].Handle == 0 || !TryOpen(owner))
        {
            FreeAll(items);
            return false;
        }


        return WriteData(items);
    }

    private static async Task<bool> SetDataAsync(nint owner, (uint Format, nint Handle)[] items)
    {
        if (items[0].Handle != 0)
        {
            for (var attempt = 0; attempt < 15; attempt++)
            {
                if (Native.OpenClipboard(owner))
                {
                    return WriteData(items);
                }

                await Task.Delay(15 + attempt * 5);
            }
        }

        FreeAll(items);
        return false;
    }

    private static bool WriteData((uint Format, nint Handle)[] items)
    {
        var ok = true;
        try
        {
            if (!Native.EmptyClipboard())
            {
                FreeAll(items);
                return false;
            }
            foreach (ref var item in items.AsSpan())
            {
                if (item.Handle == 0)
                {
                    continue;
                }

                if (Native.SetClipboardData(item.Format, item.Handle) == 0)
                {
                    ok = false;
                    Native.GlobalFree(item.Handle);
                }

                // On success the system owns the memory.
                item.Handle = 0;
            }
        }
        finally
        {
            Native.CloseClipboard();
        }

        return ok;
    }

    private static bool TryOpen(nint owner)
    {
        for (var attempt = 0; attempt < 15; attempt++)
        {
            if (Native.OpenClipboard(owner))
            {
                return true;
            }

            Thread.Sleep(15 + attempt * 5);
        }

        return false;
    }

    private static void FreeAll((uint Format, nint Handle)[] items)
    {
        foreach (var item in items)
        {
            if (item.Handle != 0)
            {
                Native.GlobalFree(item.Handle);
            }
        }
    }

    private static unsafe nint CreateDib(CapturedImage image)
    {
        var header = new BITMAPINFOHEADER
        {
            biSize = sizeof(BITMAPINFOHEADER),
            biWidth = image.Width,
            biHeight = image.Height, // bottom-up, the most compatible layout
            biPlanes = 1,
            biBitCount = 32,
            biCompression = Native.BI_RGB,
            biSizeImage = image.Stride * image.Height,
        };

        var total = sizeof(BITMAPINFOHEADER) + header.biSizeImage;
        var handle = Native.GlobalAlloc(Native.GMEM_MOVEABLE, (nuint)total);
        if (handle == 0)
        {
            return 0;
        }

        var ptr = (byte*)Native.GlobalLock(handle);
        if (ptr is null)
        {
            Native.GlobalFree(handle);
            return 0;
        }

        try
        {
            *(BITMAPINFOHEADER*)ptr = header;
            var dst = ptr + sizeof(BITMAPINFOHEADER);
            var stride = image.Stride;
            fixed (byte* src = image.Pixels)
            {
                for (var y = 0; y < image.Height; y++)
                {
                    Buffer.MemoryCopy(src + (long)(image.Height - 1 - y) * stride, dst + (long)y * stride, stride, stride);
                }
            }
        }
        finally
        {
            Native.GlobalUnlock(handle);
        }

        return handle;
    }

    private static nint CreateDropFiles(IReadOnlyList<string> paths)
    {
        // DROPFILES { DWORD pFiles; POINT pt; BOOL fNC; BOOL fWide; } followed by a double-null-terminated list.
        const int headerSize = 20;
        var list = string.Join('\0', paths.Select(Path.GetFullPath)) + "\0\0";
        var chars = Encoding.Unicode.GetBytes(list);
        var bytes = new byte[headerSize + chars.Length];
        BitConverter.TryWriteBytes(bytes.AsSpan(0, 4), headerSize);
        BitConverter.TryWriteBytes(bytes.AsSpan(16, 4), 1);
        chars.CopyTo(bytes, headerSize);
        return ToGlobal(bytes);
    }

    private static unsafe nint ToGlobal(ReadOnlySpan<byte> bytes)
    {
        var handle = Native.GlobalAlloc(Native.GMEM_MOVEABLE, (nuint)bytes.Length);
        if (handle == 0)
        {
            return 0;
        }

        var ptr = Native.GlobalLock(handle);
        if (ptr is null)
        {
            Native.GlobalFree(handle);
            return 0;
        }

        bytes.CopyTo(new Span<byte>(ptr, bytes.Length));
        Native.GlobalUnlock(handle);
        return handle;
    }
}
