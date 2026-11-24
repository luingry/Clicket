using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Clicket.Core;
using Clicket.Native;

namespace Clicket.Input;

/// <summary>
/// Copies the whole virtual screen (every monitor, physical pixels) into memory with one BitBlt. The bitmap and buffer are
/// kept between grabs and rebuilt only when the desktop size changes, so a search every few hundred milliseconds allocates
/// nothing. Call from a thread that uses physical pixels (<see cref="NativeMethods.UsePhysicalPixels"/>).
/// </summary>
internal sealed class ScreenCapture : IDisposable
{
    private IntPtr _memoryDc, _bitmap, _old, _bits;
    private int _width, _height;
    private byte[] _buffer = new byte[0];
    private PixelImage? _image;

    /// <summary>Screen coordinates of the captured picture's top-left pixel.</summary>
    public int Left { get; private set; }

    public int Top { get; private set; }

    /// <summary>The current screen; the returned picture is reused by the next grab.</summary>
    public PixelImage Grab()
    {
        var x = NativeMethods.GetSystemMetrics(NativeMethods.SmXVirtualScreen);
        var y = NativeMethods.GetSystemMetrics(NativeMethods.SmYVirtualScreen);
        var w = NativeMethods.GetSystemMetrics(NativeMethods.SmCxVirtualScreen);
        var h = NativeMethods.GetSystemMetrics(NativeMethods.SmCyVirtualScreen);
        if (w <= 0 || h <= 0) throw new InvalidOperationException("No screen to capture.");
        if (w != _width || h != _height || _bitmap == IntPtr.Zero) Allocate(w, h);
        Left = x;
        Top = y;

        var screen = NativeMethods.GetDC(IntPtr.Zero);
        if (screen == IntPtr.Zero) throw new Win32Exception("GetDC failed.");
        try
        {
            // Fails while the secure desktop (UAC, lock screen) is up: the caller treats it as "not seen this time".
            if (!NativeMethods.BitBlt(_memoryDc, 0, 0, w, h, screen, x, y, NativeMethods.SrcCopy)) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        finally
        {
            NativeMethods.ReleaseDC(IntPtr.Zero, screen);
        }
        Marshal.Copy(_bits, _buffer, 0, _buffer.Length);
        return _image!;
    }

    private void Allocate(int width, int height)
    {
        Release();
        var screen = NativeMethods.GetDC(IntPtr.Zero);
        try
        {
            _memoryDc = NativeMethods.CreateCompatibleDC(screen);
            var header = new NativeMethods.BitmapInfoHeader
            {
                Size = Marshal.SizeOf<NativeMethods.BitmapInfoHeader>(),
                Width = width,
                Height = -height, // top-down rows
                Planes = 1,
                BitCount = 32,
            };
            _bitmap = NativeMethods.CreateDIBSection(screen, ref header, 0, out _bits, IntPtr.Zero, 0);
            if (_bitmap == IntPtr.Zero || _memoryDc == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            _old = NativeMethods.SelectObject(_memoryDc, _bitmap);
        }
        finally
        {
            NativeMethods.ReleaseDC(IntPtr.Zero, screen);
        }
        _width = width;
        _height = height;
        _buffer = new byte[width * height * 4];
        _image = new PixelImage(width, height, _buffer, width * 4);
    }

    private void Release()
    {
        if (_memoryDc != IntPtr.Zero && _old != IntPtr.Zero) NativeMethods.SelectObject(_memoryDc, _old);
        if (_bitmap != IntPtr.Zero) NativeMethods.DeleteObject(_bitmap);
        if (_memoryDc != IntPtr.Zero) NativeMethods.DeleteDC(_memoryDc);
        _memoryDc = _bitmap = _old = _bits = IntPtr.Zero;
        _width = _height = 0;
    }

    public void Dispose()
    {
        Release();
        _buffer = new byte[0];
        _image = null;
    }
}
