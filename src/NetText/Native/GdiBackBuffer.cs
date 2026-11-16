using System.Runtime.InteropServices;

namespace NetText.Native;

internal sealed unsafe class GdiBackBuffer : IDisposable
{
    private nint _memDc;
    private nint _dib;
    private nint _oldBmp;
    private int _width;
    private int _height;

    public nint Ensure(nint screenDc, int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            return 0;
        }

        if (_memDc != 0 &&
            width <= _width &&
            height <= _height &&
            (long)width * height * 2 > (long)_width * _height)
        {
            return _memDc;
        }

        ReleaseSurface();
        _memDc = Win32.CreateCompatibleDC(screenDc);
        var bmi = new Win32.BITMAPINFO();
        bmi.bmiHeader.biSize = (uint)Marshal.SizeOf<Win32.BITMAPINFOHEADER>();
        bmi.bmiHeader.biWidth = width;
        bmi.bmiHeader.biHeight = -height;
        bmi.bmiHeader.biPlanes = 1;
        bmi.bmiHeader.biBitCount = 32;
        bmi.bmiHeader.biCompression = 0;

        void* bits = null;
        _dib = Win32.CreateDIBSection(screenDc, &bmi, 0, &bits, 0, 0);
        if (_dib == 0)
        {
            _dib = Win32.CreateCompatibleBitmap(screenDc, width, height);
        }

        _oldBmp = Win32.SelectObject(_memDc, _dib);
        _width = width;
        _height = height;
        return _memDc;
    }

    private void ReleaseSurface()
    {
        if (_memDc != 0)
        {
            if (_oldBmp != 0)
            {
                Win32.SelectObject(_memDc, _oldBmp);
                _oldBmp = 0;
            }
            if (_dib != 0)
            {
                Win32.DeleteObject(_dib);
                _dib = 0;
            }
            Win32.DeleteDC(_memDc);
            _memDc = 0;
        }
        _width = 0;
        _height = 0;
    }

    public void Dispose()
    {
        ReleaseSurface();
    }
}
