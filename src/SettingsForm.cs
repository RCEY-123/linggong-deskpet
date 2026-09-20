// ============================================================================
// SettingsForm.cs —— 齿轮设置界面（需求 2-7）
//   子菜单：声音 / 大模型 API / 保存路径 / 日志 / 常规 / 版本与作者
// ============================================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;

namespace AlDeskPet
{
    public class SettingsForm : Form
    {
        readonly AlTabStrip _tabs = new AlTabStrip();
        readonly Panel _body = new Panel();
        readonly Dictionary<string, Panel> _pages = new Dictionary<string, Panel>();
        readonly string[] _ids = new string[] { "sound", "llm", "path", "log", "general", "display", "about" };
        readonly string[] _titles = new string[] { "声音", "大模型 API", "保存路径", "日志", "常规", "桌宠显示", "版本与作者" };

        // 声音
        TrackBar _volume;
        Label _volumeLabel;
        AlCheck _sfxOn, _voiceOn;
        // 大模型
        AlCombo _preset;
        TextBox _baseUrl, _apiKey, _model, _sysExtra;
        TrackBar _temp;
        Label _tempLabel;
        NumericUpDown _maxTokens, _timeout, _history;
        Label _testResult;
        // 日志
        TextBox _logView;
        AlCheck _verbose;
        NumericUpDown _keepDays;
        // 常规
        AlCheck _autoStart, _tray, _startUi;
        // 桌宠显示
        AlButton[] _displayBtns = new AlButton[3];
        AlCheck _hideFullscreen, _hidePopups;
        Label _displayStatus;
        // 关于
        Label _aboutInfo;

        public SettingsForm(string initialTab)
        {
            Text = AppPaths.ProductName + " · 设置";
            ClientSize = new Size(880, 620);
            StartPosition = FormStartPosition.CenterParent;
            MinimumSize = new Size(760, 560);
            BackColor = Theme.Navy;
            ForeColor = Theme.TextMain;
            Font = Theme.Body(13.5f);
            DoubleBuffered = true;
            // 与主界面同理：尺寸变化时整块重绘，避免标题栏文字残影
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            Panel head = new Panel();
            head.SetBounds(0, 0, ClientSize.Width, 62);
            head.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            head.Paint += delegate (object s, PaintEventArgs e)
            {
                Theme.FillGradient(e.Graphics, new Rectangle(0, 0, head.Width, head.Height), Theme.Navy2, Theme.Navy, 90f);
                using (Pen pen = new Pen(Theme.Gold, 1.4f)) e.Graphics.DrawLine(pen, 0, head.Height - 1, head.Width, head.Height - 1);
                Theme.DrawGlowText(e.Graphics, "设置", Theme.TitleBold(21f), Theme.GoldBright, new Rectangle(18, 0, 300, head.Height), StringAlignment.Near);
                using (SolidBrush br = new SolidBrush(Color.FromArgb(220, 214, 160)))
                    e.Graphics.DrawString(MainForm.UiCornerText1 + "  " + MainForm.UiCornerText2, Theme.Title(15f), br,
                        new PointF(head.Width - 190, 22));
            };
            Controls.Add(head);

            _tabs.SetBounds(16, 74, ClientSize.Width - 32, 38);
            _tabs.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            _tabs.SetItems(_titles);
            _tabs.SelectedIndexChanged += delegate { ShowPage(); };
            Controls.Add(_tabs);

            _body.SetBounds(16, 118, ClientSize.Width - 32, ClientSize.Height - 190);
            _body.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            _body.BackColor = Theme.Navy;
            Controls.Add(_body);

            Panel[] pages = new Panel[] { BuildSound(), BuildLlm(), BuildPath(), BuildLog(), BuildGeneral(), BuildDisplay(), BuildAbout() };
            for (int i = 0; i < pages.Length; i++)
            {
                pages[i].Dock = DockStyle.Fill;
                pages[i].Visible = false;
                pages[i].BackColor = Theme.Navy;
                _pages[_ids[i]] = pages[i];
                _body.Controls.Add(pages[i]);
            }

            AlButton close = new AlButton("保存并关闭");
            close.Primary = true;
            close.SetBounds(ClientSize.Width - 156, ClientSize.Height - 58, 140, 42);
            close.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            close.Click += delegate { SaveAll(); Close(); };
            Controls.Add(close);

            AlButton openLog = new AlButton("打开日志文件夹");
            openLog.SetBounds(ClientSize.Width - 296, ClientSize.Height - 58, 132, 42);
            openLog.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            openLog.Click += delegate { ErrorDialogForm.OpenFolder(AppPaths.LogDir()); };
            Controls.Add(openLog);

            LoadValues();
            ShowPageById(initialTab);
            FormClosing += delegate { SaveAll(); };
        }

        void ShowPage()
        {
            int idx = _tabs.SelectedIndex;
            for (int i = 0; i < _ids.Length; i++)
            {
                Panel p;
                if (_pages.TryGetValue(_ids[i], out p)) p.Visible = i == idx;
            }
        }

