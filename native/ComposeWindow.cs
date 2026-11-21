// dsh-pet · 宠物旁边的"就地输入"小窗
//
// Codex 的交互是：鼠标移到宠物身上出现 ✎ / 🎤，点一下就在宠物旁边写字或说话。
// 这里用真实的 WinForms 控件（TextBox 必须有真焦点才能输入），窗口本身圆角+描边，
// Esc 关闭、Enter 发送、Shift+Enter 换行、点走且没内容就自动收起。
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace DshPet
{
    sealed class ComposeWindow : Form
    {
        readonly Func<string, string, string> _loc;
        readonly Action<string> _submit;
        readonly TextBox _input;
        readonly Label _placeholder;
        readonly Label _hint;
        readonly PillButton _send;
        readonly PillButton _mic;
        readonly Timer _hintTimer;
        readonly Timer _deactivateTimer;
        VoiceInput _voice;
        string _voiceBase = "";

        bool _dark;
        Color _panel;
        Color _border;
        Color _text;
        Color _dim;
        Color _accent;

        public ComposeWindow(Rectangle bounds, Func<string, string, string> loc, Action<string> submit, bool voice)
        {
            _loc = loc;
            _submit = submit;

            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            DoubleBuffered = true;

            _dark = IsDarkTheme();
            if (_dark)
            {
                _panel = Color.FromArgb(255, 32, 34, 41);
                _border = Color.FromArgb(255, 66, 70, 84);
                _text = Color.FromArgb(255, 232, 235, 243);
                _dim = Color.FromArgb(255, 138, 143, 156);
                _accent = Color.FromArgb(255, 92, 140, 255);
            }
            else
            {
                _panel = Color.FromArgb(255, 253, 253, 255);
                _border = Color.FromArgb(255, 214, 218, 230);
                _text = Color.FromArgb(255, 32, 36, 48);
                _dim = Color.FromArgb(255, 140, 146, 160);
                _accent = Color.FromArgb(255, 43, 108, 246);
            }
            BackColor = _panel;

            _input = new TextBox();
            _input.Multiline = true;
            _input.BorderStyle = BorderStyle.None;
            _input.BackColor = _panel;
            _input.ForeColor = _text;
            _input.Font = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Regular, GraphicsUnit.Point);
            _input.ScrollBars = ScrollBars.Vertical;
            _input.KeyDown += OnInputKeyDown;
            _input.TextChanged += delegate
            {
                _placeholder.Visible = _input.TextLength == 0;
                _hint.Visible = false;
            };
            // 占位文案在输入框为空时就该一直显示，只有真的开始打字才隐藏
            _input.GotFocus += delegate { _placeholder.Visible = _input.TextLength == 0; };
            _input.LostFocus += delegate { _placeholder.Visible = _input.TextLength == 0; };
            Controls.Add(_input);

            _placeholder = new Label();
            _placeholder.AutoSize = false;
            _placeholder.BackColor = _panel;
            _placeholder.ForeColor = _dim;
            _placeholder.Font = _input.Font;
            _placeholder.Text = _loc("给智能伙伴写一句话…", "Message your companion…");
            _placeholder.Click += delegate { _input.Focus(); };
            Controls.Add(_placeholder);
            // WinForms 里后加的控件在 z 序下层，占位文案会被 TextBox 盖住，必须提到最前
            _placeholder.BringToFront();

            _hint = new Label();
            _hint.AutoSize = false;
            _hint.BackColor = _panel;
            _hint.ForeColor = Color.FromArgb(255, 214, 74, 74);
            _hint.Font = new Font("Microsoft YaHei UI", 8f, FontStyle.Regular, GraphicsUnit.Point);
            _hint.Visible = false;
            Controls.Add(_hint);

            _send = new PillButton(_loc("发送", "Send"), _accent, Color.White);
            _send.Click += delegate { DoSubmit(); };
            Controls.Add(_send);

            _mic = new PillButton(_loc("语音", "Voice"), _panel, _text);
            _mic.Border = true;
            _mic.BorderColor = _border;
            _mic.Click += delegate { ToggleVoice(); };
            Controls.Add(_mic);

            _hintTimer = new Timer();
            _hintTimer.Interval = 4000;
            _hintTimer.Tick += delegate
            {
                _hintTimer.Stop();
                _hint.Visible = false;
            };

            _deactivateTimer = new Timer();
            _deactivateTimer.Interval = 160;
            _deactivateTimer.Tick += delegate
            {
                _deactivateTimer.Stop();
                if (!ContainsFocus && _input.TextLength == 0 && (_voice == null || !_voice.Recording)) Close();
            };

            // 控件都建好之后再定尺寸：早先在建控件前 Size 赋值会触发 OnResize，
            // LayoutChildren 摸到 null 控件抛 NullReferenceException（浮层日志里能看到）。
            Bounds = bounds;
            LayoutChildren();
            ApplyRoundRegion();
            if (voice) BeginInvoke(new Action(ToggleVoice));
        }

        static bool IsDarkTheme()
        {
            try
            {
                object value = Microsoft.Win32.Registry.GetValue(
                    @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                    "AppsUseLightTheme", 1);
                if (value is int) return (int)value == 0;
            }
            catch
            {
            }
            return false;
        }

        void LayoutChildren()
        {
            if (_input == null || _placeholder == null || _hint == null || _send == null || _mic == null) return;
            int pad = 12;
            int buttonH = 26;
            int buttonW = 64;
            int inputH = Height - pad * 2 - buttonH - 6;
            if (inputH < 28) inputH = 28;
            _input.SetBounds(pad, pad, Width - pad * 2, inputH);
            _placeholder.SetBounds(pad + 1, pad + 2, Width - pad * 2 - 2, inputH);
            _hint.SetBounds(pad, Height - pad - buttonH, Width - pad * 2 - buttonW * 2 - 16, buttonH);
            _send.SetBounds(Width - pad - buttonW, Height - pad - buttonH, buttonW, buttonH);
            _mic.SetBounds(Width - pad - buttonW * 2 - 8, Height - pad - buttonH, buttonW, buttonH);
        }

        void ApplyRoundRegion()
        {
            using (GraphicsPath path = RoundRect(new Rectangle(0, 0, Width, Height), 12))
            {
                Region old = Region;
                Region = new Region(path);
                if (old != null) old.Dispose();
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

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            using (SolidBrush brush = new SolidBrush(_panel))
            {
                e.Graphics.FillRectangle(brush, ClientRectangle);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using (GraphicsPath path = RoundRect(new Rectangle(0, 0, Width - 1, Height - 1), 12))
            using (Pen pen = new Pen(_border, 1f))
            {
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                e.Graphics.DrawPath(pen, path);
            }
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            LayoutChildren();
            ApplyRoundRegion();
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Activate();
            _input.Focus();
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

        void OnInputKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape)
            {
                e.SuppressKeyPress = true;
                Close();
            }
            else if (e.KeyCode == Keys.Enter && !e.Shift && !e.Control)
            {
                e.SuppressKeyPress = true;
                DoSubmit();
            }
        }

        void DoSubmit()
        {
            string text = _input.Text;
            if (text == null || text.Trim().Length == 0) return;
            if (_submit != null) _submit(text.Trim());
        }

        public void ShowError(string message)
        {
            if (IsDisposed) return;
            _hint.Text = message.Length > 120 ? message.Substring(0, 120) + "…" : message;
            _hint.Visible = true;
            _hintTimer.Stop();
            _hintTimer.Start();
        }

        public void SetText(string text)
        {
            _input.Text = text;
            _input.SelectionStart = _input.TextLength;
        }

        // ================= 语音 =================

        void ToggleVoice()
        {
            if (_voice == null) _voice = new VoiceInput();
            if (_voice.Recording)
            {
                _voice.Stop();
                _mic.Text = _loc("语音", "Voice");
                _mic.Invalidate();
                return;
            }
            if (!_voice.Available)
            {
                ShowError(_voice.UnavailableReason);
                return;
            }
            _voiceBase = _input.TextLength == 0 ? "" : _input.Text.TrimEnd() + " ";
            _voice.Reset();
            _voice.Partial += OnVoicePartial;
            _voice.Final += OnVoiceFinal;
            _voice.Failed += OnVoiceFailed;
            _voice.Start();
            _mic.Text = _loc("停止", "Stop");
            _mic.Invalidate();
            _input.Focus();
        }

        void OnVoiceFinal(string text)
        {
            if (IsDisposed || !IsHandleCreated) return;
            BeginInvoke(new Action(delegate
            {
                _voiceBase = _voiceBase + text + " ";
                SetText(_voiceBase);
                _mic.Text = _loc("语音", "Voice");
                _mic.Invalidate();
                if (_voice != null) _voice.Stop();
            }));
        }

        void OnVoicePartial(string text)
        {
            if (IsDisposed || !IsHandleCreated) return;
            BeginInvoke(new Action(delegate
            {
                SetText((_voiceBase + text).TrimStart());
            }));
        }

        void OnVoiceFailed(string message)
        {
            if (IsDisposed || !IsHandleCreated) return;
            BeginInvoke(new Action(delegate
            {
                ShowError(message);
                _mic.Text = _loc("语音", "Voice");
                _mic.Invalidate();
            }));
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _hintTimer.Stop();
            _hintTimer.Dispose();
            _deactivateTimer.Stop();
            _deactivateTimer.Dispose();
            if (_voice != null)
            {
                _voice.Dispose();
                _voice = null;
            }
            base.OnFormClosed(e);
        }
    }

    // 圆角按钮（WinForms 原生按钮画不出来，自己画）
    sealed class PillButton : Button
    {
        public Color BorderColor = Color.Transparent;
        public bool Border;

        readonly Color _fill;

        public PillButton(string text, Color fill, Color fore)
        {
            Text = text;
            _fill = fill;
            ForeColor = fore;
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            SetStyle(ControlStyles.UserPaint, true);
            SetStyle(ControlStyles.AllPaintingInWmPaint, true);
            Font = new Font("Microsoft YaHei UI", 8.5f, FontStyle.Regular, GraphicsUnit.Point);
            Cursor = Cursors.Hand;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle rect = new Rectangle(0, 0, Width - 1, Height - 1);
            using (GraphicsPath path = ComposeWindow_RoundRect(rect, 6))
            {
                Color fill = _fill;
                if (_fill.A > 0 && _fill != Color.Transparent)
                {
                    if (ClientRectangle.Contains(PointToClient(Cursor.Position)))
                    {
                        fill = ControlPaint.Light(_fill, 0.12f);
                    }
                    using (SolidBrush brush = new SolidBrush(fill))
                    {
                        e.Graphics.FillPath(brush, path);
                    }
                }
                if (Border)
                {
                    using (Pen pen = new Pen(BorderColor, 1f))
                    {
                        e.Graphics.DrawPath(pen, path);
                    }
                }
            }
            TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, ForeColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }

        static GraphicsPath ComposeWindow_RoundRect(Rectangle rect, int radius)
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
    }

    // 安装宠物用的单行输入框
    sealed class InputDialog : Form
    {
        readonly TextBox _box;

        public string Value { get { return _box.Text; } }

        public InputDialog(string title, string prompt)
        {
            Text = title;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterScreen;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;
            TopMost = true;
            ClientSize = new Size(520, 150);
            Font = new Font("Microsoft YaHei UI", 9f, FontStyle.Regular, GraphicsUnit.Point);

            Label label = new Label();
            label.Text = prompt;
            label.AutoSize = false;
            label.SetBounds(16, 14, 488, 40);
            Controls.Add(label);

            _box = new TextBox();
            _box.SetBounds(16, 56, 488, 26);
            Controls.Add(_box);

            Button ok = new Button();
            ok.Text = "确定";
            ok.SetBounds(336, 96, 78, 28);
            ok.DialogResult = DialogResult.OK;
            Controls.Add(ok);

            Button cancel = new Button();
            cancel.Text = "取消";
            cancel.SetBounds(426, 96, 78, 28);
            cancel.DialogResult = DialogResult.Cancel;
            Controls.Add(cancel);

            AcceptButton = ok;
            CancelButton = cancel;
            Shown += delegate { _box.Focus(); };
        }
    }
}