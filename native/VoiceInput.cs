// dsh-pet · 语音输入（System.Speech）
//
// 编译时用 /define:VOICE 打开；拿不到 System.Speech.dll 的机器上会退化成"不可用"，
// 点麦克风只会弹一句提示，不影响别的功能。
using System;
#if VOICE
using System.Speech.Recognition;
#endif

namespace DshPet
{
    sealed class VoiceInput : IDisposable
    {
        public event Action<string> Partial;
        public event Action<string> Final;
        public event Action<string> Failed;

#if VOICE
        SpeechRecognitionEngine _engine;
        bool _recording;
        string _pendingReason = "";
#endif

        public bool Available
        {
            get
            {
#if VOICE
                try
                {
                    return SpeechRecognitionEngine.InstalledRecognizers().Count > 0;
                }
                catch
                {
                    return false;
                }
#else
                return false;
#endif
            }
        }

        public string UnavailableReason
        {
            get
            {
#if VOICE
                try
                {
                    if (SpeechRecognitionEngine.InstalledRecognizers().Count == 0)
                    {
                        return "Windows 里没有安装语音识别引擎，无法语音输入。";
                    }
                }
                catch
                {
                }
                return _pendingReason.Length > 0 ? _pendingReason : "语音输入暂时不可用。";
#else
                return "这版浮层没有编译语音模块（缺少 System.Speech），请用文字输入。";
#endif
            }
        }

        public bool Recording
        {
            get
            {
#if VOICE
                return _recording;
#else
                return false;
#endif
            }
        }

        public void Reset()
        {
#if VOICE
            _pendingReason = "";
#endif
        }

        public void Start()
        {
#if VOICE
            if (_recording) return;
            try
            {
                if (_engine == null) _engine = CreateEngine();
                if (_engine == null)
                {
                    _pendingReason = "语音引擎创建失败。";
                    Raise(Failed, _pendingReason);
                    return;
                }
                _engine.RecognizeAsync(RecognizeMode.Multiple);
                _recording = true;
            }
            catch (Exception error)
            {
                _pendingReason = "启动语音识别失败：" + error.Message;
                Raise(Failed, _pendingReason);
            }
#else
            Raise(Failed, UnavailableReason);
#endif
        }

        public void Stop()
        {
#if VOICE
            if (!_recording) return;
            _recording = false;
            try
            {
                if (_engine != null) _engine.RecognizeAsyncCancel();
            }
            catch
            {
            }
#endif
        }

#if VOICE
        SpeechRecognitionEngine CreateEngine()
        {
            System.Collections.Generic.IReadOnlyList<RecognizerInfo> infos = SpeechRecognitionEngine.InstalledRecognizers();
            if (infos == null || infos.Count == 0) return null;
            RecognizerInfo chosen = infos[0];
            for (int i = 0; i < infos.Count; i++)
            {
                if (infos[i].Culture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
                {
                    chosen = infos[i];
                    break;
                }
            }
            SpeechRecognitionEngine engine = new SpeechRecognitionEngine(chosen);
            engine.LoadGrammar(new DictationGrammar());
            engine.SetInputToDefaultAudioDevice();
            engine.SpeechHypothesized += delegate(object sender, SpeechHypothesizedEventArgs args)
            {
                if (!_recording || args.Result == null) return;
                Raise(Partial, args.Result.Text);
            };
            engine.SpeechRecognized += delegate(object sender, SpeechRecognizedEventArgs args)
            {
                if (!_recording || args.Result == null) return;
                string text = args.Result.Text;
                if (text == null || text.Trim().Length == 0) return;
                Raise(Final, text.Trim());
            };
            engine.RecognizeCompleted += delegate(object sender, RecognizeCompletedEventArgs args)
            {
                if (args.Error != null && _recording)
                {
                    _pendingReason = "识别中断：" + args.Error.Message;
                    Raise(Failed, _pendingReason);
                }
            };
            return engine;
        }
#endif

        void Raise(Action<string> handler, string text)
        {
            if (handler != null) handler(text);
        }

        public void Dispose()
        {
#if VOICE
            Stop();
            if (_engine != null)
            {
                try
                {
                    _engine.Dispose();
                }
                catch
                {
                }
                _engine = null;
            }
#endif
        }
    }
}