        public void ShowPageById(string id)
        {
            if (string.IsNullOrEmpty(id)) return;
            for (int i = 0; i < _ids.Length; i++)
                if (_ids[i] == id) { _tabs.SelectedIndex = i; ShowPage(); return; }
        }

        // ---- 供自检使用的探针 ----

        /// <summary>设置页的 id 列表（顺序与页签一致）。</summary>
        public string[] PageIds() { return (string[])_ids.Clone(); }

        /// <summary>按 id 取设置页，取不到返回 null。</summary>
        public Panel PageById(string id)
        {
            Panel p;
            return _pages.TryGetValue(id, out p) ? p : null;
        }

        /// <summary>当前显示方式的文案（自检与日志用）。</summary>
        public string DisplayStatusText()
        {
            return _displayStatus == null ? "" : _displayStatus.Text;
        }

        /// <summary>三个显示方式按钮里第 index 个（自检用）。</summary>
        public AlButton DisplayModeButton(int index)
        {
            if (index < 0 || index >= _displayBtns.Length) return null;
            return _displayBtns[index];
        }

        // ==================== 声音 ====================

        Panel BuildSound()
        {
            Panel p = new Panel();
            AlPanel card = Ui.Card("角色语音 / 互动音效音量", 0, 0, _body.Width - 4, 190);
            card.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            p.Controls.Add(card);

            Label l = Ui.L("音量", 14f, Theme.TextMain, false);
            l.SetBounds(20, 50, 60, 24);
            card.Controls.Add(l);

            _volume = Ui.Slider(0, 100, Config.Current.volume);
            _volume.SetBounds(80, 46, 420, 30);
            _volume.ValueChanged += delegate
            {
                Config.Current.volume = _volume.Value;
                _volumeLabel.Text = _volume.Value + "%";
                Audio.Configure(_volume.Value, Config.Current.sfxEnabled);
            };
            card.Controls.Add(_volume);

            _volumeLabel = Ui.L("80%", 14f, Theme.GoldBright, true);
            _volumeLabel.SetBounds(510, 50, 70, 24);
            card.Controls.Add(_volumeLabel);

            AlButton test = new AlButton("试听音效");
            test.SetBounds(20, 96, 130, 36);
            test.Click += delegate
            {
                string f = Audio.PresetSfx("fx1", "press");
                if (string.IsNullOrEmpty(f)) { MessageBox.Show(this, "内置音效文件缺失（assets\\builtin\\sfx）。", AppPaths.ProductName); return; }
                Audio.Play(f);
            };
            card.Controls.Add(test);

            AlButton test2 = new AlButton("试听语音");
            test2.SetBounds(160, 96, 130, 36);
            test2.Click += delegate
            {
                CharacterProfile p2 = CharacterStore.Load(Config.Current.activeCharacter);
                if (p2 == null || p2.voices.entries.Count == 0) { MessageBox.Show(this, "当前角色还没有导入语音。", AppPaths.ProductName); return; }
                VoiceEntry e = p2.voices.entries[Utils.Next(0, p2.voices.entries.Count)];
                Audio.Play(Path.Combine(CharacterStore.VoicesDir(p2.id), e.file), true);
            };
            card.Controls.Add(test2);

            _sfxOn = new AlCheck("启用互动音效（按压 / 松开 / 气泡出现）");
            _sfxOn.SetBounds(20, 142, 380, 24);
            _sfxOn.CheckedChanged += delegate
            {
                Config.Current.sfxEnabled = _sfxOn.Checked;
                Audio.Configure(Config.Current.volume, _sfxOn.Checked);
            };
            card.Controls.Add(_sfxOn);

            _voiceOn = new AlCheck("启用角色语音");
            _voiceOn.SetBounds(420, 142, 240, 24);
            _voiceOn.CheckedChanged += delegate { Config.Current.voiceEnabled = _voiceOn.Checked; };
            card.Controls.Add(_voiceOn);

            Label tip = Ui.L("音量对角色语音与互动音效同时生效；单个角色是否使用音效，在「互动音效」页里另有开关。",
                12f, Theme.TextDim, false);
            tip.SetBounds(20, 210, 780, 24);
            p.Controls.Add(tip);
            return p;
        }

        // ==================== 大模型 API ====================

