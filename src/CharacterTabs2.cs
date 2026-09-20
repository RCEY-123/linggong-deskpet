// ============================================================================
// CharacterTabs2.cs —— 主界面功能页（续）：④ 角色卡 ⑤ 互动设置 ⑥ 互动音效
// ============================================================================
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace AlDeskPet
{
    // ========================================================================
    // ④ 角色卡（仅接入大模型时可用）
    // ========================================================================
    public class CardTab : TabPageBase
    {
        TextBox _persona, _style, _greeting, _rules, _user;
        TrackBar _temp;
        NumericUpDown _maxTokens;
        AlCheck _enabled;
        Label _status, _tempLabel;
        AlPanel _card;
        Label _lblPersona, _lblStyle, _lblGreeting, _lblRules, _lblUser, _lblTemp, _lblTokens, _lblTip;
        AlButton _btnImportCard, _btnExportCard, _btnTemplate, _btnTestCard;

        public CardTab(MainForm host) : base(host)
        {
            AlPanel card = Card("角色卡（仅在接入大模型时可用）", 0, 0, 826, 516);
            _card = card;
            _status = Lb(card, "", 14, 36, 640, 40, Theme.Gold, 12.5f, false);
            BtnIn(card, "去设置大模型 API", 664, 34, 148, 32, delegate { Host.OpenSettings("llm"); });

            _enabled = new AlCheck("接入大模型时启用这张角色卡（角色卡依赖大模型，固定台词模式下不生效）");
            _enabled.SetBounds(14, 78, 620, 24);
            _enabled.CheckedChanged += delegate
            {
                if (Profile == null) return;
                Profile.card.enabled = _enabled.Checked;
                CharacterStore.Save(Profile);
                RaiseChanged();
            };
            card.Controls.Add(_enabled);

            _lblPersona = Lb(card, "角色设定（System Prompt 主体：身份、背景、性格）", 14, 108, 380, 20, Theme.TextDim, 12f, false);
            _persona = Ui.T("", true, false);
            _persona.Name = "persona";
            _persona.TextChanged += delegate
            {
                if (Profile == null || Suppress) return;
                Profile.card.persona = _persona.Text;
                ScheduleSave();
            };
            card.Controls.Add(_persona);

            _lblStyle = Lb(card, "说话风格", 410, 108, 400, 20, Theme.TextDim, 12f, false);
            _style = Ui.T("", true, false);
            _style.Name = "style";
            _style.TextChanged += delegate
            {
                if (Profile == null || Suppress) return;
                Profile.card.speechStyle = _style.Text;
                ScheduleSave();
            };
            card.Controls.Add(_style);

            _lblGreeting = Lb(card, "开场白（大模型模式下打开桌宠说的第一句）", 410, 222, 400, 20, Theme.TextDim, 12f, false);
            _greeting = Ui.T("", false, false);
            _greeting.Name = "greeting";
            _greeting.TextChanged += delegate
            {
                if (Profile == null || Suppress) return;
                Profile.card.greeting = _greeting.Text;
                ScheduleSave();
            };
            card.Controls.Add(_greeting);

            _lblRules = Lb(card, "补充要求（禁止事项 / 口头禅 / 称呼等）", 410, 278, 400, 20, Theme.TextDim, 12f, false);
            _rules = Ui.T("", true, false);
            _rules.Name = "rules";
            _rules.TextChanged += delegate
            {
                if (Profile == null || Suppress) return;
                Profile.card.extraRules = _rules.Text;
                ScheduleSave();
            };
            card.Controls.Add(_rules);

            _lblTokens = Lb(card, "回复上限 tokens", 14, 386, 110, 22, Theme.TextDim, 12f, false);
            _maxTokens = Ui.Num(32, 2048, 220, 16);
            _maxTokens.SetBounds(126, 382, 80, 26);
            _maxTokens.ValueChanged += delegate
            {
                if (Profile == null || Suppress) return;
                Profile.card.maxTokens = (int)_maxTokens.Value;
                ScheduleSave();
            };
            card.Controls.Add(_maxTokens);

            _lblUser = Lb(card, "玩家称呼", 236, 386, 70, 22, Theme.TextDim, 12f, false);
            _user = Ui.T("指挥官", false, false);
            _user.SetBounds(308, 382, 140, 26);
            _user.TextChanged += delegate
            {
                if (Profile == null || Suppress) return;
                Profile.card.userName = _user.Text;
                ScheduleSave();
            };
            card.Controls.Add(_user);

            _lblTemp = Lb(card, "想象温度", 480, 386, 70, 22, Theme.TextDim, 12f, false);
            _temp = Ui.Slider(0, 200, 85);
            _temp.SetBounds(550, 380, 160, 30);
            _temp.ValueChanged += delegate
            {
                if (Profile == null || Suppress) return;
                Profile.card.temperature = _temp.Value / 100.0;
                _tempLabel.Text = (Profile.card.temperature).ToString("0.00");
                ScheduleSave();
            };
            card.Controls.Add(_temp);
            _tempLabel = Lb(card, "0.85", 718, 386, 50, 22, Theme.GoldBright, 12f, true);

            _btnImportCard = BtnIn(card, "从文件导入角色卡…", 14, 424, 180, 34, delegate { ImportCard(); });
            _btnExportCard = BtnIn(card, "导出角色卡…", 204, 424, 160, 34, delegate { ExportCard(); });
            _btnTemplate = BtnIn(card, "生成模板", 374, 424, 120, 34, delegate { MakeTemplate(); });
            _btnTestCard = BtnIn(card, "测试当前角色卡（用大模型说一句）", 504, 424, 308, 34, delegate { TestCard(); });

            _lblTip = Lb(card, "提示：桌宠的回答会显示在头顶气泡里，所以角色卡里最好写清「1~3 句、60 字以内、不要 markdown」；" +
                      "软件也会自动附带这些输出约束。",
                14, 466, 800, 44, Theme.TextDim, 11.5f, false);

            // 自适应只保留「顶部状态行 / 右上按钮 / 底部按钮与提示」，其余全部由 Relayout() 精确定位，
            // 避免左右两列互相压住（曾经 persona 文本框向右拉伸把右侧整列盖住）。
            _status.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            Anch(card, "去设置大模型 API", AnchorStyles.Top | AnchorStyles.Right);
            _lblTip.Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            _btnImportCard.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            _btnExportCard.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            _btnTemplate.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            _btnTestCard.Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            _persona.BringToFront();
            _style.BringToFront();
            _greeting.BringToFront();
            _rules.BringToFront();
        }

        /// <summary>
        /// 角色卡页的两列布局：左列「角色设定」占约 52%，右列「说话风格 / 开场白 / 补充要求」占其余宽度，
        /// 文本框随窗口高度拉伸（底部给参数行和按钮行留出固定位置）。
        /// </summary>
        public override void Relayout()
        {
            if (_card == null) return;
            int w = Width, h = Height;
            if (w <= 0 || h <= 0) return;
            _card.SetBounds(0, 0, w, h);

            int colTop = 130;
            int colBottom = h - 146;                      // 下面依次是参数行、按钮行、提示行
            if (colBottom < colTop + 120) colBottom = colTop + 120;

            int usable = Math.Max(320, w - 42);
            int leftW = Math.Max(220, (int)(usable * 0.52));
            int rightX = 14 + leftW + 16;
            int rightW = Math.Max(180, w - rightX - 14);

            _lblPersona.SetBounds(14, 108, leftW, 20);
            _persona.SetBounds(14, colTop, leftW, colBottom - colTop);

            _lblStyle.SetBounds(rightX, 108, rightW, 20);
            _style.SetBounds(rightX, colTop, rightW, 84);
            _lblGreeting.SetBounds(rightX, 222, rightW, 20);
            _greeting.SetBounds(rightX, 244, rightW, 26);
            _lblRules.SetBounds(rightX, 278, rightW, 20);
            _rules.SetBounds(rightX, 300, rightW, Math.Max(60, colBottom - 300));

            // 参数行（固定高度，跟着底边走）
            int rowY = h - 136;
            _lblTokens.SetBounds(14, rowY + 4, 110, 22);
            _maxTokens.SetBounds(126, rowY, 80, 26);
            _lblUser.SetBounds(236, rowY + 4, 70, 22);
            _user.SetBounds(308, rowY, 140, 26);
            _lblTemp.SetBounds(480, rowY + 4, 70, 22);
            _temp.SetBounds(550, rowY - 2, Math.Max(90, Math.Min(160, w - 620)), 30);
            _tempLabel.SetBounds(Math.Min(718, w - 60), rowY + 4, 50, 22);

            // 按钮行
            int btnY = h - 92;
            _btnImportCard.SetBounds(14, btnY, 180, 34);
            _btnExportCard.SetBounds(204, btnY, 160, 34);
            _btnTemplate.SetBounds(374, btnY, 120, 34);
            _btnTestCard.SetBounds(504, btnY, Math.Max(160, w - 518), 34);

            _lblTip.SetBounds(14, h - 50, Math.Max(200, w - 28), 44);
            _enabled.SetBounds(14, 78, Math.Max(200, w - 200), 24);
        }

        Timer _saveTimer;

        void ScheduleSave()
        {
            if (_saveTimer == null)
            {
                _saveTimer = new Timer();
                _saveTimer.Interval = 700;
                _saveTimer.Tick += delegate
                {
                    _saveTimer.Stop();
                    if (Profile != null) CharacterStore.Save(Profile);
                    RaiseChanged();
                };
            }
            _saveTimer.Stop();
            _saveTimer.Start();
        }

        public override void Bind(CharacterProfile p)
        {
            Profile = p;
            Suppress = true;
            if (p != null)
            {
                _persona.Text = p.card.persona;
                _style.Text = p.card.speechStyle;
                _greeting.Text = p.card.greeting;
                _rules.Text = p.card.extraRules;
                _user.Text = string.IsNullOrEmpty(p.card.userName) ? "指挥官" : p.card.userName;
                _temp.Value = Math.Max(0, Math.Min(200, (int)Math.Round(p.card.temperature * 100)));
                _tempLabel.Text = p.card.temperature.ToString("0.00");
                _maxTokens.Value = Math.Max(32, Math.Min(2048, p.card.maxTokens));
                _enabled.SetCheckedSilent(p.card.enabled);
            }
            Suppress = false;
            UpdateStatus();
        }

        void UpdateStatus()
        {
            bool configured = !string.IsNullOrEmpty(Config.Current.llm.baseUrl);
            string mode = Profile != null && Profile.interact.mode == "llm" ? "大模型对话模式" : "固定台词模式";
            _status.Text = "当前：接口 " + (configured ? "已配置（" + Config.Current.llm.model + "）" : "未配置") +
                           " · 该角色处于「" + mode + "」" +
                           (Profile != null && Profile.interact.mode == "llm" ? "" : "（角色卡暂不生效）");
            _status.ForeColor = Profile != null && Profile.interact.mode == "llm" && configured ? Theme.Ok : Theme.Gold;
        }

        void ImportCard()
        {
            if (Profile == null) return;
            using (OpenFileDialog dlg = new OpenFileDialog())
            {
                dlg.Title = "导入角色卡（txt / json）";
                dlg.Filter = "角色卡|*.txt;*.json;*.md|所有文件|*.*";
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    string text = File.ReadAllText(dlg.FileName, System.Text.Encoding.UTF8);
                    if (Path.GetExtension(dlg.FileName).ToLowerInvariant() == ".json")
                    {
                        Dictionary<string, object> d = Json.AsObj(Json.TryParse(text));
                        if (d != null)
                        {
                            Profile.card.persona = Json.S(d, "persona", Json.S(d, "system", Json.S(d, "设定", text)));
                            Profile.card.speechStyle = Json.S(d, "speechStyle", Json.S(d, "style", ""));
                            Profile.card.greeting = Json.S(d, "greeting", "");
                            Profile.card.extraRules = Json.S(d, "rules", "");
                            if (d.ContainsKey("temperature")) Profile.card.temperature = Json.N(d, "temperature", 0.85);
                            if (d.ContainsKey("maxTokens")) Profile.card.maxTokens = Json.I(d, "maxTokens", 220);
                        }
                        else Profile.card.persona = text;
                    }
                    else
                    {
                        // 纯文本：支持「【段落】」小标题切分
                        ParseCardText(text);
                    }
                    Profile.card.enabled = true;
                    CharacterStore.Save(Profile);
                    Bind(Profile);
                    RaiseChanged();
                    Info("角色卡已导入。");
                }
                catch (Exception ex) { Log.ErrorDialog("导入角色卡失败", ex); }
            }
        }

        void ParseCardText(string text)
        {
            string persona = "", style = "", greeting = "", rules = "";
            string cur = "persona";
            foreach (string raw in text.Replace("\r\n", "\n").Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0) continue;
                if (line.StartsWith("#") || line.StartsWith("【") || line.StartsWith("["))
                {
                    string key = line.Trim('#', '【', '】', '[', ']', ' ', '：', ':');
                    if (key.IndexOf("风格", StringComparison.Ordinal) >= 0 || key.IndexOf("语气", StringComparison.Ordinal) >= 0) { cur = "style"; continue; }
                    if (key.IndexOf("开场", StringComparison.Ordinal) >= 0 || key.IndexOf("问候", StringComparison.Ordinal) >= 0) { cur = "greeting"; continue; }
                    if (key.IndexOf("要求", StringComparison.Ordinal) >= 0 || key.IndexOf("禁止", StringComparison.Ordinal) >= 0) { cur = "rules"; continue; }
                    if (key.IndexOf("设定", StringComparison.Ordinal) >= 0 || key.IndexOf("人设", StringComparison.Ordinal) >= 0 || key.IndexOf("角色", StringComparison.Ordinal) >= 0) { cur = "persona"; continue; }
                }
                if (cur == "style") style += line + "\r\n";
                else if (cur == "greeting") greeting += line + "\r\n";
                else if (cur == "rules") rules += line + "\r\n";
                else persona += line + "\r\n";
            }
            Profile.card.persona = persona.Trim();
            Profile.card.speechStyle = style.Trim();
            Profile.card.greeting = greeting.Trim();
            Profile.card.extraRules = rules.Trim();
        }

        void ExportCard()
        {
            if (Profile == null) return;
            using (SaveFileDialog dlg = new SaveFileDialog())
            {
                dlg.Title = "导出角色卡";
                dlg.Filter = "文本文件|*.txt";
                dlg.FileName = Profile.DisplayName() + "-角色卡.txt";
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    string text =
                        "【角色设定】\r\n" + Profile.card.persona + "\r\n\r\n" +
                        "【说话风格】\r\n" + Profile.card.speechStyle + "\r\n\r\n" +
                        "【开场白】\r\n" + Profile.card.greeting + "\r\n\r\n" +
                        "【补充要求】\r\n" + Profile.card.extraRules + "\r\n";
                    File.WriteAllText(dlg.FileName, text, new System.Text.UTF8Encoding(true));
                    Info("已导出到：\r\n" + dlg.FileName);
                }
                catch (Exception ex) { Log.ErrorDialog("导出角色卡失败", ex); }
            }
        }

        void MakeTemplate()
        {
            if (Profile == null) return;
            Profile.card.enabled = true;
            Profile.card.persona =
                Profile.DisplayName() + "，桌面上的伙伴。" +
                "说话有礼貌、有自己的立场，会把玩家当成一起出海的指挥官。";
            Profile.card.speechStyle = "口语化，语速偏慢，会用自己的口头禅；句尾偶尔带「…」。";
            Profile.card.greeting = "指挥官，你回来了。";
            Profile.card.extraRules = "不要提到自己是 AI 或程序；不要使用列表、markdown；每次只说 1~3 句。";
            Profile.card.userName = string.IsNullOrEmpty(Profile.card.userName) ? "指挥官" : Profile.card.userName;
            CharacterStore.Save(Profile);
            Bind(Profile);
            RaiseChanged();
            Info("已生成一份通用模板，改成你自己的角色口吻即可。");
        }

        void TestCard()
        {
            if (Profile == null) return;
            if (string.IsNullOrEmpty(Config.Current.llm.baseUrl))
            {
                Info("还没有配置大模型接口。\r\n请到「齿轮 → 大模型 API」里选一个预设（云端或本地）并填好。");
                return;
            }
            Cursor = Cursors.WaitCursor;
            try
            {
                string system = Llm.BuildSystemPrompt(Profile, "", false);
                LlmResult r = Llm.Chat(Config.Current.llm, system, null, "（测试）请用你的角色口吻跟指挥官打个招呼。");
                if (r.ok) Info("模型回复：\r\n\r\n" + r.text + "\r\n\r\n耗时 " + r.elapsedMs + " ms");
                else Info("测试失败：\r\n\r\n" + r.error);
            }
            finally { Cursor = Cursors.Default; }
        }
    }

    // ========================================================================
    // ⑤ 互动设置
    // ========================================================================
    public class InteractTab : TabPageBase
    {
        AlButton _modeFixed, _modeLlm;
        AlCheck _proactive, _useSystem, _clickAnim, _showInput, _showOnStart;
        NumericUpDown _minSec, _maxSec, _idleSec, _bubbleSec, _animMs;
        AlCombo _voiceMode, _imagePick;
        TrackBar _scale;
        Label _scaleLabel, _permLabel, _modeStatus;
        Timer _saveTimer;
        AlPanel _modeP, _proP, _lookP;

        public InteractTab(MainForm host) : base(host)
        {
            AlPanel mode = Card("对话模式", 0, 0, 404, 218);
            _modeP = mode;
            _modeFixed = BtnIn(mode, "固定台词模式", 14, 38, 178, 44, delegate { SetMode("fixed"); });
            _modeLlm = BtnIn(mode, "大模型对话模式", 202, 38, 188, 44, delegate { SetMode("llm"); });
            _modeFixed.ShowCheck = true;   // 只有这两个按钮需要「金色对勾 = 当前模式」
            _modeLlm.ShowCheck = true;
            _modeStatus = Lb(mode, "", 14, 88, 376, 22, Theme.GoldBright, 12.5f, true);
            Lb(mode, "固定台词：点一下随机说一句（有语音就一起放）；" +
                     "大模型：点一下按角色卡回一句，并在下方出现输入框。",
                14, 112, 376, 34, Theme.TextDim, 11.5f, false);

            Lb(mode, "台词有语音时", 14, 152, 100, 22, Theme.TextDim, 12f, false);
            _voiceMode = Ui.C("播放对应语音", "不播放语音", "每次随机播一条语音");
            _voiceMode.SetBounds(120, 148, 270, 26);
            _voiceMode.SelectedIndexChanged += delegate
            {
                if (Profile == null || Suppress) return;
                Profile.interact.fixedVoiceMode = _voiceMode.SelectedIndex == 1 ? "off" : (_voiceMode.SelectedIndex == 2 ? "always" : "match");
                Save();
            };
            mode.Controls.Add(_voiceMode);

            _bubbleSec = Ui.Num(0, 600, 6, 1);
            _bubbleSec.SetBounds(120, 182, 70, 26);
            _bubbleSec.ValueChanged += delegate
            {
                if (Profile == null || Suppress) return;
                Profile.interact.bubbleSeconds = (int)_bubbleSec.Value;
                Save();
            };
            mode.Controls.Add(_bubbleSec);
            Lb(mode, "气泡停留（秒，0=不自动关）", 200, 186, 190, 22, Theme.TextDim, 12f, false);

            AlPanel pro = Card("主动对话", 414, 0, 412, 218);
            _proP = pro;
            _proactive = new AlCheck("允许桌宠主动开口（按下面的频率）");
            _proactive.SetBounds(14, 40, 380, 24);
            _proactive.CheckedChanged += delegate
            {
                if (Profile == null || Suppress) return;
                Profile.interact.proactive = _proactive.Checked;
                Save();
                Host.SetStatus(_proactive.Checked ? "已开启主动对话。" : "已关闭主动对话。");
            };
            pro.Controls.Add(_proactive);

            Lb(pro, "间隔下限", 14, 76, 70, 22, Theme.TextDim, 12f, false);
            _minSec = Ui.Num(5, 86400, 120, 10);
            _minSec.SetBounds(86, 72, 84, 26);
            _minSec.ValueChanged += delegate
            {
                if (Profile == null || Suppress) return;
                Profile.interact.proactiveMinSec = (int)_minSec.Value;
                if (_maxSec.Value < _minSec.Value) _maxSec.Value = _minSec.Value;
                Save();
            };
            pro.Controls.Add(_minSec);

            Lb(pro, "上限", 180, 76, 40, 22, Theme.TextDim, 12f, false);
            _maxSec = Ui.Num(5, 86400, 300, 10);
            _maxSec.SetBounds(222, 72, 84, 26);
            _maxSec.ValueChanged += delegate
            {
                if (Profile == null || Suppress) return;
                Profile.interact.proactiveMaxSec = (int)_maxSec.Value;
                Save();
            };
            pro.Controls.Add(_maxSec);

            BtnIn(pro, "30 秒", 14, 106, 70, 28, delegate { SetFreq(30, 60); });
            BtnIn(pro, "2 分钟", 90, 106, 70, 28, delegate { SetFreq(90, 180); });
            BtnIn(pro, "5 分钟", 166, 106, 70, 28, delegate { SetFreq(180, 420); });
            BtnIn(pro, "10 分钟", 242, 106, 80, 28, delegate { SetFreq(420, 900); });
            BtnIn(pro, "1 小时", 328, 106, 70, 28, delegate { SetFreq(1800, 3600); });

            Lb(pro, "多久没互动就说「待机」台词", 14, 146, 220, 22, Theme.TextDim, 12f, false);
            _idleSec = Ui.Num(30, 86400, 600, 30);
            _idleSec.SetBounds(240, 142, 84, 26);
            _idleSec.ValueChanged += delegate
            {
                if (Profile == null || Suppress) return;
                Profile.interact.idleMinSec = (int)_idleSec.Value;
                Save();
            };
            pro.Controls.Add(_idleSec);

            _useSystem = new AlCheck("根据系统事件/当前操作主动搭话（需要授权）");
            _useSystem.SetBounds(14, 178, 340, 24);
            _useSystem.CheckedChanged += delegate { ToggleSystemWatch(); };
            pro.Controls.Add(_useSystem);

            _permLabel = Lb(pro, "", 300, 180, 100, 22, Theme.TextDim, 11.5f, false);

            AlPanel look = Card("外观与动作", 0, 228, 404, 288);
            _lookP = look;
            Lb(look, "立绘大小", 14, 44, 70, 22, Theme.TextDim, 12f, false);
            _scale = Ui.Slider(40, 250, 100);
            _scale.SetBounds(86, 40, 220, 30);
            _scale.ValueChanged += delegate
            {
                if (Profile == null || Suppress) return;
                Profile.interact.scale = _scale.Value / 100.0;
                _scaleLabel.Text = _scale.Value + "%";
                Save();
            };
            look.Controls.Add(_scale);
            _scaleLabel = Lb(look, "100%", 312, 44, 60, 22, Theme.GoldBright, 12f, true);

            Lb(look, "默认立绘", 14, 82, 70, 22, Theme.TextDim, 12f, false);
            _imagePick = Ui.C();
            _imagePick.SetBounds(86, 78, 306, 26);
            _imagePick.SelectedIndexChanged += delegate
            {
                if (Profile == null || Suppress) return;
                if (_imagePick.SelectedIndex >= 0 && _imagePick.SelectedIndex < Profile.images.Count)
                {
                    Profile.imageIndex = _imagePick.SelectedIndex;
                    Save();
                }
            };
            look.Controls.Add(_imagePick);

            _clickAnim = new AlCheck("点击时 Q 弹动画（微微压缩再弹起）");
            _clickAnim.SetBounds(14, 116, 360, 24);
            _clickAnim.CheckedChanged += delegate
            {
                if (Profile == null || Suppress) return;
                Profile.interact.clickAnimation = _clickAnim.Checked;
                Save();
            };
            look.Controls.Add(_clickAnim);

            Lb(look, "动画时长（毫秒）", 14, 152, 130, 22, Theme.TextDim, 12f, false);
            _animMs = Ui.Num(120, 2000, 420, 20);
            _animMs.SetBounds(146, 148, 84, 26);
            _animMs.ValueChanged += delegate
            {
                if (Profile == null || Suppress) return;
                Profile.interact.animDurationMs = (int)_animMs.Value;
                Save();
            };
            look.Controls.Add(_animMs);

            _showInput = new AlCheck("大模型模式下显示输入框");
            _showInput.SetBounds(14, 184, 360, 24);
            _showInput.CheckedChanged += delegate
            {
                if (Profile == null || Suppress) return;
                Profile.interact.showInputBox = _showInput.Checked;
                Save();
            };
            look.Controls.Add(_showInput);

            _showOnStart = new AlCheck("启动后立即显示该角色（开启桌宠时默认用这个角色）");
            _showOnStart.SetBounds(14, 214, 380, 24);
            _showOnStart.CheckedChanged += delegate
            {
                if (Profile == null || Suppress) return;
                Profile.interact.showOnStart = _showOnStart.Checked;
                Save();
            };
            look.Controls.Add(_showOnStart);

            BtnIn(look, "预览对话效果", 14, 246, 180, 32, delegate { Host.PreviewCurrentCharacter(); });
            BtnIn(look, "重置桌宠位置", 204, 246, 180, 32, delegate
            {
                Config.Current.petPosition = "";
                Config.Save();
                Host.SetStatus("桌宠位置已重置，下次显示会回到屏幕右下角。");
            });

            // 自适应
            _voiceMode.Anchor = AnchorStyles.Top | AnchorStyles.Left;
            foreach (Control c in mode.Controls)
                if (c is Label) c.Anchor = AnchorStyles.Top | AnchorStyles.Left;
            foreach (Control c in pro.Controls)
                if (c is Label || c is AlCheck) c.Anchor = AnchorStyles.Top | AnchorStyles.Left;
            _imagePick.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            Anch(look, "预览对话效果", AnchorStyles.Bottom | AnchorStyles.Left);
            Anch(look, "重置桌宠位置", AnchorStyles.Bottom | AnchorStyles.Left);
        }

        public override void Relayout()
        {
            if (_modeP == null || _proP == null || _lookP == null) return;
            int w = Width, h = Height;
            if (w <= 0 || h <= 0) return;
            int cw = Math.Max(300, (w - 10) / 2);
            _modeP.SetBounds(0, 0, cw, 218);
            _proP.SetBounds(cw + 10, 0, Math.Max(300, w - cw - 10), 218);
            int top = 228;
            _lookP.SetBounds(0, top, w, Math.Max(220, h - top));
        }

        void SetFreq(int min, int max)
        {
            if (Profile == null) return;
            Suppress = true;
            _minSec.Value = Math.Max(_minSec.Minimum, Math.Min(_minSec.Maximum, min));
            _maxSec.Value = Math.Max(_minSec.Value, Math.Min(_maxSec.Maximum, max));
            Suppress = false;
            Profile.interact.proactiveMinSec = (int)_minSec.Value;
            Profile.interact.proactiveMaxSec = (int)_maxSec.Value;
            Profile.interact.proactive = true;
            _proactive.SetCheckedSilent(true);
            Save();
            Host.SetStatus("主动对话频率：" + min + " ~ " + max + " 秒。");
        }

        void SetMode(string mode)
        {
            if (Profile == null) return;
            bool changed = Profile.interact.mode != mode;
            Profile.interact.mode = mode;
            if (mode == "llm") Profile.card.enabled = true;
            Save();
            CharacterStore.Save(Profile);   // 模式切换立即落盘，不等防抖
            RaiseChanged();                 // 立即通知主界面 → 通知正在显示的桌宠
            UpdateModeButtons();
            if (changed) Host.SetStatus("对话模式已切换为：" + (mode == "llm" ? "大模型对话模式" : "固定台词模式"));

            // 「还没配接口」的提醒放到点击处理之后：否则模态窗口会把按钮的 MouseUp 吃掉，
            // 按钮就会一直停在「按下」的高亮状态。
            if (mode == "llm" && string.IsNullOrEmpty(Config.Current.llm.baseUrl))
            {
                try
                {
                    BeginInvoke((MethodInvoker)delegate
                    {
                        if (Ask("还没有配置大模型接口，现在去配置吗？\r\n\r\n（不配置的话，点桌宠会提示去设置，不会真的对话）"))
                            Host.OpenSettings("llm");
                    });
                }
                catch { }
            }
        }

        /// <summary>把「当前模式」画在按钮上：选中项加金色对勾 + 亮金边，另一项保持普通样式。</summary>
        void UpdateModeButtons()
        {
            bool llm = Profile != null && Profile.interact.mode == "llm";
            _modeFixed.Primary = !llm;
            _modeLlm.Primary = llm;
            if (_modeStatus != null)
            {
                bool configured = !string.IsNullOrEmpty(Config.Current.llm.baseUrl);
                if (llm)
                {
                    _modeStatus.Text = configured
                        ? "当前：大模型对话模式"
                        : "当前：大模型对话模式（⚠ 还没配置接口）";
                    _modeStatus.ForeColor = configured ? Theme.Ok : Theme.Rouge;
                }
                else
                {
                    _modeStatus.Text = "当前：固定台词模式";
                    _modeStatus.ForeColor = Theme.GoldBright;
                }
            }
            _modeFixed.Invalidate();
            _modeLlm.Invalidate();
            if (_modeP != null) _modeP.Invalidate();
        }

        void ToggleSystemWatch()
        {
            if (Profile == null) return;
            if (!_useSystem.Checked)
            {
                Profile.interact.useSystemContext = false;
                Save();
                UpdatePermLabel();
                return;
            }
            string perm = Config.Current.systemWatchPermission;
            if (perm == "allowed")
            {
                Profile.interact.useSystemContext = true;
                Save();
                SystemWatch.Start(5000);
                UpdatePermLabel();
                return;
            }
            using (PermissionForm f = new PermissionForm())
            {
                f.StartPosition = FormStartPosition.CenterParent;
                if (f.ShowDialog(this) == DialogResult.OK)
                {
                    Config.Current.systemWatchPermission = "allowed";
                    Config.Save();
                    Profile.interact.useSystemContext = true;
                    Save();
                    SystemWatch.Start(5000);
                    Host.SetStatus("已授权系统事件监听。");
                }
                else
                {
                    Config.Current.systemWatchPermission = "denied";
                    Config.Save();
                    Profile.interact.useSystemContext = false;
                    Save();
                    Suppress = true;
                    _useSystem.SetCheckedSilent(false);
                    Suppress = false;
                    Info("已拒绝授权：桌宠不会读取你在做什么，因此「按操作搭话」的功能不可用。\r\n" +
                         "之后想用的话，回到这里再勾选一次即可重新授权。");
                }
            }
            UpdatePermLabel();
        }

        void UpdatePermLabel()
        {
            string perm = Config.Current.systemWatchPermission;
            if (perm == "allowed") { _permLabel.Text = "已授权"; _permLabel.ForeColor = Theme.Ok; }
            else if (perm == "denied") { _permLabel.Text = "已拒绝"; _permLabel.ForeColor = Theme.Rouge; }
            else { _permLabel.Text = "未授权"; _permLabel.ForeColor = Theme.TextDim; }
        }

        void Save()
        {
            if (_saveTimer == null)
            {
                _saveTimer = new Timer();
                _saveTimer.Interval = 500;
                _saveTimer.Tick += delegate
                {
                    _saveTimer.Stop();
                    if (Profile != null) CharacterStore.Save(Profile);
                    RaiseChanged();
                };
            }
            _saveTimer.Stop();
            _saveTimer.Start();
        }

        public override void Bind(CharacterProfile p)
        {
            Profile = p;
            Suppress = true;
            if (p != null)
            {
                InteractionSettings s = p.interact;
                _proactive.SetCheckedSilent(s.proactive);
                _minSec.Value = Math.Max(_minSec.Minimum, Math.Min(_minSec.Maximum, s.proactiveMinSec));
                _maxSec.Value = Math.Max(_maxSec.Minimum, Math.Min(_maxSec.Maximum, s.proactiveMaxSec));
                _idleSec.Value = Math.Max(_idleSec.Minimum, Math.Min(_idleSec.Maximum, s.idleMinSec));
                _bubbleSec.Value = Math.Max(_bubbleSec.Minimum, Math.Min(_bubbleSec.Maximum, s.bubbleSeconds));
                _clickAnim.SetCheckedSilent(s.clickAnimation);
                _animMs.Value = Math.Max(_animMs.Minimum, Math.Min(_animMs.Maximum, s.animDurationMs));
                _showInput.SetCheckedSilent(s.showInputBox);
                _showOnStart.SetCheckedSilent(s.showOnStart);
                _useSystem.SetCheckedSilent(s.useSystemContext && Config.Current.systemWatchPermission == "allowed");
                _scale.Value = Math.Max(_scale.Minimum, Math.Min(_scale.Maximum, (int)Math.Round(s.scale * 100)));
                _scaleLabel.Text = _scale.Value + "%";
                _voiceMode.SelectedIndex = s.fixedVoiceMode == "off" ? 1 : (s.fixedVoiceMode == "always" ? 2 : 0);

                _imagePick.Items.Clear();
                foreach (string img in p.images) _imagePick.Items.Add(img);
                if (_imagePick.Items.Count > 0)
                {
                    int idx = p.imageIndex;
                    if (idx < 0 || idx >= _imagePick.Items.Count) idx = 0;
                    _imagePick.SelectedIndex = idx;
                }
            }
            Suppress = false;
            UpdateModeButtons();
            UpdatePermLabel();
        }
    }

    // ========================================================================
    // ⑥ 互动音效
    // ========================================================================
    public class SfxTab : TabPageBase
    {
        AlCheck _enabled;
        AlCombo _preset;
        TextBox _press, _release, _bubble;

        public SfxTab(MainForm host) : base(host)
        {
            AlPanel p = Card("互动音效", 0, 0, 826, 516);
            _enabled = new AlCheck("启用互动音效（按压 / 松开 / 气泡出现）");
            _enabled.SetBounds(14, 42, 400, 24);
            _enabled.CheckedChanged += delegate
            {
                if (Profile == null || Suppress) return;
                Profile.sfx.enabled = _enabled.Checked;
                CharacterStore.Save(Profile);
                RaiseChanged();
            };
            p.Controls.Add(_enabled);

            Lb(p, "内置音效预设", 14, 82, 110, 22, Theme.TextDim, 12f, false);
            _preset = Ui.C("音效1（俏皮双击）", "小黄鸭", "静音");
            _preset.SetBounds(130, 78, 220, 26);
            _preset.SelectedIndexChanged += delegate
            {
                if (Profile == null || Suppress) return;
                Profile.sfx.preset = _preset.SelectedIndex == 1 ? "duck" : (_preset.SelectedIndex == 2 ? "none" : "fx1");
                CharacterStore.Save(Profile);
                RaiseChanged();
            };
            p.Controls.Add(_preset);

            BtnIn(p, "试听预设", 362, 76, 120, 30, delegate { Preview(); });
            Lb(p, "内置音效与参考插件保持一致：音效1 = D1/D2，小黄鸭 = Ya1/Ya2。",
                494, 80, 320, 22, Theme.TextDim, 11.5f, false);

            _press = MakeRow(p, "按压音效", 128, delegate { Pick(0); });
            _release = MakeRow(p, "松开音效", 190, delegate { Pick(1); });
            _bubble = MakeRow(p, "气泡出现音", 252, delegate { Pick(2); });

            BtnIn(p, "试听当前组合", 14, 316, 160, 34, delegate { Preview(); });
            BtnIn(p, "清空自定义（回到预设）", 186, 316, 220, 34, delegate
            {
                if (Profile == null) return;
                Profile.sfx.press = "";
                Profile.sfx.release = "";
                Profile.sfx.bubble = "";
                CharacterStore.Save(Profile);
                Bind(Profile);
                RaiseChanged();
            });
            BtnIn(p, "打开音效目录", 418, 316, 160, 34, delegate
            {
                if (Profile != null) ErrorDialogForm.OpenFolder(CharacterStore.SfxDir(Profile.id));
            });

            Lb(p, "自定义方法：把 mp3 / wav 放进角色的 sfx 目录（或点右侧「选择…」从任意位置导入并自动复制），然后填文件名。\r\n" +
                 "音效音量与角色语音音量共用「齿轮 → 声音」里的滑块，方便统一控制。",
                14, 366, 800, 60, Theme.TextDim, 12f, false);

            Lb(p, "建议：按压音效短促（<0.5s）、松开音效稍长，配合 Q 弹动画效果最好。",
                14, 440, 800, 40, Theme.TextDim, 11.5f, false);

            // 自适应
            _card = p;
            Anch(p, "试听当前组合", AnchorStyles.Bottom | AnchorStyles.Left);
            Anch(p, "清空自定义（回到预设）", AnchorStyles.Bottom | AnchorStyles.Left);
            Anch(p, "打开音效目录", AnchorStyles.Bottom | AnchorStyles.Left);
            foreach (Control c in p.Controls)
                if (c is Label && c.Text != null && c.Text.StartsWith("自定义方法", StringComparison.Ordinal))
                    c.Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            foreach (Control c in p.Controls)
                if (c is Label && c.Text != null && c.Text.StartsWith("建议：", StringComparison.Ordinal))
                    c.Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        }

        AlPanel _card;

        public override void Relayout()
        {
            if (_card == null) return;
            if (Width <= 0 || Height <= 0) return;
            _card.SetBounds(0, 0, Width, Height);
        }

        TextBox MakeRow(Control parent, string label, int y, EventHandler onPick)
        {
            Lb(parent, label, 14, y + 4, 100, 22, Theme.TextDim, 12f, false);
            TextBox box = Ui.T("", false, false);
            box.SetBounds(120, y, 320, 26);
            box.TextChanged += delegate
            {
                if (Profile == null || Suppress) return;
                string v = box.Text.Trim();
                if (label.StartsWith("按压")) Profile.sfx.press = v;
                else if (label.StartsWith("松开")) Profile.sfx.release = v;
                else Profile.sfx.bubble = v;
                CharacterStore.Save(Profile);
                RaiseChanged();
            };
            parent.Controls.Add(box);

            AlButton pick = new AlButton("选择…");
            pick.SetBounds(450, y - 2, 90, 30);
            pick.Click += onPick;
            parent.Controls.Add(pick);

            AlButton test = new AlButton("试听");
            test.SetBounds(550, y - 2, 80, 30);
            test.Click += delegate
            {
                if (Profile == null) return;
                string file = Audio.ResolveSfx(Profile, label.StartsWith("按压") ? "press" : (label.StartsWith("松开") ? "release" : "bubble"));
                if (string.IsNullOrEmpty(file)) { Info("这个槽位没有可播放的音效（可能选了「静音」预设且没自定义）。"); return; }
                Audio.Play(file);
            };
            parent.Controls.Add(test);
            return box;
        }

        void Pick(int kind)
        {
            if (Profile == null) return;
            using (OpenFileDialog dlg = new OpenFileDialog())
            {
                dlg.Title = "选择音效文件";
                dlg.Filter = "音频文件|*.mp3;*.wav;*.ogg;*.m4a;*.wma|所有文件|*.*";
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    string name = Path.GetFileName(dlg.FileName);
                    Utils.CopyFile(dlg.FileName, Path.Combine(CharacterStore.SfxDir(Profile.id), name));
                    if (kind == 0) Profile.sfx.press = name;
                    else if (kind == 1) Profile.sfx.release = name;
                    else Profile.sfx.bubble = name;
                    CharacterStore.Save(Profile);
                    Bind(Profile);
                    RaiseChanged();
                    string file = Audio.ResolveSfx(Profile, kind == 0 ? "press" : (kind == 1 ? "release" : "bubble"));
                    if (!string.IsNullOrEmpty(file)) Audio.Play(file);
                }
                catch (Exception ex) { Log.ErrorDialog("导入音效失败", ex); }
            }
        }

        void Preview()
        {
            if (Profile == null) return;
            string press = Audio.ResolveSfx(Profile, "press");
            string release = Audio.ResolveSfx(Profile, "release");
            if (string.IsNullOrEmpty(press) && string.IsNullOrEmpty(release)) { Info("当前是静音预设。"); return; }
            Audio.Play(press);
            Timer t = new Timer();
            t.Interval = 160;
            t.Tick += delegate
            {
                t.Stop();
                t.Dispose();
                Audio.Play(release);
            };
            t.Start();
        }

        public override void Bind(CharacterProfile p)
        {
            Profile = p;
            Suppress = true;
            if (p != null)
            {
                _enabled.SetCheckedSilent(p.sfx.enabled);
                _preset.SelectedIndex = p.sfx.preset == "duck" ? 1 : (p.sfx.preset == "none" ? 2 : 0);
                _press.Text = p.sfx.press;
                _release.Text = p.sfx.release;
                _bubble.Text = p.sfx.bubble;
            }
            Suppress = false;
        }
    }
}
