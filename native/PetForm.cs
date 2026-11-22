// dsh-pet · 浮层主窗口
//
// 一个「分层窗口 + 逐像素命中」的置顶窗体：宠物、对话气泡、悬停工具条都画在
// 同一张 32bpp 位图上，再整块 UpdateLayeredWindow 推上去。这样做的好处：
//   · 真·任意形状，宠物周围透明区域可以点击穿透（WM_NCHITTEST → HTTRANSPARENT）；
//   · 不占任务栏、不抢焦点、任何窗口之上（包括全屏视频与最小化的 DSH）；
//   · 拖动位置算的是屏幕坐标，天然不受 DSH 面板/窗口裁剪。
//
// 交互（v0.2）：
//   单击    → 有结束提醒先确认，然后打开/唤起 DSH（含"隐藏到托盘"和"最小化"两种状态）
//   拖动    → 放到屏幕任意位置，松手即持久化
//   悬停    → 头顶浮出 ✎ / 🎤 小胶囊，移开 300ms 自动收起
//   双击    → 打开/唤起 DSH
//   右键    → 自绘圆角半透明菜单
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace DshPet
{
    sealed class PetForm : Form
    {
        readonly Opts _opts;
        PetState _state = new PetState();

        // —— 分层窗口合成 ——
        Bitmap _compose;
        Graphics _composeG;
        IntPtr _screenDc;
        IntPtr _memDc;
        IntPtr _dib;
        IntPtr _dibBits;
        IntPtr _dibOld;
        int _dibW;
        int _dibH;
        byte[] _scratch = new byte[0];
        byte[] _alpha = new byte[0];

        // —— 宠物贴图 ——
        Image _sheet;
        string _sheetPath = "";
        int _row;
        int _frame;
        DateTime _frameAt = DateTime.UtcNow;
        DateTime _celebrateUntil = DateTime.UtcNow.AddSeconds(-1);

        // —— 交互状态 ——
        bool _dragging;
        bool _dragMoved;
        long _dragPushAt;
        Point _dragStartScreen;
        Point _dragStartWindow;
        bool _hover;
        DateTime _hoverLostAt = DateTime.UtcNow;
        int _hoverButton;
        Point _lastMouse;

        // 动作条（✎/🎤）单独一行、宠物下方、没有容器；这三个矩形都由 Relayout 算
        Rectangle _actionsRect = Rectangle.Empty;
        Rectangle _btnCompose = Rectangle.Empty;
        Rectangle _btnVoice = Rectangle.Empty;
        Rectangle _bubbleRect = Rectangle.Empty;
        Rectangle _petRect = Rectangle.Empty;
        int _petBottomOffset;   // 宠物底边在窗口内的高度，锚点靠它保持"脚不动"
        bool _actionsBelow = true;

        // —— 气泡计时 ——
        DateTime _contentAt = DateTime.UtcNow;
        string _contentKey = "";
        DateTime _alertAt = DateTime.UtcNow;
        string _lastAlertId = "";
        

        // —— 连接与生命周期 ——
        bool _connectedOnce;
        int _failures;
        volatile bool _closing;
        int _dshPid;
        bool _focusPending;
        bool _focusRequested;
        int _lastFocusAt;
        DateTime _focusHintUntil;

        // —— 位置锚点（宠物底边中点，屏幕物理像素）——
        int _anchorX = int.MinValue;
        int _anchorY = int.MinValue;
        // 服务端上一次下发的锚点：只要它变了（比如 /pet 或别的入口改了配置），就把宠物挪过去
        int _serverPosX = int.MinValue;
        int _serverPosY = int.MinValue;

        float _dpi = 1f;

        readonly Font _titleFont;
        readonly Font _bodyFont;
        readonly Font _smallFont;
        readonly Font _iconFont;
        readonly Graphics _measure;

        System.Windows.Forms.Timer _animTimer;
        System.Windows.Forms.Timer _hoverTimer;

        OverlayMenu _menu;
        ComposeWindow _composeWindow;

        public PetForm(Opts opts)
        {
            _opts = opts;
            _dshPid = opts.DshPid;

            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            Text = "DSH 智能伙伴";
            BackColor = Color.Black;
            SetStyle(ControlStyles.Opaque, true);
            SetStyle(ControlStyles.UserMouse, true);
            SetStyle(ControlStyles.AllPaintingInWmPaint, true);
            DoubleBuffered = false;
            Size = new Size(240, 260);

            _measure = Graphics.FromHwnd(IntPtr.Zero);
            _dpi = Math.Max(1f, _measure.DpiX / 96f);
            _titleFont = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Bold, GraphicsUnit.Point);
            _bodyFont = new Font("Microsoft YaHei UI", 9f, FontStyle.Regular, GraphicsUnit.Point);
            _smallFont = new Font("Microsoft YaHei UI", 8f, FontStyle.Regular, GraphicsUnit.Point);
            _iconFont = new Font("Segoe MDL2 Assets", 11f, FontStyle.Regular, GraphicsUnit.Point);

            _animTimer = new System.Windows.Forms.Timer();
            _animTimer.Interval = 80;
            _animTimer.Tick += delegate { OnAnimTick(); };

            _hoverTimer = new System.Windows.Forms.Timer();
            _hoverTimer.Interval = 120;
            _hoverTimer.Tick += delegate { OnHoverTick(); };

            Shown += delegate
            {
                _animTimer.Start();
                _hoverTimer.Start();
                StartPolling();
                StartStallWatchdog();
            };
            FormClosing += delegate
            {
                _closing = true;
                _animTimer.Stop();
                _hoverTimer.Stop();
                if (IsComposing) _composeWindow.CloseForShutdown();
                CloseMenu();
            };
        }

        protected override bool ShowWithoutActivation
        {
            get { return true; }
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= Native.WS_EX_LAYERED | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE;
                cp.ExStyle &= ~Native.WS_EX_APPWINDOW;
                return cp;
            }
        }

        // ================= 布局与绘制 =================

        int S(int value)
        {
            return (int)Math.Round(value * _dpi);
        }

        float S(float value)
        {
            return (float)(value * _dpi);
        }

        int PetW { get { return Math.Max(32, (int)Math.Round(_state.FrameW * _state.Scale)); } }
        int PetH { get { return Math.Max(32, (int)Math.Round(_state.FrameH * _state.Scale)); } }
        int BubbleW { get { return S(300); } }

        bool HoverVisible
        {
            get { return _hover || IsComposing; }
        }

        bool IsComposing
        {
            get { return _composeWindow != null && !_composeWindow.IsDisposed && _composeWindow.Visible; }
        }

        string Localized(string zh, string en)
        {
            return _state.Lang == "en" ? en : zh;
        }

        bool FocusHintVisible { get { return DateTime.UtcNow < _focusHintUntil; } }

        bool BubbleVisible()
        {
            if (FocusHintVisible) return true;
            if (!_state.Bubble) return false;
            DateTime now = DateTime.UtcNow;
            if (_state.AlertId.Length > 0)
            {
                int alertMs = _state.AlertMs > 0 ? _state.AlertMs : 30000;
                if ((now - _alertAt).TotalMilliseconds < alertMs) return true;
            }
            if (_state.BubbleMs <= 0) return HasBubbleContent();
            if (!HasBubbleContent()) return false;
            return (now - _contentAt).TotalMilliseconds < _state.BubbleMs;
        }

        bool HasBubbleContent()
        {
            return _state.Title.Length > 0 || _state.Line1.Length > 0 || _state.AlertId.Length > 0 || _state.Toast.Length > 0;
        }

        void Relayout(bool force)
        {
            bool showBubble = BubbleVisible();
            bool showActions = HoverVisible;
            int pad = S(6);
            int gap = S(6);
            int petW = PetW;
            int petH = PetH;
            int actionsW = S(72);
            int actionsH = S(30);
            int bubbleW = showBubble ? Math.Min(BubbleW, Math.Max(petW, S(210))) : 0;
            int bubbleH = showBubble ? MeasureBubble(bubbleW) : 0;

            // 动作条默认在宠物下方的独立一行；宠物贴着屏幕底边放不下时翻到最上面（气泡之上），
            // 这样贴边时按钮也不会被裁掉。
            _actionsBelow = true;
            if (showActions)
            {
                Point anchorPoint = new Point(_anchorX == int.MinValue ? 0 : _anchorX, _anchorY == int.MinValue ? 0 : _anchorY);
                Screen screen = Screen.FromPoint(anchorPoint);
                Rectangle area = screen != null ? screen.WorkingArea : Screen.PrimaryScreen.WorkingArea;
                _actionsBelow = _anchorY == int.MinValue || area.Bottom - _anchorY >= actionsH + gap + pad;
            }

            int contentW = Math.Max(Math.Max(petW, bubbleW), showActions ? actionsW : 0);
            int winW = contentW + pad * 2;

            int y = pad;
            if (showActions && !_actionsBelow)
            {
                _actionsRect = new Rectangle((winW - actionsW) / 2, y, actionsW, actionsH);
                y += actionsH + gap;
            }
            else
            {
                _actionsRect = Rectangle.Empty;
            }

            if (showBubble)
            {
                _bubbleRect = new Rectangle((winW - bubbleW) / 2, y, bubbleW, bubbleH);
                y += bubbleH + gap;
            }
            else
            {
                _bubbleRect = Rectangle.Empty;
            }

            _petRect = new Rectangle((winW - petW) / 2, y, petW, petH);
            y += petH;

            if (showActions && _actionsBelow)
            {
                y += gap;
                _actionsRect = new Rectangle((winW - actionsW) / 2, y, actionsW, actionsH);
                y += actionsH;
            }

            int winH = y + pad;
            if (winW < S(80)) winW = S(80);

            // 两个按钮各 30×30，居中排在动作条里，中间留 10px —— 彼此分开，没有整体容器。
            int btn = S(30);
            int buttonsW = btn * 2 + S(10);
            int left = _actionsRect.X + Math.Max(0, (_actionsRect.Width - buttonsW) / 2);
            _btnCompose = new Rectangle(left, _actionsRect.Y, btn, actionsH);
            _btnVoice = new Rectangle(left + btn + S(10), _actionsRect.Y, btn, actionsH);

            _petBottomOffset = _petRect.Bottom;

            bool sizeChanged = winW != Width || winH != Height;
            if (sizeChanged || force)
            {
                ResizeCompose(winW, winH);
                Size = new Size(winW, winH);
            }

            ApplyAnchor(winW, winH);
            Render();
        }

        void ApplyAnchor(int winW, int winH)
        {
            if (_anchorX == int.MinValue)
            {
                Rectangle area = Screen.PrimaryScreen.WorkingArea;
                _anchorX = area.Right - S(140);
                _anchorY = area.Bottom - S(28);
            }
            int x = _anchorX - winW / 2;
            int y = _anchorY - _petBottomOffset;
            Point target = ClampWindow(new Point(x, y), winW, winH);
            if (Location != target) Location = target;
        }

        /// <summary>窗口整体收进"离它最近的显示器"工作区：换分辨率/拔显示器后仍然看得见。</summary>
        Point ClampWindow(Point p, int winW, int winH)
        {
            Rectangle area;
            Screen screen = Screen.FromPoint(new Point(p.X + winW / 2, p.Y + winH / 2));
            area = screen != null ? screen.WorkingArea : Screen.PrimaryScreen.WorkingArea;
            int margin = S(4);
            int x = p.X;
            int y = p.Y;
            if (x < area.Left + margin) x = area.Left + margin;
            if (y < area.Top + margin) y = area.Top + margin;
            if (x + winW > area.Right - margin) x = area.Right - margin - winW;
            if (y + winH > area.Bottom - margin) y = area.Bottom - margin - winH;
            if (x < area.Left) x = area.Left;
            if (y < area.Top) y = area.Top;
            return new Point(x, y);
        }

        int MeasureBubble(int width)
        {
            int padX = S(12);
            int padY = S(9);
            int inner = width - padX * 2;
            List<string> lines = WrapBody(BubbleBody(), inner, 3);
            int titleH = LineHeight(_titleFont);
            int bodyH = lines.Count * LineHeight(_bodyFont);
            int footerH = BubbleFooter().Length > 0 ? LineHeight(_smallFont) : 0;
            int h = padY * 2 + titleH;
            if (lines.Count > 0) h += S(3) + bodyH;
            if (footerH > 0) h += S(3) + footerH;
            return h;
        }

        string BubbleBody()
        {
            if (FocusHintVisible) return Localized("请从任务栏或托盘返回 DSH。", "Open DSH from the taskbar or system tray.");
            if (_state.AlertId.Length > 0) return _state.AlertText.Length > 0 ? _state.AlertText : _state.Line1;
            return _state.Line1;
        }

        string BubbleFooter()
        {
            return FocusHintVisible ? "" : _state.Line2;
        }

        int LineHeight(Font font)
        {
            return (int)Math.Ceiling(font.GetHeight(_measure) + 1);
        }

        // 中文按字断行、英文/标识符按词断行：早期版本逐字硬切，
        // mcp__windows__Screenshot 这种长标识符会被切成两截，读起来很别扭。
        static bool IsCjk(char c)
        {
            return (c >= 0x2E80 && c <= 0x9FFF) || (c >= 0xF900 && c <= 0xFAFF) || (c >= 0xFF00 && c <= 0xFFEF);
        }

        List<string> Tokenize(string text)
        {
            List<string> tokens = new List<string>();
            int i = 0;
            while (i < text.Length)
            {
                char c = text[i];
                if (c == ' ' || c == '\t')
                {
                    tokens.Add(" ");
                    i++;
                    continue;
                }
                if (IsCjk(c))
                {
                    tokens.Add(c.ToString());
                    i++;
                    continue;
                }
                int start = i;
                while (i < text.Length && text[i] != ' ' && text[i] != '\t' && !IsCjk(text[i])) i++;
                tokens.Add(text.Substring(start, i - start));
            }
            return tokens;
        }

        List<string> WrapBody(string text, int maxWidth, int maxLines)
        {
            List<string> lines = new List<string>();
            if (string.IsNullOrEmpty(text)) return lines;
            string[] paragraphs = text.Replace("\r", "").Split('\n');
            for (int p = 0; p < paragraphs.Length; p++)
            {
                string paragraph = paragraphs[p].Trim();
                if (paragraph.Length == 0) continue;
                string line = "";
                List<string> tokens = Tokenize(paragraph);
                for (int t = 0; t < tokens.Count; t++)
                {
                    string token = tokens[t];
                    if (token == " " && line.Length == 0) continue;
                    string candidate = line + token;
                    if (line.Length > 0 && _measure.MeasureString(candidate, _bodyFont).Width > maxWidth)
                    {
                        lines.Add(line.TrimEnd());
                        line = "";
                        if (lines.Count >= maxLines)
                        {
                            string tail = "";
                            for (int k = t; k < tokens.Count; k++) tail += tokens[k];
                            lines[lines.Count - 1] = Ellipsize(lines[lines.Count - 1] + tail, maxWidth);
                            return lines;
                        }
                        // 折行后从当前词重新开始，不能沿用已经放不下的 candidate
                        if (token == " ") continue;
                        candidate = token;
                    }
                    // 单个词本身超宽（长路径/URL）：退化成逐字切
                    if (line.Length == 0 && _measure.MeasureString(token, _bodyFont).Width > maxWidth)
                    {
                        string rest = token;
                        while (rest.Length > 0)
                        {
                            int take = 0;
                            while (take < rest.Length && _measure.MeasureString(rest.Substring(0, take + 1), _bodyFont).Width <= maxWidth) take++;
                            if (take <= 0) take = 1;
                            if (take >= rest.Length)
                            {
                                line = rest;
                                break;
                            }
                            lines.Add(rest.Substring(0, take));
                            rest = rest.Substring(take);
                            if (lines.Count >= maxLines)
                            {
                                lines[lines.Count - 1] = Ellipsize(lines[lines.Count - 1] + rest, maxWidth);
                                return lines;
                            }
                        }
                        continue;
                    }
                    line = candidate;
                }
                if (line.Length > 0)
                {
                    lines.Add(line.TrimEnd());
                    if (lines.Count >= maxLines) return lines;
                }
            }
            if (lines.Count > maxLines)
            {
                lines.RemoveRange(maxLines, lines.Count - maxLines);
            }
            return lines;
        }

        string Ellipsize(string text, int maxWidth)
        {
            if (text.Length <= 1) return text;
            string result = text;
            while (result.Length > 1 && _measure.MeasureString(result + "…", _bodyFont).Width > maxWidth)
            {
                result = result.Substring(0, result.Length - 1);
            }
            return result + "…";
        }

        void ResizeCompose(int width, int height)
        {
            if (_composeG != null) { _composeG.Dispose(); _composeG = null; }
            if (_compose != null) { _compose.Dispose(); _compose = null; }
            _compose = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            _composeG = Graphics.FromImage(_compose);
            _composeG.SmoothingMode = SmoothingMode.AntiAlias;
            // 分层窗口是逐像素 alpha 合成的，ClearType 的次像素彩边会直接烙进透明位图（蓝/红描边），
            // 必须用灰度抗锯齿，文字才在任意壁纸上干净
            _composeG.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            _composeG.InterpolationMode = InterpolationMode.HighQualityBicubic;
            EnsureDib(width, height);
        }

        void EnsureDib(int width, int height)
        {
            if (_dib != IntPtr.Zero && _dibW == width && _dibH == height) return;
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
            info.bmiHeader.biWidth = width;
            info.bmiHeader.biHeight = -height; // 负数 = 自上而下
            info.bmiHeader.biPlanes = 1;
            info.bmiHeader.biBitCount = 32;
            info.bmiHeader.biCompression = 0; // BI_RGB
            IntPtr bits;
            _dib = Native.CreateDIBSection(_memDc, ref info, 0, out bits, IntPtr.Zero, 0);
            _dibBits = bits;
            _dibOld = Native.SelectObject(_memDc, _dib);
            _dibW = width;
            _dibH = height;
            int bytes = width * height * 4;
            if (_scratch.Length != bytes) _scratch = new byte[bytes];
            if (_alpha.Length != width * height) _alpha = new byte[width * height];
        }

        void Render()
        {
            if (_composeInProgress) return;
            // 首次 Relayout 可能早于 ResizeCompose，这时还没有合成位图
            if (_compose == null || _composeG == null) return;
            _composeInProgress = true;
            try
            {
                int w = _compose.Width;
                int h = _compose.Height;
                _composeG.Clear(Color.Transparent);
                DrawActions(_composeG);
                DrawBubble(_composeG);
                DrawPet(_composeG);
                PushLayered();
            }
            catch (Exception error)
            {
                Log.W("render failed: " + error.Message);
            }
            finally
            {
                _composeInProgress = false;
            }
        }

        bool _composeInProgress;

        /// <summary>
        /// 动作条：宠物下方独立一行，**不画任何容器**——没有胶囊底、没有描边，
        /// 所以宠物周围不会出现"最外面那个框"。平时整条不可见，悬停才淡入。
        /// </summary>
        void DrawActions(Graphics g)
        {
            if (!HoverVisible || _actionsRect.IsEmpty) return;
            DrawButtonPlate(g, _btnCompose, _hoverButton == 1);
            DrawButtonPlate(g, _btnVoice, _hoverButton == 2);
            Color ink1 = _hoverButton == 1 ? Color.FromArgb(28, 38, 68) : Color.FromArgb(58, 70, 104);
            Color ink2 = _hoverButton == 2 ? Color.FromArgb(28, 38, 68) : Color.FromArgb(58, 70, 104);
            DrawIconGlyph(g, _btnCompose, "\uE70F", ink1);
            DrawIconGlyph(g, _btnVoice, "\uE720", ink2);
        }

        /// <summary>
        /// 按钮底板：只是一枚很淡的圆形玻璃，不构成"外层框"。平时更淡，指针压上去才实一点。
        /// </summary>
        void DrawButtonPlate(Graphics g, Rectangle r, bool hot)
        {
            int d = Math.Min(r.Width, r.Height) - S(2);
            Rectangle circle = new Rectangle(r.X + (r.Width - d) / 2, r.Y + (r.Height - d) / 2, d, d);
            using (GraphicsPath path = new GraphicsPath())
            {
                path.AddEllipse(circle);
                using (SolidBrush shadow = new SolidBrush(Color.FromArgb(hot ? 22 : 12, 24, 28, 44)))
                {
                    g.TranslateTransform(0f, S(1));
                    g.FillPath(shadow, path);
                    g.TranslateTransform(0f, -S(1));
                }
                using (SolidBrush plate = new SolidBrush(Color.FromArgb(hot ? 255 : 236, 255, 255, 255)))
                {
                    g.FillPath(plate, path);
                }
            }
        }

        RectangleF IconBox(Rectangle r, int size)
        {
            float d = S(size);
            return new RectangleF(r.X + (r.Width - d) / 2f, r.Y + (r.Height - d) / 2f, d, d);
        }

        /// <summary>
        /// 图标字形：Segoe MDL2 Assets 是 Windows 自带的图标字体（✎ = E70F，麦克风 = E720），
        /// 小尺寸下比自绘路径锐利，而且不会像 emoji 那样上色。
        /// </summary>
        void DrawIconGlyph(Graphics g, Rectangle r, string glyph, Color ink)
        {
            StringFormat fmt = new StringFormat();
            fmt.Alignment = StringAlignment.Center;
            fmt.LineAlignment = StringAlignment.Center;
            fmt.FormatFlags = StringFormatFlags.NoWrap;
            using (SolidBrush brush = new SolidBrush(ink))
            {
                g.DrawString(glyph, _iconFont, brush, r, fmt);
            }
            fmt.Dispose();
        }

        void DrawBubble(Graphics g)
        {
            if (!BubbleVisible() || _bubbleRect.IsEmpty) return;
            bool focusHint = FocusHintVisible;
            bool alert = !focusHint && _state.AlertId.Length > 0;
            bool success = _state.IsSuccess;
            Rectangle r = _bubbleRect;

            Color bg = alert
                ? (success ? Color.FromArgb(250, 251, 243, 226) : Color.FromArgb(250, 253, 236, 233))
                : Color.FromArgb(244, 252, 252, 254);

            // 外观：不要"框"。0.3 的三层圆角投影在浅色聊天区上就是一圈灰边，用户看到的就是它；
            // 提醒态的 1px 彩色描边同样是一圈框。现在只留一层扁平填充，成功/失败靠填充色
            // 和标题里的 ✔ / ✕ 区分，边缘不再有任何描边或投影。
            using (GraphicsPath path = Rounded(r, S(12)))
            using (SolidBrush back = new SolidBrush(bg))
            {
                g.FillPath(back, path);
            }

            int padX = S(12);
            int padY = S(9);
            int y = r.Y + padY;

            string title = alert ? (success ? Localized("任务已经结束", "Task finished") : Localized("任务执行失败", "Task failed")) : _state.Title;
            if (!alert && _state.Toast.Length > 0) title = _state.Toast;
            if (focusHint) title = Localized("未能切到 DSH", "Could not switch to DSH");
            if (title.Length == 0) title = Localized("智能伙伴", "Companion");
            if (alert) title = (success ? "✔ " : "✕ ") + title;
            title = ClipText(title, _titleFont, r.Width - padX * 2);
            using (SolidBrush ink = new SolidBrush(alert ? (success ? Color.FromArgb(150, 96, 20) : Color.FromArgb(160, 52, 40)) : Color.FromArgb(34, 38, 48)))
            {
                g.DrawString(title, _titleFont, ink, new PointF(r.X + padX, y));
            }
            y += LineHeight(_titleFont);

            List<string> lines = WrapBody(BubbleBody(), r.Width - padX * 2, 3);
            if (lines.Count > 0)
            {
                y += S(3);
                using (SolidBrush ink = new SolidBrush(Color.FromArgb(72, 78, 94)))
                {
                    for (int i = 0; i < lines.Count; i++)
                    {
                        g.DrawString(lines[i], _bodyFont, ink, new PointF(r.X + padX, y));
                        y += LineHeight(_bodyFont);
                    }
                }
            }

            string footer = BubbleFooter();
            if (footer.Length > 0)
            {
                y += S(2);
                StringFormat fmt = new StringFormat();
                fmt.Alignment = StringAlignment.Far;
                fmt.FormatFlags = StringFormatFlags.NoWrap;
                using (SolidBrush ink = new SolidBrush(Color.FromArgb(146, 152, 168)))
                {
                    g.DrawString(ClipText(footer, _smallFont, r.Width - padX * 2), _smallFont, ink,
                        new RectangleF(r.X + padX, y, r.Width - padX * 2, LineHeight(_smallFont)), fmt);
                }
                fmt.Dispose();
            }
        }

        string ClipText(string text, Font font, int maxWidth)
        {
            if (string.IsNullOrEmpty(text)) return "";
            if (_measure.MeasureString(text, font).Width <= maxWidth) return text;
            string result = text;
            while (result.Length > 1 && _measure.MeasureString(result + "…", font).Width > maxWidth)
            {
                result = result.Substring(0, result.Length - 1);
            }
            return result + "…";
        }

        void DrawPet(Graphics g)
        {
            if (_petRect.IsEmpty) return;
            Rectangle dest = _petRect;

            if (_sheet == null)
            {
                DrawPlaceholder(g, dest);
                return;
            }
            int cols = Math.Max(1, _state.Cols);
            int rows = Math.Max(1, _state.Rows);
            int row = Math.Min(_row, rows - 1);
            int frames = Math.Max(1, RowFrames(row));
            int frame = _frame % frames;
            int col = Math.Min(frame, cols - 1);
            Rectangle src = new Rectangle(col * _state.FrameW, row * _state.FrameH, _state.FrameW, _state.FrameH);
            try
            {
                g.DrawImage(_sheet, dest, src, GraphicsUnit.Pixel);
            }
            catch (Exception error)
            {
                Log.W("draw sprite failed: " + error.Message);
            }
        }

        void DrawPlaceholder(Graphics g, Rectangle dest)
        {
            using (GraphicsPath path = Rounded(dest, S(18)))
            using (LinearGradientBrush brush = new LinearGradientBrush(dest, Color.FromArgb(214, 228, 252), Color.FromArgb(150, 186, 244), 60f))
            {
                g.FillPath(brush, path);
            }
            int eye = Math.Max(3, dest.Width / 9);
            using (SolidBrush ink = new SolidBrush(Color.FromArgb(40, 62, 104)))
            {
                g.FillEllipse(ink, dest.X + dest.Width / 2 - eye * 3, dest.Y + dest.Height / 2 - eye, eye, eye);
                g.FillEllipse(ink, dest.X + dest.Width / 2 + eye * 2, dest.Y + dest.Height / 2 - eye, eye, eye);
            }
        }

        static GraphicsPath Rounded(Rectangle r, int radius)
        {
            int d = Math.Max(2, radius * 2);
            GraphicsPath path = new GraphicsPath();
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        /// <summary>把合成位图（直通 alpha）预乘后写进 DIB，再整块推送。</summary>
        void PushLayered()
        {
            if (_compose == null || _dib == IntPtr.Zero) return;
            int w = _compose.Width;
            int h = _compose.Height;
            BitmapData data = _compose.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                int stride = data.Stride;
                int rowBytes = w * 4;
                for (int y = 0; y < h; y++)
                {
                    Marshal.Copy(new IntPtr(data.Scan0.ToInt64() + (long)y * stride), _scratch, y * rowBytes, rowBytes);
                }
            }
            finally
            {
                _compose.UnlockBits(data);
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

            Native.POINT dst = new Native.POINT(Left, Top);
            Native.SIZE size = new Native.SIZE(w, h);
            Native.POINT src = new Native.POINT(0, 0);
            Native.BLENDFUNCTION blend = new Native.BLENDFUNCTION();
            blend.BlendOp = 0;
            blend.BlendFlags = 0;
            blend.SourceConstantAlpha = 255;
            blend.AlphaFormat = 1;
            if (!Native.UpdateLayeredWindow(Handle, _screenDc, ref dst, ref size, _memDc, ref src, 0, ref blend, Native.ULW_ALPHA))
            {
                int err = Marshal.GetLastWin32Error();
                if (err != 0) Log.W("UpdateLayeredWindow failed err=" + err);
            }

            if (_opts.Dump && ++_dumpCounter % 20 == 0)
            {
                try { _compose.Save(_opts.LogPath + ".dump.png", ImageFormat.Png); } catch { }
            }
        }

        int _dumpCounter;

        bool HitTest(Point p)
        {
            if (p.X < 0 || p.Y < 0 || p.X >= _dibW || p.Y >= _dibH) return false;
            return _alpha[p.Y * _dibW + p.X] > 24;
        }

        // ================= 动画 =================

        static int RowFrames(int row)
        {
            int[] table = new int[] { 6, 8, 8, 4, 5, 8, 6, 6, 6 };
            return table[Math.Min(Math.Max(row, 0), table.Length - 1)];
        }

        static int RowFps(int row)
        {
            int[] table = new int[] { 6, 14, 14, 6, 8, 8, 5, 8, 5 };
            return table[Math.Min(Math.Max(row, 0), table.Length - 1)];
        }

        static int RowForState(string status)
        {
            if (status == "running") return 7;
            if (status == "waiting") return 6;
            if (status == "failed") return 5;
            if (status == "done") return 8;
            return 0;
        }

        void SetRow(int row)
        {
            if (row == _row) return;
            _row = row;
            _frame = 0;
            _frameAt = DateTime.UtcNow;
        }

        void OnAnimTick()
        {
            if (_closing) return;
            DateTime now = DateTime.UtcNow;
            int row;
            if (_dragging)
            {
                row = _dragMoved && Cursor.Position.X < _dragStartScreen.X ? 2 : 1;
            }
            else if (now < _celebrateUntil)
            {
                row = 4;
            }
            else if (_state.AlertId.Length > 0)
            {
                row = _state.IsSuccess ? 3 : RowForState(_state.Status);
            }
            else
            {
                row = RowForState(_state.Status);
            }
            SetRow(row);

            double wait = 1000.0 / Math.Max(2, RowFps(_row));
            if ((now - _frameAt).TotalMilliseconds >= wait)
            {
                _frameAt = now;
                _frame = (_frame + 1) % Math.Max(1, RowFrames(_row));
                Render();
            }

            // 气泡到期后要收起来：定时重排一次，避免"到点了还挂着"
            if (_lastLayoutHash != LayoutHash()) Relayout(false);
        }

        int _lastLayoutHash = -1;

        int LayoutHash()
        {
            int hash = 17;
            hash = hash * 31 + (BubbleVisible() ? 1 : 0);
            hash = hash * 31 + (FocusHintVisible ? 1 : 0);
            hash = hash * 31 + (HoverVisible ? 1 : 0);
            hash = hash * 31 + PetW;
            hash = hash * 31 + PetH;
            hash = hash * 31 + (_state.Title ?? "").GetHashCode();
            hash = hash * 31 + (_state.Line1 ?? "").GetHashCode();
            hash = hash * 31 + (_state.Line2 ?? "").GetHashCode();
            hash = hash * 31 + (_state.AlertId ?? "").GetHashCode();
            hash = hash * 31 + (_state.Toast ?? "").GetHashCode();
            return hash;
        }

        void ReleaseStaleCapture()
        {
            if (_dragging || !IsHandleCreated) return;
            if (Native.GetCapture() != Handle) return;
            Native.ReleaseCapture();
            Log.W("released stale capture");
        }

        void FinishPointerInteraction(bool buttonReleased)
        {
            if (!_dragging) return;
            bool moved = _dragMoved;
            _dragging = false;
            _dragMoved = false;
            if (Native.GetCapture() == Handle) Native.ReleaseCapture();

            if (moved)
            {
                PinAnchorFromWindow();
                Log.W("drag to " + Location.X + "," + Location.Y);
                PostPos();
            }
            else if (buttonReleased)
            {
                OnPetClicked();
            }
            Relayout(false);
        }

        void OnHoverTick()
        {
            if (_closing || !IsHandleCreated) return;
            if (_dragging && !Native.IsPrimaryMouseButtonDown()) FinishPointerInteraction(false);
            ReleaseStaleCapture();
            Point cursor = Cursor.Position;
            bool inside = Bounds.Contains(cursor);
            int button = 0;
            if (HoverVisible)
            {
                Point local = PointToClient(cursor);
                if (_btnCompose.Contains(local)) button = 1;
                else if (_btnVoice.Contains(local)) button = 2;
            }
            if (button != _hoverButton)
            {
                _hoverButton = button;
                Render();
            }
            if (inside)
            {
                _hoverLostAt = DateTime.UtcNow;
                if (!_hover)
                {
                    _hover = true;
                    Relayout(false);
                }
            }
            else if (_hover && (DateTime.UtcNow - _hoverLostAt).TotalMilliseconds > 300)
            {
                _hover = false;
                _hoverButton = 0;
                Relayout(false);
            }
        }

        // ================= 窗口消息 =================

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Native.WM_NCHITTEST)
            {
                // 菜单打开时宠物让出输入。写作窗打开时只有未被覆盖的操作按钮响应。
                if (_menu != null && !_menu.IsDisposed && _menu.Visible)
                {
                    m.Result = (IntPtr)Native.HTTRANSPARENT;
                    return;
                }
                Point screen = new Point((short)(m.LParam.ToInt32() & 0xFFFF), (short)((m.LParam.ToInt32() >> 16) & 0xFFFF));
                Point local = PointToClient(screen);
                bool acceptsClick = IsComposing
                    ? !_composeWindow.Bounds.Contains(screen) && (_btnCompose.Contains(local) || _btnVoice.Contains(local))
                    : HitTest(local);
                m.Result = (IntPtr)(acceptsClick ? Native.HTCLIENT : Native.HTTRANSPARENT);
                return;
            }
            if (m.Msg == Native.WM_MOUSEACTIVATE)
            {
                m.Result = (IntPtr)Native.MA_NOACTIVATE;
                return;
            }
            if (m.Msg == Native.WM_LBUTTONDOWN)
            {
                Point p = PointOf(m.LParam);
                _lastMouse = p;
                if (HoverVisible && (_btnCompose.Contains(p) || _btnVoice.Contains(p)))
                {
                    OpenCompose(_btnVoice.Contains(p));
                    return;
                }
                _dragging = true;
                _dragMoved = false;
                _dragStartScreen = Cursor.Position;
                _dragStartWindow = Location;
                Log.W("mouse down at " + _dragStartScreen.X + "," + _dragStartScreen.Y);
                return;
            }
            if (m.Msg == Native.WM_MOUSEMOVE)
            {
                if (_dragging)
                {
                    Point now = Cursor.Position;
                    int dx = now.X - _dragStartScreen.X;
                    int dy = now.Y - _dragStartScreen.Y;
                    if (!_dragMoved && Math.Abs(dx) + Math.Abs(dy) > 4)
                    {
                        _dragMoved = true;
                        // 只有真的拖动时才抢鼠标；单击不抢，避免偶发丢 up 后捕获悬挂
                        Native.SetCapture(Handle);
                    }
                    if (_dragMoved)
                    {
                        Location = ClampWindow(new Point(_dragStartWindow.X + dx, _dragStartWindow.Y + dy), Width, Height);
                        // 残影：窗口先移动，分层表面还停在上一帧，下一次推送要等 80ms 的动画定时器，
                        // 快速拖动时中间这些帧就是用户看到的拖尾。这里立刻补推一帧
                        // （内容没变，PushLayered 只做一次全量 DIB 拷贝），并按 8ms 节流。
                        long nowTick = Environment.TickCount;
                        if (nowTick - _dragPushAt >= 8)
                        {
                            _dragPushAt = nowTick;
                            PushLayered();
                        }
                    }
                }
                return;
            }
            if (m.Msg == Native.WM_LBUTTONUP)
            {
                FinishPointerInteraction(true);
                return;
            }
            if (m.Msg == Native.WM_CAPTURECHANGED || m.Msg == Native.WM_CANCELMODE)
            {
                FinishPointerInteraction(false);
                base.WndProc(ref m);
                return;
            }
            // 双击的第一次松手已按单击处理。
            if (m.Msg == Native.WM_LBUTTONDBLCLK) return;
            if (m.Msg == Native.WM_RBUTTONUP)
            {
                ShowMenu();
                return;
            }
            if (m.Msg == Native.WM_DPICHANGED)
            {
                int dpi = (int)(m.WParam.ToInt64() & 0xFFFF);
                if (dpi > 0) _dpi = dpi / 96f;
                Relayout(true);
                return;
            }
            if (m.Msg == Native.WM_DISPLAYCHANGE)
            {
                Relayout(true);
                return;
            }
            base.WndProc(ref m);
        }

        static Point PointOf(IntPtr lParam)
        {
            int value = lParam.ToInt32();
            return new Point((short)(value & 0xFFFF), (short)((value >> 16) & 0xFFFF));
        }

        void PinAnchorFromWindow()
        {
            _anchorX = Location.X + Width / 2;
            _anchorY = Location.Y + Height - S(6);
        }

        void OnPetClicked()
        {
            // 单击 = Codex 那种"点宠物就回到 DSH"。有结束提醒时顺手确认掉。
            if (_state.AlertId.Length > 0) PostAck();
            FocusDsh("click");
        }

        // ================= 菜单 / 输入窗 =================

        void ShowMenu()
        {
            CloseMenu();
            FinishPointerInteraction(false);
            ReleaseStaleCapture();
            List<OverlayMenu.Entry> entries = BuildMenuEntries();
            _menu = new OverlayMenu(entries, OnMenuPick, _dpi);
            Rectangle working = Screen.FromRectangle(RectangleToScreen(_petRect)).WorkingArea;
            Point origin = new Point(Math.Max(working.Left, Right - _menu.PanelWidth),
                Math.Max(working.Top, Bottom - S(6) - _menu.PanelHeight));
            _menu.ShowAt(origin);
        }

        List<OverlayMenu.Entry> BuildMenuEntries()
        {
            List<OverlayMenu.Entry> list = new List<OverlayMenu.Entry>();
            list.Add(OverlayMenu.Entry.Item("open", Localized("打开 DSH", "Open DSH"), true));
            list.Add(OverlayMenu.Entry.Separator());

            OverlayMenu.Entry pets = OverlayMenu.Entry.Submenu("pets", Localized("切换宠物", "Pets"));
            for (int i = 0; i < _state.PetIds.Count; i++)
            {
                string id = _state.PetIds[i];
                string name = i < _state.PetNames.Count ? _state.PetNames[i] : id;
                pets.Children.Add(OverlayMenu.Entry.Check("pet:" + id, name, id == _state.PetId, true));
            }
            if (pets.Children.Count == 0) pets.Children.Add(OverlayMenu.Entry.Item("pets:none", Localized("还没有宠物", "No pets yet"), false));
            pets.Children.Add(OverlayMenu.Entry.Separator());
            pets.Children.Add(OverlayMenu.Entry.Item("install", Localized("安装 petdex 宠物…", "Install a petdex pet…"), true));
            pets.Children.Add(OverlayMenu.Entry.Item("opendir", Localized("打开宠物目录", "Open pets folder"), true));
            list.Add(pets);

            OverlayMenu.Entry bubble = OverlayMenu.Entry.Submenu("bubble", Localized("气泡显示", "Bubble"));
            bubble.Children.Add(OverlayMenu.Entry.Check("bubbleMs:3000", Localized("3 秒", "3 s"), _state.BubbleMs == 3000, _state.Bubble));
            bubble.Children.Add(OverlayMenu.Entry.Check("bubbleMs:5000", Localized("5 秒", "5 s"), _state.BubbleMs == 5000, _state.Bubble));
            bubble.Children.Add(OverlayMenu.Entry.Check("bubbleMs:8000", Localized("8 秒", "8 s"), _state.BubbleMs == 8000, _state.Bubble));
            bubble.Children.Add(OverlayMenu.Entry.Check("bubbleMs:0", Localized("一直显示", "Always"), _state.BubbleMs == 0, _state.Bubble));
            bubble.Children.Add(OverlayMenu.Entry.Separator());
            bubble.Children.Add(OverlayMenu.Entry.Check("bubble:off", Localized("关闭气泡", "Hide bubble"), !_state.Bubble, true));
            list.Add(bubble);

            OverlayMenu.Entry size = OverlayMenu.Entry.Submenu("size", Localized("大小", "Size"));
            int[] percents = new int[] { 40, 55, 70, 85, 100 };
            for (int i = 0; i < percents.Length; i++)
            {
                int pct = percents[i];
                bool on = Math.Abs(_state.Scale * 100 - pct) < 2.5;
                size.Children.Add(OverlayMenu.Entry.Check("scale:" + pct, pct + "%", on, true));
            }
            list.Add(size);

            list.Add(OverlayMenu.Entry.Check("sound", Localized("结束提示音", "Finish sound"), _state.Sound, true));
            list.Add(OverlayMenu.Entry.Item("copy", Localized("复制最近一条", "Copy latest line"), (_state.Line1 ?? "").Length > 0));
            list.Add(OverlayMenu.Entry.Separator());

            OverlayMenu.Entry rehearse = OverlayMenu.Entry.Submenu("test", Localized("测试提醒", "Rehearse alert"));
            rehearse.Children.Add(OverlayMenu.Entry.Item("test:completed", Localized("成功演练（绿色 ✔）", "Success (✔)"), true));
            rehearse.Children.Add(OverlayMenu.Entry.Item("test:failed", Localized("失败演练（红色 ✕）", "Failure (✕)"), true));
            list.Add(rehearse);

            list.Add(OverlayMenu.Entry.Item("quit", Localized("退出宠物窗", "Quit overlay"), true));
            return list;
        }

        void OnMenuPick(string id)
        {
            try
            {
                if (id == "open")
                {
                    // 先把自己的菜单关掉再激活：菜单窗口此刻占着前台，直接抢会一直被拒。
                    CloseMenu();
                    BeginInvoke(new Action(delegate { FocusDsh("menu"); }));
                }
                else if (id == "copy") CopyLastLine();
                else if (id == "quit") Close();
                else if (id == "install") InstallPet();
                else if (id == "opendir") OpenPetsFolder();
                else if (id == "sound") PostConfig("sound", _state.Sound ? "0" : "1");
                else if (id.StartsWith("bubbleMs:")) PostConfig("bubbleMs", id.Substring("bubbleMs:".Length));
                else if (id == "bubble:off") PostConfig("bubble", "0");
                else if (id.StartsWith("scale:")) PostConfig("scale", id.Substring("scale:".Length));
                else if (id.StartsWith("pet:")) PostConfig("pet", id.Substring("pet:".Length));
                else if (id.StartsWith("test:")) PostTest(id.Substring("test:".Length));
            }
            catch (Exception error)
            {
                Log.W("menu action failed: " + error.Message);
            }
        }

        void CloseMenu()
        {
            if (_menu != null && !_menu.IsDisposed)
            {
                _menu.Close();
                _menu = null;
            }
        }

        /// <summary>把当前气泡正文放进剪贴板：分层窗口里的文字选不中，这是唯一的复制口子。</summary>
        void CopyLastLine()
        {
            try
            {
                string text = _state.Line1 ?? "";
                if (text.Length == 0) text = _state.Title ?? "";
                if (text.Length == 0)
                {
                    Log.W("copy skipped: nothing to copy");
                    return;
                }
                Clipboard.SetText(text);
                Log.W("copied bubble text " + text.Length + " chars");
            }
            catch (Exception error)
            {
                Log.W("copy failed: " + error.Message);
            }
        }

        void OpenPetsFolder()
        {
            try
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dsh", "pets");
                Directory.CreateDirectory(dir);
                Process.Start("explorer.exe", "\"" + dir + "\"");
            }
            catch (Exception error)
            {
                Log.W("open pets folder failed: " + error.Message);
            }
        }

        void InstallPet()
        {
            InputDialog dialog = new InputDialog(
                Localized("安装 petdex 宠物", "Install petdex pet"),
                Localized("输入 petdex 名称、petdex.dev 链接，或宠物 zip / 文件夹路径：", "petdex name, petdex.dev link, or a local zip / folder:"));
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            string source = dialog.Value.Trim();
            if (source.Length == 0) return;
            System.Windows.Forms.Cursor previous = this.Cursor;
            this.Cursor = Cursors.WaitCursor;
            try
            {
                Dictionary<string, string> form = new Dictionary<string, string>();
                form["source"] = source;
                string text = Http.PostForm(_opts.Host + _opts.Prefix + "/install", _opts.Token, form, 900000);
                Log.W("install result: " + text.Replace("\n", " "));
                MessageBox.Show(this, text, Localized("安装结果", "Install result"),
                    MessageBoxButtons.OK, text.StartsWith("已安装") ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
            }
            catch (Exception error)
            {
                Log.W("install failed: " + error.Message);
                MessageBox.Show(this, error.Message, Localized("安装失败", "Install failed"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            finally
            {
                this.Cursor = previous;
            }
        }

        void OpenCompose(bool voice)
        {
            ReleaseStaleCapture();
            if (IsComposing)
            {
                _composeWindow.Activate();
                if (voice) _composeWindow.StartVoice();
                return;
            }

            ComposeWindow window = new ComposeWindow(
                new Rectangle(0, 0, S(350), S(224)),
                Localized, LoadComposeTargets, SubmitText, voice);
            _composeWindow = window;
            window.SizeChanged += delegate { PlaceCompose(window); };
            window.FormClosed += delegate
            {
                if (_composeWindow != window) return;
                _composeWindow = null;
                if (!_closing) Relayout(false);
            };
            PlaceCompose(window);
            window.Show(this);
            Relayout(false);
            PlaceCompose(window);
        }

        void PlaceCompose(ComposeWindow window)
        {
            if (window.IsDisposed) return;
            Rectangle pet = _petRect;
            pet.Offset(Location);
            Rectangle area = Screen.FromRectangle(pet).WorkingArea;
            int gap = S(8);
            int above = Math.Min(pet.Top, Top) - window.Height - gap;
            int below = Math.Max(pet.Bottom, Bottom) + gap;
            int x = pet.Left + (pet.Width - window.Width) / 2;
            int y = above >= area.Top ? above : below;
            if (y + window.Height > area.Bottom)
            {
                int right = pet.Right + gap;
                int left = pet.Left - gap - window.Width;
                if (right + window.Width <= area.Right) x = right;
                else if (left >= area.Left) x = left;
                y = pet.Top - (window.Height - pet.Height) / 2;
            }

            x = Math.Max(area.Left, Math.Min(x, area.Right - window.Width));
            y = Math.Max(area.Top, Math.Min(y, area.Bottom - window.Height));
            window.Location = new Point(x, y);
        }

        static Dictionary<string, string> DecodeComposeResponse(string response)
        {
            Dictionary<string, string> fields = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string line in response.Split('\n'))
            {
                int equals = line.IndexOf('=');
                if (equals <= 0) continue;
                fields[line.Substring(0, equals)] = Uri.UnescapeDataString(line.Substring(equals + 1).TrimEnd('\r'));
            }
            return fields;
        }

        Task<ComposeTarget[]> LoadComposeTargets()
        {
            return Task.Run(delegate
            {
                string response = Http.Get(_opts.Host + _opts.Prefix + "/compose-targets", _opts.Token, 8000);
                Dictionary<string, string> fields = DecodeComposeResponse(response);
                List<ComposeTarget> targets = new List<ComposeTarget>();
                string preferred;
                fields.TryGetValue("preferredId", out preferred);
                for (int index = 0; ; index++)
                {
                    string prefix = "target." + index + ".";
                    string id;
                    if (!fields.TryGetValue(prefix + "id", out id)) break;
                    string title;
                    fields.TryGetValue(prefix + "title", out title);
                    targets.Add(new ComposeTarget(id, title, id == preferred));
                }
                return targets.ToArray();
            });
        }

        Task<ComposeSubmitResult> SubmitText(string targetId, string text, string requestId)
        {
            string unknown = Localized(
                "发送结果待确认。可用同一草稿重试，或去 DSH 检查。",
                "Delivery is unconfirmed. Retry the same draft or check DSH.");
            string unavailable = Localized("连接不上 DSH，草稿已保留。", "Can't connect to DSH. Your draft is still here.");
            return Task.Run(delegate
            {
                try
                {
                    Dictionary<string, string> form = new Dictionary<string, string>
                    {
                        { "targetId", targetId },
                        { "text", text },
                        { "requestId", requestId }
                    };
                    string response = Http.PostForm(_opts.Host + _opts.Prefix + "/say", _opts.Token, form, 20000);
                    Dictionary<string, string> fields = DecodeComposeResponse(response);
                    string status, message;
                    fields.TryGetValue("status", out status);
                    fields.TryGetValue("message", out message);
                    if (status == "sent") return new ComposeSubmitResult(true, message);
                    if (status == "failed") return new ComposeSubmitResult(false, message ?? unavailable);
                    return new ComposeSubmitResult(false, message ?? unknown, true);
                }
                catch (WebException error)
                {
                    HttpWebResponse response = error.Response as HttpWebResponse;
                    if (response != null)
                    {
                        HttpStatusCode code = response.StatusCode;
                        response.Dispose();
                        if ((int)code >= 400 && (int)code < 500) return new ComposeSubmitResult(false, unavailable);
                    }
                    if (error.Status == WebExceptionStatus.ConnectFailure)
                        return new ComposeSubmitResult(false, unavailable);
                    return new ComposeSubmitResult(false, unknown, true);
                }
            });
        }

        // ================= 与宿主通信 =================

        void PostPos()
        {
            Dictionary<string, string> form = new Dictionary<string, string>();
            form["pos"] = "1";
            form["posx"] = _anchorX.ToString();
            form["posy"] = _anchorY.ToString();
            _serverPosX = _anchorX;
            _serverPosY = _anchorY;
            Post("/config", form);
        }

        void PostConfig(string key, string value)
        {
            Dictionary<string, string> form = new Dictionary<string, string>();
            form[key] = value;
            Post("/config", form);
        }

        void PostAck()
        {
            Dictionary<string, string> form = new Dictionary<string, string>();
            form["alertId"] = _state.AlertId;
            Post("/ack", form);
            _alertAt = DateTime.UtcNow.AddMilliseconds(-(_state.AlertMs > 0 ? _state.AlertMs : 30000));
            Relayout(false);
        }

        void PostTest(string kind)
        {
            Dictionary<string, string> form = new Dictionary<string, string>();
            form["kind"] = kind;
            Post("/test", form);
        }

        void Post(string endpoint, Dictionary<string, string> form)
        {
            string url = _opts.Host + _opts.Prefix + endpoint;
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    Http.PostForm(url, _opts.Token, form);
                }
                catch (Exception error)
                {
                    Log.W("POST " + endpoint + " failed: " + error.Message);
                }
            });
        }

        void PostReady()
        {
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    Dictionary<string, string> form = new Dictionary<string, string>();
                    form["event"] = "ready";
                    form["width"] = Width.ToString();
                    form["height"] = Height.ToString();
                    form["scale"] = _state.Scale.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    Http.PostForm(_opts.Host + _opts.Prefix + "/overlay", _opts.Token, form);
                }
                catch (Exception error)
                {
                    Log.W("post ready failed: " + error.Message);
                }
            });
        }

        /// <summary>浮层 UI 看门狗，记录消息处理停顿和恢复时间。</summary>
        void StartStallWatchdog()
        {
            Thread watchdog = new Thread(delegate()
            {
                while (!_closing)
                {
                    Thread.Sleep(2000);
                    if (_closing || !IsHandleCreated) continue;
                    Stopwatch probe = Stopwatch.StartNew();
                    int responded = 0;
                    try
                    {
                        BeginInvoke(new Action(delegate
                        {
                            Interlocked.Exchange(ref responded, 1);
                            if (probe.ElapsedMilliseconds >= 1500) Log.W("ui resumed after " + probe.ElapsedMilliseconds + "ms");
                        }));
                    }
                    catch (InvalidOperationException) { continue; }

                    bool reported = false;
                    while (!_closing && Interlocked.CompareExchange(ref responded, 0, 0) == 0)
                    {
                        if (!reported && probe.ElapsedMilliseconds >= 1500)
                        {
                            Log.W("ui stall: overlay window messages pending");
                            reported = true;
                        }
                        Thread.Sleep(250);
                    }
                }
            });
            watchdog.IsBackground = true;
            watchdog.Name = "dsh-pet-stall";
            watchdog.Start();
        }

        void StartPolling()
        {
            Thread thread = new Thread(PollLoop);
            thread.IsBackground = true;
            thread.Name = "dsh-pet-poll";
            thread.Start();
        }

        void PollLoop()
        {
            while (!_closing)
            {
                int sleep = 400;
                try
                {
                    // DSH 主进程没了就收摊：宠物跟随 DSH 生命周期，不留孤儿。
                    int dshPid = _dshPid > 0 ? _dshPid : _state.DshPid;
                    if (dshPid > 0 && !Native.IsProcessAlive(dshPid))
                    {
                        Log.W("DSH 已退出，浮层退出");
                        CloseFromThread();
                        return;
                    }

                    string text = Http.Get(_opts.Host + _opts.Prefix + "/state", _opts.Token, 5000);
                    PetState state = PetState.Parse(text);
                    _failures = 0;
                    Invoke(new Action(delegate { ApplyState(state); }));
                    bool busy = state.Status == "running" || state.Status == "waiting" || state.AlertId.Length > 0;
                    sleep = busy ? 400 : 1000;
                }
                catch (Exception error)
                {
                    _failures++;
                    Log.W("poll failed (" + _failures + "): " + error.Message);
                    if (_failures >= 4 && _connectedOnce)
                    {
                        Invoke(new Action(delegate { ShowDisconnected(); }));
                    }
                    if (_failures >= 25) // 10 秒连不上又没有 DSH 进程信息：别做僵尸
                    {
                        Log.W("host unreachable too long; exiting");
                        CloseFromThread();
                        return;
                    }
                    sleep = 800;
                }
                Thread.Sleep(sleep);
            }
        }

        void CloseFromThread()
        {
            try
            {
                Invoke(new Action(delegate
                {
                    _closing = true;
                    Close();
                }));
            }
            catch { }
        }

        static string StripElapsed(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            return System.Text.RegularExpressions.Regex.Replace(text, @"\d+(\.\d+)?\s*s", "").Trim();
        }

        void ShowDisconnected()
        {
            _state.Title = Localized("DSH 已断开，重连中…", "DSH disconnected, reconnecting…");
            _state.Line1 = _opts.Host;
            _state.Line2 = "";
            _state.AlertId = "";
            _contentAt = DateTime.UtcNow;
            _contentKey = "disconnected";
            Relayout(false);
        }

        void ApplyState(PetState state)
        {
            bool first = !_connectedOnce;
            _connectedOnce = true;
            if (state.DshPid > 0) _dshPid = state.DshPid;
            if (state.PosX != int.MinValue && (state.PosX != _serverPosX || state.PosY != _serverPosY))
            {
                _serverPosX = state.PosX;
                _serverPosY = state.PosY;
                // 拖动过程中别抢方向盘；松手后的回写会带着同一个值回来，不会互相打架
                if (!_dragging)
                {
                    _anchorX = state.PosX;
                    _anchorY = state.PosY;
                    Relayout(true);
                }
            }

            // 内容指纹不能带上每秒都在变的耗时，否则气泡永远不会自动收起
            string key = string.Join("|", new string[]
            {
                state.Title, state.Line1, StripElapsed(state.Line2), state.Status, state.AlertId, state.Toast, state.PetId, state.Scale.ToString()
            });
            if (key != _contentKey)
            {
                _contentKey = key;
                _contentAt = DateTime.UtcNow;
            }

            if (state.AlertId.Length > 0 && state.AlertId != _lastAlertId)
            {
                _lastAlertId = state.AlertId;
                _alertAt = DateTime.UtcNow;
                _celebrateUntil = DateTime.UtcNow.AddSeconds(2.2);
                if (state.Sound)
                {
                    try
                    {
                        System.Media.SystemSound sound = state.IsSuccess ? System.Media.SystemSounds.Asterisk : System.Media.SystemSounds.Hand;
                        sound.Play();
                    }
                    catch { }
                }
                Log.W("alert shown id=" + state.AlertId + " kind=" + state.AlertKind);
            }
            if (state.AlertId.Length == 0) _lastAlertId = "";

            string previousPath = _sheetPath;
            _state = state;
            if (state.PetPath != previousPath) LoadSheet(state.PetPath);
            Relayout(false);

            if (first)
            {
                PostReady();
                Log.W("connected, overlay ready");
            }
        }

        void LoadSheet(string path)
        {
            _sheetPath = path ?? "";
            if (_sheet != null)
            {
                _sheet.Dispose();
                _sheet = null;
            }
            if (string.IsNullOrEmpty(path)) return;
            try
            {
                if (!File.Exists(path))
                {
                    Log.W("sprite missing: " + path);
                    return;
                }
                using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    _sheet = Image.FromStream(stream, true, false);
                }
                Log.W("sprite loaded " + _sheet.Width + "x" + _sheet.Height + " " + path);
            }
            catch (Exception error)
            {
                Log.W("sprite load failed: " + error.Message);
            }
        }

        // ================= 打开 DSH =================

        void FocusDsh(string reason)
        {
            int now = Environment.TickCount;
            if (_focusPending || (_focusRequested && unchecked((uint)(now - _lastFocusAt)) < 500)) return;
            _focusRequested = true;
            _lastFocusAt = now;
            int pid = _dshPid > 0 ? _dshPid : _state.DshPid;
            if (pid <= 0)
            {
                Log.W("cannot open DSH (" + reason + "): no pid yet");
                ShowFocusHint();
                return;
            }

            _focusPending = true;
            Log.W("focus requested (" + reason + ") pid=" + pid);
            ThreadPool.QueueUserWorkItem(delegate
            {
                Stopwatch elapsed = Stopwatch.StartNew();
                bool activated = false;
                try
                {
                    IntPtr hwnd = Native.FindMainWindow(pid);
                    string result = "DSH window unavailable";
                    if (hwnd != IntPtr.Zero) activated = Native.RequestDshOpen(hwnd, pid, out result);
                    Log.W("focus result (" + reason + "): " + result + ", hwnd=" + hwnd.ToInt64()
                        + ", elapsed=" + elapsed.ElapsedMilliseconds + "ms");
                }
                catch (Exception error)
                {
                    Log.W("focus failed (" + reason + "): " + error.Message);
                }

                try
                {
                    BeginInvoke(new Action(delegate
                    {
                        _focusPending = false;
                        if (_closing) return;
                        if (!activated) ShowFocusHint();
                        else if (FocusHintVisible)
                        {
                            _focusHintUntil = DateTime.MinValue;
                            Relayout(false);
                        }
                    }));
                }
                catch (InvalidOperationException) { }
            });
        }

        void ShowFocusHint()
        {
            _focusHintUntil = DateTime.UtcNow.AddSeconds(4);
            Relayout(false);
        }
    }
}