        Panel BuildLlm()
        {
            Panel p = new Panel();
            AlPanel card = Ui.Card("接入大模型（兼容本地与云端，OpenAI 兼容接口）", 0, 0, _body.Width - 4, _body.Height - 4);
            card.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            p.Controls.Add(card);

            Label l = Ui.L("预设", 12.5f, Theme.TextDim, false);
            l.SetBounds(20, 44, 60, 22);
            card.Controls.Add(l);

            _preset = Ui.C();
            foreach (LlmPreset lp in Llm.Presets()) _preset.Items.Add(lp.title);
            _preset.SetBounds(80, 40, 300, 26);
            _preset.SelectedIndexChanged += delegate
            {
                List<LlmPreset> list = Llm.Presets();
                if (_preset.SelectedIndex < 0 || _preset.SelectedIndex >= list.Count) return;
                LlmPreset chosen = list[_preset.SelectedIndex];
                _baseUrl.Text = chosen.baseUrl;
                _model.Text = chosen.model;
                if (chosen.provider == "local") _apiKey.Text = "";
                _hintLabel.Text = chosen.hint + (chosen.needKey ? "（需要 API Key）" : "（本地服务通常不需要 Key）");
            };
            card.Controls.Add(_preset);

            _hintLabel = Ui.L("", 12f, Theme.Gold, false);
            _hintLabel.SetBounds(394, 44, 430, 22);
            card.Controls.Add(_hintLabel);

            Label l2 = Ui.L("接口地址", 12.5f, Theme.TextDim, false);
            l2.SetBounds(20, 82, 60, 22);
            card.Controls.Add(l2);
            _baseUrl = Ui.T("", false, false);
            _baseUrl.SetBounds(80, 78, 744, 26);
            _baseUrl.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            card.Controls.Add(_baseUrl);

            Label l3 = Ui.L("API Key", 12.5f, Theme.TextDim, false);
            l3.SetBounds(20, 118, 60, 22);
            card.Controls.Add(l3);
            _apiKey = Ui.T("", false, false);
            _apiKey.UseSystemPasswordChar = true;
            _apiKey.SetBounds(80, 114, 420, 26);
            card.Controls.Add(_apiKey);

            AlButton show = new AlButton("显示");
            show.SetBounds(510, 112, 60, 30);
            show.Click += delegate { _apiKey.UseSystemPasswordChar = !_apiKey.UseSystemPasswordChar; show.Text = _apiKey.UseSystemPasswordChar ? "显示" : "隐藏"; };
            card.Controls.Add(show);

            Label keyTip = Ui.L("本地服务（Ollama / LM Studio / OneAPI）一般留空", 11.5f, Theme.TextDim, false);
            keyTip.SetBounds(580, 118, 250, 22);
            card.Controls.Add(keyTip);

            Label l4 = Ui.L("模型名", 12.5f, Theme.TextDim, false);
            l4.SetBounds(20, 154, 60, 22);
            card.Controls.Add(l4);
            _model = Ui.T("", false, false);
            _model.SetBounds(80, 150, 250, 26);
            card.Controls.Add(_model);

            Label l5 = Ui.L("超时(秒)", 12.5f, Theme.TextDim, false);
            l5.SetBounds(350, 154, 70, 22);
            card.Controls.Add(l5);
            _timeout = Ui.Num(5, 600, 60, 5);
            _timeout.SetBounds(424, 150, 70, 26);
            card.Controls.Add(_timeout);

            Label l6 = Ui.L("上下文轮数", 12.5f, Theme.TextDim, false);
            l6.SetBounds(510, 154, 90, 22);
            card.Controls.Add(l6);
            _history = Ui.Num(0, 50, 8, 1);
            _history.SetBounds(604, 150, 70, 26);
            card.Controls.Add(_history);

            Label l7 = Ui.L("温度", 12.5f, Theme.TextDim, false);
            l7.SetBounds(20, 192, 60, 22);
            card.Controls.Add(l7);
            _temp = Ui.Slider(0, 200, (int)Math.Round(Config.Current.llm.temperature * 100));
            _temp.SetBounds(80, 188, 300, 30);
            _temp.ValueChanged += delegate { _tempLabel.Text = (_temp.Value / 100.0).ToString("0.00"); };
            card.Controls.Add(_temp);
            _tempLabel = Ui.L("0.85", 12.5f, Theme.GoldBright, true);
            _tempLabel.SetBounds(388, 192, 60, 22);
            card.Controls.Add(_tempLabel);

            Label l8 = Ui.L("回复上限 tokens", 12.5f, Theme.TextDim, false);
            l8.SetBounds(460, 192, 120, 22);
            card.Controls.Add(l8);
            _maxTokens = Ui.Num(32, 4096, 220, 16);
            _maxTokens.SetBounds(584, 188, 80, 26);
            card.Controls.Add(_maxTokens);

            Label l9 = Ui.L("全局附加提示词（会拼在每个角色卡的后面）", 12.5f, Theme.TextDim, false);
            l9.SetBounds(20, 228, 400, 22);
            card.Controls.Add(l9);
            _sysExtra = Ui.T("", true, false);
            _sysExtra.SetBounds(20, 252, 804, 96);
            _sysExtra.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            card.Controls.Add(_sysExtra);

            AlButton testBtn = new AlButton("测试连接");
            testBtn.Primary = true;
            testBtn.SetBounds(20, 360, 130, 38);
            testBtn.Click += delegate { TestConnection(); };
            card.Controls.Add(testBtn);

            AlButton saveBtn = new AlButton("保存这些设置");
            saveBtn.SetBounds(160, 360, 140, 38);
            saveBtn.Click += delegate { SaveLlm(); MessageBox.Show(this, "已保存。", AppPaths.ProductName); };
            card.Controls.Add(saveBtn);

            _testResult = Ui.L("", 12.5f, Theme.TextDim, false);
            _testResult.SetBounds(316, 366, 508, 60);
            card.Controls.Add(_testResult);

            return p;
        }

