// dsh-pet · 独立的文字和语音草稿窗
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
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

        public bool Composing { get; private set; }

        protected override void WndProc(ref Message message)
        {
            if (message.Msg == WmImeStartComposition) Composing = true;
            base.WndProc(ref message);
            if (message.Msg == WmImeEndComposition) Composing = false;
        }

        protected override void OnLostFocus(EventArgs e)
        {
            Composing = false;
            base.OnLostFocus(e);
        }
    }

    sealed class ComposeWindow : Form
    {
        readonly Func<string, string, string> _loc;
        readonly Func<Task<ComposeTarget[]>> _load;
        readonly Func<string, string, string, Task<ComposeSubmitResult>> _submit;
        readonly ComposeTextBox _input;
        readonly ComboBox _targets;
        readonly Label _targetLabel;
        readonly Label _placeholder;
        readonly Label _feedback;
        readonly PillButton _refresh;
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
        string _requestId;
        string _requestText;
        string _requestTarget;
        bool _loadingTargets;
        bool _sending;
        bool _uncertain;
        bool _shuttingDown;
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
            _scale = Math.Max(1f, bounds.Height / 224f);

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

            _targetLabel = NewLabel(_dim);
            Controls.Add(_targetLabel);
            _refresh = new PillButton(loc("刷新", "Refresh"), _panel, _text);
            _refresh.Border = true;
            _refresh.BorderColor = border;
            _refresh.AccessibleName = loc("刷新会话列表", "Refresh session list");
            _refresh.Click += delegate { LoadTargets(); };
            Controls.Add(_refresh);

            _targets = new ComboBox();
            _targets.DropDownStyle = ComboBoxStyle.DropDownList;
            _targets.BackColor = _panel;
            _targets.ForeColor = _text;
            _targets.MaxDropDownItems = 8;
            _targets.AccessibleName = loc("发送到 DSH 会话", "Destination DSH session");
            _targets.SelectedIndexChanged += delegate { UpdateActions(); };
            _targets.DropDownClosed += delegate
            {
                if (!ContainsFocus && !_closed) _deactivateTimer.Start();
            };
            Controls.Add(_targets);

            _input = new ComposeTextBox();
            _input.Multiline = true;
            _input.AcceptsReturn = true;
            _input.BorderStyle = BorderStyle.None;
            _input.BackColor = _panel;
            _input.ForeColor = _text;
            _input.ScrollBars = ScrollBars.Vertical;
            _input.MaxLength = 6000;
            _input.AccessibleName = loc("消息草稿", "Message draft");
            _input.KeyDown += OnInputKeyDown;
            _input.TextChanged += delegate
            {
                _placeholder.Visible = _input.TextLength == 0;
                UpdateActions();
            };
            Controls.Add(_input);

            _placeholder = NewLabel(_dim);
            _placeholder.Text = loc("给智能伙伴写点什么…", "Message your companion…");
            _placeholder.AccessibleRole = AccessibleRole.StaticText;
            _placeholder.Click += delegate { _input.Focus(); };
            Controls.Add(_placeholder);
            _placeholder.BringToFront();

            _feedback = NewLabel(_dim);
            _feedback.TextAlign = ContentAlignment.MiddleLeft;
            Controls.Add(_feedback);

            _send = new PillButton(loc("发送", "Send"), accent, Color.White);
            _send.Click += delegate { Submit(); };
            Controls.Add(_send);

            _mic = new PillButton(loc("听写", "Dictate"), _panel, _text);
            _mic.Border = true;
            _mic.BorderColor = border;
            _mic.Click += delegate
            {
                if (_voice == null) StartVoice();
                else StopVoice();
            };
            Controls.Add(_mic);

            _deactivateTimer = new Timer();
            _deactivateTimer.Interval = 160;
            _deactivateTimer.Tick += delegate
            {
                _deactivateTimer.Stop();
                if (!ContainsFocus && !_targets.DroppedDown && _input.TextLength == 0 &&
                    !_sending && _voice == null) Close();
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
            int pad = S(12);
            int width = Math.Max(1, ClientSize.Width - pad * 2);
            int buttonH = S(28);
            int buttonW = S(76);
            int footerY = ClientSize.Height - pad - buttonH;
            int statusH = S(39);
            _targetLabel.SetBounds(pad, pad + S(3), Math.Max(1, width - buttonW - S(8)), S(20));
            _refresh.SetBounds(ClientSize.Width - pad - buttonW, pad, buttonW, S(23));
            _targets.SetBounds(pad, _refresh.Bottom + S(7), width, _targets.PreferredHeight);
            int inputY = _targets.Bottom + S(12);
            int inputH = Math.Max(S(22), footerY - inputY - statusH);
            _input.SetBounds(pad + S(2), inputY, Math.Max(1, width - S(4)), inputH);
            _placeholder.SetBounds(_input.Left + 1, _input.Top + 1, Math.Max(1, width - S(20)), S(23));
            _feedback.SetBounds(pad, footerY - statusH, width, statusH - S(3));
            _mic.SetBounds(ClientSize.Width - pad - buttonW * 2 - S(6), footerY, buttonW, buttonH);
            _send.SetBounds(ClientSize.Width - pad - buttonW, footerY, buttonW, buttonH);

            if (Width < 2 || Height < 2) return;
            using (GraphicsPath path = PillButton.RoundRect(new Rectangle(0, 0, Width, Height), S(12)))
            {
                Region previous = Region;
                Region = new Region(path);
                if (previous != null) previous.Dispose();
            }
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            LayoutChildren();
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Activate();
            _input.Focus();
            LoadTargets();
            if (_startWithVoice) StartVoice();
        }

        protected override void OnDeactivate(EventArgs e)
        {
            base.OnDeactivate(e);
            if (_deactivateTimer != null && !_closed) _deactivateTimer.Start();
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
                int index = _targets.SelectedIndex;
                return index >= 0 && index < _choices.Length ? _choices[index] : null;
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
                        _targets.Items.Add(choice.Title + (choice.Recent ? _loc("（最近活动）", " (recent)") : ""));
                        if (previousId != null ? previousId == choice.Id : choice.Recent && selected < 0) selected = i;
                    }
                    if (previousId == null && selected < 0 && _choices.Length == 1) selected = 0;
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
            _targetLabel.Text = _loadingTargets ? _loc("正在读取 DSH 会话…", "Loading DSH sessions…") :
                _choices.Length == 0 ? _loc("没有可用的 DSH 会话", "No available DSH sessions") :
                target == null ? _loc("请选择发送会话", "Choose a session") : _loc("发送到 DSH 会话", "Send to DSH session");
            _targets.Enabled = !_sending && !_loadingTargets && _choices.Length != 0;
            _refresh.Enabled = !_sending && !_loadingTargets;
            _input.ReadOnly = _sending;
            _mic.Enabled = !_sending && !_startingVoice && !_stoppingVoice;
            _mic.Text = _startingVoice ? _loc("连接中…", "Starting…") :
                _stoppingVoice ? _loc("转写中…", "Finishing…") :
                _voice != null ? _loc("停止", "Stop") : _loc("听写", "Dictate");
            _send.Enabled = !_loadingTargets && !_sending && target != null && _voice == null &&
                !String.IsNullOrWhiteSpace(_input.Text);
            _send.Text = _sending ? _loc("发送中…", "Sending…") : _loc("发送", "Send");
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
                Close();
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
                _requestId = Guid.NewGuid().ToString("N");
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
            _input.Focus();
        }

        void Feedback(string message, bool warning)
        {
            _feedback.ForeColor = warning ? _warning : _dim;
            _feedback.Text = message;
            _feedback.AccessibleName = message;
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
            Feedback(preview.Length == 0 ? status : status + Environment.NewLine + preview, false);
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
            string prefix = _input.TextLength == 0 ? "" : Environment.NewLine;
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
                Task previous = _voiceCleanup;
                _voiceCleanup = Task.Run(async delegate
                {
                    await previous.ConfigureAwait(false);
                    try { voice.Dispose(); }
                    catch (Exception error) { System.Diagnostics.Debug.WriteLine(error.Message); }
                });
            }
        }

        public void CloseForShutdown()
        {
            _shuttingDown = true;
            Close();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (_sending && !_shuttingDown && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Feedback(_loc("发送仍在进行，请稍候。", "The message is still being sent."), false);
            }
            base.OnFormClosing(e);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _closed = true;
            _deactivateTimer.Dispose();
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