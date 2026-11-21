// dsh-pet · 自绘右键菜单（圆角 + 半透明 + 二级菜单）
//
// 为什么不用 WinForms 的 ContextMenuStrip：默认外形是方角灰底，和"宠物"这件事不搭；
// 而且二级菜单要自己控制位置。这里用一个分层窗口画整个菜单，子菜单是同一窗口里
// 的第二列——避免"子窗口激活 → 父窗口 Deactivate → 菜单自己关掉"这类经典坑。
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Windows.Forms;
using Microsoft.Win32;

namespace DshPet
{
    sealed class OverlayMenu : Form
    {
        public sealed class Entry
        {
            public string Id = "";
            public string Text = "";
            public bool Enabled = true;
            public bool Checked;
            public bool IsSeparator;
            public List<Entry> Children;

            public bool IsSubmenu { get { return Children != null; } }

            public static Entry Item(string id, string text, bool enabled)
            {
                Entry e = new Entry();
                e.Id = id;
                e.Text = text;
                e.Enabled = enabled;
                return e;
            }

            public static Entry Separator()
            {
                Entry e = new Entry();
                e.IsSeparator = true;
                e.Enabled = false;
                return e;
            }

            public static Entry Submenu(string id, string text)
            {
                Entry e = new Entry();
                e.Id = id;
                e.Text = text;
                e.Children = new List<Entry>();
                return e;
            }

            public static Entry Check(string id, string text, bool isChecked, bool enabled)
            {
                Entry e = Item(id, text, enabled);
                e.Checked = isChecked;
                return e;
            }
        }

        // 面板四周留出来画阴影的透明边距
        const int ShadowMargin = 12;
        const int PanelPadY = 7;
        const int ItemHeight = 30;
        const int SeparatorHeight = 9;
        const int PadLeft = 12;
        const int CheckWidth = 20;
        const int ArrowWidth = 16;
        const int PadRight = 10;
        const int MinWidth = 156;
        const int ColumnGap = 1;

        readonly List<Entry> _entries;
        readonly Action<string> _onPick;
        readonly LayeredSurface _surface = new LayeredSurface();
        readonly Timer _deactivateTimer = new Timer();

        float _dpi = 1f;
        int _panelW;
        int _panelH;
        int _hoverMain = -1;
        // 主列（及子菜单列）在屏幕上的期望位置；子菜单放到左边时用 _contentShift 平移窗口内容
        int _panelScreenX = int.MinValue;
        int _panelScreenY = int.MinValue;
        int _contentShift;
        System.Windows.Forms.Timer _hoverTimer;
        int _hoverSub = -1;
        int _openSub = -1;
        int _subLeft;
        int _subTop;
        int _subW;
        int _subH;

        Rectangle[] _mainRects = new Rectangle[0];
        Rectangle[] _subRects = new Rectangle[0];

        bool _dark;
        Color _panel;
        Color _border;
        Color _text;
        Color _dim;
        Color _hoverBg;
        Color _sep;
        Color _accent;

        Font _font;
        Font _boldFont;

        public OverlayMenu(List<Entry> entries, Action<string> onPick)
        {
            _entries = entries;
            _onPick = onPick;

            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            DoubleBuffered = true;
            SetStyle(ControlStyles.Opaque, true);
            SetStyle(ControlStyles.OptimizedDoubleBuffer, true);
            BackColor = Color.Black;

            using (Graphics g = Graphics.FromHwnd(IntPtr.Zero))
            {
                _dpi = Math.Max(1f, g.DpiX / 96f);
            }

            _font = new Font("Microsoft YaHei UI", 9f, FontStyle.Regular, GraphicsUnit.Point);
            _boldFont = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold, GraphicsUnit.Point);