        Label _hintLabel;

        void TestConnection()
        {
            SaveLlm();
            Cursor = Cursors.WaitCursor;
            _testResult.ForeColor = Theme.Gold;
            _testResult.Text = "正在测试…";
            Application.DoEvents();
            LlmResult r = Llm.TestConnection(Config.Current.llm);
            Cursor = Cursors.Default;
            Config.Current.llm.lastTestTime = Utils.NowStamp();
            Config.Current.llm.lastTestResult = r.ok ? r.text : r.error;
            Config.Save();
            _testResult.ForeColor = r.ok ? Theme.Ok : Theme.Rouge;
            _testResult.Text = r.ok ? r.text : r.error;
        }

        void SaveLlm()
        {
            Config.Current.llm.baseUrl = _baseUrl.Text.Trim();
            Config.Current.llm.apiKey = _apiKey.Text.Trim();
            Config.Current.llm.model = _model.Text.Trim();
            Config.Current.llm.temperature = _temp.Value / 100.0;
            Config.Current.llm.maxTokens = (int)_maxTokens.Value;
            Config.Current.llm.timeoutSec = (int)_timeout.Value;
            Config.Current.llm.historyTurns = (int)_history.Value;
            Config.Current.llm.systemExtra = _sysExtra.Text.Trim();
            List<LlmPreset> list = Llm.Presets();
            if (_preset.SelectedIndex >= 0 && _preset.SelectedIndex < list.Count)
            {
                Config.Current.llm.provider = list[_preset.SelectedIndex].provider;
            }
            Config.Save();
        }

        // ==================== 保存路径 ====================

