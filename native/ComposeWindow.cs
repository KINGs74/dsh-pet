// dsh-pet · 独立的文字和语音草稿窗
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace DshPet
{
    sealed class ComposeTarget
    {
        public readonly string Id;
        public readonly string Title;
        public readonly bool Recent;

        public ComposeTarget(string id, string title, bool recent)
        {
            Id = id;
            Title = title;
            Recent = recent;
        }
    }

    sealed class ComposeSubmitResult
    {
        public readonly bool Ok;
        public readonly string Message;
        public readonly bool Uncertain;

        public ComposeSubmitResult(bool ok, string message, bool uncertain = false)
        {
            Ok = ok;
            Message = message;
            Uncertain = uncertain;
        }
    }

    sealed class ComposeTextBox : TextBox
    {
        const int WmImeStartComposition = 0x010d;
        const int WmImeEndComposition = 0x010e;
        const int EmSetCueBanner = 0x1501;

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr SendMessage(IntPtr hwnd, int message, IntPtr wParam, string lParam);

        readonly string _cue;

        public ComposeTextBox(string cue)
        {
            _cue = cue;
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            SendMessage(Handle, EmSetCueBanner, new IntPtr(1), _cue);
        }

        public bool Composing { get; private set; }

        protected override void WndProc(ref Message message)
        {
            if (message.Msg == WmImeStartComposition) Composing = true;
            base.WndProc(ref message);
            if (message.Msg == WmImeEndComposition) Composing = false;
        }

        protected override bool IsInputKey(Keys keyData)
        {
            return keyData == Keys.Enter || base.IsInputKey(keyData);
        }

        protected override void OnLostFocus(EventArgs e)
        {
            Composing = false;
            base.OnLostFocus(e);
        }
    }

    sealed class ComposeWindow : Form
    {
        public const int BaseHeight = 44;
        const int FeedbackHeight = 30;
        public event EventHandler RecordingChanged;
        public bool Recording { get { return _voice != null; } }

        readonly Func<string, string, string> _loc;
        readonly Func<Task<ComposeTarget[]>> _load;
        readonly Func<string, string, string, Task<ComposeSubmitResult>> _submit;
        readonly ComposeTextBox _input;
        readonly ComboBox _targets;
        readonly ToolTip _tips = new ToolTip();
        readonly string _refreshLabel;
        readonly Label _feedback;
        readonly PillButton _close;
        readonly PillButton _send;
        readonly PillButton _mic;
        readonly Timer _deactivateTimer;
        readonly float _scale;
        readonly bool _startWithVoice;
        readonly bool _english;
        readonly Color _panel;
        readonly Color _text;
        readonly Color _dim;
        readonly Color _warning;

        ComposeTarget[] _choices = new ComposeTarget[0];
        int _selectedTargetIndex = -1;
        string _requestId;
        string _requestText;
        string _requestTarget;
        bool _loadingTargets;
        bool _sending;
        bool _uncertain;
        bool _closingPermanently;
        bool _closed;

        VoiceInput _voice;
        Task _voiceCleanup = Task.FromResult(0);
        StringBuilder _recognized;
        string _recentVoiceSegment;
        bool _startingVoice;
        bool _stoppingVoice;
        string _speechLanguage;

        public ComposeWindow(Rectangle bounds, Func<string, string, string> loc,
            Func<Task<ComposeTarget[]>> loadTargets,
            Func<string, string, string, Task<ComposeSubmitResult>> submit, bool startVoice)
        {
            _loc = loc;
            _load = loadTargets;
            _submit = submit;
            _startWithVoice = startVoice;
            _english = loc("zh", "en") == "en";
            _scale = Math.Max(1f, bounds.Height / (float)BaseHeight);
            _refreshLabel = loc("刷新会话列表", "Refresh sessions");

            Text = loc("宠物草稿", "Pet draft");
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
            DoubleBuffered = true;
            Font = new Font("Microsoft YaHei UI", 9f);

            bool dark = IsDarkTheme();
            _panel = dark ? Color.FromArgb(32, 34, 41) : Color.FromArgb(253, 253, 255);
            _text = dark ? Color.FromArgb(232, 235, 243) : Color.FromArgb(32, 36, 48);
            _dim = dark ? Color.FromArgb(171, 175, 190) : Color.FromArgb(94, 101, 119);
            _warning = dark ? Color.FromArgb(255, 174, 174) : Color.FromArgb(169, 47, 47);
            Color border = dark ? Color.FromArgb(83, 88, 105) : Color.FromArgb(210, 216, 231);
            Color accent = dark ? Color.FromArgb(57, 101, 215) : Color.FromArgb(40, 89, 205);
            BackColor = _panel;


            _close = new PillButton("×", _panel, _text);
            _close.Font = new Font(Font.FontFamily, 12f);
            _close.AccessibleName = loc("关闭输入框", "Close composer");
            _tips.SetToolTip(_close, _close.AccessibleName);
            _close.Click += delegate { Dismiss(); };
            Controls.Add(_close);

            _targets = new ComboBox();
            _targets.DropDownStyle = ComboBoxStyle.DropDownList;
            _targets.BackColor = _panel;
            _targets.ForeColor = _text;
            _targets.MaxDropDownItems = 8;
            _targets.DrawMode = DrawMode.OwnerDrawFixed;
            _targets.DrawItem += DrawTarget;
            _targets.Items.Add(_refreshLabel);
            _targets.SelectionChangeCommitted += delegate
            {
                if (_targets.SelectedIndex == _choices.Length)
                {
                    _targets.SelectedIndex = _selectedTargetIndex;
                    LoadTargets();
                    return;
                }
                _selectedTargetIndex = _targets.SelectedIndex;
                UpdateActions();
                _input.Focus();
            };
            _targets.DropDownClosed += delegate
            {
                if (!ContainsFocus && !_closed) _deactivateTimer.Start();
            };
            Controls.Add(_targets);

            _input = new ComposeTextBox(loc("写点什么…", "Message…"));
            _input.BorderStyle = BorderStyle.None;
            _input.BackColor = _panel;
            _input.ForeColor = _text;
            _input.MaxLength = 6000;
            _input.AccessibleName = loc("消息草稿", "Message draft");
            _input.KeyDown += OnInputKeyDown;
            _input.TextChanged += delegate
            {
                if (_feedback != null && _feedback.Text.Length > 0 && !Recording && !_sending && !_uncertain)
                    Feedback("", false);
                UpdateActions();
            };
            Controls.Add(_input);

            _feedback = NewLabel(_dim);
            _feedback.TextAlign = ContentAlignment.MiddleLeft;
            _feedback.Visible = false;
            Controls.Add(_feedback);

            _send = new PillButton(loc("发送", "Send"), accent, Color.White);
            _send.Click += delegate { Submit(); };
            Controls.Add(_send);

            _mic = new PillButton(loc("语音", "Voice"), _panel, _text);
            _mic.Border = true;
            _mic.BorderColor = border;
            _mic.Click += delegate { ToggleVoice(); };
            Controls.Add(_mic);

            _deactivateTimer = new Timer();
            _deactivateTimer.Interval = 160;
            _deactivateTimer.Tick += delegate
            {
                _deactivateTimer.Stop();
                if (Visible && !ContainsFocus && !_targets.DroppedDown && _input.TextLength == 0 &&
                    !_sending && _voice == null) Dismiss();
            };

            Bounds = bounds;
            UpdateActions();
        }

        Label NewLabel(Color foreground)
        {
            Label label = new Label();
            label.AutoEllipsis = true;
            label.BackColor = _panel;
            label.ForeColor = foreground;
            return label;
        }

        static bool IsDarkTheme()
        {
            try
            {
                object value = Microsoft.Win32.Registry.GetValue(
                    @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                    "AppsUseLightTheme", 1);
                return value is int && (int)value == 0;
            }
            catch { return false; }
        }

        int S(int value)
        {
            return (int)Math.Round(value * _scale);
        }

        void LayoutChildren()
        {
            if (_mic == null) return;
            int pad = S(7);
            int gap = S(3);
            int rowH = S(30);
            int rowY = (S(BaseHeight) - rowH) / 2;
            int closeW = S(25);
            int buttonW = S(45);
            int comboH = _targets.PreferredHeight;
            _close.SetBounds(ClientSize.Width - pad - closeW, rowY, closeW, rowH);
            _send.SetBounds(_close.Left - gap - buttonW, rowY, buttonW, rowH);
            _mic.SetBounds(_send.Left - gap - buttonW, rowY, buttonW, rowH);
            _targets.SetBounds(pad, rowY + (rowH - comboH) / 2, S(91), comboH);
            _targets.DropDownWidth = Math.Max(_targets.Width, S(330));
            int inputX = _targets.Right + S(7);
            int inputH = _input.PreferredHeight;
            _input.SetBounds(inputX, rowY + (rowH - inputH) / 2,
                Math.Max(1, _mic.Left - gap - inputX), inputH);
            _feedback.SetBounds(pad, S(BaseHeight), Math.Max(1, ClientSize.Width - pad * 2), S(FeedbackHeight - 4));

            if (Width < 2 || Height < 2) return;
            using (GraphicsPath path = PillButton.RoundRect(new Rectangle(0, 0, Width, Height), S(12)))
            {
                Region previous = Region;
                Region = new Region(path);
                if (previous != null) previous.Dispose();
            }
        }

        void DrawTarget(object sender, DrawItemEventArgs e)
        {
            if (e.Bounds.Width <= 0 || e.Bounds.Height <= 0) return;
            bool edit = (e.State & DrawItemState.ComboBoxEdit) != 0;
            bool highlighted = !edit && (e.State & DrawItemState.Selected) != 0;
            Color background = highlighted ? SystemColors.Highlight : _panel;
            Color foreground = highlighted ? SystemColors.HighlightText : _text;
            string title = e.Index < 0 ? TargetPlaceholder() :
                e.Index < _choices.Length ? TargetTitle(_choices[e.Index], !edit) : _refreshLabel;
            using (SolidBrush brush = new SolidBrush(background)) e.Graphics.FillRectangle(brush, e.Bounds);
            Rectangle bounds = Rectangle.Inflate(e.Bounds, -S(4), 0);
            TextRenderer.DrawText(e.Graphics, title, e.Font, bounds, foreground,
                TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.VerticalCenter |
                TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
            if (highlighted) e.DrawFocusRectangle();
        }

        string TargetPlaceholder()
        {
            return _loadingTargets ? _loc("读取会话…", "Loading…") :
                _choices.Length == 0 ? _loc("无可用会话", "No sessions") : _loc("选择会话", "Choose session");
        }

        string TargetTitle(ComposeTarget target, bool showRecent)
        {
            return target.Title + (target.Recent && showRecent ? _loc("（最近活动）", " (recent)") : "");
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            LayoutChildren();
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            FocusDraft();
            LoadTargets();
            if (_startWithVoice) StartVoice();
        }

        public void FocusDraft()
        {
            if (_closed || !Visible) return;
            Activate();
            _input.Focus();
        }

        protected override void OnDeactivate(EventArgs e)
        {
            base.OnDeactivate(e);
            if (_deactivateTimer != null && !_closed && Visible) _deactivateTimer.Start();
        }

        protected override void OnActivated(EventArgs e)
        {
            base.OnActivated(e);
            if (_deactivateTimer != null) _deactivateTimer.Stop();
        }

        ComposeTarget SelectedTarget
        {
            get
            {
                return _selectedTargetIndex >= 0 && _selectedTargetIndex < _choices.Length ?
                    _choices[_selectedTargetIndex] : null;
            }
        }

        async void LoadTargets()
        {
            if (_loadingTargets || _sending || _closed) return;
            string previousId = SelectedTarget == null ? null : SelectedTarget.Id;
            _loadingTargets = true;
            UpdateActions();
            try
            {
                ComposeTarget[] choices = await _load();
                if (_closed) return;
                _targets.BeginUpdate();
                try
                {
                    _choices = choices ?? new ComposeTarget[0];
                    _targets.Items.Clear();
                    int selected = -1;
                    for (int i = 0; i < _choices.Length; i++)
                    {
                        ComposeTarget choice = _choices[i];
                        _targets.Items.Add(TargetTitle(choice, true));
                        if (previousId != null ? previousId == choice.Id : choice.Recent && selected < 0) selected = i;
                    }
                    if (previousId == null && selected < 0 && _choices.Length == 1) selected = 0;
                    _targets.Items.Add(_refreshLabel);
                    _selectedTargetIndex = selected;
                    _targets.SelectedIndex = selected;
                }
                finally { _targets.EndUpdate(); }
            }
            catch (Exception)
            {
                if (!_closed) Feedback(_loc("读取会话失败，请刷新后重试。", "Could not load sessions. Refresh to retry."), true);
            }
            finally
            {
                _loadingTargets = false;
                if (!_closed) UpdateActions();
            }
        }

        void UpdateActions()
        {
            if (_mic == null || _closed) return;
            ComposeTarget target = SelectedTarget;
            string destination = target == null ? TargetPlaceholder() :
                _loc("发送到：", "Send to: ") + TargetTitle(target, true);
            _targets.AccessibleName = destination;
            _tips.SetToolTip(_targets, destination);
            _targets.Enabled = !_sending && !_loadingTargets;
            _targets.Invalidate();
            _input.ReadOnly = _sending;
            _mic.Enabled = !_sending && !_stoppingVoice;
            _mic.AccessibleName = _stoppingVoice ? _loc("正在转写", "Finishing dictation") :
                Recording ? _loc("停止听写", "Stop dictation") : _loc("开始听写", "Start dictation");
            _tips.SetToolTip(_mic, _mic.AccessibleName);
            _mic.Text = Recording ? _loc("停止", "Stop") : _loc("语音", "Voice");
            _send.Enabled = !_loadingTargets && !_sending && target != null && _voice == null &&
                !String.IsNullOrWhiteSpace(_input.Text);
            _send.Text = _sending ? "…" : _loc("发送", "Send");
            _send.AccessibleName = _sending ? _loc("发送中", "Sending") : _loc("发送", "Send");
        }

        void OnInputKeyDown(object sender, KeyEventArgs e)
        {
            if (_input.Composing || e.KeyCode == Keys.ProcessKey) return;
            if (e.KeyCode == Keys.Enter && !e.Shift && !e.Control && !e.Alt)
            {
                e.SuppressKeyPress = true;
                Submit();
            }
        }

        protected override bool ProcessCmdKey(ref Message message, Keys keyData)
        {
            if (keyData == Keys.Escape && !_targets.DroppedDown && !_input.Composing)
            {
                Dismiss();
                return true;
            }
            return base.ProcessCmdKey(ref message, keyData);
        }

        async void Submit()
        {
            if (!_send.Enabled || _input.Composing) return;
            ComposeTarget target = SelectedTarget;
            string draft = _input.Text.Trim();
            if (_uncertain && (_requestText != draft || _requestTarget != target.Id))
            {
                DialogResult confirm = MessageBox.Show(this,
                    _loc("上一次发送结果待确认。这次修改后的草稿会作为一条新消息发送。继续吗？",
                        "The previous send is unconfirmed. The changed draft will be sent as a new message. Continue?"),
                    Text, MessageBoxButtons.OKCancel, MessageBoxIcon.Information, MessageBoxDefaultButton.Button2);
                if (confirm != DialogResult.OK) return;
            }
            if (_requestId == null || _requestTarget != target.Id || _requestText != draft)
            {
                _requestId = Guid.NewGuid().ToString("D");
                _requestTarget = target.Id;
                _requestText = draft;
                _uncertain = false;
            }
            _sending = true;
            UpdateActions();
            Feedback(_loc("正在提交给 DSH…", "Submitting to DSH…"), false);
            ComposeSubmitResult result;
            try
            {
                result = await _submit(target.Id, draft, _requestId);
            }
            catch (Exception)
            {
                result = new ComposeSubmitResult(false, "", true);
            }
            if (_closed) return;
            _sending = false;
            UpdateActions();
            if (result != null && result.Ok)
            {
                _closingPermanently = true;
                Close();
                return;
            }
            _uncertain = _uncertain || result == null || result.Uncertain;
            string message = result == null ? "" : result.Message;
            if (String.IsNullOrWhiteSpace(message))
            {
                message = _uncertain
                    ? _loc("发送结果待确认。可重试同一份草稿。", "Send unconfirmed. You can retry this draft.")
                    : _loc("发送失败，请重试。", "Send failed. Please retry.");
            }
            Feedback(message, true);
            if (Visible && ContainsFocus) _input.Focus();
        }

        void Feedback(string message, bool warning)
        {
            _feedback.ForeColor = warning ? _warning : _dim;
            _feedback.Text = message;
            _feedback.AccessibleName = message;
            _tips.SetToolTip(_feedback, message);
            bool hasFeedback = !String.IsNullOrWhiteSpace(message);
            _feedback.Visible = hasFeedback;
            int height = S(BaseHeight + (hasFeedback ? FeedbackHeight : 0));
            if (Height != height) Height = height;
        }

        public void ToggleVoice()
        {
            if (_closed || _sending || _stoppingVoice) return;
            if (!Recording) StartVoice();
            else if (_startingVoice) FinishVoice("");
            else StopVoice();
        }

        void RaiseRecordingChanged()
        {
            EventHandler handler = RecordingChanged;
            if (handler != null) handler(this, EventArgs.Empty);
        }

        public async void StartVoice()
        {
            if (_closed || _sending || _voice != null) return;
            VoiceInput voice = new VoiceInput(_english);
            _voice = voice;
            _recognized = new StringBuilder();
            _recentVoiceSegment = "";
            _startingVoice = true;
            _speechLanguage = "";
            voice.Partial += delegate(string text) { DispatchVoice(voice, delegate { ShowVoicePreview(text); }); };
            voice.Final += delegate(string text) { DispatchVoice(voice, delegate { AcceptVoiceSegment(text); }); };
            voice.Completed += delegate(string error) { DispatchVoice(voice, delegate { FinishVoice(error); }); };
            UpdateActions();
            Feedback(_loc("正在打开麦克风…", "Opening microphone…"), false);
            RaiseRecordingChanged();
            try
            {
                await _voiceCleanup;
                if (_closed || _voice != voice) return;
                string language = await Task.Run(new Func<string>(voice.Start));
                if (_closed || _voice != voice) return;
                _speechLanguage = language;
                _startingVoice = false;
                UpdateActions();
                ShowVoicePreview("");
            }
            catch (Exception error)
            {
                if (!_closed && _voice == voice)
                    FinishVoice(_loc("听写不可用：", "Dictation unavailable: ") + error.Message);
            }
        }

        async void StopVoice()
        {
            VoiceInput voice = _voice;
            if (voice == null || _startingVoice || _stoppingVoice) return;
            _stoppingVoice = true;
            UpdateActions();
            ShowVoicePreview("");
            try { await Task.Run(new Action(voice.Stop)); }
            catch (Exception error)
            {
                if (!_closed && _voice == voice)
                    FinishVoice(_loc("停止听写失败：", "Could not stop dictation: ") + error.Message);
            }
        }

        void DispatchVoice(VoiceInput voice, Action callback)
        {
            try
            {
                BeginInvoke(new Action(delegate
                {
                    if (!_closed && _voice == voice) callback();
                }));
            }
            catch (InvalidOperationException) { }
        }

        void ShowVoicePreview(string partial)
        {
            string preview = partial.Length == 0 ? _recentVoiceSegment : partial;
            string status = _stoppingVoice ? _loc("正在完成转写", "Finishing dictation") :
                _loc("正在听写", "Listening") + (_speechLanguage.Length == 0 ? "" : " (" + _speechLanguage + ")");
            Feedback(preview.Length == 0 ? status : status + " · " + preview, false);
        }

        void AcceptVoiceSegment(string text)
        {
            _recentVoiceSegment = text;
            int remaining = _input.MaxLength + 1 - _recognized.Length;
            if (remaining > 0)
            {
                if (_recognized.Length > 0)
                {
                    _recognized.Append(' ');
                    remaining--;
                }
                _recognized.Append(text, 0, Math.Min(text.Length, remaining));
            }
            ShowVoicePreview("");
        }

        void FinishVoice(string error)
        {
            string recognized = _recognized.ToString();
            ReleaseVoice();
            string prefix = _input.TextLength == 0 ? "" : " ";
            int available = Math.Max(0, _input.MaxLength - _input.TextLength - prefix.Length);
            int insertLength = Math.Min(recognized.Length, available);
            if (insertLength > 0 && Char.IsHighSurrogate(recognized[insertLength - 1])) insertLength--;
            if (insertLength > 0)
            {
                _input.AppendText(prefix + recognized.Substring(0, insertLength));
                _input.SelectionStart = _input.TextLength;
                if (ContainsFocus) _input.Focus();
            }
            UpdateActions();
            if (!String.IsNullOrEmpty(error)) Feedback(error, true);
            else if (recognized.Length > insertLength)
                Feedback(_loc("草稿已达长度上限。", "The draft has reached its length limit."), true);
            else if (recognized.Length == 0)
                Feedback(_loc("没有识别到语音。", "No speech was recognized."), false);
            else Feedback(_loc("已加入草稿，可编辑后发送。", "Added to the draft. Review before sending."), false);
        }

        void ReleaseVoice()
        {
            VoiceInput voice = _voice;
            _voice = null;
            _recognized = null;
            _startingVoice = false;
            _stoppingVoice = false;
            if (voice != null)
            {
                RaiseRecordingChanged();
                Task previous = _voiceCleanup;
                _voiceCleanup = Task.Run(async delegate
                {
                    await previous.ConfigureAwait(false);
                    try { voice.Dispose(); }
                    catch (Exception error) { System.Diagnostics.Debug.WriteLine(error.Message); }
                });
            }
        }

        public void Dismiss()
        {
            if (_closed) return;
            Close();
        }

        public void CloseForShutdown()
        {
            _closingPermanently = true;
            Close();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!_closingPermanently && e.CloseReason == CloseReason.UserClosing &&
                (_sending || _uncertain || _input.TextLength > 0))
            {
                e.Cancel = true;
                _targets.DroppedDown = false;
                Hide();
                _deactivateTimer.Stop();
                if (_voice != null)
                {
                    ReleaseVoice();
                    UpdateActions();
                    if (!_uncertain) Feedback(_loc("听写已取消，草稿已保留。", "Dictation canceled. Your draft is still here."), false);
                }
            }
            base.OnFormClosing(e);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _closed = true;
            _deactivateTimer.Dispose();
            _tips.Dispose();
            ReleaseVoice();
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
            using (GraphicsPath path = RoundRect(rect, 6))
            {
                Color fill = Enabled ? _fill : ControlPaint.Light(_fill, 0.45f);
                if (_fill.A > 0 && _fill != Color.Transparent)
                {
                    if (Enabled && ClientRectangle.Contains(PointToClient(Cursor.Position)))
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
            TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, Enabled ? ForeColor : SystemColors.GrayText,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }

        internal static GraphicsPath RoundRect(Rectangle rect, int radius)
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