            _dark = IsDarkTheme();
            if (_dark)
            {
                _panel = Color.FromArgb(246, 30, 32, 38);
                _border = Color.FromArgb(255, 62, 66, 78);
                _text = Color.FromArgb(255, 232, 235, 243);
                _dim = Color.FromArgb(255, 132, 137, 150);
                _hoverBg = Color.FromArgb(255, 46, 52, 64);
                _sep = Color.FromArgb(255, 52, 56, 66);
                _accent = Color.FromArgb(255, 122, 162, 255);
            }
            else
            {
                _panel = Color.FromArgb(248, 253, 253, 255);
                _border = Color.FromArgb(255, 210, 214, 226);
                _text = Color.FromArgb(255, 32, 36, 48);
                _dim = Color.FromArgb(255, 130, 136, 150);
                _hoverBg = Color.FromArgb(255, 232, 239, 252);
                _sep = Color.FromArgb(255, 230, 233, 242);
                _accent = Color.FromArgb(255, 43, 108, 246);
            }

            _deactivateTimer.Interval = 140;
            _deactivateTimer.Tick += delegate
            {
                _deactivateTimer.Stop();
                if (!ContainsFocus && _openSub < 0) Close();
            };

            // 悬停高亮/子菜单改用轮询光标：置顶的无激活窗口在某些环境下收不到 WM_MOUSEMOVE
            _hoverTimer = new System.Windows.Forms.Timer();
            _hoverTimer.Interval = 90;
            _hoverTimer.Tick += delegate { PollHover(); };

            Relayout(false);
        }

        void PollHover()
        {
            if (!Visible)
            {
                _hoverTimer.Stop();
                return;
            }
            Point client = PointToClient(Cursor.Position);
            if (!ClientRectangle.Contains(client))
            {
                if (_hoverMain != -1 || _hoverSub != -1 || _openSub >= 0)
                {
                    _hoverMain = -1;
                    _hoverSub = -1;
                    if (_openSub >= 0)
                    {
                        _openSub = -1;
                        Relayout(false);
                    }
                    else
                    {
                        Render();
                    }
                }
                return;
            }
            OnMouseMove(new MouseEventArgs(MouseButtons.None, 0, client.X, client.Y, 0));
        }

        static bool IsDarkTheme()
        {
            try
            {
                object value = Registry.GetValue(
                    @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                    "AppsUseLightTheme", 1);
                if (value is int) return (int)value == 0;
            }
            catch
            {
            }
            return false;
        }