        Panel BuildPath()
        {
            Panel p = new Panel();
            AlPanel card = Ui.Card("角色信息保存路径", 0, 0, _body.Width - 4, 300);
            card.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            p.Controls.Add(card);

            Label l = Ui.L("当前数据目录", 12.5f, Theme.TextDim, false);
            l.SetBounds(20, 46, 100, 22);
            card.Controls.Add(l);

            TextBox box = Ui.T(AppPaths.DataDir(), false, true);
            box.SetBounds(20, 70, 804, 26);
            box.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            card.Controls.Add(box);

            Label l2 = Ui.L("这里保存：config.json（全局设置）、characters\\（每个角色的立绘 / 台词 / 语音 / 音效 / 角色卡）、logs\\（日志）",
                12f, Theme.TextDim, false);
            l2.SetBounds(20, 104, 800, 22);
            card.Controls.Add(l2);

            AlButton change = new AlButton("更改目录（自动迁移）…");
            change.Primary = true;
            change.SetBounds(20, 140, 220, 38);
            change.Click += delegate
            {
                using (FolderBrowserDialog dlg = new FolderBrowserDialog())
                {
                    dlg.Description = "选择角色信息的保存目录（会自动把现有角色与日志迁移过去）";
                    dlg.ShowNewFolderButton = true;
                    if (dlg.ShowDialog(this) != DialogResult.OK) return;
                    string message;
                    if (Config.MoveDataDir(dlg.SelectedPath, out message))
                    {
                        box.Text = AppPaths.DataDir();
                        MessageBox.Show(this, message, AppPaths.ProductName);
                    }
                    else MessageBox.Show(this, message, AppPaths.ProductName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            };
            card.Controls.Add(change);

            AlButton open = new AlButton("打开目录");
            open.SetBounds(250, 140, 120, 38);
            open.Click += delegate { ErrorDialogForm.OpenFolder(AppPaths.DataDir()); };
            card.Controls.Add(open);

            AlButton reset = new AlButton("恢复默认目录");
            reset.SetBounds(380, 140, 140, 38);
            reset.Click += delegate
            {
                string message;
                if (Config.MoveDataDir(AppPaths.DefaultDataDir(), out message))
                {
                    box.Text = AppPaths.DataDir();
                    MessageBox.Show(this, message, AppPaths.ProductName);
                }
                else MessageBox.Show(this, message, AppPaths.ProductName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            };
            card.Controls.Add(reset);

            Label tip = Ui.L("提示：目录里包含你导入的全部素材，换电脑时直接整目录拷走即可。卸载程序会一并清除这些文件。",
                12f, Theme.TextDim, false);
            tip.SetBounds(20, 196, 800, 24);
            card.Controls.Add(tip);

            return p;
        }

        // ==================== 日志 ====================

        Panel BuildLog()
        {
            Panel p = new Panel();
            AlPanel card = Ui.Card("日志", 0, 0, _body.Width - 4, _body.Height - 4);
            card.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            p.Controls.Add(card);

            Label l = Ui.L("日志文件", 12.5f, Theme.TextDim, false);
            l.SetBounds(20, 44, 80, 22);
            card.Controls.Add(l);

            TextBox path = Ui.T(Log.CurrentLogFile(), false, true);
            path.SetBounds(100, 40, 724, 26);
            path.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            card.Controls.Add(path);

            _logView = Ui.T("", true, true);
            _logView.SetBounds(20, 78, 804, 200);
            _logView.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            card.Controls.Add(_logView);

            AlButton refresh = new AlButton("刷新");
            refresh.SetBounds(20, 292, 100, 36);
            refresh.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            refresh.Click += delegate { LoadLogTail(); };
            card.Controls.Add(refresh);

            AlButton openFile = new AlButton("用记事本打开");
            openFile.SetBounds(130, 292, 140, 36);
            openFile.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            openFile.Click += delegate
            {
                try { Process.Start("notepad.exe", "\"" + Log.CurrentLogFile() + "\""); }
                catch (Exception ex) { Log.Warn("打开日志失败：" + ex.Message); }
            };
            card.Controls.Add(openFile);

            AlButton exportLog = new AlButton("导出日志…");
            exportLog.SetBounds(280, 292, 120, 36);
            exportLog.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            exportLog.Click += delegate { ExportLog(); };
            card.Controls.Add(exportLog);

            _verbose = new AlCheck("记录详细日志（排查问题时打开）");
            _verbose.SetBounds(420, 296, 300, 24);
            _verbose.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            _verbose.CheckedChanged += delegate
            {
                Config.Current.logVerbose = _verbose.Checked;
                Log.Verbose = _verbose.Checked;
                Config.Save();
            };
            card.Controls.Add(_verbose);

            Label l2 = Ui.L("保留天数", 12.5f, Theme.TextDim, false);
            l2.SetBounds(20, 340, 80, 22);
            l2.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            card.Controls.Add(l2);

            _keepDays = Ui.Num(1, 365, Config.Current.logKeepDays, 1);
            _keepDays.SetBounds(100, 336, 70, 26);
            _keepDays.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            card.Controls.Add(_keepDays);

            AlButton clean = new AlButton("立即清理旧日志");
            clean.SetBounds(184, 334, 150, 34);
            clean.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            clean.Click += delegate
            {
                Config.Current.logKeepDays = (int)_keepDays.Value;
                Config.Save();
                Log.CleanupOldLogs((int)_keepDays.Value);
                MessageBox.Show(this, "已清理超过 " + _keepDays.Value + " 天的日志。", AppPaths.ProductName);
                LoadLogTail();
            };
            card.Controls.Add(clean);

            Label tip = Ui.L("出错时会自动弹窗并显示日志路径：那个弹窗里可以直接复制路径或打开日志，把日志发给 AI 或技术人员即可。",
                12f, Theme.TextDim, false);
            tip.SetBounds(20, 372, 804, 24);
            tip.Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            card.Controls.Add(tip);

            LoadLogTail();
            return p;
        }

        void LoadLogTail()
        {
            try
            {
                string file = Log.CurrentLogFile();
                if (!File.Exists(file)) { _logView.Text = "（今天还没有日志）"; return; }
                string[] lines = File.ReadAllLines(file, Encoding.UTF8);
                int take = Math.Min(lines.Length, 120);
                StringBuilder sb = new StringBuilder();
                for (int i = lines.Length - take; i < lines.Length; i++) sb.AppendLine(lines[i]);
                _logView.Text = sb.ToString();
                _logView.SelectionStart = _logView.TextLength;
                _logView.ScrollToCaret();
            }
            catch (Exception ex)
            {
                _logView.Text = "读取日志失败：" + ex.Message;
            }
        }

        void ExportLog()
        {
            using (SaveFileDialog dlg = new SaveFileDialog())
            {
                dlg.Title = "导出日志（可直接发给 AI 或技术人员）";
                dlg.Filter = "日志文件|*.log;*.txt";
                dlg.FileName = "灵工桌宠-日志-" + Utils.NowFileStamp() + ".txt";
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    StringBuilder sb = new StringBuilder();
                    sb.AppendLine("灵工桌宠 诊断日志");
                    sb.AppendLine("版本：" + AppPaths.Version + "  作者：" + AppPaths.Author);
                    sb.AppendLine("时间：" + Utils.NowStamp());
                    sb.AppendLine("安装目录：" + AppPaths.InstallDir());
                    sb.AppendLine("数据目录：" + AppPaths.DataDir());
                    sb.AppendLine("系统：" + Environment.OSVersion.VersionString + " / " + (Environment.Is64BitProcess ? "x64" : "x86"));
                    sb.AppendLine("音频自检：" + Audio.SelfTest());
                    sb.AppendLine("---------------- 日志 ----------------");
                    if (File.Exists(Log.CurrentLogFile())) sb.AppendLine(File.ReadAllText(Log.CurrentLogFile(), Encoding.UTF8));
                    File.WriteAllText(dlg.FileName, sb.ToString(), new UTF8Encoding(true));
                    MessageBox.Show(this, "已导出：\r\n" + dlg.FileName, AppPaths.ProductName);
                }
                catch (Exception ex)
                {
                    Log.ErrorDialog("导出日志失败", ex);
                }
            }
        }

        // ==================== 常规 ====================

        Panel BuildGeneral()
        {
            Panel p = new Panel();
            AlPanel card = Ui.Card("常规", 0, 0, _body.Width - 4, 260);
            card.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            p.Controls.Add(card);

            _autoStart = new AlCheck("开机自动启动桌宠（写入当前用户启动项，不需要管理员权限）");
            _autoStart.SetBounds(20, 44, 500, 24);
            _autoStart.CheckedChanged += delegate
            {
                Config.Current.autoStart = _autoStart.Checked;
                Config.Save();
                ApplyAutoStart(_autoStart.Checked);
            };
            card.Controls.Add(_autoStart);

            _tray = new AlCheck("在系统托盘显示图标（方便随时打开设置界面）");
            _tray.SetBounds(20, 80, 500, 24);
            _tray.CheckedChanged += delegate
            {
                Config.Current.showTray = _tray.Checked;
                Config.Save();
                if (_tray.Checked) PetManager.EnsureTray();
                else PetManager.DisposeTray();
            };
            card.Controls.Add(_tray);

            _startUi = new AlCheck("每次启动都先打开这个设置界面（关闭则直接显示桌宠）");
            _startUi.SetBounds(20, 116, 500, 24);
            _startUi.CheckedChanged += delegate
            {
                Config.Current.firstRunDone = !_startUi.Checked;
                Config.Save();
            };
            card.Controls.Add(_startUi);

            Label tip = Ui.L("提示：桌宠默认会记住上次的位置；位置乱了可以在桌宠右键菜单里「重置位置」。",
                12f, Theme.TextDim, false);
            tip.SetBounds(20, 156, 780, 24);
            card.Controls.Add(tip);

            AlButton resetAll = new AlButton("重置全部设置为默认值");
            resetAll.Danger = true;
            resetAll.SetBounds(20, 190, 220, 38);
            resetAll.Click += delegate
            {
                if (MessageBox.Show(this, "重置全局设置（音量 / 大模型 / 位置等）？\r\n\r\n角色、台词、语音等素材不会被删除。",
                    AppPaths.ProductName, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
                string dataDir = Config.Current.dataDir;
                string active = Config.Current.activeCharacter;
                List<string> order = Config.Current.characterOrder;
                Config.Current = new AppSettings();
                Config.Current.dataDir = dataDir;
                Config.Current.activeCharacter = active;
                Config.Current.characterOrder = order;
                Config.Save();
                LoadValues();
                UpdateDisplayStatus();
                PetManager.ApplyDisplaySettings();
                MessageBox.Show(this, "已重置。", AppPaths.ProductName);
            };
            card.Controls.Add(resetAll);

            return p;
        }

        // ==================== 桌宠显示 ====================

        Panel BuildDisplay()
        {
            Panel p = new Panel();
            AlPanel card = Ui.Card("桌宠显示方式（也可以直接在桌宠上右键 →「桌宠显示」切换）", 0, 0, _body.Width - 4, 240);
            card.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            p.Controls.Add(card);

            string[] ids = PetDisplayMode.Ids();
            string[] titles = PetDisplayMode.Titles();
            string[] details = PetDisplayMode.Details();
            int detailW = Math.Max(220, card.Width - 356);
            for (int i = 0; i < ids.Length && i < _displayBtns.Length; i++)
            {
                string captured = ids[i];
                int y = 42 + i * 58;
                AlButton b = new AlButton(titles[i]);
                b.ShowCheck = true;                    // 金色对勾 = 当前选中
                b.SetBounds(20, y, 300, 44);
                b.Click += delegate { SetDisplayMode(captured); };
                card.Controls.Add(b);
                _displayBtns[i] = b;

                Label d = Ui.L(details[i], 11.5f, Theme.TextDim, false);
                d.AutoSize = false;                    // 需要按宽度折行
                d.SetBounds(334, y - 4, detailW, 54);
                card.Controls.Add(d);
            }

            _displayStatus = Ui.L("", 12.5f, Theme.GoldBright, true);
            _displayStatus.SetBounds(20, 212, Math.Max(300, card.Width - 40), 22);
            card.Controls.Add(_displayStatus);

            AlPanel extra = Ui.Card("附加行为", 0, 248, _body.Width - 4, 168);
            extra.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            p.Controls.Add(extra);

            _hideFullscreen = new AlCheck("其他程序全屏时自动隐藏桌宠（进后台运行，回到桌面再重新显示）");
            _hideFullscreen.SetBounds(20, 46, 600, 24);
            _hideFullscreen.CheckedChanged += delegate
            {
                Config.Current.hideOnFullscreen = _hideFullscreen.Checked;
                Config.Save();
                PetManager.ApplyDisplaySettings();
                UpdateDisplayStatus();
            };
            extra.Controls.Add(_hideFullscreen);

            _hidePopups = new AlCheck("冻结 / 进后台时一并收起气泡与输入框");
            _hidePopups.SetBounds(20, 82, 600, 24);
            _hidePopups.CheckedChanged += delegate
            {
                Config.Current.hidePopupsWhenInactive = _hidePopups.Checked;
                Config.Save();
            };
            extra.Controls.Add(_hidePopups);

            Label tip = Ui.L("全屏自动隐藏用的是本机窗口 API 判断「前台窗口是否盖满屏幕」，不截屏、不读窗口标题；" +
                "只有需要时才会每 0.3 秒检测一次（选「始终最上层」且关掉全屏隐藏则完全不检测）。", 11.5f, Theme.TextDim, false);
            tip.AutoSize = false;
            tip.SetBounds(20, 116, Math.Max(300, extra.Width - 40), 44);
            extra.Controls.Add(tip);

            return p;
        }

        /// <summary>切换显示方式：立刻存盘并让正在桌面上的桌宠马上生效。</summary>
        void SetDisplayMode(string id)
        {
            Config.Current.petDisplayMode = PetDisplayMode.Normalize(id);
            Config.Save();
            UpdateDisplayStatus();
            PetManager.ApplyDisplaySettings();
        }

        void UpdateDisplayStatus()
        {
            if (_displayStatus == null) return;
            string mode = PetDisplayMode.Normalize(Config.Current.petDisplayMode);
            string[] ids = PetDisplayMode.Ids();
            for (int i = 0; i < ids.Length && i < _displayBtns.Length; i++)
            {
                if (_displayBtns[i] != null) _displayBtns[i].Primary = ids[i] == mode;
            }
            string text = "当前：" + PetDisplayMode.Title(mode);
            if (mode != PetDisplayMode.FullscreenHide && Config.Current.hideOnFullscreen)
                text += "；有其他程序全屏时自动隐藏";
            _displayStatus.Text = text;
        }

        public static void ApplyAutoStart(bool enabled)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true))
                {
                    if (key == null) return;
                    if (enabled)
                        key.SetValue(AppPaths.ProductNameEn, "\"" + Application.ExecutablePath + "\" --autostart");
                    else
                        key.DeleteValue(AppPaths.ProductNameEn, false);
                }
                Log.Info("开机自启：" + (enabled ? "已开启" : "已关闭"));
            }
            catch (Exception ex)
            {
                Log.Warn("设置开机自启失败：" + ex.Message);
            }
        }

