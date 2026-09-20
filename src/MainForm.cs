// ============================================================================
// MainForm.cs —— 主控制界面（深蓝 + 柔金 · 圆润简约）
//   左侧角色列表 · 中间六个功能页 · 右上角齿轮 · 底部「开启桌宠」
// ============================================================================
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Windows.Forms;

namespace AlDeskPet
{
    /// <summary>齿轮按钮（自绘，避免字体里没有 ⚙ 字形）。</summary>
    public class GearButton : Control
    {
        bool _hover;
        public GearButton()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                     ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
            Cursor = Cursors.Hand;
            Size = new Size(48, 48);
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
            using (GraphicsPath path = Theme.RoundedRect(r, Theme.PillRadius(Height)))
            {
                using (LinearGradientBrush br = new LinearGradientBrush(r,
                    _hover ? Theme.PanelHi : Theme.Panel,
                    Theme.Navy, 90f))
                    g.FillPath(br, path);
                using (Pen pen = new Pen(_hover ? Theme.GoldBright : Theme.GoldDim, 1.2f))
                    g.DrawPath(pen, path);
            }
            float cx = Width / 2f, cy = Height / 2f, R = Math.Min(Width, Height) * 0.30f;
            Color gc = _hover ? Theme.GoldBright : Theme.Gold;
            using (Pen pen = new Pen(gc, 2.2f))
            {
                g.DrawEllipse(pen, cx - R, cy - R, R * 2, R * 2);
                for (int i = 0; i < 8; i++)
                {
                    double a = Math.PI / 4 * i;
                    float x1 = (float)(cx + Math.Cos(a) * R), y1 = (float)(cy + Math.Sin(a) * R);
                    float x2 = (float)(cx + Math.Cos(a) * (R + 5)), y2 = (float)(cy + Math.Sin(a) * (R + 5));
                    g.DrawLine(pen, x1, y1, x2, y2);
                }
            }
            using (SolidBrush br = new SolidBrush(Color.FromArgb(14, 28, 52)))
                g.FillEllipse(br, cx - R * 0.42f, cy - R * 0.42f, R * 0.84f, R * 0.84f);
            using (Pen pen = new Pen(gc, 1.6f))
                g.DrawEllipse(pen, cx - R * 0.42f, cy - R * 0.42f, R * 0.84f, R * 0.84f);
        }
    }

    public class MainForm : Form
    {
        public const string UiCornerText1 = "灵工巧物";
        public const string UiCornerText2 = "天祈智临";

        readonly AlTabStrip _tabs = new AlTabStrip();
        readonly Panel _content = new Panel();
        readonly ListBox _charList = new ListBox();
        readonly Label _status = new Label();
        readonly Dictionary<string, TabPageBase> _pages = new Dictionary<string, TabPageBase>();
        readonly string[] _tabIds = new string[] { "assets", "lines", "voice", "card", "interact", "sfx" };
        readonly string[] _tabTitles = new string[] { "角色素材", "角色台词", "角色语音", "角色卡", "互动设置", "互动音效" };

        public CharacterProfile Current;
        Timer _saveTimer;
        AlButton _startButton, _trayButton;
        AlButton _btnNew, _btnDel, _btnTemplate, _btnCopy;
        AlButton _btnHelp, _btnDataDir, _btnExit;
        AlPanel _leftPanel;
        bool _suppressListEvents;
        bool _layouting;

        public MainForm()
        {
            Text = AppPaths.ProductName + " · v" + AppPaths.Version;
            ClientSize = new Size(1160, 780);
            MinimumSize = new Size(1000, 700);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Theme.Navy;
            ForeColor = Theme.TextMain;
            Font = Theme.Body(13.5f);
            DoubleBuffered = true;
            // ResizeRedraw：窗口尺寸变化时重绘整个客户区。
            // 否则 WinForms 只重绘「失效区域」，放大后旧标题文字会残留在中间（看起来像画了两份）。
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            try { Icon = IconFromExe(); } catch { }

            BuildLeftPanel();
            BuildTabs();
            BuildBottomBar();
            BuildHeaderControls();

            FormClosing += OnClosing;
            Shown += delegate { RefreshList(); };
            ApplyLayout();
        }

        // ==================== 自适应布局（窗口最大化/拉伸时保持内容铺满） ====================

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            ApplyLayout();
        }

        /// <summary>按当前客户区大小重排所有区域；窗口放大到全屏时内容会一起铺满。</summary>
        public void ApplyLayout()
        {
            if (_layouting) return;
            _layouting = true;
            try
            {
                int w = ClientSize.Width;
                int h = ClientSize.Height;
                const int left = 16, leftW = 250, gap = 12, bottomBar = 90;

                if (_leftPanel != null)
                {
                    _leftPanel.SetBounds(left, 96, leftW, Math.Max(240, h - 96 - bottomBar));

                    int listH = Math.Max(150, _leftPanel.Height - 40 - 152);
                    _charList.SetBounds(12, 40, leftW - 24, listH);

                    int by = 40 + listH + 10;
                    if (_btnNew != null)
                    {
                        _btnNew.SetBounds(12, by, 110, 34);
                        _btnDel.SetBounds(128, by, 110, 34);
                        _btnTemplate.SetBounds(12, by + 40, 226, 34);
                        _btnCopy.SetBounds(12, by + 80, 226, 34);
                    }
                }

                int cx = left + leftW + gap;
                int cw = Math.Max(420, w - cx - 16);

                _tabs.SetBounds(cx, 96, cw, 40);
                _content.SetBounds(cx, 140, cw, Math.Max(220, h - 140 - bottomBar));

                int barY = h - 78;
                int startW = 104;
                int x = w - 16 - startW;
                if (_startButton != null) _startButton.SetBounds(x, barY - 4, startW, 52);
                x -= 12 + 76;
                if (_btnExit != null) _btnExit.SetBounds(x, barY + 4, 76, 42);
                x -= 10 + 118;
                if (_btnDataDir != null) _btnDataDir.SetBounds(x, barY + 4, 118, 42);
                x -= 10 + 100;
                if (_btnHelp != null) _btnHelp.SetBounds(x, barY + 4, 100, 42);

                _status.SetBounds(20, barY - 8, Math.Max(200, x - 32), 62);
                if (_trayButton != null) _trayButton.SetBounds(x, barY - 46, 148, 32);

                // 整块重绘：保证标题栏里的宋体字不会留下旧位置的残影
                Invalidate(true);
            }
            catch (Exception ex)
            {
                Log.Debug("布局异常：" + ex.Message);
            }
            finally { _layouting = false; }
        }

        static Icon IconFromExe()
        {
            try { return Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
            catch { return null; }
        }

        // ==================== 布局 ====================

        void BuildHeaderControls()
        {
            GearButton gear = new GearButton();
            gear.SetBounds(ClientSize.Width - 72, 20, 48, 48);
            gear.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            gear.Click += delegate { OpenSettings(null); };
            Ui.Tip(gear, "设置：语音音量 / 大模型 API / 保存路径 / 日志 / 版本信息");
            Controls.Add(gear);
        }

        void BuildLeftPanel()
        {
            AlPanel p = Ui.Card("角色列表", 16, 96, 250, 596);
            _leftPanel = p;
            Controls.Add(p);

            _charList.SetBounds(12, 40, 226, 418);
            _charList.BorderStyle = BorderStyle.None;
            _charList.BackColor = Color.FromArgb(14, 28, 52);
            _charList.ForeColor = Theme.TextMain;
            _charList.Font = Theme.Body(13f);
            _charList.DrawMode = DrawMode.OwnerDrawFixed;
            _charList.ItemHeight = 54;
            _charList.IntegralHeight = false;
            _charList.DrawItem += DrawCharacterRow;
            _charList.SelectedIndexChanged += delegate
            {
                if (_suppressListEvents) return;
                int idx = _charList.SelectedIndex;
                if (idx < 0 || idx >= _charIds.Count) return;
                ActivateCharacter(_charIds[idx], false);
            };
            _charList.DoubleClick += delegate { StartPet(); };
            p.Controls.Add(_charList);

            Btn(p, "新建角色…", 12, 468, 110, 34, delegate { NewCharacter(); });
            Btn(p, "删除", 128, 468, 110, 34, delegate { DeleteCharacter(); });
            Btn(p, "用内置模板新建…", 12, 508, 226, 34, delegate { NewFromTemplate(); });
            Btn(p, "复制当前角色", 12, 548, 226, 34, delegate { CopyCharacter(); });
            // 记住这四个按钮：窗口缩放时要跟着面板高度走
            _btnNew = (AlButton)p.Controls[p.Controls.Count - 4];
            _btnDel = (AlButton)p.Controls[p.Controls.Count - 3];
            _btnTemplate = (AlButton)p.Controls[p.Controls.Count - 2];
            _btnCopy = (AlButton)p.Controls[p.Controls.Count - 1];
        }

        readonly List<string> _charIds = new List<string>();

        void BuildTabs()
        {
            _tabs.SetBounds(278, 96, 866, 40);
            _tabs.SetItems(_tabTitles);
            _tabs.SelectedIndexChanged += delegate { ShowTab(); };
            Controls.Add(_tabs);

            _content.SetBounds(278, 140, 866, 552);
            _content.BackColor = Theme.Navy;
            Controls.Add(_content);

            TabPageBase[] pages = new TabPageBase[]
            {
                new AssetsTab(this), new LinesTab(this), new VoiceTab(this),
                new CardTab(this), new InteractTab(this), new SfxTab(this)
            };
            for (int i = 0; i < pages.Length; i++)
            {
                pages[i].Visible = false;
                pages[i].Changed += delegate { OnProfileChanged(); };
                _pages[_tabIds[i]] = pages[i];
                _content.Controls.Add(pages[i]);
            }
            ShowTab();
        }

        void BuildBottomBar()
        {
            _status.SetBounds(20, 706, 700, 60);
            _status.AutoSize = false;
            _status.BackColor = Color.Transparent;
            _status.ForeColor = Theme.TextDim;
            _status.Font = Theme.Body(12.5f);
            Controls.Add(_status);

            Btn(this, "使用说明", 740, 718, 100, 42, delegate { ShowHelp(); });
            Btn(this, "打开数据目录", 848, 718, 118, 42, delegate { ErrorDialogForm.OpenFolder(AppPaths.DataDir()); });
            Btn(this, "退出", 974, 718, 76, 42, delegate { ExitApplication(); });
            _btnHelp = (AlButton)Controls[Controls.Count - 3];
            _btnDataDir = (AlButton)Controls[Controls.Count - 2];
            _btnExit = (AlButton)Controls[Controls.Count - 1];

            _trayButton = Btn(this, "隐藏到托盘", 740, 668, 148, 34, delegate
            {
                HideToTray();
            });
            _trayButton.Visible = false;

            _startButton = Btn(this, "开启桌宠", 1044, 712, 100, 52, delegate { StartPet(); });
            _startButton.Primary = true;
            _startButton.Font = Theme.BodyBold(15f);
        }

        AlButton Btn(Control parent, string text, int x, int y, int w, int h, EventHandler onClick)
        {
            AlButton b = new AlButton(text);
            b.SetBounds(x, y, w, h);
            if (onClick != null) b.Click += onClick;
            parent.Controls.Add(b);
            return b;
        }

        // ==================== 绘制 ====================

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Rectangle r = new Rectangle(0, 0, ClientSize.Width, ClientSize.Height);
            using (LinearGradientBrush br = new LinearGradientBrush(r, Theme.Navy2, Theme.Navy, 115f))
                g.FillRectangle(br, r);
            Theme.FillHexBackground(g, r);

            // 顶部标题栏（圆角底部 + 柔和描边）
            Rectangle head = new Rectangle(0, 0, ClientSize.Width, 84);
            using (LinearGradientBrush br = new LinearGradientBrush(head, Theme.Navy3, Theme.Navy, 90f))
                g.FillRectangle(br, head);
            using (SolidBrush br = new SolidBrush(Color.FromArgb(90, 226, 206, 150)))
                g.FillRectangle(br, new Rectangle(0, 82, ClientSize.Width, 1));

            DrawEmblem(g, new Rectangle(20, 16, 52, 52));
            using (SolidBrush br = new SolidBrush(Theme.GoldBright))
                g.DrawString(AppPaths.ProductName, Theme.TitleBold(26f), br, new PointF(84, 12));
            using (SolidBrush br = new SolidBrush(Theme.TextDim))
                g.DrawString("DESK PET · 桌面伴侣 · v" + AppPaths.Version, Theme.Body(11.5f), br, new PointF(88, 50));

            // 右上角宋体字（需求原文：UI 右上角添加宋体中文字符）—— 创作者标识，保持柔金
            using (SolidBrush br = new SolidBrush(Theme.GoldBright))
            {
                g.DrawString(UiCornerText1, Theme.Title(21f), br, new PointF(ClientSize.Width - 214, 14));
                g.DrawString(UiCornerText2, Theme.Title(21f), br, new PointF(ClientSize.Width - 214, 44));
            }
            using (Pen pen = new Pen(Color.FromArgb(80, 226, 206, 150), 1f))
            {
                g.DrawLine(pen, ClientSize.Width - 224, 18, ClientSize.Width - 224, 68);
            }
        }

        /// <summary>顶部徽记：圆角方形（squircle）+ 简约圆环，去掉尖角与舰锚。</summary>
        void DrawEmblem(Graphics g, Rectangle r)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (GraphicsPath path = Theme.RoundedRect(r, (int)Math.Round(r.Width * 0.28)))
            {
                using (LinearGradientBrush br = new LinearGradientBrush(r, Theme.Navy3, Theme.Navy, 90f))
                    g.FillPath(br, path);
                using (Pen pen = new Pen(Color.FromArgb(150, 226, 206, 150), 1.4f))
                    g.DrawPath(pen, path);
            }
            // 简约圆环（柔金），中心一个小圆点
            float cx = r.X + r.Width / 2f, cy = r.Y + r.Height / 2f;
            float rad = r.Width * 0.24f;
            using (Pen pen = new Pen(Theme.Gold, 2.4f))
                g.DrawEllipse(pen, cx - rad, cy - rad, rad * 2, rad * 2);
            using (SolidBrush br = new SolidBrush(Theme.GoldBright))
                g.FillEllipse(br, cx - 2.6f, cy - 2.6f, 5.2f, 5.2f);
        }

        // ==================== 角色列表 ====================

        public void RefreshList()
        {
            _suppressListEvents = true;
            _charIds.Clear();
            _charList.Items.Clear();
            List<CharacterProfile> all = CharacterStore.ListAll();
            foreach (CharacterProfile p in all)
            {
                _charIds.Add(p.id);
                _charList.Items.Add(p.DisplayName());
            }
            string active = Config.Current.activeCharacter;
            int idx = _charIds.IndexOf(active);
            if (idx < 0 && _charIds.Count > 0)
            {
                idx = 0;
                Config.Current.activeCharacter = _charIds[0];
                Config.Save();
            }
            if (idx >= 0) _charList.SelectedIndex = idx;
            _suppressListEvents = false;

            LoadCurrent();
        }

        void DrawCharacterRow(object sender, DrawItemEventArgs e)
        {
            if (e.Index < 0 || e.Index >= _charIds.Count) return;
            bool sel = (e.State & DrawItemState.Selected) == DrawItemState.Selected;
            CharacterProfile p = CharacterStore.Load(_charIds[e.Index]);
            using (SolidBrush br = new SolidBrush(sel ? Color.FromArgb(52, 88, 146) : Color.FromArgb(14, 28, 52)))
                e.Graphics.FillRectangle(br, e.Bounds);
            if (sel)
            {
                using (SolidBrush br = new SolidBrush(Theme.Gold))
                    e.Graphics.FillRectangle(br, new Rectangle(e.Bounds.X, e.Bounds.Y, 3, e.Bounds.Height));
            }
            if (p == null) return;

            // 头像
            Rectangle avatar = new Rectangle(e.Bounds.X + 8, e.Bounds.Y + 5, 44, 44);
            string img = p.CurrentImage();
            Bitmap thumb = string.IsNullOrEmpty(img) ? null : ImageCache.GetThumb(Path.Combine(CharacterStore.ImagesDir(p.id), img), 44);
            using (GraphicsPath path = Theme.RoundedRect(avatar, 10))
            {
                using (SolidBrush br = new SolidBrush(Color.FromArgb(10, 20, 40)))
                    e.Graphics.FillPath(br, path);
                if (thumb != null)
                {
                    Region old = e.Graphics.Clip;
                    e.Graphics.SetClip(path);
                    int w = (int)(thumb.Width * 44.0 / thumb.Height);
                    if (w > 60) w = 60;
                    e.Graphics.DrawImage(thumb, new Rectangle(avatar.X + (44 - w) / 2, avatar.Y, w, 44));
                    e.Graphics.Clip = old;
                }
                using (Pen pen = new Pen(sel ? Theme.Gold : Color.FromArgb(70, 104, 160), 1.2f))
                    e.Graphics.DrawPath(pen, path);
            }

            Rectangle textRect = new Rectangle(avatar.Right + 8, e.Bounds.Y + 4, e.Bounds.Width - avatar.Width - 22, 24);
            using (SolidBrush br = new SolidBrush(sel ? Theme.GoldBright : Theme.TextMain))
                e.Graphics.DrawString(p.DisplayName(), Theme.BodyBold(14f), br, textRect);

            Rectangle subRect = new Rectangle(avatar.Right + 8, e.Bounds.Y + 26, e.Bounds.Width - avatar.Width - 22, 22);
            string sub = (string.IsNullOrEmpty(p.faction) ? "" : p.faction + " · ") + (string.IsNullOrEmpty(p.shipType) ? "" : p.shipType + " · ")
                       + (p.interact.mode == "llm" ? "大模型" : "固定台词");
            using (SolidBrush br = new SolidBrush(sel ? Theme.Gold : Theme.TextDim))
                e.Graphics.DrawString(sub, Theme.Body(11f), br, subRect);
        }

        void ActivateCharacter(string id, bool announce)
        {
            if (string.IsNullOrEmpty(id)) return;
            Config.Current.activeCharacter = id;
            Config.Save();
            LoadCurrent();
            if (announce) SetStatus("已切换到角色：" + (Current != null ? Current.DisplayName() : id));
        }

        void LoadCurrent()
        {
            string id = Config.Current.activeCharacter;
            Current = string.IsNullOrEmpty(id) ? null : CharacterStore.Load(id);
            if (Current != null && !string.IsNullOrEmpty(Current.voices.manifest) == false)
            {
                // 每次绑定前刷新一次语音清单（保证清单状态是最新的）
                VoiceBankLoader.Load(Current, Current.voices.manifest);
            }
            foreach (KeyValuePair<string, TabPageBase> kv in _pages) kv.Value.Bind(Current);
            string name = Current != null ? Current.DisplayName() : "（没有角色）";
            int lines = Current != null ? Current.lines.Count() : 0;
            SetStatus("当前角色：" + name + " · 台词 " + lines + " 条 · 模式 " +
                      (Current != null && Current.interact.mode == "llm" ? "大模型对话" : "固定台词") +
                      " · 数据目录：" + AppPaths.DataDir());
            if (!string.IsNullOrEmpty(Program.StartupTip))
            {
                SetStatus(Program.StartupTip);
                Program.StartupTip = "";
            }
        }

        void ShowTab()
        {
            int idx = _tabs.SelectedIndex;
            for (int i = 0; i < _tabIds.Length; i++)
            {
                TabPageBase page;
                if (_pages.TryGetValue(_tabIds[i], out page)) page.Visible = (i == idx);
            }
        }

        public void ShowTabById(string id)
        {
            for (int i = 0; i < _tabIds.Length; i++)
                if (_tabIds[i] == id) { _tabs.SelectedIndex = i; ShowTab(); return; }
        }

        // ==================== 角色操作 ====================

        void NewCharacter()
        {
            string name = PromptForm.Ask(this, "新建角色", "给这位舰船起个名字：", "新角色");
            if (string.IsNullOrEmpty(name)) return;
            CharacterProfile p = CharacterStore.Create(name, null);
            if (p == null) return;
            RefreshList();
            int idx = _charIds.IndexOf(p.id);
            if (idx >= 0) { _charList.SelectedIndex = idx; ActivateCharacter(p.id, false); }
            SetStatus("已创建角色「" + name + "」，请到「角色素材」导入立绘。");
        }

        void NewFromTemplate()
        {
            List<string> ids = CharacterStore.BuiltinCharacterIds();
            if (ids.Count == 0) { MessageBox.Show(this, "没有内置角色模板（assets\\builtin\\characters 为空）。", AppPaths.ProductName); return; }
            string[] titles = new string[ids.Count];
            string[] tips = new string[ids.Count];
            for (int i = 0; i < ids.Count; i++)
            {
                titles[i] = CharacterStore.BuiltinDisplayName(ids[i]);
                tips[i] = ids[i];
            }
            int pick = ChoiceForm.Ask(this, "用内置模板新建角色", "选择一个内置角色（会复制它的立绘、台词、语音与角色卡）：", titles, tips);
            if (pick < 0) return;
            string name = PromptForm.Ask(this, "角色名称", "角色显示名称：", titles[pick]);
            if (string.IsNullOrEmpty(name)) return;
            Cursor = Cursors.WaitCursor;
            CharacterProfile p;
            try { p = CharacterStore.ImportBuiltinAs(ids[pick], name); }
            finally { Cursor = Cursors.Default; }
            if (p == null) { MessageBox.Show(this, "导入失败，详情见日志。", AppPaths.ProductName); return; }
            RefreshList();
            int idx = _charIds.IndexOf(p.id);
            if (idx >= 0) { _charList.SelectedIndex = idx; ActivateCharacter(p.id, false); }
            SetStatus("已用模板「" + titles[pick] + "」创建角色：" + p.DisplayName());
        }

        void CopyCharacter()
        {
            if (Current == null) return;
            string name = PromptForm.Ask(this, "复制角色", "新角色的名称：", Current.DisplayName() + " 副本");
            if (string.IsNullOrEmpty(name)) return;
            Cursor = Cursors.WaitCursor;
            CharacterProfile p;
            try { p = CharacterStore.Create(name, Current.id); }
            finally { Cursor = Cursors.Default; }
            if (p == null) return;
            RefreshList();
            int idx = _charIds.IndexOf(p.id);
            if (idx >= 0) { _charList.SelectedIndex = idx; ActivateCharacter(p.id, false); }
            SetStatus("已复制出角色：" + p.DisplayName());
        }

        void DeleteCharacter()
        {
            if (Current == null) return;
            string msg = "删除角色「" + Current.DisplayName() + "」？\r\n\r\n该角色的立绘、台词、语音、角色卡都会被一并删除（不可恢复）。";
            if (MessageBox.Show(this, msg, AppPaths.ProductName, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            PetManager.StopIfShowing(Current.id);
            CharacterStore.Delete(Current.id);
            RefreshList();
        }

        void OnProfileChanged()
        {
            if (_saveTimer == null)
            {
                _saveTimer = new Timer();
                _saveTimer.Interval = 600;
                _saveTimer.Tick += delegate
                {
                    _saveTimer.Stop();
                    if (Current != null) CharacterStore.Save(Current);
                    PetManager.NotifyProfileUpdated(Current);
                };
            }
            _saveTimer.Stop();
            _saveTimer.Start();
            if (_charList.SelectedIndex >= 0)
            {
                _charList.Invalidate();
                _charList.Refresh();
            }
        }

        // ==================== 桌宠 / 设置 / 其它 ====================

        void StartPet()
        {
            if (Current == null)
            {
                MessageBox.Show(this, "还没有角色，先点「新建角色…」或「用内置模板新建…」。", AppPaths.ProductName);
                return;
            }
            CharacterStore.Save(Current);
            if (Current.images.Count == 0)
            {
                if (MessageBox.Show(this, "这个角色还没有立绘素材，桌宠会显示成一个占位方块。\r\n\r\n先去「角色素材」导入图片吗？",
                    AppPaths.ProductName, MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                {
                    ShowTabById("assets");
                    _tabs.SelectedIndex = 0;
                    return;
                }
            }
            string error;
            bool ok = PetManager.Start(Current, out error);
            if (!ok)
            {
                MessageBox.Show(this, "启动桌宠失败：" + error + "\r\n\r\n详见日志：" + Log.CurrentLogFile(),
                    AppPaths.ProductName, MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            Hide();
        }

        public void PreviewCurrentCharacter()
        {
            StartPet();
        }

        void HideToTray()
        {
            Hide();
            PetManager.EnsureTray();
        }

        void ExitApplication()
        {
            bool petRuns = PetManager.IsRunning;
            string msg = petRuns ? "退出后桌宠也会一起关闭，确定退出？" : "确定退出？";
            if (MessageBox.Show(this, msg, AppPaths.ProductName, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            Program.ShutdownApp();
        }

        void ShowHelp()
        {
            string file = Path.Combine(AppPaths.InstallDir(), "使用说明.txt");
            if (File.Exists(file))
            {
                try { System.Diagnostics.Process.Start("notepad.exe", "\"" + file + "\""); return; }
                catch { }
            }
            MessageBox.Show(this,
                "使用说明文件没找到。可以打开数据目录查看日志与角色素材：\r\n" + AppPaths.DataDir(),
                AppPaths.ProductName);
        }

        public void OpenSettings(string tab)
        {
            try
            {
                using (SettingsForm f = new SettingsForm(tab))
                {
                    f.ShowDialog(this);
                }
                // 设置里可能改了数据目录 / 音量 / 大模型，刷新界面状态
                Audio.Configure(Config.Current.volume, Config.Current.sfxEnabled);
                LoadCurrent();
                PetManager.NotifySettingsUpdated();
            }
            catch (Exception ex)
            {
                Log.ErrorDialog("打开设置失败", ex);
            }
        }

        public void SetStatus(string text)
        {
            _status.Text = text;
        }

        public void RefreshHeader()
        {
            Invalidate();
        }

        // ==================== 供自检使用的探针 ====================

        /// <summary>当前页第一个面板的尺寸（验证自适应布局是否生效）。</summary>
        public Size CurrentPagePanelSize()
        {
            TabPageBase page;
            if (_pages.TryGetValue(_tabIds[_tabs.SelectedIndex], out page))
                foreach (Control c in page.Controls) return c.Size;
            return Size.Empty;
        }

        /// <summary>当前显示的功能页（自检用）。</summary>
        public TabPageBase CurrentPage()
        {
            TabPageBase page;
            return _pages.TryGetValue(_tabIds[_tabs.SelectedIndex], out page) ? page : null;
        }

        /// <summary>左侧角色面板的高度（验证自适应布局是否生效）。</summary>
        public int LeftPanelHeight()
        {
            return _leftPanel == null ? 0 : _leftPanel.Height;
        }

        /// <summary>关闭主界面时的三种结局（纯判定，便于自检）。</summary>
        public enum CloseDecision
        {
            /// <summary>直接关闭（不拦、不弹窗）。</summary>
            Close = 0,
            /// <summary>只把界面收起来（取消这次关闭 + 隐藏 + 托盘气泡）。</summary>
            HideAndCancel = 1,
            /// <summary>问一句「确定退出？」再决定。</summary>
            AskThenClose = 2
        }

        /// <summary>
        /// 关机 / 注销 / 被任务管理器结束这类「必须马上放行」的关闭原因。
        /// 这类请求绝不能取消、也不能弹确认框，否则 Windows 会报「此应用正在阻止关机」。
        /// </summary>
        public static bool IsSessionEnd(CloseReason reason)
        {
            return reason == CloseReason.WindowsShutDown
                || reason == CloseReason.TaskManagerClosing
                || reason == CloseReason.ApplicationExitCall;
        }

        /// <summary>关闭主界面的决策（纯函数，便于自检）。</summary>
        public static CloseDecision DecideClose(CloseReason reason, bool shuttingDown, bool petsRunning)
        {
            if (IsSessionEnd(reason)) return CloseDecision.Close;   // 系统要关机：立刻放行
            if (shuttingDown) return CloseDecision.Close;
            if (petsRunning) return CloseDecision.HideAndCancel;    // 桌宠还在跑：界面只收起来
            return CloseDecision.AskThenClose;
        }

        void OnClosing(object sender, FormClosingEventArgs e)
        {
            CloseDecision decision = DecideClose(e.CloseReason, Program.ShuttingDown, PetManager.IsRunning);
            if (decision == CloseDecision.Close)
            {
                if (IsSessionEnd(e.CloseReason)) Program.BeginSessionShutdown(e.CloseReason);
                else Program.ShuttingDown = true;
                return;
            }
            if (decision == CloseDecision.HideAndCancel)
            {
                // 桌宠还在跑：主界面只是收起来，不要真的退出
                e.Cancel = true;
                Hide();
                PetManager.EnsureTray();
                PetManager.Balloon("主界面已收起", "桌宠还在桌面上，点托盘图标可以再次打开设置界面。");
                return;
            }
            DialogResult r = MessageBox.Show(this,
                "退出「" + AppPaths.ProductName + "」？\r\n\r\n（桌宠没有在运行，退出后就没有任何窗口了）",
                AppPaths.ProductName, MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (r != DialogResult.Yes) { e.Cancel = true; return; }
            Program.ShuttingDown = true;
        }

        /// <summary>
        /// 自检用：按指定关闭原因真跑一遍关闭逻辑，返回「是否取消了这次关闭」。
        /// 注意：WindowsShutDown 会走真实的关机收尾（自检里 Program.SuppressExitForTest 拦住退出）。
        /// </summary>
        public bool SimulateClosingForTest(CloseReason reason)
        {
            FormClosingEventArgs e = new FormClosingEventArgs(reason, false);
            OnClosing(this, e);
            return e.Cancel;
        }
    }
}
