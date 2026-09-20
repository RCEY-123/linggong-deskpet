// ============================================================================
// Audio.cs —— 音效 / 语音播放（MCI 为主，SoundPlayer 兜底；支持音量与并发管理）
// ----------------------------------------------------------------------------
// 说明：不引入任何第三方解码库。MCI（winmm）原生支持 wav / mp3，
//      且能通过 setaudio volume 做「应用内音量」，符合设置里「角色语音大小」的需求。
// ============================================================================
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace AlDeskPet
{
    public static class Audio
    {
        [DllImport("winmm.dll", CharSet = CharSet.Unicode, EntryPoint = "mciSendStringW")]
        static extern int mciSendString(string command, StringBuilder returnValue, int returnLength, IntPtr hwndCallback);

        class Channel
        {
            public string alias;
            public string file;
            public DateTime openedAt;
            public bool playing;
            public bool isVoice;
        }

        static readonly List<Channel> Channels = new List<Channel>();
        static readonly object Gate = new object();
        static Timer _reaper;
        static int _volume = 80;
        static bool _enabled = true;
        static bool _mciUsable = true;
        static bool _warnedOnce;
        static int _seq;

        public static void Configure(int volume, bool enabled)
        {
            _volume = Math.Max(0, Math.Min(100, volume));
            _enabled = enabled;
            if (_volume == 0) StopAll();
        }

        public static int Volume() { return _volume; }

        /// <summary>播放一个音频文件（异步，不阻塞 UI）。</summary>
        public static void Play(string file)
        {
            Play(file, false);
        }

        public static void Play(string file, bool isVoice)
        {
            if (!_enabled || _volume <= 0) return;
            if (string.IsNullOrEmpty(file) || !File.Exists(file)) return;
            try
            {
                lock (Gate)
                {
                    EnsureReaper();
                    if (isVoice) StopVoices();          // 语音不叠着放
                    if (Channels.Count >= 4) CloseChannel(0);

                    string alias = "alpet" + (++_seq).ToString(CultureInfo.InvariantCulture);
                    if (!MciOpen(file, alias))
                    {
                        FallbackPlay(file);
                        return;
                    }
                    Channel ch = new Channel();
                    ch.alias = alias;
                    ch.file = file;
                    ch.openedAt = DateTime.Now;
                    ch.playing = true;
                    ch.isVoice = isVoice;

                    int vol = _volume * 10;
                    mciSendString("setaudio " + alias + " volume to " + vol.ToString(CultureInfo.InvariantCulture), null, 0, IntPtr.Zero);
                    int rc = mciSendString("play " + alias, null, 0, IntPtr.Zero);
                    if (rc != 0)
                    {
                        mciSendString("close " + alias, null, 0, IntPtr.Zero);
                        FallbackPlay(file);
                        return;
                    }
                    Channels.Add(ch);
                    Log.Debug("播放音频：" + Path.GetFileName(file) + "（音量 " + _volume + "）");
                }
            }
            catch (Exception ex)
            {
                WarnOnce("音频播放失败：" + ex.Message, ex);
            }
        }

        static bool MciOpen(string file, string alias)
        {
            StringBuilder err = new StringBuilder(256);
            string cmd = "open \"" + file + "\" alias " + alias;
            int rc = mciSendString(cmd, null, 0, IntPtr.Zero);
            if (rc == 0) return true;
            // 有些解码器需要显式指定 type
            string ext = Path.GetExtension(file).ToLowerInvariant();
            string type = ext == ".mp3" ? "mpegvideo" : (ext == ".wav" ? "waveaudio" : "");
            if (type.Length > 0)
            {
                rc = mciSendString("open \"" + file + "\" type " + type + " alias " + alias, null, 0, IntPtr.Zero);
                if (rc == 0) return true;
            }
            mciSendString("close " + alias, null, 0, IntPtr.Zero);
            if (!_mciUsable) return false;
            mciSendString("mciGetErrorString " + rc.ToString(CultureInfo.InvariantCulture), err, err.Capacity, IntPtr.Zero);
            Log.Warn("MCI 打开失败：" + Path.GetFileName(file) + " rc=" + rc + " " + err.ToString());
            return false;
        }

        static void FallbackPlay(string file)
        {
            try
            {
                if (Path.GetExtension(file).ToLowerInvariant() == ".wav")
                {
                    System.Media.SoundPlayer player = new System.Media.SoundPlayer(file);
                    player.Play();
                    Log.Debug("使用 SoundPlayer 兜底播放：" + Path.GetFileName(file));
                }
                else
                {
                    WarnOnce("当前系统无法解码该音频（" + Path.GetExtension(file) + "），已跳过：" + Path.GetFileName(file), null);
                }
            }
            catch (Exception ex)
            {
                WarnOnce("兜底播放失败：" + ex.Message, ex);
            }
        }

        static void WarnOnce(string message, Exception ex)
        {
            if (_warnedOnce) return;
            _warnedOnce = true;
            Log.Warn(message);
            if (ex != null) Log.Debug(ex.ToString());
        }

        static void EnsureReaper()
        {
            if (_reaper != null) return;
            _reaper = new Timer();
            _reaper.Interval = 400;
            _reaper.Tick += delegate { Reap(); };
            _reaper.Start();
        }

        static void Reap()
        {
            lock (Gate)
            {
                for (int i = Channels.Count - 1; i >= 0; i--)
                {
                    Channel ch = Channels[i];
                    bool done = false;
                    if ((DateTime.Now - ch.openedAt).TotalSeconds > 180) done = true;   // 保护：异常长音
                    else
                    {
                        StringBuilder sb = new StringBuilder(64);
                        if (mciSendString("status " + ch.alias + " mode", sb, sb.Capacity, IntPtr.Zero) == 0)
                        {
                            string mode = sb.ToString().Trim().ToLowerInvariant();
                            if (mode.Length == 0 || mode == "stopped" || mode == "not ready") done = true;
                        }
                        else done = true;
                    }
                    if (done) CloseChannel(i);
                }
                if (Channels.Count == 0 && _reaper != null)
                {
                    _reaper.Stop();
                    _reaper.Dispose();
                    _reaper = null;
                }
            }
        }

        static void CloseChannel(int index)
        {
            if (index < 0 || index >= Channels.Count) return;
            Channel ch = Channels[index];
            try { mciSendString("close " + ch.alias, null, 0, IntPtr.Zero); }
            catch { }
            Channels.RemoveAt(index);
        }

        public static void StopAll()
        {
            lock (Gate)
            {
                for (int i = Channels.Count - 1; i >= 0; i--) CloseChannel(i);
            }
        }

        static void StopVoices()
        {
            lock (Gate)
            {
                for (int i = Channels.Count - 1; i >= 0; i--)
                    if (Channels[i].isVoice) CloseChannel(i);
            }
        }

        public static bool IsVoicePlaying()
        {
            lock (Gate)
            {
                foreach (Channel c in Channels) if (c.isVoice && c.playing) return true;
            }
            return false;
        }

        /// <summary>
        /// 停止某一个文件的播放（桌宠进入后台 / 冻结时把自己的语音收掉）。
        /// 注意：MCI 的别名是进程级的，同一文件被两只桌宠同时播时会一起停——
        /// 这种情况只可能出现在「同一个角色放了两次且台词相同」，可以接受。
        /// </summary>
        public static void StopFile(string file)
        {
            if (string.IsNullOrEmpty(file)) return;
            try
            {
                lock (Gate)
                {
                    for (int i = Channels.Count - 1; i >= 0; i--)
                    {
                        if (string.Equals(Channels[i].file, file, StringComparison.OrdinalIgnoreCase))
                            CloseChannel(i);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Debug("停止指定音频异常：" + ex.Message);
            }
        }

        /// <summary>指定的音频文件是否正在播放（用于「上一句还没说完就不切下一句」）。</summary>
        public static bool IsPlaying(string file)
        {
            if (string.IsNullOrEmpty(file)) return false;
            lock (Gate)
            {
                foreach (Channel c in Channels)
                {
                    if (!c.playing) continue;
                    if (string.Equals(c.file, file, StringComparison.OrdinalIgnoreCase)) return true;
                }
            }
            return false;
        }

        /// <summary>解析音效文件：优先角色自定义，其次内置预设，最后内置默认。</summary>
        public static string ResolveSfx(CharacterProfile p, string kind)
        {
            try
            {
                if (p != null && p.sfx != null && p.sfx.enabled)
                {
                    string custom = "";
                    if (kind == "press") custom = p.sfx.press;
                    else if (kind == "release") custom = p.sfx.release;
                    else if (kind == "bubble") custom = p.sfx.bubble;

                    if (!string.IsNullOrEmpty(custom))
                    {
                        if (Path.IsPathRooted(custom) && File.Exists(custom)) return custom;
                        string inChar = Path.Combine(CharacterStore.SfxDir(p.id), custom);
                        if (File.Exists(inChar)) return inChar;
                        string inBuiltin = Path.Combine(AppPaths.BuiltinDir(), "sfx", custom);
                        if (File.Exists(inBuiltin)) return inBuiltin;
                    }
                }
                return PresetSfx(p == null || p.sfx == null ? "fx1" : p.sfx.preset, kind);
            }
            catch { return ""; }
        }

        /// <summary>内置音效预设（与参考插件一致：音效1 = D1/D2，小黄鸭 = Ya1/Ya2）。</summary>
        public static string PresetSfx(string preset, string kind)
        {
            string dir = Path.Combine(AppPaths.BuiltinDir(), "sfx");
            string name;
            if (preset == "none") return "";
            if (preset == "duck")
                name = kind == "press" ? "press-duck.mp3" : (kind == "release" ? "release-duck.mp3" : "press-duck.mp3");
            else
                name = kind == "press" ? "press.mp3" : (kind == "release" ? "release.mp3" : "press.mp3");

            string path = Path.Combine(dir, name);
            if (File.Exists(path)) return path;

            // 兜底：内置目录里可能有同族其它名字
            string alt = Path.Combine(dir, kind == "press" ? "press-duck.mp3" : "release-duck.mp3");
            if (File.Exists(alt)) return alt;
            return "";
        }

        /// <summary>可用音频文件列表（供 UI 选择）。</summary>
        public static bool IsAudioFile(string file)
        {
            return VoiceBankLoader.IsAudio(file);
        }

        /// <summary>自检：验证 MCI 是否可用（不依赖声卡是否真的响）。</summary>
        public static string SelfTest()
        {
            try
            {
                StringBuilder sb = new StringBuilder(128);
                int rc = mciSendString("sysinfo all quantity", sb, sb.Capacity, IntPtr.Zero);
                if (rc != 0) return "MCI 不可用（rc=" + rc + "）：音效将静默降级，不影响其它功能。";
                return "MCI 可用（系统 MCI 设备数 " + sb.ToString().Trim() + "）";
            }
            catch (Exception ex)
            {
                return "MCI 自检异常：" + ex.Message;
            }
        }
    }
}
