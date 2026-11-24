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
using System.Windows.Forms;

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

        const int OuterMargin = 6;
        const int PanelPadY = 6;
        const int ItemHeight = 29;
        const int SeparatorHeight = 9;
        const int PadLeft = 12;
        const int CheckWidth = 20;
        const int ArrowWidth = 16;
        const int PadRight = 10;
        const int MinWidth = 156;
        const int ColumnGap = 6;

        readonly List<Entry> _entries;
        readonly Action<string> _onPick;
        readonly LayeredSurface _surface = new LayeredSurface();
        readonly Timer _deactivateTimer = new Timer();

        readonly float _dpi;
        int _mainW;
        int _mainH;
        int _panelW;
        int _panelH;
        int _hoverMain = -1;
        bool _keyboardMode;
        int _previousMouseButtons;

        Point _lastPollPoint = new Point(int.MinValue, int.MinValue);
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

        OverlayPalette _palette = OverlayPalette.Current();
        readonly Font _font;

        public OverlayMenu(List<Entry> entries, Action<string> onPick, float dpi)
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

            _dpi = dpi > 0 ? dpi : 1f;
            _font = new Font("Microsoft YaHei UI", 12.5f * _dpi, FontStyle.Regular, GraphicsUnit.Pixel);

            _deactivateTimer.Interval = 140;
            _deactivateTimer.Tick += delegate
            {
                _deactivateTimer.Stop();
                if (Native.GetForegroundWindow() != Handle) Close();
            };

            // 补足透明区域的鼠标事件，外部点击继续由下方窗口处理。
            _hoverTimer = new System.Windows.Forms.Timer();
            _hoverTimer.Interval = 35;
            _hoverTimer.Tick += delegate { PollHover(); };

            Relayout();
        }

        void PollHover()
        {
            if (!Visible)
            {
                _hoverTimer.Stop();
                return;
            }
            Point screen = Cursor.Position;
            Point client = PointToClient(screen);
            int buttons = Native.PressedMouseButtons();
            bool pressedOutside = (buttons & ~_previousMouseButtons) != 0 && !_surface.HitTest(client);
            _previousMouseButtons = buttons;
            if (pressedOutside)
            {
                Close();
                return;
            }
            // 键盘操作期间把高亮交给键盘管：光标没动就别来抢（否则 ↑↓ 选中的项每 90ms 被清一次）
            if (screen != _lastPollPoint)
            {
                _lastPollPoint = screen;
                _keyboardMode = false;
            }
            if (_keyboardMode) return;
            if (!ClientRectangle.Contains(client))
            {
                if (_hoverMain != -1 || _hoverSub != -1 || _openSub >= 0)
                {
                    _hoverMain = -1;
                    _hoverSub = -1;
                    if (_openSub >= 0)
                    {
                        _openSub = -1;
                        Relayout();
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
            using (StringFormat fmt = StringFormat.GenericTypographic)
            {
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                for (int i = 0; i < _entries.Count; i++)
                {
                    Entry e = _entries[i];
                    if (e.IsSeparator) continue;
                    SizeF size = g.MeasureString(e.Text, _font, 4096, fmt);
                    int need = (int)Math.Ceiling(size.Width) + S(PadLeft + CheckWidth + PadRight + ArrowWidth);
                    if (need > mainW) mainW = need;
                }
            }
            if (mainW < S(MinWidth)) mainW = S(MinWidth);

            List<Entry> sub = SubEntries();
            if (sub != null)
            {
                using (Graphics g = Graphics.FromHwnd(IntPtr.Zero))
                using (StringFormat fmt = StringFormat.GenericTypographic)
                {
                    g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                    for (int i = 0; i < sub.Count; i++)
                    {
                        Entry e = sub[i];
                        if (e.IsSeparator) continue;
                        SizeF size = g.MeasureString(e.Text, _font, 4096, fmt);
                        int need = (int)Math.Ceiling(size.Width) + S(PadLeft + CheckWidth + PadRight);
                        if (need > subW) subW = need;
                    }
                }
                if (subW < S(140)) subW = S(140);
            }
        }

        void Relayout()
        {
            int mainW;
            int subW;
            MeasureColumns(out mainW, out subW);

            int height = S(PanelPadY) * 2;
            for (int i = 0; i < _entries.Count; i++)
            {
                height += _entries[i].IsSeparator ? S(SeparatorHeight) : S(ItemHeight);
            }

            _mainW = mainW;
            _mainH = height;
            _panelW = _mainW;
            _panelH = _mainH;
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
                int gap = S(ColumnGap);
                int leftSpace = desiredX - wa.Left - S(OuterMargin);
                int rightSpace = wa.Right - desiredX - mainW - S(OuterMargin);
                bool flip = rightSpace < subW + gap && leftSpace > rightSpace;
                _subLeft = flip ? -(subW + gap) : mainW + gap;
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
                    _subRects[i] = new Rectangle(_subLeft + S(PadLeft) / 2, sy, subW - S(PadLeft), h);
                    sy += h;
                }
                int minX = Math.Min(0, _subLeft);
                int maxX = Math.Max(mainW, _subLeft + subW);
                _contentShift = -minX;
                _panelW = maxX - minX;
                _panelH = Math.Max(height, _subTop + subH);
            }
            else
            {
                _subW = 0;
                _subH = 0;
                _contentShift = 0;
            }

            int margin = S(OuterMargin);
            int width = _panelW + margin * 2;
            int heightPx = _panelH + margin * 2;
            _surface.Resize(width, heightPx);
            RenderAt(MenuLocation(width, heightPx));
        }

        Rectangle WorkArea()
        {
            Point p = _panelScreenX == int.MinValue ? Cursor.Position : new Point(_panelScreenX, _panelScreenY);
            Screen s = Screen.FromPoint(p);
            if (s == null) s = Screen.PrimaryScreen;
            return s.WorkingArea;
        }

        // 主列位置是锚点：窗口尺寸（含翻到左边的子菜单）变化时，主列不能跟着动
        Point MenuLocation(int width, int height)
        {
            if (_panelScreenX == int.MinValue) return Location;
            int margin = S(OuterMargin);
            Rectangle wa = WorkArea();
            // 主列画在 margin + _contentShift 处，窗口左边界要反向抵消，否则主列会被推两次
            int x = _panelScreenX - margin - _contentShift;
            int y = _panelScreenY - margin;
            if (x + width > wa.Right) x = wa.Right - width;
            if (x < wa.Left) x = wa.Left;
            if (y + height > wa.Bottom) y = wa.Bottom - height;
            if (y < wa.Top) y = wa.Top;
            return new Point(x, y);
        }

        public void ShowAt(Point panelOrigin)
        {
            // 靠近屏幕边缘时先把主列挤回可视区，再定位
            Point probe = panelOrigin;
            Screen s = Screen.FromPoint(probe);
            if (s == null) s = Screen.PrimaryScreen;
            Rectangle wa = s.WorkingArea;
            int margin = S(OuterMargin);
            int x = panelOrigin.X;
            int y = panelOrigin.Y;
            if (x + _panelW + margin > wa.Right) x = wa.Right - _panelW - margin;
            if (x < wa.Left + margin) x = wa.Left + margin;
            if (y + _panelH + margin > wa.Bottom) y = wa.Bottom - _panelH - margin;
            if (y < wa.Top + margin) y = wa.Top + margin;
            _panelScreenX = x;
            _panelScreenY = y;
            Relayout();
            Show();
            Activate();
            _previousMouseButtons = Native.PressedMouseButtons();
            Present(Location);
            if (_hoverTimer != null) _hoverTimer.Start();
        }

        public int PanelWidth { get { return _mainW; } }
        public int PanelHeight { get { return _mainH; } }

        void Render()
        {
            RenderAt(Location);
        }

        void RenderAt(Point destination)
        {
            Graphics g = _surface.Graphics;
            if (g == null) return;
            g.Clear(Color.Transparent);
            PaintPanel(g, Offset(new Rectangle(0, 0, _mainW, _mainH)));
            for (int i = 0; i < _entries.Count; i++)
            {
                bool open = _openSub == i;
                DrawRow(g, _entries[i], Offset(_mainRects[i]), _hoverMain == i || open, true, open && _subLeft < 0);
            }

            List<Entry> sub = SubEntries();
            if (sub != null)
            {
                PaintPanel(g, Offset(new Rectangle(_subLeft, _subTop, _subW, _subH)));
                for (int i = 0; i < sub.Count; i++)
                    DrawRow(g, sub[i], Offset(_subRects[i]), _hoverSub == i, false, false);
            }
            Present(destination);
        }

        void Present(Point destination)
        {
            if (_surface.Push(this, destination)) UpdateBounds();
        }

        void PaintPanel(Graphics g, Rectangle rect)
        {
            using (GraphicsPath shape = RoundRect(rect, S(13)))
            using (SolidBrush brush = new SolidBrush(_palette.Surface))
                g.FillPath(brush, shape);
        }

        Rectangle Offset(Rectangle rect)
        {
            int margin = S(OuterMargin);
            rect.Offset(margin + _contentShift, margin);
            return rect;
        }

        void DrawRow(Graphics g, Entry entry, Rectangle bounds, bool hovered, bool hasArrowSpace, bool pointsLeft)
        {
            float midY = bounds.Top + bounds.Height / 2f;
            if (entry.IsSeparator)
            {
                using (Pen line = new Pen(_palette.Line, Math.Max(1, S(1))))
                    g.DrawLine(line, bounds.Left + S(6), midY, bounds.Right - S(6), midY);
                return;
            }

            bool highlighted = hovered && entry.Enabled;
            if (highlighted)
            {
                Rectangle highlight = Rectangle.Inflate(bounds, 0, -S(1));
                using (GraphicsPath shape = RoundRect(highlight, S(8)))
                using (SolidBrush brush = new SolidBrush(_palette.Hover))
                    g.FillPath(brush, shape);
            }

            Color text = !entry.Enabled ? _palette.Muted : entry.Id == "quit" ? _palette.Failure : _palette.Text;
            int textLeft = bounds.Left + S(CheckWidth + 4);
            int textWidth = Math.Max(1, bounds.Right - textLeft - S(hasArrowSpace ? ArrowWidth : 6));
            Rectangle textBox = new Rectangle(textLeft, bounds.Top, textWidth, bounds.Height);
            using (StringFormat format = new StringFormat())
            using (SolidBrush brush = new SolidBrush(text))
            {
                format.LineAlignment = StringAlignment.Center;
                format.FormatFlags = StringFormatFlags.NoWrap;
                format.Trimming = StringTrimming.EllipsisCharacter;
                g.DrawString(entry.Text, _font, brush, textBox, format);
            }

            if (entry.Checked)
            {
                float x = bounds.Left + S(11);
                using (Pen stroke = SymbolPen(entry.Enabled ? _palette.Accent : _palette.Muted))
                    g.DrawLines(stroke, new PointF[] {
                        new PointF(x - S(4), midY), new PointF(x - S(1), midY + S(3)), new PointF(x + S(5), midY - S(4))
                    });
            }

            if (hasArrowSpace && entry.IsSubmenu)
            {
                float x = bounds.Right - S(9);
                int direction = pointsLeft ? -1 : 1;
                using (Pen stroke = SymbolPen(highlighted ? _palette.Accent : _palette.Muted))
                    g.DrawLines(stroke, new PointF[] {
                        new PointF(x - direction * S(2), midY - S(4)), new PointF(x + direction * S(2), midY),
                        new PointF(x - direction * S(2), midY + S(4))
                    });
            }
        }

        Pen SymbolPen(Color color)
        {
            Pen stroke = new Pen(color, Math.Max(1f, 1.5f * _dpi));
            stroke.StartCap = LineCap.Round;
            stroke.EndCap = LineCap.Round;
            stroke.LineJoin = LineJoin.Round;
            return stroke;
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
            Point local = new Point(p.X - S(OuterMargin) - _contentShift, p.Y - S(OuterMargin));
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
            Point local = new Point(p.X - S(OuterMargin), p.Y - S(OuterMargin));
            for (int i = 0; i < _subRects.Length; i++)
            {
                if (_subRects[i].Contains(local)) return i;
            }
            return -1;
        }

        protected override void WndProc(ref Message m)
        {
            if (OverlayPalette.IsThemeMessage(m.Msg))
            {
                OverlayPalette palette = OverlayPalette.Current();
                if (_palette != palette)
                {
                    _palette = palette;
                    if (Visible) Render();
                }
            }
            if (m.Msg == Native.WM_NCHITTEST)
            {
                int coordinates = unchecked((int)m.LParam.ToInt64());
                Point screen = new Point((short)(coordinates & 0xFFFF), (short)(coordinates >> 16));
                Point local = PointToClient(screen);
                m.Result = _surface.HitTest(local) ? new IntPtr(Native.HTCLIENT) : new IntPtr(Native.HTTRANSPARENT);
                return;
            }
            base.WndProc(ref m);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (e.Button != MouseButtons.None || Cursor.Position != _lastPollPoint) _keyboardMode = false;
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
                        Relayout();
                        changed = false;
                    }
                    else if (_openSub >= 0)
                    {
                        _openSub = -1;
                        Relayout();
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
                Relayout();
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
            if (HandleKey(keyData)) return true;
            return base.ProcessCmdKey(ref msg, keyData);
        }

        bool HandleKey(Keys keyData)
        {
            if (keyData == Keys.Escape)
            {
                Close();
                return true;
            }

            if (keyData == Keys.Down) { _keyboardMode = true; MoveSelection(1); return true; }
            if (keyData == Keys.Up) { _keyboardMode = true; MoveSelection(-1); return true; }
            if (keyData == Keys.Left)
            {
                _keyboardMode = true;
                if (_openSub >= 0)
                {
                    _hoverSub = -1;
                    _hoverMain = _openSub;
                    _openSub = -1;
                    Relayout();
                    Render();
                    return true;
                }
                return true;
            }
            if (keyData == Keys.Right)
            {
                _keyboardMode = true;
                if (_openSub < 0 && _hoverMain >= 0 && _entries[_hoverMain].IsSubmenu) OpenSubmenu(_hoverMain);
                else MoveSelection(1);
                return true;
            }
            if (keyData == Keys.Enter || keyData == Keys.Space)
            {
                _keyboardMode = true;
                ActivateSelection();
                return true;
            }
            return false;
        }

        /// <summary>↑↓ 在"当前这一列"里挪一格，跳过分隔线与不可用项。</summary>
        void MoveSelection(int delta)
        {
            if (_openSub >= 0 && _hoverSub >= 0)
            {
                List<Entry> children = SubEntries();
                if (children != null)
                {
                    int next = NextSelectable(children, _hoverSub, delta);
                    if (next >= 0 && next != _hoverSub)
                    {
                        _hoverSub = next;
                        Render();
                    }
                    return;
                }
            }
            int moved = NextSelectable(_entries, _hoverMain, delta);
            if (moved < 0) return;
            _hoverMain = moved;
            Entry entry = _entries[moved];
            if (entry.IsSubmenu) OpenSubmenu(moved);
            else if (_openSub >= 0)
            {
                _openSub = -1;
                _hoverSub = -1;
                Relayout();
                Render();
            }
            else
            {
                Render();
            }
        }

        static int NextSelectable(List<Entry> list, int from, int delta)
        {
            if (list == null || list.Count == 0) return -1;
            int index = from;
            for (int guard = 0; guard < list.Count; guard++)
            {
                index += delta;
                if (index < 0) index = list.Count - 1;
                if (index >= list.Count) index = 0;
                Entry candidate = list[index];
                if (!candidate.IsSeparator && candidate.Enabled) return index;
            }
            return -1;
        }

        void OpenSubmenu(int mainIndex)
        {
            if (mainIndex < 0 || mainIndex >= _entries.Count) return;
            if (!_entries[mainIndex].IsSubmenu) return;
            _hoverMain = mainIndex;
            _openSub = mainIndex;
            _subTop = 0;
            _hoverSub = -1;
            Relayout();
            List<Entry> children = SubEntries();
            int first = NextSelectable(children, -1, 1);
            if (first >= 0) _hoverSub = first;
            Render();
        }

        /// <summary>回车：选中项是子菜单就展开，否则直接执行。</summary>
        void ActivateSelection()
        {
            if (_openSub >= 0 && _hoverSub >= 0)
            {
                List<Entry> children = SubEntries();
                if (children != null && _hoverSub < children.Count)
                {
                    Entry child = children[_hoverSub];
                    if (!child.IsSeparator && child.Enabled) Pick(child.Id);
                }
                return;
            }
            if (_hoverMain < 0 || _hoverMain >= _entries.Count) return;
            Entry entry = _entries[_hoverMain];
            if (entry.IsSeparator) return;
            if (entry.IsSubmenu) OpenSubmenu(_hoverMain);
            else if (entry.Enabled) Pick(entry.Id);
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
            _font.Dispose();
            base.OnFormClosed(e);
        }
    }
}