        int S(int value)
        {
            return (int)Math.Round(value * _dpi);
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= Native.WS_EX_LAYERED | Native.WS_EX_TOOLWINDOW;
                return cp;
            }
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
        }

        protected override void OnPaint(PaintEventArgs e)
        {
        }

        // 二级菜单的条目列表（没有打开子菜单时为空）
        List<Entry> SubEntries()
        {
            if (_openSub < 0 || _openSub >= _entries.Count) return null;
            Entry parent = _entries[_openSub];
            if (!parent.IsSubmenu) return null;
            return parent.Children;
        }

        void MeasureColumns(out int mainW, out int subW)
        {
            mainW = 0;
            subW = 0;
            using (Graphics g = Graphics.FromHwnd(IntPtr.Zero))
            {
                g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
                StringFormat fmt = StringFormat.GenericTypographic;
                for (int i = 0; i < _entries.Count; i++)
                {
                    Entry e = _entries[i];
                    if (e.IsSeparator) continue;
                    SizeF size = g.MeasureString(e.Text, e.IsSubmenu ? _boldFont : _font, 4096, fmt);
                    int need = (int)Math.Ceiling(size.Width) + PadLeft + CheckWidth + PadRight + ArrowWidth;
                    if (need > mainW) mainW = need;
                }
            }
            if (mainW < S(MinWidth)) mainW = S(MinWidth);

            List<Entry> sub = SubEntries();
            if (sub != null)
            {
                using (Graphics g = Graphics.FromHwnd(IntPtr.Zero))
                {
                    g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
                    StringFormat fmt = StringFormat.GenericTypographic;
                    for (int i = 0; i < sub.Count; i++)
                    {
                        Entry e = sub[i];
                        if (e.IsSeparator) continue;
                        SizeF size = g.MeasureString(e.Text, _font, 4096, fmt);
                        int need = (int)Math.Ceiling(size.Width) + PadLeft + CheckWidth + PadRight;
                        if (need > subW) subW = need;
                    }
                }
                if (subW < S(140)) subW = S(140);
            }
        }

        void Relayout(bool keepSub)
        {
            int mainW;
            int subW;
            MeasureColumns(out mainW, out subW);

            int height = S(PanelPadY) * 2;
            for (int i = 0; i < _entries.Count; i++)
            {
                height += _entries[i].IsSeparator ? S(SeparatorHeight) : S(ItemHeight);
            }

            _panelW = mainW;
            _panelH = height;
            _mainRects = new Rectangle[_entries.Count];
            int y = S(PanelPadY);
            for (int i = 0; i < _entries.Count; i++)
            {
                Entry e = _entries[i];
                int h = e.IsSeparator ? S(SeparatorHeight) : S(ItemHeight);
                _mainRects[i] = new Rectangle(S(PadLeft) / 2, y, mainW - S(PadLeft), h);
                y += h;
            }

            List<Entry> sub = SubEntries();
            _subRects = new Rectangle[0];
            if (sub != null)
            {
                int subH = S(PanelPadY) * 2;
                for (int i = 0; i < sub.Count; i++)
                {
                    subH += sub[i].IsSeparator ? S(SeparatorHeight) : S(ItemHeight);
                }
                _subW = subW;
                _subH = subH;
                Rectangle wa = WorkArea();
                int desiredX = _panelScreenX == int.MinValue ? wa.Left : _panelScreenX;
                // 右边放不下就把子菜单翻到左边，窗口随之向左长出去
                bool flip = desiredX + mainW + ColumnGap + subW > wa.Right - S(ShadowMargin);
                _subLeft = flip ? -(subW + ColumnGap) : mainW + ColumnGap;
                Rectangle anchor = _mainRects[_openSub];
                _subTop = anchor.Y - S(PanelPadY);
                if (_subTop + subH > height - S(PanelPadY)) _subTop = height - S(PanelPadY) - subH;
                if (_subTop < 0) _subTop = 0;

                _subRects = new Rectangle[sub.Count];
                int sy = _subTop + S(PanelPadY);
                for (int i = 0; i < sub.Count; i++)
                {
                    Entry e = sub[i];
                    int h = e.IsSeparator ? S(SeparatorHeight) : S(ItemHeight);
                    _subRects[i] = new Rectangle(_subLeft, sy, subW, h);
                    sy += h;
                }
                int minX = Math.Min(0, _subLeft);
                int maxX = Math.Max(mainW, _subLeft + subW);
                _contentShift = -minX;
                _panelW = maxX - minX;
                _panelH = Math.Max(height, _subTop + subH);
            }
            else if (!keepSub)
            {
                _subW = 0;
                _subH = 0;
                _contentShift = 0;
            }

            int margin = S(ShadowMargin);
            Size = new Size(_panelW + margin * 2, _panelH + margin * 2);
            if (_surface.Width != Width || _surface.Height != Height) _surface.Resize(Width, Height);
            PlaceWindow();
            Render();
        }

        Rectangle WorkArea()
        {
            Point p = _panelScreenX == int.MinValue ? Cursor.Position : new Point(_panelScreenX, _panelScreenY);
            Screen s = Screen.FromPoint(p);
            if (s == null) s = Screen.PrimaryScreen;
            return s.WorkingArea;
        }

        // 主列位置是锚点：窗口尺寸（含翻到左边的子菜单）变化时，主列不能跟着动
        void PlaceWindow()
        {
            if (_panelScreenX == int.MinValue) return;
            int margin = S(ShadowMargin);
            Rectangle wa = WorkArea();
            // 主列画在 margin + _contentShift 处，窗口左边界要反向抵消，否则主列会被推两次
            int x = _panelScreenX - margin - _contentShift;
            int y = _panelScreenY - margin;
            if (x + Width > wa.Right) x = wa.Right - Width;
            if (x < wa.Left) x = wa.Left;
            if (y + Height > wa.Bottom) y = wa.Bottom - Height;
            if (y < wa.Top) y = wa.Top;
            Location = new Point(x, y);
        }

        public void ShowAt(Point panelOrigin)
        {
            // 靠近屏幕边缘时先把主列挤回可视区，再定位
            Point probe = panelOrigin;
            Screen s = Screen.FromPoint(probe);
            if (s == null) s = Screen.PrimaryScreen;
            Rectangle wa = s.WorkingArea;
            int margin = S(ShadowMargin);
            int x = panelOrigin.X;
            int y = panelOrigin.Y;
            if (x + _panelW + margin > wa.Right) x = wa.Right - _panelW - margin;
            if (x < wa.Left + margin) x = wa.Left + margin;
            if (y + _panelH + margin > wa.Bottom) y = wa.Bottom - _panelH - margin;
            if (y < wa.Top + margin) y = wa.Top + margin;
            _panelScreenX = x;
            _panelScreenY = y;
            Relayout(false);
            Show();
            Activate();
            _surface.Push(this);
            if (_hoverTimer != null) _hoverTimer.Start();
        }

        public int PanelWidth { get { return _panelW; } }
        public int PanelHeight { get { return _panelH; } }

        void Render()
        {
            Graphics g = _surface.Graphics;
            if (g == null) return;
            g.Clear(Color.Transparent);
            int margin = S(ShadowMargin);
            Rectangle panel = new Rectangle(margin + _contentShift, margin, _panelW, _panelH);

            // 阴影：三层递减的半透明圆角矩形，够用且不引入位图模糊
            int shadow = S(4);
            for (int i = shadow; i >= 1; i--)
            {
                int alpha = 10 + (shadow - i) * 6;
                Rectangle r = new Rectangle(panel.X - i, panel.Y - i, panel.Width + i * 2, panel.Height + i * 2);
                using (GraphicsPath path = RoundRect(r, S(10) + i))
                using (SolidBrush brush = new SolidBrush(Color.FromArgb(alpha, 12, 16, 28)))
                {
                    g.FillPath(brush, path);
                }
            }

            using (GraphicsPath path = RoundRect(panel, S(10)))
            using (SolidBrush brush = new SolidBrush(_panel))
            using (Pen pen = new Pen(_border, 1f))
            {
                g.FillPath(brush, path);
                g.DrawPath(pen, path);
            }

            for (int i = 0; i < _entries.Count; i++)
            {
                DrawMainItem(g, i);
            }

            List<Entry> sub = SubEntries();
            if (sub != null)
            {
                Rectangle subRect = new Rectangle(margin + _contentShift + _subLeft, margin + _subTop, _subW, _subH);
                using (GraphicsPath path = RoundRect(subRect, S(10)))
                using (SolidBrush brush = new SolidBrush(_panel))
                using (Pen pen = new Pen(_border, 1f))
                {
                    g.FillPath(brush, path);
                    g.DrawPath(pen, path);
                }
                for (int i = 0; i < sub.Count; i++)
                {
                    DrawSubItem(g, i);
                }
            }

            _surface.Push(this);
        }

        void DrawMainItem(Graphics g, int index)
        {
            Entry e = _entries[index];
            Rectangle rect = Offset(_mainRects[index]);
            if (e.IsSeparator)
            {
                using (Pen pen = new Pen(_sep, 1f))
                {
                    g.DrawLine(pen, rect.Left + S(10), rect.Top + rect.Height / 2, rect.Right - S(6), rect.Top + rect.Height / 2);
                }
                return;
            }
            if (index == _hoverMain) FillHover(g, rect);
            DrawRow(g, e, rect, index == _hoverMain, true);
        }

        void DrawSubItem(Graphics g, int index)
        {
            List<Entry> sub = SubEntries();
            if (sub == null || index >= sub.Count) return;
            Entry e = sub[index];
            Rectangle rect = Offset(_subRects[index]);
            if (e.IsSeparator)
            {
                using (Pen pen = new Pen(_sep, 1f))
                {
                    g.DrawLine(pen, rect.Left + S(10), rect.Top + rect.Height / 2, rect.Right - S(6), rect.Top + rect.Height / 2);
                }
                return;
            }
            if (index == _hoverSub) FillHover(g, rect);
            DrawRow(g, e, rect, index == _hoverSub, false);
        }

        Rectangle Offset(Rectangle rect)
        {
            int margin = S(ShadowMargin);
            return new Rectangle(rect.X + margin + _contentShift, rect.Y + margin, rect.Width, rect.Height);
        }

        void FillHover(Graphics g, Rectangle rect)
        {
            Rectangle r = new Rectangle(rect.X + S(4), rect.Y + S(1), rect.Width - S(8) - S(PadLeft), rect.Height - S(2));
            using (GraphicsPath path = RoundRect(r, S(7)))
            using (SolidBrush brush = new SolidBrush(_hoverBg))
            {
                g.FillPath(brush, path);
            }
        }

        void DrawRow(Graphics g, Entry e, Rectangle rect, bool hovered, bool hasArrow)
        {
            Color color = e.Enabled ? _text : _dim;
            int textLeft = rect.X + S(PadLeft) / 2 + S(CheckWidth);

            if (e.Checked)
            {
                using (Font glyph = new Font("Segoe UI Symbol", 9f, FontStyle.Regular, GraphicsUnit.Point))
                using (SolidBrush brush = new SolidBrush(e.Enabled ? _accent : _dim))
                {
                    g.DrawString("✓", glyph, brush, rect.X + S(6), rect.Y + (rect.Height - S(18)) / 2f);
                }
            }

            StringFormat fmt = new StringFormat(StringFormat.GenericTypographic);
            fmt.LineAlignment = StringAlignment.Center;
            fmt.FormatFlags |= StringFormatFlags.NoWrap;
            // 宽度必须用右边界推算；textLeft 是绝对坐标，用 rect.Width 相减在子菜单翻到左侧时会变负数，文字整段消失
            int textWidth = rect.Right - textLeft - (hasArrow ? S(ArrowWidth) : S(6));
            if (textWidth < S(24)) textWidth = S(24);
            Rectangle textRect = new Rectangle(textLeft, rect.Y, textWidth, rect.Height);
            using (SolidBrush brush = new SolidBrush(color))
            {
                g.DrawString(e.Text, e.IsSubmenu ? _boldFont : _font, brush, textRect, fmt);
            }
            fmt.Dispose();

            if (hasArrow && e.IsSubmenu)
            {
                using (Font glyph = new Font("Segoe UI Symbol", 8f, FontStyle.Regular, GraphicsUnit.Point))
                using (SolidBrush brush = new SolidBrush(hovered ? _accent : _dim))
                {
                    g.DrawString("▶", glyph, brush, rect.Right - S(ArrowWidth), rect.Y + (rect.Height - S(16)) / 2f);
                }
            }
        }

        static GraphicsPath RoundRect(Rectangle rect, int radius)
        {
            GraphicsPath path = new GraphicsPath();
            int d = Math.Max(2, Math.Min(radius * 2, Math.Min(rect.Width, rect.Height)));
            path.AddArc(rect.X, rect.Y, d, d, 180, 90);
            path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
            path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
            path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        int HitMain(Point p)
        {
            Point local = new Point(p.X - S(ShadowMargin) - _contentShift, p.Y - S(ShadowMargin));
            for (int i = 0; i < _mainRects.Length; i++)
            {
                if (_mainRects[i].Contains(local)) return i;
            }
            return -1;
        }

        int HitSub(Point p)
        {
            if (SubEntries() == null) return -1;
            p = new Point(p.X - _contentShift, p.Y);
            Point local = new Point(p.X - S(ShadowMargin), p.Y - S(ShadowMargin));
            for (int i = 0; i < _subRects.Length; i++)
            {
                if (_subRects[i].Contains(local)) return i;
            }
            return -1;
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Native.WM_NCHITTEST)
            {
                Point screen = new Point(m.LParam.ToInt32() & 0xFFFF, (m.LParam.ToInt32() >> 16) & 0xFFFF);
                Point local = PointToClient(screen);
                m.Result = _surface.HitTest(local) ? new IntPtr(Native.HTCLIENT) : new IntPtr(Native.HTTRANSPARENT);
                return;
            }
            base.WndProc(ref m);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int main = HitMain(e.Location);
            int sub = HitSub(e.Location);
            bool changed = false;

            if (sub >= 0)
            {
                if (_hoverSub != sub) { _hoverSub = sub; changed = true; }
            }
            else if (_hoverSub != -1)
            {
                _hoverSub = -1;
                changed = true;
            }

            if (main >= 0)
            {
                if (_hoverMain != main)
                {
                    _hoverMain = main;
                    changed = true;
                    Entry entry = _entries[main];
                    if (entry.IsSubmenu)
                    {
                        _openSub = main;
                        _subTop = 0;
                        Relayout(true);
                        changed = false;
                    }
                    else if (_openSub >= 0)
                    {
                        _openSub = -1;
                        Relayout(false);
                        changed = false;
                    }
                }
            }

            if (changed) Render();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            // 移出窗口（含透明边距）时收起高亮，但保留子菜单，避免鼠标抖动导致闪烁
            if (_hoverMain != -1 || _hoverSub != -1)
            {
                _hoverMain = -1;
                _hoverSub = -1;
                Render();
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button == MouseButtons.Right)
            {
                Close();
                return;
            }
            if (e.Button != MouseButtons.Left) return;

            int sub = HitSub(e.Location);
            if (sub >= 0)
            {
                List<Entry> list = SubEntries();
                if (list != null && sub < list.Count)
                {
                    Entry entry = list[sub];
                    if (!entry.IsSeparator && entry.Enabled) Pick(entry.Id);
                }
                return;
            }

            int main = HitMain(e.Location);
            if (main < 0) return;
            Entry item = _entries[main];
            if (item.IsSeparator) return;
            if (item.IsSubmenu)
            {
                _openSub = main;
                Relayout(true);
                return;
            }
            if (item.Enabled) Pick(item.Id);
        }

        void Pick(string id)
        {
            Log.W("menu pick " + id);
            Close();
            if (_onPick != null)
            {
                try
                {
                    _onPick(id);
                }
                catch (Exception error)
                {
                    Log.W("menu action failed: " + error.Message);
                }
            }
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Escape)
            {
                if (_openSub >= 0)
                {
                    _openSub = -1;
                    Relayout(false);
                    return true;
                }
                Close();
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void OnDeactivate(EventArgs e)
        {
            base.OnDeactivate(e);
            _deactivateTimer.Start();
        }

        protected override void OnActivated(EventArgs e)
        {
            base.OnActivated(e);
            _deactivateTimer.Stop();
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _deactivateTimer.Stop();
            _deactivateTimer.Dispose();
            if (_hoverTimer != null)
            {
                _hoverTimer.Stop();
                _hoverTimer.Dispose();
                _hoverTimer = null;
            }
            _surface.Dispose();
            base.OnFormClosed(e);
        }
    }
}