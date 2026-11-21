// PetOverlay.cs — DSH 宠物浮层（独立顶层窗口，C# 5 / .NET Framework 4.8）
//
// 设计要点：
//   * 无边框 + WS_EX_LAYERED + UpdateLayeredWindow：逐像素透明、无锯齿；
//   * WS_EX_TOOLWINDOW + ShowInTaskbar=false：不进任务栏、不占 Alt-Tab；
//   * WM_NCHITTEST：只有宠物不透明像素/气泡区域接收鼠标，其余穿透桌面；
//   * 拖动直接用 SetWindowPos，坐标基于虚拟屏 → 可拖到屏幕任意位置、跨显示器；
//   * 状态来自 DSH Host 的 /x-dsh-pet/state（行协议，值 URL 编码）。
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
using System.Text;
using System.Threading;
using System.Windows.Forms;

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        Opts opts = Opts.Parse(args);
        Log.Init(opts.LogPath);

        bool created;
        Mutex mutex;
        try { mutex = new Mutex(true, "Local\\DshPetOverlay.SingleInstance", out created); }
        catch { mutex = null; created = true; }
        if (!created)
        {
            Log.W("another instance already running; exit");
            return 0;
        }

        try
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new PetForm(opts));
            return 0;
        }
        catch (Exception ex)
        {
            Log.W("fatal: " + ex.ToString());
            return 1;
        }
        finally
        {
            if (mutex != null) mutex.ReleaseMutex();
        }
    }
}

sealed class Opts
{
    public string Host = "";
    public string Token = "";
    public string LogPath = "";
    // 主机的路由前缀；正常情况下永远是 /x-dsh-pet，重载撞车时主机会退到 /x-dsh-pet-2
    public string Prefix = "/x-dsh-pet";
    public bool Dump;

    public static Opts Parse(string[] a)
    {
        Opts o = new Opts();
        if (a == null) return o;
        for (int i = 0; i < a.Length; i++)
        {
            string k = a[i];
            string v = (i + 1 < a.Length) ? a[i + 1] : "";
            if (k == "--host") { o.Host = v; i++; }
            else if (k == "--token") { o.Token = v; i++; }
            else if (k == "--log") { o.LogPath = v; i++; }
            else if (k == "--prefix") { o.Prefix = v; i++; }
            else if (k == "--dump") { o.Dump = true; }
        }
        if (o.LogPath.Length == 0)
            o.LogPath = Path.Combine(Path.GetTempPath(), "dsh-pet-overlay.log");
        o.Host = o.Host.TrimEnd('/');
        if (o.Prefix.Length == 0) o.Prefix = "/x-dsh-pet";
        if (!o.Prefix.StartsWith("/")) o.Prefix = "/" + o.Prefix;
        o.Prefix = o.Prefix.TrimEnd('/');
        return o;
    }
}

static class Log
{
    static string _path = "";
    static readonly object _lock = new object();

    public static void Init(string path)
    {
        _path = path;
        try
        {
            if (File.Exists(path) && new FileInfo(path).Length > 512 * 1024)
                File.Delete(path);
        }
        catch { }
    }

    public static void W(string msg)
    {
        try
        {
            lock (_lock)
                File.AppendAllText(_path, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "  " + msg + "\r\n", Encoding.UTF8);
        }
        catch { }
    }
}

static class Http
{
    public static string Get(string url, string token)
    {
        HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
        req.Timeout = 5000;
        req.ReadWriteTimeout = 5000;
        req.Proxy = null;
        req.KeepAlive = false;
        if (token != null) req.Headers["X-Pet-Token"] = token;
        using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
        using (StreamReader sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
            return sr.ReadToEnd();
    }

    public static string PostForm(string url, string token, Dictionary<string, string> form)
    {
        return PostForm(url, token, form, 5000);
    }

    /// <summary>
    /// timeoutMs 可调：装宠物要下载 2.4MB 素材，5 秒必然超时，
    /// 用户会看到"安装失败"而其实后台还在装 —— 所以安装用长超时。
    /// </summary>
    public static string PostForm(string url, string token, Dictionary<string, string> form, int timeoutMs)
    {
        StringBuilder sb = new StringBuilder();
        foreach (KeyValuePair<string, string> pair in form)
        {
            if (sb.Length > 0) sb.Append('&');
            sb.Append(Uri.EscapeDataString(pair.Key)).Append('=').Append(Uri.EscapeDataString(pair.Value ?? ""));
        }
        HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
        req.Method = "POST";
        req.Timeout = timeoutMs;
        req.ReadWriteTimeout = timeoutMs;
        req.Proxy = null;
        req.KeepAlive = false;
        req.ContentType = "application/x-www-form-urlencoded";
        if (token != null) req.Headers["X-Pet-Token"] = token;
        byte[] body = Encoding.UTF8.GetBytes(sb.ToString());
        req.ContentLength = body.Length;
        using (Stream s = req.GetRequestStream()) s.Write(body, 0, body.Length);
        using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
        using (StreamReader sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
            return sr.ReadToEnd();
    }
}

sealed class PetState
{
    public string Status = "unknown";
    public string Title = "";
    public string Line1 = "";
    public string Line2 = "";
    public double Elapsed = 0;
    public string AlertId = "";
    public string AlertKind = "";
    public string AlertText = "";
    public string PetId = "";
    public string PetName = "";
    public string PetPath = "";
    public int FrameW = 192;
    public int FrameH = 208;
    public int Cols = 8;
    public int Rows = 11;
    public double Scale = 0.55;
    public bool Bubble = true;
    public bool Sound = true;
    public bool Notify = true;
    public string Lang = "zh";
    public string Toast = "";
    public string Overlay = "ok";
    public int PosX = int.MinValue;
    public int PosY = int.MinValue;
    public List<string> PetIds = new List<string>();
    public List<string> PetNames = new List<string>();

    public static PetState Parse(string text)
    {
        PetState s = new PetState();
        if (text == null) return s;
        string[] lines = text.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].TrimEnd('\r');
            int eq = line.IndexOf('=');
            if (eq <= 0) continue;
            string k = line.Substring(0, eq);
            string v = Uri.UnescapeDataString(line.Substring(eq + 1));
            if (k == "status") s.Status = v;
            else if (k == "title") s.Title = v;
            else if (k == "line1") s.Line1 = v;
            else if (k == "line2") s.Line2 = v;
            else if (k == "elapsed") { double d; if (double.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out d)) s.Elapsed = d; }
            else if (k == "alert.id") s.AlertId = v;
            else if (k == "alert.kind") s.AlertKind = v;
            else if (k == "alert.text") s.AlertText = v;
            else if (k == "pet.id") s.PetId = v;
            else if (k == "pet.name") s.PetName = v;
            else if (k == "pet.path") s.PetPath = v;
            else if (k == "frame.w") { int n; if (int.TryParse(v, out n)) s.FrameW = n; }
            else if (k == "frame.h") { int n; if (int.TryParse(v, out n)) s.FrameH = n; }
            else if (k == "frame.cols") { int n; if (int.TryParse(v, out n)) s.Cols = n; }
            else if (k == "frame.rows") { int n; if (int.TryParse(v, out n)) s.Rows = n; }
            else if (k == "scale") { double d; if (double.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out d)) s.Scale = d; }
            else if (k == "bubble") s.Bubble = v == "1";
            else if (k == "sound") s.Sound = v == "1";
            else if (k == "notify") s.Notify = v == "1";
            else if (k == "lang") s.Lang = v;
            else if (k == "toast") s.Toast = v;
            else if (k == "overlay") s.Overlay = v;
            else if (k == "pos.x") { int n; if (int.TryParse(v, out n)) s.PosX = n; }
            else if (k == "pos.y") { int n; if (int.TryParse(v, out n)) s.PosY = n; }
            else if (k.StartsWith("petlist."))
            {
                string[] parts = k.Split('.');
                int idx;
                if (parts.Length == 3 && int.TryParse(parts[1], out idx))
                {
                    while (s.PetIds.Count <= idx) { s.PetIds.Add(""); s.PetNames.Add(""); }
                    if (parts[2] == "id") s.PetIds[idx] = v;
                    else if (parts[2] == "name") s.PetNames[idx] = v;
                }
            }
        }
        return s;
    }
}

