// dsh-pet · Win32 互操作
//
// 只收拢真正用到的那几个 API：分层窗口合成、逐像素命中、窗口枚举与前台唤醒、
// 进程存活探测。命名保持和 MSDN 一致，方便对照文档排查。
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32;

namespace DshPet
{
    static class Native
    {
        public const int WS_EX_LAYERED = 0x00080000;
        public const int WS_EX_TOOLWINDOW = 0x00000080;
        public const int WS_EX_NOACTIVATE = 0x08000000;
        public const int WS_EX_APPWINDOW = 0x00040000;
        public const int WS_EX_TRANSPARENT = 0x00000020;

        public const int WM_NCHITTEST = 0x0084;
        public const int WM_MOUSEACTIVATE = 0x0021;
        public const int WM_CANCELMODE = 0x001F;
        public const int WM_CAPTURECHANGED = 0x0215;
        public const int WM_LBUTTONDOWN = 0x0201;
        public const int WM_LBUTTONUP = 0x0202;
        public const int WM_MOUSEMOVE = 0x0200;
        public const int WM_LBUTTONDBLCLK = 0x0203;
        public const int WM_RBUTTONUP = 0x0205;
        public const int WM_DISPLAYCHANGE = 0x007E;
        public const int WM_DPICHANGED = 0x02E0;

        public const int HTTRANSPARENT = -1;
        public const int HTCLIENT = 1;
        public const int MA_NOACTIVATE = 3;


        public const int ULW_ALPHA = 2;

        const int GWL_STYLE = -16;
        const int GWL_EXSTYLE = -20;
        const int WS_THICKFRAME = 0x00040000;
        const uint GW_OWNER = 4;
        const uint FLASHW_ALL = 3;
        const int SM_SWAPBUTTON = 23;
        const int VK_LBUTTON = 0x01;
        const int VK_RBUTTON = 0x02;
        const int VK_MBUTTON = 0x04;

        public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

