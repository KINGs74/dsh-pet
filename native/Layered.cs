// dsh-pet · 可复用的分层绘制面
//
// 宠物和菜单共用主题。菜单的 32bpp 位图转换为预乘 alpha 后与屏幕坐标一同提交。
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace DshPet
{
    sealed class OverlayPalette
    {
        public Color Surface { get; private set; }
        public Color Text { get; private set; }
        public Color Muted { get; private set; }
        public Color Line { get; private set; }
        public Color Hover { get; private set; }
        public Color Selected { get; private set; }
        public Color Accent { get; private set; }
        public Color Marker { get; private set; }
        public Color MarkerText { get; private set; }
        public Color Success { get; private set; }
        public Color SuccessFill { get; private set; }
        public Color Warning { get; private set; }
        public Color WarningFill { get; private set; }
        public Color Failure { get; private set; }
        public Color FailureFill { get; private set; }

        public static readonly OverlayPalette Light = new OverlayPalette
        {
            Surface = Color.FromArgb(250, 249, 251, 255),
            Text = Color.FromArgb(24, 37, 61),
            Muted = Color.FromArgb(84, 99, 123),
            Line = Color.FromArgb(220, 228, 241),
            Hover = Color.FromArgb(234, 241, 252),
            Selected = Color.FromArgb(221, 234, 253),
            Accent = Color.FromArgb(34, 93, 181),
            Marker = Color.FromArgb(36, 96, 189),
            MarkerText = Color.White,
            Success = Color.FromArgb(35, 108, 77),
            SuccessFill = Color.FromArgb(222, 240, 230),
            Warning = Color.FromArgb(138, 82, 22),
            WarningFill = Color.FromArgb(251, 236, 207),
            Failure = Color.FromArgb(164, 46, 68),
            FailureFill = Color.FromArgb(251, 228, 234),
        };

        public static readonly OverlayPalette Dark = new OverlayPalette
        {
            Surface = Color.FromArgb(251, 29, 38, 56),
            Text = Color.FromArgb(237, 243, 252),
            Muted = Color.FromArgb(174, 187, 208),
            Line = Color.FromArgb(59, 73, 96),
            Hover = Color.FromArgb(48, 63, 89),
            Selected = Color.FromArgb(49, 74, 115),
            Accent = Color.FromArgb(161, 195, 255),
            Marker = Color.FromArgb(172, 200, 253),
            MarkerText = Color.FromArgb(25, 49, 93),
            Success = Color.FromArgb(159, 223, 188),
            SuccessFill = Color.FromArgb(39, 66, 61),
            Warning = Color.FromArgb(240, 199, 126),
            WarningFill = Color.FromArgb(72, 56, 39),
            Failure = Color.FromArgb(250, 164, 179),
            FailureFill = Color.FromArgb(78, 46, 64),
        };

        public static OverlayPalette Current()
        {
            try
            {
                object value = Microsoft.Win32.Registry.GetValue(
                    @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                    "AppsUseLightTheme", 1);
                if (value is int && (int)value == 0) return Dark;
            }
            catch { }
            return Light;
        }

        public static bool IsThemeMessage(int message)
        {
            const int SettingChanged = 0x001A;
            const int ThemeChanged = 0x031A;
            return message == SettingChanged || message == ThemeChanged;
        }
    }

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
            _graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
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

        public bool Push(Form form, Point destination)
        {
            if (_bitmap == null || _dibBits == IntPtr.Zero) return false;
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

            Native.POINT dst = new Native.POINT(destination.X, destination.Y);
            Native.SIZE size = new Native.SIZE(w, h);
            Native.POINT src = new Native.POINT(0, 0);
            Native.BLENDFUNCTION blend = new Native.BLENDFUNCTION();
            blend.BlendOp = 0;
            blend.BlendFlags = 0;
            blend.SourceConstantAlpha = 255;
            blend.AlphaFormat = 1;
            return Native.UpdateLayeredWindow(form.Handle, _screenDc, ref dst, ref size, _memDc, ref src, 0, ref blend, Native.ULW_ALPHA);
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