// dsh-pet · 浮层入口：参数、日志、HTTP 客户端、状态行协议
//
// 这个进程完全独立于 DSH：DSH 桌面端不向插件开放窗口能力，所以"能拖到屏幕
// 任何角落、最小化后仍可见"只能由自己的置顶分层窗口实现。它只做三件事：
//   1. 每 400ms 拉一次 /state（行协议），把对话状态画成宠物 + 气泡；
//   2. 把用户操作（拖动位置 / 切换宠物 / 写消息）回写成 POST；
//   3. 盯着 DSH 主进程，DSH 一退出就自己收拾行李退出，绝不留孤儿。
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace DshPet
{
    /// <summary>启动参数（由宿主 src/overlay.js 传入）。</summary>
    sealed class Opts
    {
        public string Host = "http://127.0.0.1:14711";
        public string Token = "";
        public string LogPath = "";
        public string Prefix = "/x-dsh-pet";
        public int DshPid = 0;
        public string DshExe = "";
        public bool Dump = false;

        public static Opts Parse(string[] args)
        {
            Opts o = new Opts();
            for (int i = 0; i < args.Length; i++)
            {
                string k = args[i];
                string v = i + 1 < args.Length ? args[i + 1] : "";
                if (k == "--host") { o.Host = v; i++; }
                else if (k == "--token") { o.Token = v; i++; }
                else if (k == "--log") { o.LogPath = v; i++; }
                else if (k == "--prefix") { o.Prefix = v; i++; }
                else if (k == "--dsh-pid") { int.TryParse(v, out o.DshPid); i++; }
                else if (k == "--dsh-exe") { o.DshExe = v; i++; }
                else if (k == "--dump") { o.Dump = true; }
            }
            if (o.Prefix.Length == 0) o.Prefix = "/x-dsh-pet";
            if (!o.Prefix.StartsWith("/")) o.Prefix = "/" + o.Prefix;
            o.Prefix = o.Prefix.TrimEnd('/');
            o.Host = o.Host.TrimEnd('/');
            return o;
        }
    }

    /// <summary>简单滚动日志：超过 512KB 就换名重开，避免长期运行把盘写满。</summary>
    static class Log
    {
        static readonly object Sync = new object();
        static string _path = "";

        public static void Init(string path)
        {
            _path = path ?? "";
            try
            {
                if (_path.Length > 0)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(_path));
                    FileInfo fi = new FileInfo(_path);
                    if (fi.Exists && fi.Length > 512 * 1024)
                    {
                        string old = _path + ".1";
                        if (File.Exists(old)) File.Delete(old);
                        File.Move(_path, old);
                    }
                }
            }
            catch { }
        }

        public static void W(string message)
        {
            string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "  " + message + Environment.NewLine;
            lock (Sync)
            {
                try
                {
                    if (_path.Length > 0) File.AppendAllText(_path, line, Encoding.UTF8);
                }
                catch { }
            }
        }
    }

    /// <summary>浮层 → 宿主的 HTTP 调用。宿主在环回地址上，DSH 不鉴权，我们自己带令牌。</summary>
    static class Http
    {
        public static string Get(string url, string token, int timeoutMs)
        {
            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
            req.Proxy = null;
            req.KeepAlive = true;
            req.Timeout = timeoutMs;
            req.ReadWriteTimeout = timeoutMs;
            req.Method = "GET";
            if (token.Length > 0) req.Headers["X-Pet-Token"] = token;
            using (HttpWebResponse res = (HttpWebResponse)req.GetResponse())
            using (StreamReader reader = new StreamReader(res.GetResponseStream(), Encoding.UTF8))
            {
                return reader.ReadToEnd();
            }
        }

        public static string Get(string url, string token)
        {
            return Get(url, token, 5000);
        }

        public static string PostForm(string url, string token, Dictionary<string, string> form)
        {
            return PostForm(url, token, form, 8000);
        }

        public static string PostForm(string url, string token, Dictionary<string, string> form, int timeoutMs)
        {
            StringBuilder body = new StringBuilder();
            foreach (KeyValuePair<string, string> pair in form)
            {
                if (body.Length > 0) body.Append('&');
                body.Append(Uri.EscapeDataString(pair.Key)).Append('=').Append(Uri.EscapeDataString(pair.Value ?? ""));
            }
            byte[] bytes = Encoding.UTF8.GetBytes(body.ToString());
            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
            req.Proxy = null;
            req.Method = "POST";
            req.ContentType = "application/x-www-form-urlencoded";
            req.Timeout = timeoutMs;
            req.ReadWriteTimeout = timeoutMs;
            req.ContentLength = bytes.Length;
            if (token.Length > 0) req.Headers["X-Pet-Token"] = token;
            using (Stream stream = req.GetRequestStream())
            {
                stream.Write(bytes, 0, bytes.Length);
            }
            using (HttpWebResponse res = (HttpWebResponse)req.GetResponse())
            using (StreamReader reader = new StreamReader(res.GetResponseStream(), Encoding.UTF8))
            {
                return reader.ReadToEnd();
            }
        }
    }

    /// <summary>/state 的行协议：key=URL 编码值，一行一条。</summary>
    sealed class PetState
    {
        public string Status = "unknown";
        public string Title = "";
        public string Line1 = "";
        public string Line2 = "";
        public int Elapsed = 0;
        public string AlertId = "";
        public string AlertKind = "";
        public string AlertText = "";
        public int AlertAge = 0;
        public string PetId = "";
        public string PetName = "";
        public string PetPath = "";
        public int FrameW = 192;
        public int FrameH = 208;
        public int Cols = 8;
        public int Rows = 9;
        public double Scale = 0.55;
        public bool Bubble = true;
        public bool Sound = true;
        public string Lang = "zh";
        public string Toast = "";
        public string Overlay = "";
        public int PosX = int.MinValue;
        public int PosY = int.MinValue;
        public int BubbleMs = 5000;
        public int AlertMs = 30000;
        public int DshPid = 0;
        public readonly List<string> PetIds = new List<string>();
        public readonly List<string> PetNames = new List<string>();

        public bool IsSuccess
        {
            get { return AlertKind.Length == 0 || AlertKind == "completed"; }
        }

        public static PetState Parse(string text)
        {
            PetState s = new PetState();
            if (string.IsNullOrEmpty(text)) return s;
            string[] lines = text.Replace("\r", "").Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string key = line.Substring(0, eq);
                string value = Uri.UnescapeDataString(line.Substring(eq + 1));
                switch (key)
                {
                    case "status": s.Status = value; break;
                    case "title": s.Title = value; break;
                    case "line1": s.Line1 = value; break;
                    case "line2": s.Line2 = value; break;
                    case "elapsed": s.Elapsed = ToInt(value); break;
                    case "alert.id": s.AlertId = value; break;
                    case "alert.kind": s.AlertKind = value; break;
                    case "alert.text": s.AlertText = value; break;
                    case "alert.age": s.AlertAge = ToInt(value); break;
                    case "pet.id": s.PetId = value; break;
                    case "pet.name": s.PetName = value; break;
                    case "pet.path": s.PetPath = value; break;
                    case "frame.w": s.FrameW = Math.Max(16, ToInt(value)); break;
                    case "frame.h": s.FrameH = Math.Max(16, ToInt(value)); break;
                    case "frame.cols": s.Cols = Math.Max(1, ToInt(value)); break;
                    case "frame.rows": s.Rows = Math.Max(1, ToInt(value)); break;
                    case "scale": s.Scale = ToDouble(value, 0.55); break;
                    case "bubble": s.Bubble = value == "1"; break;
                    case "sound": s.Sound = value == "1"; break;
                    case "lang": s.Lang = value; break;
                    case "toast": s.Toast = value; break;
                    case "overlay": s.Overlay = value; break;
                    case "pos.x": s.PosX = ToInt(value); break;
                    case "pos.y": s.PosY = ToInt(value); break;
                    case "bubbleMs": s.BubbleMs = ToInt(value); break;
                    case "alertMs": s.AlertMs = ToInt(value); break;
                    case "dsh.pid": s.DshPid = ToInt(value); break;
                    default:
                        if (key.StartsWith("petlist.") && key.EndsWith(".id")) s.PetIds.Add(value);
                        else if (key.StartsWith("petlist.") && key.EndsWith(".name")) s.PetNames.Add(value);
                        break;
                }
            }
            if (s.Scale < 0.2) s.Scale = 0.2;
            if (s.Scale > 2.0) s.Scale = 2.0;
            return s;
        }

        static int ToInt(string value)
        {
            int n;
            return int.TryParse(value, out n) ? n : 0;
        }

        static double ToDouble(string value, double fallback)
        {
            double n;
            return double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out n) ? n : fallback;
        }
    }

    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            Opts opts = Opts.Parse(args);
            Log.Init(opts.LogPath);

            // 只允许一个浮层：宿主重载时旧进程可能还没退，第二个直接让位。
            bool created;
            Mutex mutex = new Mutex(true, "Local\\DshPetOverlay.SingleInstance", out created);
            if (!created)
            {
                Log.W("another instance already running; exit");
                return;
            }

            AppDomain.CurrentDomain.UnhandledException += delegate(object sender, UnhandledExceptionEventArgs e)
            {
                Log.W("unhandled: " + (e.ExceptionObject == null ? "?" : e.ExceptionObject.ToString()));
            };
            Application.ThreadException += delegate(object sender, System.Threading.ThreadExceptionEventArgs e)
            {
                Log.W("thread exception: " + e.Exception);
            };

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Log.W("overlay start version=0.3.3 host=" + opts.Host + " prefix=" + opts.Prefix + " dshPid=" + opts.DshPid);
            Application.Run(new PetForm(opts));
            Log.W("overlay exit");
            GC.KeepAlive(mutex);
        }
    }
}