sealed class InputDialog : Form
{
    public string Value = "";
    readonly TextBox _box;

    public InputDialog(string title, string prompt, string initial)
    {
        Text = title;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MinimizeBox = false;
        MaximizeBox = false;
        ClientSize = new Size(440, 140);
        TopMost = true;
        ShowInTaskbar = true;
        Font = new Font("Microsoft YaHei UI", 9f, FontStyle.Regular, GraphicsUnit.Point);

        Label label = new Label();
        label.Text = prompt;
        label.AutoSize = false;
        label.SetBounds(14, 12, 412, 36);
        Controls.Add(label);

        _box = new TextBox();
        _box.Text = initial ?? "";
        _box.SetBounds(14, 52, 412, 24);
        Controls.Add(_box);

        Button ok = new Button();
        ok.Text = "确定";
        ok.SetBounds(266, 94, 76, 28);
        ok.DialogResult = DialogResult.OK;
        Controls.Add(ok);

        Button cancel = new Button();
        cancel.Text = "取消";
        cancel.SetBounds(350, 94, 76, 28);
        cancel.DialogResult = DialogResult.Cancel;
        Controls.Add(cancel);

        AcceptButton = ok;
        CancelButton = cancel;
        Shown += delegate { _box.Focus(); _box.SelectAll(); };
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        base.OnFormClosed(e);
        Value = _box.Text;
    }
}

sealed class PetForm : Form
{
    const int WM_NCHITTEST = 0x0084;
    const int WM_MOUSEACTIVATE = 0x0021;
    const int WM_LBUTTONDOWN = 0x0201;
    const int WM_LBUTTONUP = 0x0202;
    const int WM_LBUTTONDBLCLK = 0x0203;
    const int WM_RBUTTONUP = 0x0205;
    const int WM_MOUSEMOVE = 0x0200;
    const int HTCLIENT = 1;
    const int HTTRANSPARENT = -1;
    const int MA_NOACTIVATE = 3;
    const int PAD = 6;
    const int GAP = 6;
    const int BUBBLE_W = 300;

    static readonly int[] RowFrames = new int[] { 6, 8, 8, 4, 5, 8, 6, 6, 6 };
    static readonly int[] RowFps = new int[] { 6, 14, 14, 6, 8, 8, 5, 8, 5 };

    readonly Opts _opts;
    readonly NotifyIcon _tray;
    readonly System.Windows.Forms.Timer _anim;
    readonly Thread _pollThread;
    readonly Font _titleFont;
    readonly Font _bodyFont;
    readonly Font _smallFont;
    readonly Graphics _measure;
    volatile bool _closing;

    PetState _state = new PetState();
    Image _sheet;
    string _sheetPath = "";
    int _row = 0;
    int _frame = 0;
    DateTime _stateChangedAt = DateTime.UtcNow;
    DateTime _celebrateUntil = DateTime.MinValue;
    DateTime _toastUntil = DateTime.MinValue;
    bool _expanded;
    bool _posInitialized;
    bool _dragCandidate;
    bool _dragging;
    Point _dragStartCursor;
    Point _dragStartWin;
    bool _connectedOnce;
    DateTime _lastOk = DateTime.UtcNow;
    string _menuSig = "";
    bool _welcomeChecked;

    // layered-window plumbing
    IntPtr _screenDc, _memDc, _dib, _oldDib, _bits;
    int _dibW, _dibH;
    byte[] _scratch = new byte[0];
    byte[] _alpha = new byte[0];
    int _dumpCounter;
    Bitmap _compose;
    Graphics _composeG;
    int _winW, _winH;

    public PetForm(Opts opts)
    {
        _opts = opts;
        _titleFont = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Bold, GraphicsUnit.Point);
        _bodyFont = new Font("Microsoft YaHei UI", 9f, FontStyle.Regular, GraphicsUnit.Point);
        _smallFont = new Font("Microsoft YaHei UI", 8f, FontStyle.Regular, GraphicsUnit.Point);
        _measure = Graphics.FromHwnd(IntPtr.Zero);

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        Rectangle wa = Screen.PrimaryScreen.WorkingArea;
        Location = new Point(wa.Right - 200, wa.Bottom - 220);
        Size = new Size(160, 200);
        SetStyle(ControlStyles.Opaque, true);
        SetStyle(ControlStyles.UserMouse, true);

        _anim = new System.Windows.Forms.Timer();
        _anim.Interval = 120;
        _anim.Tick += OnAnimTick;
        _anim.Start();

        _tray = new NotifyIcon();
        _tray.Text = "DSH 智能伙伴 · 左键拖动 / 双击打开 DSH / 右键菜单";
        _tray.Icon = SystemIcons.Application;
        _tray.Visible = true;
        _tray.DoubleClick += delegate { FocusDsh(); };
        // 点通知就等于"我看到了"，免得用户还得回到宠物身上点一下
        _tray.BalloonTipClicked += delegate { PostAck(); };
        RebuildMenu();

