// dsh-pet · 可复用的分层绘制面
//
// 把"32bpp 合成位图 → 预乘 alpha → DIB → UpdateLayeredWindow"这套流程收成一个类，
// 菜单和宠物窗口都用它。重点是预乘：UpdateLayeredWindow 要求每个颜色分量已乘以
// alpha，直接塞 GDI+ 的直通 alpha 会出现黑边/白边。
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace DshPet
{
    sealed class LayeredSurface : IDisposable
    {
        Bitmap _bitmap;
        Graphics _graphics;
        IntPtr _screenDc;
        IntPtr _memDc;
        IntPtr _dib;
        IntPtr _dibBits;
        IntPtr _dibOld;
        byte[] _scratch = new byte[0];
        byte[] _alpha = new byte[0];

        public int Width { get; private set; }
        public int Height { get; private set; }
        public Graphics Graphics { get { return _graphics; } }

        public void SaveDump(string path)
        {
            if (_bitmap == null) return;
            try
            {
                _bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            }
            catch
            {
            }
        }

        public void Resize(int width, int height)
        {
            if (width <= 0 || height <= 0) return;
            if (Width == width && Height == height && _bitmap != null) return;
            DisposeBitmaps();
            Width = width;
            Height = height;
            _bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            _graphics = Graphics.FromImage(_bitmap);
            _graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            _graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            EnsureDib();
        }

        void EnsureDib()
        {
            if (_screenDc == IntPtr.Zero) _screenDc = Native.GetDC(IntPtr.Zero);
            if (_memDc == IntPtr.Zero) _memDc = Native.CreateCompatibleDC(_screenDc);
            if (_dib != IntPtr.Zero)
            {
                Native.SelectObject(_memDc, _dibOld);
                Native.DeleteObject(_dib);
                _dib = IntPtr.Zero;
            }
            Native.BITMAPINFO info = new Native.BITMAPINFO();
            info.bmiHeader.biSize = (uint)Marshal.SizeOf(typeof(Native.BITMAPINFOHEADER));
            info.bmiHeader.biWidth = Width;
            info.bmiHeader.biHeight = -Height;
            info.bmiHeader.biPlanes = 1;
            info.bmiHeader.biBitCount = 32;
            IntPtr bits;
            _dib = Native.CreateDIBSection(_memDc, ref info, 0, out bits, IntPtr.Zero, 0);
            _dibBits = bits;
            _dibOld = Native.SelectObject(_memDc, _dib);
            int bytes = Width * Height * 4;
            if (_scratch.Length != bytes) _scratch = new byte[bytes];
            if (_alpha.Length != Width * Height) _alpha = new byte[Width * Height];
        }

        public void Push(Form form)
        {
            if (_bitmap == null) return;
            int w = Width;
            int h = Height;
            BitmapData data = _bitmap.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                int rowBytes = w * 4;
                for (int y = 0; y < h; y++)
                {
                    Marshal.Copy(new IntPtr(data.Scan0.ToInt64() + (long)y * data.Stride), _scratch, y * rowBytes, rowBytes);
                }
            }
            finally
            {
                _bitmap.UnlockBits(data);
            }

            for (int i = 0, p = 0; i < _scratch.Length; i += 4, p++)
            {
                byte a = _scratch[i + 3];
                if (a == 0)
                {
                    _scratch[i] = 0;
                    _scratch[i + 1] = 0;
                    _scratch[i + 2] = 0;
                }
                else if (a < 255)
                {
                    _scratch[i] = (byte)((_scratch[i] * a) / 255);
                    _scratch[i + 1] = (byte)((_scratch[i + 1] * a) / 255);
                    _scratch[i + 2] = (byte)((_scratch[i + 2] * a) / 255);
                }
                _alpha[p] = a;
            }

            if (_dibBits != IntPtr.Zero)
            {
                for (int y = 0; y < h; y++)
                {
                    Marshal.Copy(_scratch, y * w * 4, _dibBits + y * w * 4, w * 4);
                }
            }

            Native.POINT dst = new Native.POINT(form.Left, form.Top);
            Native.SIZE size = new Native.SIZE(w, h);
            Native.POINT src = new Native.POINT(0, 0);
            Native.BLENDFUNCTION blend = new Native.BLENDFUNCTION();
            blend.BlendOp = 0;
            blend.BlendFlags = 0;
            blend.SourceConstantAlpha = 255;
            blend.AlphaFormat = 1;
            Native.UpdateLayeredWindow(form.Handle, _screenDc, ref dst, ref size, _memDc, ref src, 0, ref blend, Native.ULW_ALPHA);
        }

        public bool HitTest(Point p)
        {
            if (p.X < 0 || p.Y < 0 || p.X >= Width || p.Y >= Height) return false;
            return _alpha[p.Y * Width + p.X] > 16;
        }

        void DisposeBitmaps()
        {
            if (_graphics != null) { _graphics.Dispose(); _graphics = null; }
            if (_bitmap != null) { _bitmap.Dispose(); _bitmap = null; }
        }

        public void Dispose()
        {
            DisposeBitmaps();
            if (_dib != IntPtr.Zero)
            {
                Native.SelectObject(_memDc, _dibOld);
                Native.DeleteObject(_dib);
                _dib = IntPtr.Zero;
            }
            if (_memDc != IntPtr.Zero) { Native.DeleteDC(_memDc); _memDc = IntPtr.Zero; }
            if (_screenDc != IntPtr.Zero) { Native.ReleaseDC(IntPtr.Zero, _screenDc); _screenDc = IntPtr.Zero; }
        }
    }
}