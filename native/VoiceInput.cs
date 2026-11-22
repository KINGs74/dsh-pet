// dsh-pet · Windows 本地语音听写
using System;
#if VOICE
using System.Speech.Recognition;
#endif

namespace DshPet
{
    // One recognizer per recording. Start, stop and disposal run off the UI thread.
    sealed class VoiceInput : IDisposable
    {
        readonly bool _english;
#if VOICE
        readonly object _gate = new object();
        SpeechRecognitionEngine _engine;
        bool _disposed;
#endif

        public event Action<string> Partial;
        public event Action<string> Final;
        public event Action<string> Completed;

        public VoiceInput(bool english)
        {
            _english = english;
        }

        string Localize(string chinese, string english)
        {
            return _english ? english : chinese;
        }

        public string Start()
        {
#if VOICE
            lock (_gate)
            {
                if (_disposed) return "";
                var recognizers = SpeechRecognitionEngine.InstalledRecognizers();
                if (recognizers.Count == 0)
                    throw new InvalidOperationException(Localize(
                        "Windows 没有安装可用的听写引擎。", "Windows has no compatible dictation recognizer."));

                string preferred = _english ? "en" : "zh";
                RecognizerInfo chosen = recognizers[0];
                foreach (RecognizerInfo recognizer in recognizers)
                {
                    if (recognizer.Culture.TwoLetterISOLanguageName == preferred)
                    {
                        chosen = recognizer;
                        break;
                    }
                }

                _engine = new SpeechRecognitionEngine(chosen);
                _engine.LoadGrammar(new DictationGrammar());
                try { _engine.SetInputToDefaultAudioDevice(); }
                catch (Exception)
                {
                    throw new InvalidOperationException(Localize(
                        "请检查 Windows 麦克风权限和默认录音设备。",
                        "Check Windows microphone access and the default recording device."));
                }

                _engine.SpeechHypothesized += delegate(object sender, SpeechHypothesizedEventArgs args)
                {
                    if (args.Result != null) Raise(Partial, args.Result.Text);
                };
                _engine.SpeechRecognized += delegate(object sender, SpeechRecognizedEventArgs args)
                {
                    if (args.Result != null && !String.IsNullOrWhiteSpace(args.Result.Text))
                        Raise(Final, args.Result.Text.Trim());
                };
                _engine.RecognizeCompleted += delegate(object sender, RecognizeCompletedEventArgs args)
                {
                    string error = args.Error == null ? "" :
                        Localize("听写中断：", "Dictation interrupted: ") + args.Error.Message;
                    Raise(Completed, error);
                };
                _engine.RecognizeAsync(RecognizeMode.Multiple);
                return _english ? chosen.Culture.EnglishName : chosen.Culture.NativeName;
            }
#else
            throw new InvalidOperationException(Localize(
                "此版本未编译语音模块，请使用文字输入。",
                "This build has no speech support. You can still type."));
#endif
        }

        public void Stop()
        {
#if VOICE
            lock (_gate)
            {
                if (!_disposed && _engine != null) _engine.RecognizeAsyncStop();
            }
#endif
        }

#if VOICE
        static void Raise(Action<string> handler, string text)
        {
            if (handler != null) handler(text ?? "");
        }
#endif

        public void Dispose()
        {
#if VOICE
            SpeechRecognitionEngine engine;
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                engine = _engine;
                _engine = null;
            }
            if (engine == null) return;
            try { engine.RecognizeAsyncCancel(); }
            catch (InvalidOperationException) { }
            finally
            {
                try { engine.Dispose(); }
                catch (InvalidOperationException) { }
            }
#endif
        }
    }
}