        [StructLayout(LayoutKind.Sequential)]
        public struct FLASHWINFO
        {
            public uint cbSize;
            public IntPtr hwnd;
            public uint dwFlags;
            public uint uCount;
            public uint dwTimeout;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT
        {
            public int X;
            public int Y;
            public POINT(int x, int y) { X = x; Y = y; }
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct SIZE
        {
            public int cx;
            public int cy;
            public SIZE(int w, int h) { cx = w; cy = h; }
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct BLENDFUNCTION
        {
            public byte BlendOp;
            public byte BlendFlags;
            public byte SourceConstantAlpha;
            public byte AlphaFormat;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct BITMAPINFOHEADER
        {
            public uint biSize;
            public int biWidth;
            public int biHeight;
            public ushort biPlanes;
            public ushort biBitCount;
            public uint biCompression;
            public uint biSizeImage;
            public int biXPelsPerMeter;
            public int biYPelsPerMeter;
            public uint biClrUsed;
            public uint biClrImportant;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct BITMAPINFO
        {
            public BITMAPINFOHEADER bmiHeader;
            public uint bmiColors;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize,
            IntPtr hdcSrc, ref POINT pprSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);

        [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
        [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleDC(IntPtr hDC);
        [DllImport("gdi32.dll")] public static extern bool DeleteDC(IntPtr hDC);
        [DllImport("gdi32.dll")] public static extern IntPtr SelectObject(IntPtr hDC, IntPtr hObject);
        [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr hObject);

        [DllImport("gdi32.dll")]
        public static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFO pbmi, uint iUsage,
            out IntPtr ppvBits, IntPtr hSection, uint dwOffset);

        [DllImport("user32.dll")] public static extern IntPtr SetCapture(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern IntPtr GetCapture();

        [DllImport("user32.dll")] public static extern bool ReleaseCapture();
        [DllImport("user32.dll")] static extern short GetAsyncKeyState(int virtualKey);
        [DllImport("user32.dll")] static extern int GetSystemMetrics(int index);

        public static bool IsPrimaryMouseButtonDown()
        {
            int button = GetSystemMetrics(SM_SWAPBUTTON) != 0 ? VK_RBUTTON : VK_LBUTTON;
            return GetAsyncKeyState(button) < 0;
        }

        public static int PressedMouseButtons()
        {
            return (GetAsyncKeyState(VK_LBUTTON) < 0 ? 1 : 0)
                | (GetAsyncKeyState(VK_RBUTTON) < 0 ? 2 : 0)
                | (GetAsyncKeyState(VK_MBUTTON) < 0 ? 4 : 0);
        }

        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr hWnd);
        [DllImport("user32.dll")] static extern bool IsHungAppWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);

        [DllImport("user32.dll")]
        public static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr hWnd, StringBuilder name, int count);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongW", ExactSpelling = true)] static extern int GetWindowStyle(IntPtr hWnd, int index);
        [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr hWnd, uint command);

        [DllImport("user32.dll")]
        public static extern bool FlashWindowEx(ref FLASHWINFO pwfi);

        [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT lpPoint);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CloseHandle(IntPtr handle);

        /// <summary>进程还活着吗（用于 DSH 退出后浮层自动收摊）。</summary>
        public static bool IsProcessAlive(int pid)
        {
            if (pid <= 0) return true;
            IntPtr handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (handle == IntPtr.Zero) return false;
            try
            {
                int code;
                if (!GetExitCodeProcess(handle, out code)) return false;
                return code == 259; // STILL_ACTIVE
            }
            finally
            {
                CloseHandle(handle);
            }
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool GetExitCodeProcess(IntPtr hProcess, out int lpExitCode);

        static bool BelongsToProcess(IntPtr hwnd, int pid)
        {
            uint actualPid;
            return GetWindowThreadProcessId(hwnd, out actualPid) != 0 && actualPid == (uint)pid;
        }

        public static IntPtr FindMainWindow(int pid)
        {
            IntPtr best = IntPtr.Zero;
            int bestScore = -1;
            StringBuilder windowClass = new StringBuilder(64);
            StringBuilder title = new StringBuilder(512);
            EnumWindows(delegate(IntPtr hwnd, IntPtr ignored)
            {
                if (!BelongsToProcess(hwnd, pid) || GetWindow(hwnd, GW_OWNER) != IntPtr.Zero) return true;
                int extendedStyle = GetWindowStyle(hwnd, GWL_EXSTYLE);
                if ((extendedStyle & (WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE)) != 0) return true;
                if (GetClassName(hwnd, windowClass, windowClass.Capacity) == 0
                    || !string.Equals(windowClass.ToString(), "Chrome_WidgetWin_1", StringComparison.Ordinal)) return true;
                if (GetWindowText(hwnd, title, title.Capacity) == 0) return true;

                string caption = title.ToString();
                if (caption.StartsWith("DevTools", StringComparison.OrdinalIgnoreCase)) return true;
                bool productTitle = caption.Equals("DeepSeek Harness", StringComparison.OrdinalIgnoreCase)
                    || caption.Equals("DSH", StringComparison.OrdinalIgnoreCase)
                    || caption.EndsWith(" — DeepSeek Harness", StringComparison.OrdinalIgnoreCase)
                    || caption.EndsWith(" - DeepSeek Harness", StringComparison.OrdinalIgnoreCase);
                if (!productTitle && (GetWindowStyle(hwnd, GWL_STYLE) & WS_THICKFRAME) == 0) return true;

                int score = (productTitle ? 8 : 0) + (IsWindowVisible(hwnd) ? 2 : 0);
                if (score > bestScore)
                {
                    bestScore = score;
                    best = hwnd;
                }
                return true;
            }, IntPtr.Zero);
            return best;
        }

        // dsh://open is registered by the desktop app. Its Windows secondary instance exits
        // at the Electron single-instance lock, handing restore/show/focus to the owner.
        public static bool RequestDshOpen(IntPtr hwnd, int pid, out string result)
        {
            if (!BelongsToProcess(hwnd, pid))
            {
                result = "DSH window unavailable";
                return false;
            }
            if (IsHungAppWindow(hwnd))
            {
                result = "DSH window is not responding";
                return false;
            }

            string executable;
            using (Process current = Process.GetProcessById(pid))
            {
                executable = current.MainModule.FileName;
            }
            string registered = Registry.GetValue(
                @"HKEY_CLASSES_ROOT\dsh\shell\open\command", "", null) as string;
            if (!IsDshProtocolHandler(registered, executable))
            {
                result = "DSH protocol handler does not match the running app";
                Flash(hwnd);
                return false;
            }

            ProcessStartInfo start = new ProcessStartInfo(executable, "dsh://open");
            start.WorkingDirectory = Path.GetDirectoryName(executable);
            start.UseShellExecute = false;
            // The overlay may have inherited this from the DSH Node host.
            start.EnvironmentVariables.Remove("ELECTRON_RUN_AS_NODE");
            if (!BelongsToProcess(hwnd, pid))
            {
                result = "DSH window closed before handoff";
                return false;
            }

            using (Process handoff = Process.Start(start))
            {
                if (handoff == null)
                {
                    result = "could not start DSH protocol handoff";
                    return false;
                }
                // Wait for the short-lived instance to dispatch to the existing desktop app.
                if (!handoff.WaitForExit(1500))
                {
                    result = "DSH protocol handoff did not finish";
                    Flash(hwnd);
                    return false;
                }
                if (handoff.ExitCode != 0)
                {
                    result = "DSH protocol handoff exited with code " + handoff.ExitCode;
                    Flash(hwnd);
                    return false;
                }
            }

            Stopwatch wait = Stopwatch.StartNew();
            while (wait.ElapsedMilliseconds < 800 && BelongsToProcess(hwnd, pid))
            {
                if (GetForegroundWindow() == hwnd && IsWindowVisible(hwnd) && !IsIconic(hwnd))
                {
                    result = "DSH handoff completed; window foreground";
                    return true;
                }
                Thread.Sleep(40);
            }
            result = "DSH handoff completed; foreground not observed";
            if (BelongsToProcess(hwnd, pid)) Flash(hwnd);
            return false;
        }

        internal static bool IsDshProtocolHandler(string command, string executable)
        {
            return !String.IsNullOrWhiteSpace(executable) && command != null &&
                String.Equals(command.Trim(), "\"" + executable + "\" \"%1\"", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>任务栏闪烁提示（非阻塞，纯提示）。</summary>
        public static void Flash(IntPtr hWnd)
        {
            if (hWnd == IntPtr.Zero || !IsWindow(hWnd)) return;
            FLASHWINFO info = new FLASHWINFO();
            info.cbSize = (uint)Marshal.SizeOf(typeof(FLASHWINFO));
            info.hwnd = hWnd;
            info.dwFlags = FLASHW_ALL;
            info.uCount = 3;
            info.dwTimeout = 0;
            FlashWindowEx(ref info);
        }
    }
}