        Log.W("overlay start host=" + _opts.Host + " exe=" + Application.ExecutablePath);

        _pollThread = new Thread(PollLoop);
        _pollThread.IsBackground = true;
        _pollThread.Start();
    }

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams cp = base.CreateParams;
            cp.ExStyle |= 0x00080000; // WS_EX_LAYERED
            cp.ExStyle |= 0x00000080; // WS_EX_TOOLWINDOW
            cp.ExStyle |= 0x08000000; // WS_EX_NOACTIVATE
            cp.ExStyle &= ~0x00040000; // no WS_EX_APPWINDOW
            return cp;
        }
    }

    protected override bool ShowWithoutActivation { get { return true; } }

    protected override void OnPaintBackground(PaintEventArgs e) { }

    protected override void OnPaint(PaintEventArgs e) { }

    // ---------------- state polling ----------------

    void PollLoop()
    {
        int failures = 0;
        while (!_closing)
        {
            try
            {
                string body = Http.Get(_opts.Host + _opts.Prefix + "/state", _opts.Token);
                PetState st = PetState.Parse(body);
                failures = 0;
                _lastOk = DateTime.UtcNow;
                try { BeginInvoke((MethodInvoker)delegate { ApplyState(st); }); }
                catch (Exception) { }
            }
            catch (Exception ex)
            {
                failures++;
                if (failures == 1) Log.W("poll failed: " + ex.Message);
                if (failures == 4 && _connectedOnce)
                {
                    // 断线时不要把上一轮的进度定格在那儿继续计时，明确告诉用户"我在重连"
                    try
                    {
                        BeginInvoke((MethodInvoker)delegate
                        {
                            _state.Line1 = _state.Lang == "en" ? "DSH disconnected, reconnecting…" : "DSH 已断开，重连中…";
                            _state.Line2 = _opts.Host;
                            Compose();
                        });
                    }
                    catch (Exception) { }
                }
                if (failures == 12 && !_connectedOnce)
                {
                    try
                    {
                        BeginInvoke((MethodInvoker)delegate
                        {
                            _state.Line1 = "等待 DSH 主机…";
                            _state.Line2 = _opts.Host;
                            Compose();
                        });
                    }
                    catch (Exception) { }
                }
                if (failures > 60 && (DateTime.UtcNow - _lastOk).TotalSeconds > 45)
                {
                    Log.W("host unreachable for 45s; exiting");
                    try { BeginInvoke((MethodInvoker)delegate { Close(); }); }
                    catch (Exception) { }
                    return;
                }
            }
            // 忙碌时 400ms（气泡要跟手），空闲时退到 1.2s（省电，反正也没东西变）
            bool busy = _state.Status == "running" || _state.Status == "waiting" || _state.AlertId.Length > 0;
            Thread.Sleep(failures > 0 ? 800 : (busy ? 400 : 1200));
        }
    }

    /// <summary>
    /// 第一次跑到（并且真的连上主机）时给一句用法提示：光看一只宠物没人知道能拖、能点、能右键。
    /// 只提示一次，标记文件放在日志同目录。
    /// </summary>
    void MaybeWelcome()
    {
        if (_welcomeChecked) return;
        _welcomeChecked = true;
        string marker;
        try { marker = Path.Combine(Path.GetDirectoryName(_opts.LogPath), ".welcomed"); }
        catch (Exception) { marker = ""; }
        if (marker.Length > 0 && File.Exists(marker)) return;
        try
        {
            _tray.BalloonTipTitle = "智能伙伴已就位";
            _tray.BalloonTipText = "左键拖动可以把我放到屏幕任意位置，双击打开 DSH，右键有更多设置。";
            _tray.ShowBalloonTip(6000);
            if (marker.Length > 0) File.WriteAllText(marker, DateTime.Now.ToString("s"));
        }
        catch (Exception) { }
    }

    void ApplyState(PetState st)
    {
        _connectedOnce = true;
        MaybeWelcome();
        string prevStatus = _state.Status;
        string prevAlert = _state.AlertId;

        if (st.PetPath.Length > 0 && st.PetPath != _sheetPath) LoadSheet(st.PetPath);

        bool newAlert = prevAlert.Length == 0 && st.AlertId.Length > 0;
        if (newAlert)
        {
            bool ok = st.AlertKind.Length == 0 || st.AlertKind == "completed";
            _celebrateUntil = DateTime.UtcNow.AddSeconds(2.2);
            if (st.Sound)
            {
                try
                {
                    // 成功与失败给不同的声音，闭着眼睛也能听出区别
                    (ok ? System.Media.SystemSounds.Asterisk : System.Media.SystemSounds.Hand).Play();
                }
                catch (Exception) { }
            }
            string title = st.AlertText.Length > 0
                ? st.AlertText
                : st.Lang == "en"
                    ? (ok ? "Task finished" : "Task failed")
                    : (ok ? "任务已经结束" : "任务意外结束");
            _tray.BalloonTipTitle = title;
            _tray.BalloonTipText = st.Line1.Length > 0 ? st.Line1 : st.Line2;
            if (st.Notify) { try { _tray.ShowBalloonTip(4000); } catch (Exception) { } }
            Log.W("alert shown id=" + st.AlertId + " kind=" + st.AlertKind);
        }
        if (prevAlert.Length > 0 && st.AlertId.Length == 0) _celebrateUntil = DateTime.MinValue;

        if (st.Toast.Length > 0 && st.Toast != _state.Toast) _toastUntil = DateTime.UtcNow.AddSeconds(6);
        if (st.Status != prevStatus) { _stateChangedAt = DateTime.UtcNow; Log.W("status " + prevStatus + " -> " + st.Status); }

        string sig = MenuSignature(st);
        _state = st;
        SetRow(RowFor(st));
        if (sig != _menuSig) { _menuSig = sig; RebuildMenu(); }
        Compose();
    }

    static string MenuSignature(PetState st)
    {
        StringBuilder sb = new StringBuilder();
        sb.Append(st.PetId).Append('|').Append(st.Scale).Append('|').Append(st.Bubble ? 1 : 0)
          .Append(st.Sound ? 1 : 0).Append(st.Notify ? 1 : 0);
        for (int i = 0; i < st.PetIds.Count; i++) sb.Append('|').Append(st.PetIds[i]);
        return sb.ToString();
    }

    /// <summary>只在动画行真的变化时复位帧号，否则每次轮询都会把动画打回第一帧</summary>
    void SetRow(int row)
    {
        if (row == _row) return;
        _row = row;
        _frame = 0;
        _anim.Interval = Math.Max(40, 1000 / RowFps[Math.Min(row, RowFps.Length - 1)]);
    }

    static int RowFor(PetState st)
    {
        switch (st.Status)
        {
            case "running": return 7;
            case "waiting": return 6;
            case "failed": return 5;
            case "done": return 8;
            case "idle": return 0;
            default: return 0;
        }
    }

    void OnAnimTick(object sender, EventArgs e)
    {
        DateTime now = DateTime.UtcNow;
        int row;
        if (_dragging && Math.Abs(Cursor.Position.X - _dragStartCursor.X) > 2)
            row = Cursor.Position.X >= _dragStartCursor.X ? 1 : 2;
        else if (now < _celebrateUntil) row = 4;
        else if (_state.AlertId.Length > 0) row = IsSuccess ? 3 : RowFor(_state);
        else row = RowFor(_state);

        SetRow(row);
        _frame = (_frame + 1) % Math.Max(1, RowFrames[Math.Min(_row, RowFrames.Length - 1)]);
        Compose();
    }

    // ---------------- sprite ----------------

    void LoadSheet(string path)
    {
        try
        {
            using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (Image img = Image.FromStream(fs))
                _sheet = new Bitmap(img);
            _sheetPath = path;
            Log.W("sheet loaded " + path + " " + _sheet.Width + "x" + _sheet.Height);
            ConfigureGrid(_sheet.Width, _sheet.Height);
        }
        catch (Exception ex)
        {
            Log.W("sheet load failed " + path + ": " + ex.Message);
            _sheet = null;
            _sheetPath = "";
        }
    }

    void ConfigureGrid(int w, int h)
    {
        if (w <= 0 || h <= 0) return;
        int cols = 8;
        if (w % cols != 0) return;
        int fw = w / cols;
        if (fw <= 0) return;
        int rows = h / fw;
        if (rows < 9) return;
        _state.FrameW = fw;
        _state.FrameH = h / rows;
        _state.Cols = cols;
        _state.Rows = rows;
    }

    // ---------------- composition ----------------

    void Compose()
    {
        try
        {
            int petW = Math.Max(24, (int)Math.Round(_state.FrameW * _state.Scale));
            int petH = Math.Max(24, (int)Math.Round(_state.FrameH * _state.Scale));
            bool en = _state.Lang == "en";

            string title;
            if (_state.AlertId.Length > 0)
                title = _state.AlertText.Length > 0
                    ? _state.AlertText
                    : (en ? (IsSuccess ? "Task finished" : "Task ended") : (IsSuccess ? "任务已经结束" : "任务意外结束"));
            else
                title = _state.Title.Length > 0 ? _state.Title : (en ? "DSH companion" : "DSH 智能伙伴");
            title = Ellipsis(_measure, title, _titleFont, BUBBLE_W - 34);
            string line1 = _state.Line1;
            string line2 = _state.Line2;
            if (DateTime.UtcNow < _toastUntil && _state.Toast.Length > 0) line2 = _state.Toast;

            bool showBubble = _state.Bubble && (_expanded || _state.AlertId.Length > 0
                || _state.Status == "running" || _state.Status == "waiting" || _state.Status == "failed"
                || (DateTime.UtcNow - _stateChangedAt).TotalSeconds < 8
                || DateTime.UtcNow < _toastUntil);

            List<string> bodyLines = new List<string>();
            int bubbleH = 0;
            int bodyH = BodyLineHeight;
            int footerH = FooterLineHeight;
            if (showBubble)
            {
                bodyLines = Wrap(_measure, line1, _bodyFont, BUBBLE_W - 24, 3);
                bubbleH = 8 + TitleLineHeight + (bodyLines.Count > 0 ? 4 + bodyLines.Count * bodyH : 0)
                    + (line2.Length > 0 ? footerH + 4 : 0) + 8;
            }

            int winW = Math.Max(petW, showBubble ? BUBBLE_W : 0) + PAD * 2;
            int winH = (showBubble ? bubbleH + GAP : 0) + petH + PAD * 2;

            if (!_posInitialized && _state.PosX != int.MinValue)
            {
                Location = new Point(_state.PosX - winW / 2, _state.PosY - winH);
                _posInitialized = true;
            }

            // 以「宠物底边中点」为锚点，尺寸变化时保持宠物不跳动
            Point anchor = new Point(Left + Math.Max(1, _winW) / 2, Top + Math.Max(1, _winH));
            ResizeCompose(winW, winH);
            if (!_dragging)
            {
                Point target = new Point(anchor.X - winW / 2, anchor.Y - winH);
                // 气泡出现会让窗口变高，贴边时会顶出屏幕：重排后整体夹回工作区
                target = ClampWindow(target, winW, winH);
                Location = target;
            }

            _composeG.Clear(Color.Transparent);
            _composeG.SmoothingMode = SmoothingMode.AntiAlias;
            _composeG.InterpolationMode = InterpolationMode.HighQualityBicubic;
            _composeG.PixelOffsetMode = PixelOffsetMode.HighQuality;
            _composeG.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

            if (showBubble)
            {
                Rectangle bubble = new Rectangle((winW - BUBBLE_W) / 2, PAD, BUBBLE_W, bubbleH);
                DrawBubble(_composeG, bubble, title, bodyLines, line2);
            }

            int px = (winW - petW) / 2;
            int py = winH - PAD - petH;
            DrawPet(_composeG, new Rectangle(px, py, petW, petH));

            PushLayered();
            MaybeDump();
        }
        catch (Exception ex)
        {
            Log.W("compose failed: " + ex.GetType().Name + " " + ex.Message);
        }
    }

    void ResizeCompose(int w, int h)
    {
        w = Math.Max(16, w);
        h = Math.Max(16, h);
        if (_compose != null && _compose.Width == w && _compose.Height == h) return;
        if (_composeG != null) { _composeG.Dispose(); _composeG = null; }
        if (_compose != null) { _compose.Dispose(); _compose = null; }
        _compose = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        _composeG = Graphics.FromImage(_compose);
        _winW = w;
        _winH = h;
        EnsureDib(w, h);
    }

    void EnsureDib(int w, int h)
    {
        if (_dib != IntPtr.Zero && _dibW == w && _dibH == h) return;
        DisposeDib();
        _screenDc = Win32.GetDC(IntPtr.Zero);
        _memDc = Win32.CreateCompatibleDC(_screenDc);
        Win32.BITMAPINFO bmi = new Win32.BITMAPINFO();
        bmi.bmiHeader.biSize = 40;
        bmi.bmiHeader.biWidth = w;
        bmi.bmiHeader.biHeight = -h;
        bmi.bmiHeader.biPlanes = 1;
        bmi.bmiHeader.biBitCount = 32;
        bmi.bmiHeader.biCompression = 0;
        _dib = Win32.CreateDIBSection(_memDc, ref bmi, 0, out _bits, IntPtr.Zero, 0);
        _oldDib = Win32.SelectObject(_memDc, _dib);
        _dibW = w;
        _dibH = h;
        _alpha = new byte[w * h];
    }

    void DisposeDib()
    {
        if (_oldDib != IntPtr.Zero) { Win32.SelectObject(_memDc, _oldDib); _oldDib = IntPtr.Zero; }
        if (_dib != IntPtr.Zero) { Win32.DeleteObject(_dib); _dib = IntPtr.Zero; }
        if (_memDc != IntPtr.Zero) { Win32.DeleteDC(_memDc); _memDc = IntPtr.Zero; }
        if (_screenDc != IntPtr.Zero) { Win32.ReleaseDC(IntPtr.Zero, _screenDc); _screenDc = IntPtr.Zero; }
        _bits = IntPtr.Zero;
    }

    void PushLayered()
    {
        if (_compose == null || _bits == IntPtr.Zero) return;
        int w = Math.Min(_dibW, _compose.Width);
        int h = Math.Min(_dibH, _compose.Height);
        Rectangle rect = new Rectangle(0, 0, w, h);
        BitmapData bd = _compose.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            int srcStride = bd.Stride;
            int rowBytes = w * 4;
            int total = srcStride * h;
            if (_scratch.Length != total) _scratch = new byte[total];
            Marshal.Copy(bd.Scan0, _scratch, 0, total);

            if (_alpha.Length != _dibW * _dibH) _alpha = new byte[_dibW * _dibH];

            // 直通 alpha -> 预乘 alpha（UpdateLayeredWindow 要求预乘），同时刷新命中掩码
            for (int y = 0; y < h; y++)
            {
                int row = y * srcStride;
                for (int x = 0; x < w; x++)
                {
                    int i = row + x * 4;
                    byte a = _scratch[i + 3];
                    _alpha[y * _dibW + x] = a;
                    if (a == 0) { _scratch[i] = 0; _scratch[i + 1] = 0; _scratch[i + 2] = 0; }
                    else if (a != 255)
                    {
                        _scratch[i] = (byte)(_scratch[i] * a / 255);
                        _scratch[i + 1] = (byte)(_scratch[i + 1] * a / 255);
                        _scratch[i + 2] = (byte)(_scratch[i + 2] * a / 255);
                    }
                }
            }

            long dstStride = _dibW * 4;
            for (int y = 0; y < h; y++)
                Marshal.Copy(_scratch, y * srcStride, (IntPtr)(_bits.ToInt64() + y * dstStride), rowBytes);
        }
        finally
        {
            _compose.UnlockBits(bd);
        }

        Win32.POINT dst = new Win32.POINT(Left, Top);
        Win32.SIZE size = new Win32.SIZE(_dibW, _dibH);
        Win32.POINT src = new Win32.POINT(0, 0);
        Win32.BLENDFUNCTION blend = new Win32.BLENDFUNCTION();
        blend.BlendOp = 0; // AC_SRC_OVER
        blend.SourceConstantAlpha = 255;
        blend.AlphaFormat = 1; // AC_SRC_ALPHA
        if (!Win32.UpdateLayeredWindow(Handle, _screenDc, ref dst, ref size, _memDc, ref src, 0, ref blend, 2))
            Log.W("UpdateLayeredWindow failed err=" + Marshal.GetLastWin32Error() + " visible=" + Win32.IsWindowVisible(Handle) + " hwnd=" + Handle + " size=" + _dibW + "x" + _dibH + " memdc=" + _memDc + " dib=" + _dib + " screenDc=" + _screenDc);
    }

    bool HitTest(Point client)
    {
        if (client.X < 0 || client.Y < 0 || client.X >= _dibW || client.Y >= _dibH) return false;
        int idx = client.Y * _dibW + client.X;
        if (idx < 0 || idx >= _alpha.Length) return false;
        return _alpha[idx] > 24;
    }

    void MaybeDump()
    {
        if (!_opts.Dump) return;
        _dumpCounter++;
        if (_dumpCounter % 10 != 0) return;
        try
        {
            if (_compose != null)
                _compose.Save(Path.ChangeExtension(_opts.LogPath, ".dump.png"), ImageFormat.Png);
        }
        catch (Exception) { }
    }

    void DrawBubble(Graphics g, Rectangle box, string title, List<string> lines, string footer)
    {
        bool alert = _state.AlertId.Length > 0;
        bool fail = alert && !IsSuccess;
        Color bg = alert
            ? (fail ? Color.FromArgb(242, 254, 244, 242) : Color.FromArgb(242, 255, 250, 240))
            : Color.FromArgb(238, 252, 252, 254);
        Color border = alert
            ? (fail ? Color.FromArgb(220, 226, 106, 92) : Color.FromArgb(215, 246, 178, 92))
            : Color.FromArgb(215, 206, 210, 222);
        using (GraphicsPath path = RoundRect(box, 12))
        using (SolidBrush b = new SolidBrush(bg))
        using (Pen p = new Pen(border, 1))
        {
            g.FillPath(b, path);
            g.DrawPath(p, path);
        }

        int x = box.X + 12;
        int y = box.Y + 8;
        if (alert)
        {
            Color glyph = fail ? Color.FromArgb(255, 200, 60, 48) : Color.FromArgb(255, 22, 140, 84);
            using (SolidBrush b = new SolidBrush(glyph))
                g.DrawString(fail ? "✕" : "✔", _titleFont, b, x - 1, y - 2);
            x += 18;
        }
        using (SolidBrush b = new SolidBrush(Color.FromArgb(255, 24, 28, 34)))
            g.DrawString(title, _titleFont, b, new RectangleF(x, y, box.Width - (x - box.X) - 12, TitleLineHeight));

        y += TitleLineHeight + 2;
        int bodyH = BodyLineHeight;
        using (SolidBrush b = new SolidBrush(Color.FromArgb(255, 62, 68, 76)))
            foreach (string line in lines)
            {
                g.DrawString(line, _bodyFont, b, new RectangleF(box.X + 12, y, box.Width - 24, bodyH));
                y += bodyH;
            }

        if (footer.Length > 0)
        {
            StringFormat right = new StringFormat();
            right.Alignment = StringAlignment.Far;
            right.Trimming = StringTrimming.EllipsisCharacter;
            using (SolidBrush b = new SolidBrush(Color.FromArgb(255, 120, 126, 136)))
                g.DrawString(footer, _smallFont, b,
                    new RectangleF(box.X + 12, box.Bottom - FooterLineHeight - 8, box.Width - 24, FooterLineHeight), right);
            right.Dispose();
        }
    }

    int TitleLineHeight { get { return MeasureLine(_titleFont); } }
    int BodyLineHeight { get { return MeasureLine(_bodyFont); } }
    int FooterLineHeight { get { return MeasureLine(_smallFont); } }

    int MeasureLine(Font font)
    {
        if (font == null) return 16;
        if (_measure != null) return (int)Math.Ceiling(font.GetHeight(_measure) + 1f);
        return (int)Math.Ceiling(font.GetHeight() + 1f);
    }

    /// <summary>提醒是否属于"正常完成"——失败/中断要用不同的图形与提示音</summary>
    bool IsSuccess
    {
        get { return _state.AlertKind.Length == 0 || _state.AlertKind == "completed"; }
    }

    void DrawPet(Graphics g, Rectangle box)
    {
        if (_sheet != null && _state.Cols > 0 && _state.Rows > 0)
        {
            int row = Math.Min(_row, _state.Rows - 1);
            int frames = RowFrames[Math.Min(_row, RowFrames.Length - 1)];
            int col = _frame % Math.Max(1, Math.Min(frames, _state.Cols));
            Rectangle src = new Rectangle(col * _state.FrameW, row * _state.FrameH, _state.FrameW, _state.FrameH);
            try
            {
                g.DrawImage(_sheet, box, src, GraphicsUnit.Pixel);
                return;
            }
            catch (Exception ex) { Log.W("draw frame failed: " + ex.Message); }
        }
        DrawPlaceholder(g, box);
    }

    void DrawPlaceholder(Graphics g, Rectangle box)
    {
        int s = Math.Min(box.Width, box.Height);
        Rectangle body = new Rectangle(box.X + (box.Width - s) / 2, box.Y + (box.Height - s) / 2 + s / 10, s, (int)(s * 0.82));
        using (GraphicsPath p = RoundRect(body, s / 3))
        using (SolidBrush b = new SolidBrush(Color.FromArgb(255, 120, 170, 255)))
        using (SolidBrush eye = new SolidBrush(Color.FromArgb(255, 28, 32, 40)))
        {
            g.FillPath(b, p);
            int eyeD = Math.Max(4, s / 7);
            g.FillEllipse(eye, body.X + body.Width / 2 - eyeD * 2, body.Y + body.Height / 2 - eyeD, eyeD, eyeD);
            g.FillEllipse(eye, body.X + body.Width / 2 + eyeD, body.Y + body.Height / 2 - eyeD, eyeD, eyeD);
        }
    }

    static GraphicsPath RoundRect(Rectangle r, int radius)
    {
        GraphicsPath p = new GraphicsPath();
        int d = Math.Max(2, radius * 2);
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    /// 逐字折行：中文没有空格，按空格切词会把整段当成一个“词”，一旦超宽就被省略号截成一行。
    /// 这里按字符累加测宽，必要时才回退到最近的空格，兼顾英文单词不被劈开。
    static List<string> Wrap(Graphics g, string text, Font font, int maxWidth, int maxLines)
    {
        List<string> output = new List<string>();
        if (string.IsNullOrEmpty(text) || g == null) return output;
        text = text.Replace("\r", " ").Replace("\n", " ").Replace("\t", " ").Replace("\u00a0", " ");
        while (text.IndexOf("  ") >= 0) text = text.Replace("  ", " ");
        text = text.Trim();
        if (text.Length == 0) return output;

        StringBuilder current = new StringBuilder();
        for (int i = 0; i < text.Length; i++)
        {
            char ch = text[i];
            if (current.Length > 0 && g.MeasureString(current.ToString() + ch, font).Width > maxWidth)
            {
                string line = current.ToString();
                int space = line.LastIndexOf(' ');
                if (space > 0 && line.Length - space <= 24)
                {
                    output.Add(line.Substring(0, space));
                    current = new StringBuilder(line.Substring(space + 1));
                    if (ch != ' ') current.Append(ch);
                }
                else
                {
                    output.Add(line);
                    current = new StringBuilder();
                    if (ch != ' ') current.Append(ch);
                }
                if (output.Count >= maxLines)
                {
                    output[output.Count - 1] = Ellipsis(g, output[output.Count - 1] + "…", font, maxWidth);
                    return output;
                }
            }
            else
            {
                current.Append(ch);
            }
        }
        if (current.Length > 0)
        {
            string line = current.ToString();
            if (g.MeasureString(line, font).Width > maxWidth) line = Ellipsis(g, line, font, maxWidth);
            output.Add(line);
        }
        if (output.Count > maxLines)
        {
            output = output.GetRange(0, maxLines);
            output[maxLines - 1] = Ellipsis(g, output[maxLines - 1] + "…", font, maxWidth);
        }
        return output;
    }

    static string Ellipsis(Graphics g, string text, Font font, int maxWidth)
    {
        while (text.Length > 1 && g.MeasureString(text, font).Width > maxWidth)
            text = text.Substring(0, text.Length - 2) + "…";
        return text;
    }

    // ---------------- interaction ----------------

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_NCHITTEST)
        {
            int lp = (int)(long)m.LParam;
            Point pt = PointToClient(new Point((short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF)));
            m.Result = (IntPtr)(HitTest(pt) ? HTCLIENT : HTTRANSPARENT);
            return;
        }
        if (m.Msg == WM_MOUSEACTIVATE) { m.Result = (IntPtr)MA_NOACTIVATE; return; }

        if (m.Msg == WM_LBUTTONDOWN)
        {
            _dragCandidate = true;
            _dragging = false;
            _dragStartCursor = Cursor.Position;
            _dragStartWin = Location;
            Win32.SetCapture(Handle);
            Log.W("mouse down at " + Cursor.Position.X + "," + Cursor.Position.Y);
            return;
        }
        if (m.Msg == WM_MOUSEMOVE && _dragCandidate)
        {
            Point cur = Cursor.Position;
            int dx = cur.X - _dragStartCursor.X;
            int dy = cur.Y - _dragStartCursor.Y;
            if (!_dragging && (Math.Abs(dx) > 4 || Math.Abs(dy) > 4)) _dragging = true;
            if (_dragging)
            {
                Location = ClampToScreen(new Point(_dragStartWin.X + dx, _dragStartWin.Y + dy));
                PushLayered();
            }
            return;
        }
        if (m.Msg == WM_LBUTTONUP)
        {
            Win32.ReleaseCapture();
            bool wasDragging = _dragging;
            _dragCandidate = false;
            _dragging = false;
            if (wasDragging)
            {
                Log.W("drag to " + Location.X + "," + Location.Y);
                PostConfig("pos", "1");
            }
            else
            {
                _expanded = !_expanded;
                if (_state.AlertId.Length > 0) PostAck();
                Compose();
            }
            return;
        }
        if (m.Msg == WM_LBUTTONDBLCLK) { FocusDsh(); return; }
        if (m.Msg == WM_RBUTTONUP)
        {
            ContextMenuStrip menu = BuildMenu();
            menu.Show(Cursor.Position);
            return;
        }
        base.WndProc(ref m);
    }

    /// <summary>
    /// 重排后把整个窗口夹回所在显示器的工作区：气泡是从宠物底边往上长的，
    /// 宠物贴着屏幕上沿时窗口会顶出屏幕外，用户就再也看不到气泡了。
    /// </summary>
    Point ClampWindow(Point p, int w, int h)
    {
        Rectangle area;
        try { area = Screen.FromPoint(new Point(p.X + w / 2, p.Y + h / 2)).WorkingArea; }
        catch (Exception) { area = Screen.PrimaryScreen.WorkingArea; }
        int x = Math.Max(area.Left, Math.Min(area.Right - w, p.X));
        int y = Math.Max(area.Top, Math.Min(area.Bottom - h, p.Y));
        if (w > area.Width) x = area.Left;
        if (h > area.Height) y = area.Top;
        return new Point(x, y);
    }

    Point ClampToScreen(Point p)
    {
        Rectangle vs = SystemInformation.VirtualScreen;
        int w = _winW > 0 ? _winW : Width;
        int h = _winH > 0 ? _winH : Height;
        int x = Math.Max(vs.Left - w / 2, Math.Min(vs.Right - w / 2, p.X));
        int y = Math.Max(vs.Top - h / 2, Math.Min(vs.Bottom - h / 2, p.Y));
        return new Point(x, y);
    }

    void FocusDsh()
    {
        try
        {
            IntPtr found = IntPtr.Zero;
            Win32.EnumWindows(delegate(IntPtr hWnd, IntPtr lParam)
            {
                if (!Win32.IsWindowVisible(hWnd)) return true;
                uint pid;
                Win32.GetWindowThreadProcessId(hWnd, out pid);
                if (pid == 0) return true;
                try
                {
                    Process p = Process.GetProcessById((int)pid);
                    string name = p.ProcessName.ToLowerInvariant();
                    if (name.Contains("deepseek") || name.Contains("dsh"))
                    {
                        StringBuilder sb = new StringBuilder(512);
                        Win32.GetWindowText(hWnd, sb, sb.Capacity);
                        if (sb.Length > 0) { found = hWnd; return false; }
                    }
                }
                catch (Exception) { }
                return true;
            }, IntPtr.Zero);
            if (found != IntPtr.Zero)
            {
                Win32.ShowWindow(found, 9); // SW_RESTORE
                Win32.SetForegroundWindow(found);
                Log.W("focused DSH window");
            }
            else Log.W("DSH window not found");
        }
        catch (Exception ex) { Log.W("focus failed: " + ex.Message); }
    }

    // ---------------- menu ----------------

    void RebuildMenu() { try { ContextMenuStrip m = BuildMenu(); _tray.ContextMenuStrip = m; } catch (Exception) { } }

    ContextMenuStrip BuildMenu()
    {
        ContextMenuStrip menu = new ContextMenuStrip();
        menu.Font = new Font("Microsoft YaHei UI", 9f, FontStyle.Regular, GraphicsUnit.Point);
        ToolStripMenuItem open = new ToolStripMenuItem("打开 DSH");
        open.Font = new Font(menu.Font, FontStyle.Bold);
        open.Click += delegate { FocusDsh(); };
        menu.Items.Add(open);
        string overlayNote = _state.Overlay.Length > 0 && _state.Overlay != "running"
            ? "浮层与 DSH 的连接：" + _state.Overlay
            : "";
        if (overlayNote.Length > 0) menu.Items.Add(new ToolStripMenuItem(overlayNote) { Enabled = false });
        menu.Items.Add(new ToolStripSeparator());

        ToolStripMenuItem pets = new ToolStripMenuItem("切换宠物");
        for (int i = 0; i < _state.PetIds.Count; i++)
        {
            string id = _state.PetIds[i];
            string name = i < _state.PetNames.Count ? _state.PetNames[i] : id;
            ToolStripMenuItem item = new ToolStripMenuItem(name);
            item.Checked = id == _state.PetId;
            item.Click += delegate { PostConfig("pet", id); };
            pets.DropDownItems.Add(item);
        }
        if (pets.DropDownItems.Count > 0) pets.DropDownItems.Add(new ToolStripSeparator());
        pets.DropDownItems.Add("安装 petdex 宠物…", null, delegate { InstallPet(); });
        pets.DropDownItems.Add("打开宠物目录", null, delegate
        {
            try { Process.Start("explorer.exe", Path.GetDirectoryName(Path.GetDirectoryName(_opts.LogPath))); }
            catch (Exception) { }
        });
        menu.Items.Add(pets);

        ToolStripMenuItem scaleMenu = new ToolStripMenuItem("缩放");
        double[] values = new double[] { 0.4, 0.55, 0.7, 0.85, 1.0, 1.25 };
        foreach (double v in values)
        {
            double vv = v;
            ToolStripMenuItem item = new ToolStripMenuItem((int)Math.Round(v * 100) + "%");
            item.Checked = Math.Abs(_state.Scale - vv) < 0.01;
            item.Click += delegate { PostConfig("scale", vv.ToString(System.Globalization.CultureInfo.InvariantCulture)); };
            scaleMenu.DropDownItems.Add(item);
        }
        menu.Items.Add(scaleMenu);

        ToolStripMenuItem bubble = new ToolStripMenuItem("显示对话气泡");
        bubble.Checked = _state.Bubble;
        bubble.Click += delegate { PostConfig("bubble", _state.Bubble ? "0" : "1"); };
        menu.Items.Add(bubble);

        ToolStripMenuItem sound = new ToolStripMenuItem("结束提示音");
        sound.Checked = _state.Sound;
        sound.Click += delegate { PostConfig("sound", _state.Sound ? "0" : "1"); };
        menu.Items.Add(sound);

        // 这一项走的是 Windows 右下角通知，跟浮层上的气泡没关系，名字要说清楚
        ToolStripMenuItem notify = new ToolStripMenuItem("结束时弹系统通知");
        notify.Checked = _state.Notify;
        notify.Click += delegate { PostConfig("notify", _state.Notify ? "0" : "1"); };
        menu.Items.Add(notify);
        menu.Items.Add(new ToolStripSeparator());
        ToolStripMenuItem rehearse = new ToolStripMenuItem("测试结束提醒");
        ToolStripMenuItem rehearseOk = new ToolStripMenuItem("成功演练（绿色 ✔）", null, delegate { RequestPreview("completed"); });
        ToolStripMenuItem rehearseFail = new ToolStripMenuItem("失败演练（红色 ✕）", null, delegate { RequestPreview("failed"); });
        rehearse.DropDownItems.Add(rehearseOk);
        rehearse.DropDownItems.Add(rehearseFail);
        menu.Items.Add(rehearse);
        menu.Items.Add(new ToolStripMenuItem("重置位置（回到当前屏幕右下角）", null, delegate
        {
            Rectangle wa = (Screen.FromControl(this) ?? Screen.PrimaryScreen).WorkingArea;
            Location = new Point(wa.Right - Math.Max(220, _winW > 0 ? _winW : 220), wa.Bottom - Math.Max(200, _winH > 0 ? _winH : 200));
            _posInitialized = true;
            PostConfig("pos", "1");
            Compose();
        }));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("退出宠物窗", null, delegate { Close(); }));
        return menu;
    }

    void InstallPet()
    {
        using (InputDialog dlg = new InputDialog("安装 petdex 宠物", "输入 petdex 名称、petdex.dev 链接，或宠物 zip / 文件夹路径：", ""))
        {
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            string source = dlg.Value.Trim();
            if (source.Length == 0) return;
            Cursor previous = Cursor;
            try
            {
                // 装宠物要抓脚本 + 下载 ~2.4MB 素材，主机侧预算 90s/个，这里给足 180s
                Cursor = Cursors.WaitCursor;
                Dictionary<string, string> form = new Dictionary<string, string>();
                form["source"] = source;
                string result = Http.PostForm(_opts.Host + _opts.Prefix + "/install", _opts.Token, form, 180000);
                Log.W("install result: " + result.Replace("\r", " ").Replace("\n", " "));
                MessageBox.Show(result, "安装宠物", MessageBoxButtons.OK,
                    result.StartsWith("已安装") ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
            }
            catch (Exception ex)
            {
                Log.W("install failed: " + ex.Message);
                MessageBox.Show("安装失败：" + ex.Message, "安装宠物", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            finally { Cursor = previous; }
        }
    }

    // ---------------- host posts ----------------

    void PostConfig(string key, string value)
    {
        try
        {
            Dictionary<string, string> form = new Dictionary<string, string>();
            form[key] = value;
            form["posx"] = (Left + Math.Max(1, _winW) / 2).ToString();
            form["posy"] = (Top + Math.Max(1, _winH)).ToString();
            Http.PostForm(_opts.Host + _opts.Prefix + "/config", _opts.Token, form);
            Log.W("config " + key + "=" + value);
        }
        catch (Exception ex) { Log.W("config post failed: " + ex.Message); }
        try { BeginInvoke((MethodInvoker)delegate { Compose(); }); } catch (Exception) { }
    }

    void PostAck()
    {
        try
        {
            Dictionary<string, string> form = new Dictionary<string, string>();
            form["alertId"] = _state.AlertId;
            Http.PostForm(_opts.Host + _opts.Prefix + "/ack", _opts.Token, form);
            Log.W("ack " + _state.AlertId);
        }
        catch (Exception ex) { Log.W("ack failed: " + ex.Message); }
    }

    void RequestPreview(string kind)
    {
        ThreadPool.QueueUserWorkItem(delegate
        {
            try
            {
                Dictionary<string, string> form = new Dictionary<string, string>();
                form["kind"] = kind;
                Http.PostForm(_opts.Host + _opts.Prefix + "/test", _opts.Token, form);
                Log.W("preview alert requested kind=" + kind);
            }
            catch (Exception ex) { Log.W("preview alert failed: " + ex.Message); }
        });
    }

    void PostReady()
    {
        try
        {
            Dictionary<string, string> form = new Dictionary<string, string>();
            form["event"] = "ready";
            form["x"] = Left.ToString();
            form["y"] = Top.ToString();
            form["width"] = Width.ToString();
            form["height"] = Height.ToString();
            form["scale"] = _state.Scale.ToString();
            Http.PostForm(_opts.Host + _opts.Prefix + "/overlay", _opts.Token, form);
        }
        catch (Exception ex) { Log.W("ready post failed: " + ex.Message); }
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Compose();
        PostReady();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _closing = true;
        _anim.Stop();
        try { _tray.Visible = false; _tray.Dispose(); } catch (Exception) { }
        DisposeDib();
        _measure.Dispose();
        _titleFont.Dispose();
        _bodyFont.Dispose();
        _smallFont.Dispose();
        base.OnFormClosing(e);
    }
}

static class Win32
{
    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X; public int Y; public POINT(int x, int y) { X = x; Y = y; } }

    [StructLayout(LayoutKind.Sequential)]
    public struct SIZE { public int W; public int H; public SIZE(int w, int h) { W = w; H = h; } }

    [StructLayout(LayoutKind.Sequential)]
    public struct BLENDFUNCTION { public byte BlendOp; public byte BlendFlags; public byte SourceConstantAlpha; public byte AlphaFormat; }

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
    public struct BITMAPINFO { public BITMAPINFOHEADER bmiHeader; public uint bmiColors; }

    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)] public static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize, IntPtr hdcSrc, ref POINT pptSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);
    [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
    [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] public static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] public static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] public static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFO pbmi, uint usage, out IntPtr bits, IntPtr hSection, uint offset);
    [DllImport("user32.dll")] public static extern IntPtr SetCapture(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool ReleaseCapture();
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc proc, IntPtr lParam);
}