        public static bool IsAutoStartEnabled()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", false))
                {
                    if (key == null) return false;
                    return key.GetValue(AppPaths.ProductNameEn) != null;
                }
            }
            catch { return false; }
        }

        // ==================== 版本与作者 ====================

        Panel BuildAbout()
        {
            Panel p = new Panel();
            AlPanel card = Ui.Card("版本与作者信息", 0, 0, _body.Width - 4, _body.Height - 4);
            card.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            p.Controls.Add(card);

            _aboutInfo = Ui.L("", 13f, Theme.TextMain, false);
            _aboutInfo.SetBounds(24, 48, 780, 330);
            _aboutInfo.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            card.Controls.Add(_aboutInfo);

            AlButton openInstall = new AlButton("打开安装目录");
            openInstall.SetBounds(24, 386, 150, 38);
            openInstall.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            openInstall.Click += delegate { ErrorDialogForm.OpenFolder(AppPaths.InstallDir()); };
            card.Controls.Add(openInstall);

            AlButton openData = new AlButton("打开数据目录");
            openData.SetBounds(184, 386, 150, 38);
            openData.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            openData.Click += delegate { ErrorDialogForm.OpenFolder(AppPaths.DataDir()); };
            card.Controls.Add(openData);

            AlButton uninstall = new AlButton("卸载本软件…");
            uninstall.Danger = true;
            uninstall.SetBounds(344, 386, 150, 38);
            uninstall.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            uninstall.Click += delegate { RunUninstaller(); };
            card.Controls.Add(uninstall);

            UpdateAboutText();
            return p;
        }

        void UpdateAboutText()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine(AppPaths.ProductName + "   v" + AppPaths.Version);
            sb.AppendLine("作者 / 维护：" + AppPaths.Author);
            sb.AppendLine("许可：MIT（代码）；内置美术与音频素材按原样提供，仅供本机个人使用");
            sb.AppendLine();
            sb.AppendLine("安装目录：" + AppPaths.InstallDir());
            sb.AppendLine("数据目录：" + AppPaths.DataDir());
            sb.AppendLine("运行环境：.NET Framework " + Environment.Version + " · " + (Environment.Is64BitProcess ? "64 位" : "32 位"));
            sb.AppendLine("音频自检：" + Audio.SelfTest());
            sb.AppendLine();
            sb.AppendLine("核心能力参照「DSH 小鲸鱼记账挂件」的设计思路重做：");
            sb.AppendLine("  · 自定义角色立绘 / 台词 / 语音与清单 / 角色卡 / 互动音效");
            sb.AppendLine("  · 点击随机台词气泡、按压 Q 弹动画、按压与松开音效");
            sb.AppendLine("  · 多角色各自独立，可同时显示在桌面");
            sb.AppendLine("  · 固定台词与大模型两种对话模式（兼容本地与云端接口）");
            sb.AppendLine("  · 主动对话、系统事件监听（需授权）、错误日志一键导出");
            _aboutInfo.Text = sb.ToString();
        }

        void RunUninstaller()
        {
            string exe = Path.Combine(AppPaths.InstallDir(), "卸载-灵工桌宠.exe");
            if (!File.Exists(exe))
            {
                MessageBox.Show(this, "没找到卸载程序：\r\n" + exe + "\r\n\r\n也可以到「Windows 设置 → 应用」里卸载「" + AppPaths.ProductName + "」。",
                    AppPaths.ProductName, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (MessageBox.Show(this, "运行卸载程序？桌宠会先被关闭。", AppPaths.ProductName, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            try
            {
                Process.Start(exe);
                Program.ShutdownApp();
            }
            catch (Exception ex)
            {
                Log.ErrorDialog("启动卸载程序失败", ex);
            }
        }

        // ==================== 载入 / 保存 ====================

        void LoadValues()
        {
            _volume.Value = Math.Max(0, Math.Min(100, Config.Current.volume));
            _volumeLabel.Text = _volume.Value + "%";
            _sfxOn.SetCheckedSilent(Config.Current.sfxEnabled);
            _voiceOn.SetCheckedSilent(Config.Current.voiceEnabled);

            LlmSettings s = Config.Current.llm;
            _baseUrl.Text = s.baseUrl;
            _apiKey.Text = s.apiKey;
            _model.Text = s.model;
            _temp.Value = Math.Max(0, Math.Min(200, (int)Math.Round(s.temperature * 100)));
            _tempLabel.Text = s.temperature.ToString("0.00");
            _maxTokens.Value = Math.Max(32, Math.Min(4096, s.maxTokens));
            _timeout.Value = Math.Max(5, Math.Min(600, s.timeoutSec));
            _history.Value = Math.Max(0, Math.Min(50, s.historyTurns));
            _sysExtra.Text = s.systemExtra;
            int presetIndex = 0;
            List<LlmPreset> presets = Llm.Presets();
            for (int i = 0; i < presets.Count; i++)
                if (presets[i].baseUrl == s.baseUrl) { presetIndex = i; break; }
            _preset.SelectedIndex = presetIndex;
            _hintLabel.Text = presets[presetIndex].hint;
            _testResult.Text = string.IsNullOrEmpty(s.lastTestResult) ? "" : (s.lastTestTime + "：" + s.lastTestResult);

            _verbose.SetCheckedSilent(Config.Current.logVerbose);
            _keepDays.Value = Math.Max(1, Math.Min(365, Config.Current.logKeepDays));
            _autoStart.SetCheckedSilent(IsAutoStartEnabled() || Config.Current.autoStart);
            _tray.SetCheckedSilent(Config.Current.showTray);
            _startUi.SetCheckedSilent(!Config.Current.firstRunDone);

            if (_hideFullscreen != null) _hideFullscreen.SetCheckedSilent(Config.Current.hideOnFullscreen);
            if (_hidePopups != null) _hidePopups.SetCheckedSilent(Config.Current.hidePopupsWhenInactive);
            UpdateDisplayStatus();
        }

        void SaveAll()
        {
            try
            {
                Config.Current.volume = _volume.Value;
                Config.Current.sfxEnabled = _sfxOn.Checked;
                Config.Current.voiceEnabled = _voiceOn.Checked;
            Config.Current.logVerbose = _verbose.Checked;
                Config.Current.logKeepDays = (int)_keepDays.Value;
                Config.Current.showTray = _tray.Checked;
                if (_hideFullscreen != null) Config.Current.hideOnFullscreen = _hideFullscreen.Checked;
                if (_hidePopups != null) Config.Current.hidePopupsWhenInactive = _hidePopups.Checked;
                Config.Current.petDisplayMode = PetDisplayMode.Normalize(Config.Current.petDisplayMode);
                SaveLlm();
                Config.Save();
                Audio.Configure(Config.Current.volume, Config.Current.sfxEnabled);
                PetManager.ApplyDisplaySettings();   // 显示方式改了要马上作用到桌面上的桌宠
            }
            catch (Exception ex)
            {
                Log.ErrorDialog("保存设置失败", ex);
            }
        }
    }
}
