// ============================================================================
// CharacterTabs.cs —— 主界面的六个功能页：
//   ① 角色素材 ② 角色台词 ③ 角色语音 ④ 角色卡 ⑤ 互动设置 ⑥ 互动音效
// ============================================================================
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Windows.Forms;

namespace AlDeskPet
{
    public abstract class TabPageBase : Panel
    {
        public CharacterProfile Profile;
        public event EventHandler Changed;
        protected MainForm Host;
        protected bool Suppress;

        protected TabPageBase(MainForm host)
        {
            Host = host;
            Dock = DockStyle.Fill;
            BackColor = Theme.Navy;
            DoubleBuffered = true;
        }

        public abstract void Bind(CharacterProfile p);
        public virtual void Flush() { }

        protected void RaiseChanged()
        {
            if (Suppress) return;
            if (Changed != null) Changed(this, EventArgs.Empty);
        }

        protected AlButton Btn(string text, int x, int y, int w, int h, EventHandler onClick)
        {
            AlButton b = new AlButton(text);
            b.SetBounds(x, y, w, h);
            if (onClick != null) b.Click += onClick;
            Controls.Add(b);
            return b;
        }

        protected AlButton BtnIn(Control parent, string text, int x, int y, int w, int h, EventHandler onClick)
        {
            AlButton b = new AlButton(text);
            b.SetBounds(x, y, w, h);
            if (onClick != null) b.Click += onClick;
            parent.Controls.Add(b);
            return b;
        }

        protected Label Lb(Control parent, string text, int x, int y, int w, int h, Color c, float size, bool bold)
        {
            Label l = Ui.L(text, size, c, bold);
            l.AutoSize = false;
            l.SetBounds(x, y, w, h);
            parent.Controls.Add(l);
            return l;
        }

        protected AlPanel Card(string title, int x, int y, int w, int h)
        {
            AlPanel p = Ui.Card(title, x, y, w, h);
            Controls.Add(p);
            return p;
        }

        protected void Info(string text)
        {
            MessageBox.Show(this, text, AppPaths.ProductName, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        protected bool Ask(string text)
        {
            return MessageBox.Show(this, text, AppPaths.ProductName, MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;
        }

        // ---------- 自适应布局助手（窗口最大化 / 拉伸时内容跟着铺满） ----------

        /// <summary>按可见文字找控件（递归；给已建好的按钮/标签补 Anchor 用，自检也会用到）。</summary>
        public static Control FindCtrl(Control parent, string text)
        {
            if (parent == null) return null;
            foreach (Control c in parent.Controls)
            {
                if (string.Equals(c.Text, text, StringComparison.Ordinal)) return c;
                if (c.Controls.Count > 0)
                {
                    Control found = FindCtrl(c, text);
                    if (found != null) return found;
                }
            }
            return null;
        }

        protected void Anch(Control parent, string text, AnchorStyles style)
        {
            Control c = FindCtrl(parent, text);
            if (c != null) c.Anchor = style;
        }

        /// <summary>按控件名递归查找（自检定位用）。</summary>
        public static Control FindByName(Control parent, string name)
        {
            if (parent == null) return null;
            foreach (Control c in parent.Controls)
            {
                if (string.Equals(c.Name, name, StringComparison.Ordinal)) return c;
                if (c.Controls.Count > 0)
                {
                    Control found = FindByName(c, name);
                    if (found != null) return found;
                }
            }
            return null;
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            try { Relayout(); }
            catch (Exception ex) { Log.Debug("页面重排异常：" + ex.Message); }
        }

        /// <summary>窗口尺寸变化时重排本页的面板（面板内的控件靠 Anchor 跟随）。</summary>
        public virtual void Relayout() { }
    }

    // ========================================================================
    // ① 角色素材
    // ========================================================================
    public class AssetsTab : TabPageBase
    {
        Panel _preview;
        ListBox _list;
        Label _info;
        AlPanel _pv, _lp;

        public AssetsTab(MainForm host) : base(host)
        {
            _pv = Card("立绘预览", 0, 0, 486, 516);
            _preview = new Panel();
            _preview.SetBounds(14, 40, 458, 420);
            _preview.BackColor = Color.FromArgb(12, 24, 46);
            _preview.Paint += PaintPreview;
            _pv.Controls.Add(_preview);
            _info = Lb(_pv, "", 14, 468, 458, 40, Theme.TextDim, 12.5f, false);

            _lp = Card("素材列表", 496, 0, 330, 516);
            _list = Ui.LB();
            _list.SetBounds(14, 40, 302, 300);
            _list.ItemHeight = 34;
            _list.DrawItem += DrawImageRow;
            _list.SelectedIndexChanged += delegate { _preview.Invalidate(); };
            _lp.Controls.Add(_list);

            BtnIn(_lp, "导入图片…", 14, 350, 145, 34, delegate { ImportImages(); });
            BtnIn(_lp, "删除", 171, 350, 145, 34, delegate { DeleteImage(); });
            BtnIn(_lp, "设为当前立绘", 14, 390, 145, 34, delegate { SetCurrent(); });
            BtnIn(_lp, "一键抠图", 171, 390, 145, 34, delegate { CutBackground(); });
            BtnIn(_lp, "清空素材", 14, 430, 302, 34, delegate { ClearImages(); });

            Lb(_lp, "支持 PNG / JPG / BMP / GIF（取首帧）。背景不是纯色的立绘建议先用抠图工具处理。",
                14, 470, 302, 40, Theme.TextDim, 11.5f, false);

            // 自适应：预览区与列表随面板拉伸，底部按钮贴着下边
            _preview.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
            _list.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
            _info.Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            Anch(_lp, "导入图片…", AnchorStyles.Bottom | AnchorStyles.Left);
            Anch(_lp, "删除", AnchorStyles.Bottom | AnchorStyles.Left);
            Anch(_lp, "设为当前立绘", AnchorStyles.Bottom | AnchorStyles.Left);
            Anch(_lp, "一键抠图", AnchorStyles.Bottom | AnchorStyles.Left);
            Anch(_lp, "清空素材", AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right);
            foreach (Control c in _lp.Controls)
                if (c is Label) c.Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        }

        public override void Relayout()
        {
            if (_pv == null || _lp == null) return;
            int w = Width, h = Height;
            if (w <= 0 || h <= 0) return;
            const int listW = 330;
            int pvW = Math.Max(300, w - listW - 10);
            _pv.SetBounds(0, 0, pvW, h);
            _lp.SetBounds(pvW + 10, 0, Math.Min(listW, Math.Max(240, w - pvW - 10)), h);
        }

        public override void Bind(CharacterProfile p)
        {
            Profile = p;
            Suppress = true;
            _list.Items.Clear();
            if (p != null)
            {
                foreach (string f in p.images) _list.Items.Add(f);
                if (_list.Items.Count > 0)
                {
                    int idx = p.imageIndex;
                    if (idx < 0 || idx >= _list.Items.Count) idx = 0;
                    _list.SelectedIndex = idx;
                }
            }
            Suppress = false;
            _preview.Invalidate();
            UpdateInfo();
        }

        void UpdateInfo()
        {
            if (Profile == null) { _info.Text = ""; return; }
            string cur = Profile.CurrentImage();
            if (string.IsNullOrEmpty(cur))
            {
                _info.Text = "还没有素材：点右侧「导入图片…」把立绘 PNG 放进来。";
                return;
            }
            string path = Path.Combine(CharacterStore.ImagesDir(Profile.id), cur);
            string extra = "";
            try
            {
                if (File.Exists(path))
                {
                    Bitmap bmp = ImageCache.Get(path);
                    if (bmp != null)
                    {
                        extra = " · " + bmp.Width + "×" + bmp.Height + " · " + Utils.HumanSize(new FileInfo(path).Length)
                              + (ImageFx.HasTransparency(bmp) ? " · 含透明通道" : " · 不透明（建议抠图）");
                    }
                }
            }
            catch { }
            _info.Text = "当前立绘：" + cur + extra;
        }

        string Selected()
        {
            if (_list.SelectedIndex < 0) return "";
            return Convert.ToString(_list.Items[_list.SelectedIndex]);
        }

        void DrawImageRow(object sender, DrawItemEventArgs e)
        {
            if (e.Index < 0) return;
            bool sel = (e.State & DrawItemState.Selected) == DrawItemState.Selected;
            using (SolidBrush br = new SolidBrush(sel ? Color.FromArgb(52, 88, 146) : Color.FromArgb(14, 28, 52)))
                e.Graphics.FillRectangle(br, e.Bounds);

            string name = Convert.ToString(_list.Items[e.Index]);
            string path = Profile == null ? "" : Path.Combine(CharacterStore.ImagesDir(Profile.id), name);
            Bitmap thumb = ImageCache.GetThumb(path, 30);
            if (thumb != null)
            {
                int tw = (int)(thumb.Width * 30.0 / thumb.Height);
                if (tw > 60) tw = 60;
                e.Graphics.DrawImage(thumb, new Rectangle(e.Bounds.X + 3, e.Bounds.Y + 2, tw, 30));
            }
            Rectangle tr = new Rectangle(e.Bounds.X + 70, e.Bounds.Y, e.Bounds.Width - 76, e.Bounds.Height);
            using (StringFormat sf = new StringFormat())
            {
                sf.LineAlignment = StringAlignment.Center;
                sf.Trimming = StringTrimming.EllipsisPath;
                sf.FormatFlags = StringFormatFlags.NoWrap;
                bool isCurrent = Profile != null && Profile.CurrentImage() == name;
                using (SolidBrush br = new SolidBrush(sel ? Theme.GoldBright : (isCurrent ? Theme.Gold : Theme.TextMain)))
                    e.Graphics.DrawString((isCurrent ? "★ " : "") + name, Theme.Body(13f), br, tr, sf);
            }
        }

        void PaintPreview(object sender, PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            Rectangle r = new Rectangle(0, 0, _preview.Width, _preview.Height);
            ImageFx.DrawChecker(g, r, 16);
            using (Pen pen = new Pen(Color.FromArgb(60, 96, 150), 1f)) g.DrawRectangle(pen, 0, 0, r.Width - 1, r.Height - 1);

            string name = "";
            string path = "";
            if (Profile != null)
            {
                int idx = _list.SelectedIndex;
                if (idx >= 0 && idx < Profile.images.Count) name = Profile.images[idx];
                else name = Profile.CurrentImage();
                if (!string.IsNullOrEmpty(name)) path = Path.Combine(CharacterStore.ImagesDir(Profile.id), name);
            }
            Bitmap bmp = string.IsNullOrEmpty(path) ? null : ImageCache.Get(path);
            if (bmp == null)
            {
                Theme.DrawGlowText(g, "尚未导入立绘素材", Theme.BodyBold(15f), Theme.TextDim, r, StringAlignment.Center);
                return;
            }
            double scale = Math.Min((r.Width - 24.0) / bmp.Width, (r.Height - 24.0) / bmp.Height);
            if (scale > 1.0) scale = 1.0;
            int w = Math.Max(1, (int)(bmp.Width * scale));
            int h = Math.Max(1, (int)(bmp.Height * scale));
            Rectangle dst = new Rectangle(r.X + (r.Width - w) / 2, r.Y + (r.Height - h) / 2, w, h);
            g.DrawImage(bmp, dst);
        }

        void ImportImages()
        {
            if (Profile == null) return;
            using (OpenFileDialog dlg = new OpenFileDialog())
            {
                dlg.Title = "选择角色立绘素材（可多选）";
                dlg.Filter = "图片文件|*.png;*.jpg;*.jpeg;*.bmp;*.gif|所有文件|*.*";
                dlg.Multiselect = true;
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                int added = 0;
                foreach (string f in dlg.FileNames)
                {
                    string name = CharacterStore.ImportImage(Profile.id, f);
                    if (name.Length > 0)
                    {
                        if (!Profile.images.Contains(name)) Profile.images.Add(name);
                        added++;
                    }
                }
                if (added > 0 && Profile.imageIndex < 0) Profile.imageIndex = 0;
                CharacterStore.Save(Profile);
                Bind(Profile);
                RaiseChanged();
                Host.SetStatus("已导入 " + added + " 张素材。");
            }
        }

        void DeleteImage()
        {
            if (Profile == null) return;
            string name = Selected();
            if (name.Length == 0) { Info("先在上面的列表里选一张素材。"); return; }
            if (!Ask("确定删除素材「" + name + "」？文件会从角色目录里删掉。")) return;
            try
            {
                string path = Path.Combine(CharacterStore.ImagesDir(Profile.id), name);
                ImageCache.Invalidate(path);
                if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception ex) { Log.ErrorDialog("删除素材失败", ex); }
            Profile.images.Remove(name);
            if (Profile.imageIndex >= Profile.images.Count) Profile.imageIndex = Math.Max(0, Profile.images.Count - 1);
            CharacterStore.Save(Profile);
            Bind(Profile);
            RaiseChanged();
        }

        void SetCurrent()
        {
            if (Profile == null) return;
            string name = Selected();
            if (name.Length == 0) return;
            Profile.imageIndex = _list.SelectedIndex;
            CharacterStore.Save(Profile);
            Bind(Profile);
            RaiseChanged();
            Host.SetStatus("已把「" + name + "」设为当前立绘。");
        }

        void CutBackground()
        {
            if (Profile == null) return;
            string name = Selected();
            if (name.Length == 0) { Info("先选一张要抠图的立绘。"); return; }
            string path = Path.Combine(CharacterStore.ImagesDir(Profile.id), name);
            if (!File.Exists(path)) return;
            if (!Ask("将对「" + name + "」做纯色背景抠图：\r\n\r\n· 从四边取背景色做洪水填充，背景变透明\r\n· 原图会先备份到 images\\_originals\\\r\n· 结果另存为同名 .png\r\n\r\n继续？")) return;
            try
            {
                Bitmap bmp = ImageFx.LoadUnlocked(path);
                if (bmp == null) return;

                string backupDir = Path.Combine(CharacterStore.ImagesDir(Profile.id), "_originals");
                Directory.CreateDirectory(backupDir);
                string backup = Path.Combine(backupDir, name);
                if (!File.Exists(backup)) Utils.CopyFile(path, backup);

                ImageFx.BgRemovalResult r = ImageFx.RemoveEdgeBackground(bmp, 24, true);
                if (r.ok)
                {
                    Bitmap trimmed = ImageFx.TrimTransparent(bmp);
                    string target = Path.ChangeExtension(path, ".png");
                    trimmed.Save(target, System.Drawing.Imaging.ImageFormat.Png);
                    if (trimmed != bmp) bmp.Dispose();
                    ImageCache.Invalidate(target);
                    ImageCache.Invalidate(path);
                    string newName = Path.GetFileName(target);
                    if (!Profile.images.Contains(newName)) Profile.images.Add(newName);
                    Profile.imageIndex = Profile.images.IndexOf(newName);
                    CharacterStore.Save(Profile);
                    Bind(Profile);
                    RaiseChanged();
                    Info(r.message + "\r\n\r\n已另存为：" + newName + "\r\n原图备份在 images\\_originals\\。");
                }
                else
                {
                    Info(r.message);
                }
            }
            catch (Exception ex) { Log.ErrorDialog("抠图失败", ex); }
        }

        void ClearImages()
        {
            if (Profile == null || Profile.images.Count == 0) return;
            if (!Ask("清空该角色的全部素材文件？此操作不可撤销（会删除 images 目录下的图片）。")) return;
            try
            {
                foreach (string f in Profile.images)
                {
                    string path = Path.Combine(CharacterStore.ImagesDir(Profile.id), f);
                    ImageCache.Invalidate(path);
                    if (File.Exists(path)) File.Delete(path);
                }
            }
            catch (Exception ex) { Log.ErrorDialog("清空素材失败", ex); }
            Profile.images.Clear();
            Profile.imageIndex = 0;
            CharacterStore.Save(Profile);
            Bind(Profile);
            RaiseChanged();
        }
    }

    // ========================================================================
    // ② 角色台词
    // ========================================================================
    public class LinesTab : TabPageBase
    {
        readonly ListBox _list = null;
        readonly Dictionary<string, AlButton> _sectionButtons = new Dictionary<string, AlButton>();
        string _section = "click";
        TextBox _text, _voice;
        NumericUpDown _weight, _size;
        AlCombo _color;
        AlCheck _bold;
        Label _count;

        public LinesTab(MainForm host) : base(host)
        {
            _sec = Card("台词段落", 0, 0, 200, 516);
            AlPanel sec = _sec;
            int y = 44;
            foreach (string name in LineSet.SectionNames())
            {
                string captured = name;
                AlButton b = BtnIn(sec, LineSet.SectionTitle(name), 12, y, 176, 44, delegate { SelectSection(captured); });
                _sectionButtons[name] = b;
                y += 50;
            }
            Lb(sec, "「问候」在打开桌宠时显示；「待机」在长时间没互动时显示。", 12, y + 6, 176, 90, Theme.TextDim, 11.5f, false);

            AlPanel mid = Card("台词列表", 210, 0, 336, 516);
            _mid = mid;
            _list = Ui.LB();
            _list.SetBounds(14, 40, 308, 366);
            _list.DrawItem += DrawLineRow;
            _list.SelectedIndexChanged += delegate { LoadEditor(); };
            mid.Controls.Add(_list);

            _count = Lb(mid, "", 14, 410, 308, 22, Theme.TextDim, 12f, false);
            BtnIn(mid, "↑ 上移", 14, 434, 148, 32, delegate { MoveLine(-1); });
            BtnIn(mid, "↓ 下移", 174, 434, 148, 32, delegate { MoveLine(1); });

            AlPanel right = Card("编辑台词", 556, 0, 270, 516);
            _right = right;
            Lb(right, "台词内容（一句话一行，气泡里会原样显示）", 14, 36, 242, 20, Theme.TextDim, 11.5f, false);
            _text = Ui.T("", true, false);
            _text.SetBounds(14, 58, 242, 128);
            right.Controls.Add(_text);

            Lb(right, "权重", 14, 196, 50, 20, Theme.TextDim, 12f, false);
            _weight = Ui.Num(1, 100, 3, 1);
            _weight.SetBounds(64, 192, 70, 26);
            right.Controls.Add(_weight);

            Lb(right, "字号", 148, 196, 50, 20, Theme.TextDim, 12f, false);
            _size = Ui.Num(0, 40, 0, 1);
            _size.SetBounds(198, 192, 58, 26);
            right.Controls.Add(_size);

            Lb(right, "配色", 14, 230, 50, 20, Theme.TextDim, 12f, false);
            _color = Ui.C("（默认）", "gold", "white", "rouge", "indigo", "candy", "cyan");
            _color.SetBounds(64, 226, 122, 26);
            right.Controls.Add(_color);

            _bold = new AlCheck("加粗");
            _bold.SetBounds(196, 226, 60, 26);
            right.Controls.Add(_bold);

            Lb(right, "绑定语音文件名（可留空，由语音清单决定）", 14, 262, 242, 20, Theme.TextDim, 11.5f, false);
            _voice = Ui.T("", false, false);
            _voice.SetBounds(14, 284, 242, 26);
            right.Controls.Add(_voice);

            BtnIn(right, "新增台词", 14, 322, 116, 34, delegate { AddLine(); });
            BtnIn(right, "保存修改", 140, 322, 116, 34, delegate { SaveEdit(); });
            BtnIn(right, "删除选中", 14, 362, 116, 34, delegate { DeleteLine(); });
            BtnIn(right, "清空该段", 140, 362, 116, 34, delegate { ClearSection(); });

            BtnIn(right, "导入台词文件…", 14, 414, 242, 32, delegate { ImportLines(); });
            BtnIn(right, "导出台词到文件…", 14, 450, 242, 32, delegate { ExportLines(); });
            BtnIn(right, "恢复该角色内置台词", 14, 486, 242, 26, delegate { RestoreBuiltin(); });

            // 自适应：列表与编辑区跟随拉伸，底部按钮贴下边
            _list.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
            _count.Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            Anch(mid, "↑ 上移", AnchorStyles.Bottom | AnchorStyles.Left);
            Anch(mid, "↓ 下移", AnchorStyles.Bottom | AnchorStyles.Left);
            _text.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            Anch(right, "导入台词文件…", AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right);
            Anch(right, "导出台词到文件…", AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right);
            Anch(right, "恢复该角色内置台词", AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right);
            Anch(sec, "「问候」在打开桌宠时显示；「待机」在长时间没互动时显示。",
                AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right);
        }

        AlPanel _sec, _mid, _right;

        public override void Relayout()
        {
            if (_sec == null || _mid == null || _right == null) return;
            int w = Width, h = Height;
            if (w <= 0 || h <= 0) return;
            const int secW = 200, rightW = 270;
            int midW = Math.Max(240, w - secW - rightW - 20);
            _sec.SetBounds(0, 0, secW, h);
            _mid.SetBounds(secW + 10, 0, midW, h);
            _right.SetBounds(secW + 10 + midW + 10, 0, Math.Max(230, w - secW - midW - 20), h);
        }

        public override void Bind(CharacterProfile p)
        {
            Profile = p;
            Suppress = true;
            SelectSection(_section, true);
            RefreshList();
            Suppress = false;
        }

        void SelectSection(string name) { SelectSection(name, false); }

        void SelectSection(string name, bool silent)
        {
            _section = name;
            foreach (KeyValuePair<string, AlButton> kv in _sectionButtons)
                kv.Value.Primary = kv.Key == name;
            RefreshList();
            if (!silent) Host.SetStatus("正在编辑：" + LineSet.SectionTitle(name));
        }

        void RefreshList()
        {
            Suppress = true;
            _list.Items.Clear();
            if (Profile != null)
            {
                List<LineItem> items = Profile.lines.Section(_section);
                foreach (LineItem it in items) _list.Items.Add(it.text);
                _count.Text = "共 " + items.Count + " 条";
            }
            else _count.Text = "";
            foreach (KeyValuePair<string, AlButton> kv in _sectionButtons)
            {
                int n = Profile == null ? 0 : Profile.lines.Section(kv.Key).Count;
                kv.Value.Text = LineSet.SectionTitle(kv.Key) + "（" + n + "）";
            }
            Suppress = false;
            _list.Invalidate();
        }

        void DrawLineRow(object sender, DrawItemEventArgs e)
        {
            if (e.Index < 0) return;
            bool sel = (e.State & DrawItemState.Selected) == DrawItemState.Selected;
            using (SolidBrush br = new SolidBrush(sel ? Color.FromArgb(52, 88, 146) : Color.FromArgb(14, 28, 52)))
                e.Graphics.FillRectangle(br, e.Bounds);
            if (Profile == null) return;
            List<LineItem> items = Profile.lines.Section(_section);
            if (e.Index >= items.Count) return;
            LineItem it = items[e.Index];

            string sub = "w" + it.weight.ToString("0.#") + (it.size > 0 ? " · " + it.size + "px" : "");
            string voiceFile = ResolveVoice(Profile, it);
            if (!string.IsNullOrEmpty(voiceFile)) sub = "♪ " + sub;

            Rectangle textRect = new Rectangle(e.Bounds.X + 6, e.Bounds.Y, e.Bounds.Width - 12, e.Bounds.Height);
            using (StringFormat sf = new StringFormat())
            {
                sf.LineAlignment = StringAlignment.Center;
                sf.Trimming = StringTrimming.EllipsisCharacter;
                sf.FormatFlags = StringFormatFlags.NoWrap;
                Color c = string.IsNullOrEmpty(it.color) ? (sel ? Theme.GoldBright : Theme.TextMain) : Theme.LineColor(it.color, Theme.TextMain);
                using (SolidBrush br = new SolidBrush(c))
                    e.Graphics.DrawString(it.text, it.bold ? Theme.BodyBold(13f) : Theme.Body(13f), br, textRect, sf);
                sf.Alignment = StringAlignment.Far;
                using (SolidBrush br = new SolidBrush(sel ? Theme.Gold : Theme.TextDim))
                    e.Graphics.DrawString(sub, Theme.Body(11f), br, textRect, sf);
            }
        }

        static string ResolveVoice(CharacterProfile p, LineItem it)
        {
            if (!string.IsNullOrEmpty(it.voice)) return it.voice;
            if (p != null && p.voices != null) return p.voices.Find(it.text);
            return "";
        }

        void LoadEditor()
        {
            if (Profile == null || _list.SelectedIndex < 0) return;
            List<LineItem> items = Profile.lines.Section(_section);
            if (_list.SelectedIndex >= items.Count) return;
            LineItem it = items[_list.SelectedIndex];
            Suppress = true;
            _text.Text = it.text;
            _weight.Value = (decimal)Math.Max(1, Math.Min(100, it.weight));
            _size.Value = Math.Max(0, Math.Min(40, it.size));
            _bold.SetCheckedSilent(it.bold);
            _voice.Text = it.voice;
            int idx = 0;
            if (string.IsNullOrEmpty(it.color)) idx = 0;
            else
            {
                for (int i = 0; i < _color.Items.Count; i++)
                    if (Convert.ToString(_color.Items[i]) == it.color) { idx = i; break; }
            }
            _color.SelectedIndex = idx;
            Suppress = false;
        }

        LineItem ReadEditor()
        {
            LineItem it = new LineItem();
            it.text = _text.Text.Trim();
            it.weight = (double)_weight.Value;
            it.size = (int)_size.Value;
            it.color = _color.SelectedIndex <= 0 ? "" : Convert.ToString(_color.SelectedItem);
            it.bold = _bold.Checked;
            it.voice = _voice.Text.Trim();
            return it;
        }

        void AddLine()
        {
            if (Profile == null) { Info("先在左侧选择一个角色。"); return; }
            LineItem it = ReadEditor();
            if (it.text.Length == 0) { Info("台词内容不能为空。"); return; }
            Profile.lines.Section(_section).Add(it);
            CharacterStore.Save(Profile);
            RefreshList();
            _list.SelectedIndex = _list.Items.Count - 1;
            RaiseChanged();
        }

        void SaveEdit()
        {
            if (Profile == null || _list.SelectedIndex < 0) { Info("先选中一条台词。"); return; }
            LineItem it = ReadEditor();
            if (it.text.Length == 0) { Info("台词内容不能为空。"); return; }
            List<LineItem> items = Profile.lines.Section(_section);
            items[_list.SelectedIndex] = it;
            CharacterStore.Save(Profile);
            int keep = _list.SelectedIndex;
            RefreshList();
            if (keep < _list.Items.Count) _list.SelectedIndex = keep;
            RaiseChanged();
        }

        void DeleteLine()
        {
            if (Profile == null || _list.SelectedIndex < 0) return;
            List<LineItem> items = Profile.lines.Section(_section);
            items.RemoveAt(_list.SelectedIndex);
            CharacterStore.Save(Profile);
            RefreshList();
            RaiseChanged();
        }

        void ClearSection()
        {
            if (Profile == null) return;
            if (!Ask("清空「" + LineSet.SectionTitle(_section) + "」的全部台词？")) return;
            Profile.lines.Section(_section).Clear();
            CharacterStore.Save(Profile);
            RefreshList();
            RaiseChanged();
        }

        void MoveLine(int delta)
        {
            if (Profile == null || _list.SelectedIndex < 0) return;
            List<LineItem> items = Profile.lines.Section(_section);
            int from = _list.SelectedIndex;
            int to = from + delta;
            if (to < 0 || to >= items.Count) return;
            LineItem tmp = items[from];
            items[from] = items[to];
            items[to] = tmp;
            CharacterStore.Save(Profile);
            RefreshList();
            _list.SelectedIndex = to;
            RaiseChanged();
        }

        void ImportLines()
        {
            if (Profile == null) return;
            using (OpenFileDialog dlg = new OpenFileDialog())
            {
                dlg.Title = "导入台词文件（.txt / .json）";
                dlg.Filter = "台词文件|*.txt;*.json|所有文件|*.*";
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                bool merge = Ask("是否与该角色现有台词合并？\r\n\r\n是 = 追加到对应段落\r\n否 = 覆盖全部段落");
                string report;
                LineSet baseSet = merge ? CharacterStore.CloneLines(Profile.lines) : new LineSet();
                LineSet set = LinePack.ImportFile(dlg.FileName, baseSet, out report);
                Profile.lines = set;
                // 复制台词文件到角色目录留档
                try
                {
                    string dst = Path.Combine(CharacterStore.LinesDir(Profile.id), Path.GetFileName(dlg.FileName));
                    File.Copy(dlg.FileName, dst, true);
                }
                catch { }
                CharacterStore.Save(Profile);
                Bind(Profile);
                RaiseChanged();
                Info(report);
            }
        }

        void ExportLines()
        {
            if (Profile == null) return;
            using (SaveFileDialog dlg = new SaveFileDialog())
            {
                dlg.Title = "导出台词";
                dlg.Filter = "文本文件|*.txt";
                dlg.FileName = Profile.DisplayName() + "-台词.txt";
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    File.WriteAllText(dlg.FileName, LinePack.ToText(Profile), new System.Text.UTF8Encoding(true));
                    Info("已导出到：\r\n" + dlg.FileName);
                }
                catch (Exception ex) { Log.ErrorDialog("导出台词失败", ex); }
            }
        }

        void RestoreBuiltin()
        {
            if (Profile == null) return;
            string file = Path.Combine(AppPaths.BuiltinDir(), "lines", Profile.id + ".txt");
            if (!File.Exists(file))
            {
                Info("该角色没有内置台词文件（只有从内置模板导入的角色才有）。");
                return;
            }
            if (!Ask("用内置台词覆盖当前全部段落？")) return;
            Profile.lines = LinePack.ParseTextFile(file, new LineSet());
            CharacterStore.Save(Profile);
            Bind(Profile);
            RaiseChanged();
            Info("已恢复内置台词。");
        }
    }

    // ========================================================================
    // ③ 角色语音
    // ========================================================================
    public class VoiceTab : TabPageBase
    {
        ListBox _list;
        TextBox _status;
        AlPanel _lp, _rp;

        public VoiceTab(MainForm host) : base(host)
        {
            AlPanel lp = Card("语音包 / 音频文件", 0, 0, 500, 516);
            _lp = lp;
            _status = Ui.T("", true, true);
            _status.SetBounds(14, 40, 472, 96);
            lp.Controls.Add(_status);

            _list = Ui.LB();
            _list.SetBounds(14, 146, 472, 220);
            _list.ItemHeight = 26;
            _list.DoubleClick += delegate { PlaySelected(); };
            lp.Controls.Add(_list);

            BtnIn(lp, "导入音频…", 14, 378, 112, 34, delegate { ImportAudio(); });
            BtnIn(lp, "试听", 134, 378, 90, 34, delegate { PlaySelected(); });
            BtnIn(lp, "删除", 232, 378, 90, 34, delegate { DeleteAudio(); });
            BtnIn(lp, "打开语音目录", 330, 378, 156, 34, delegate
            {
                if (Profile != null) ErrorDialogForm.OpenFolder(CharacterStore.VoicesDir(Profile.id));
            });

            BtnIn(lp, "生成/更新清单模板", 14, 420, 220, 36, delegate { MakeManifest(); });
            BtnIn(lp, "重新载入清单并校验", 244, 420, 242, 36, delegate { ReloadManifest(); });

            Lb(lp, "推荐流程：先导入音频（文件名随意）→ 点「生成/更新清单模板」→ 打开 manifest.txt 逐行核对「台词文本|音频文件名」。",
                14, 464, 472, 44, Theme.TextDim, 11.5f, false);

            AlPanel rp = Card("为什么要写清单", 510, 0, 316, 516);
            _rp = rp;
            TextBox tip = Ui.T(
                "需求提醒：\r\n" +
                "台词音频应当有对应的清单，否则会出现「说这句、放那句」的串词问题。\r\n\r\n" +
                "推荐清单格式（manifest.txt，UTF-8）：\r\n" +
                "  台词文本|音频文件名\r\n" +
                "  台词文本|音频文件名\r\n" +
                "例如：\r\n" +
                "  妾身…困了…|v01.mp3\r\n" +
                "  指挥官…过来…|v02.mp3\r\n\r\n" +
                "也支持：\r\n" +
                "  · 反过来写「音频文件名|台词文本」\r\n" +
                "  · CSV（逗号分隔）\r\n" +
                "  · manifest.json：{\"台词\":\"文件.mp3\"} 或 [{\"text\":\"…\",\"file\":\"…\"}]\r\n\r\n" +
                "没有清单时，软件会退化为「用音频文件名当台词文本」来匹配；" +
                "文件名正好等于台词也能自动挂上。",
                true, true);
            tip.SetBounds(14, 40, 288, 400);
            rp.Controls.Add(tip);
            BtnIn(rp, "写入示例清单文件", 14, 452, 288, 36, delegate { WriteSample(); });

            // 自适应
            _status.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            _list.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
            Anch(lp, "导入音频…", AnchorStyles.Bottom | AnchorStyles.Left);
            Anch(lp, "试听", AnchorStyles.Bottom | AnchorStyles.Left);
            Anch(lp, "删除", AnchorStyles.Bottom | AnchorStyles.Left);
            Anch(lp, "打开语音目录", AnchorStyles.Bottom | AnchorStyles.Left);
            Anch(lp, "生成/更新清单模板", AnchorStyles.Bottom | AnchorStyles.Left);
            Anch(lp, "重新载入清单并校验", AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right);
            foreach (Control c in lp.Controls)
                if (c is Label) c.Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            tip.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
            Anch(rp, "写入示例清单文件", AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right);
        }

        public override void Relayout()
        {
            if (_lp == null || _rp == null) return;
            int w = Width, h = Height;
            if (w <= 0 || h <= 0) return;
            const int rightW = 316;
            int leftW = Math.Max(320, w - rightW - 10);
            _lp.SetBounds(0, 0, leftW, h);
            _rp.SetBounds(leftW + 10, 0, Math.Max(240, w - leftW - 10), h);
        }

        public override void Bind(CharacterProfile p)
        {
            Profile = p;
            RefreshList();
        }

        void RefreshList()
        {
            Suppress = true;
            _list.Items.Clear();
            if (Profile == null)
            {
                _status.Text = "先选择一个角色。";
                Suppress = false;
                return;
            }
            string dir = CharacterStore.VoicesDir(Profile.id);
            if (Directory.Exists(dir))
            {
                foreach (string f in Directory.GetFiles(dir))
                {
                    if (!Audio.IsAudioFile(f)) continue;
                    _list.Items.Add(Path.GetFileName(f) + "|" + Utils.HumanSize(new FileInfo(f).Length));
                }
            }
            VoiceBankLoader.Report r = VoiceBankLoader.Load(Profile, Profile.voices != null ? Profile.voices.manifest : "");
            string head = "语音目录：" + dir + "\r\n";
            if (_list.Items.Count == 0) head += "尚未导入任何音频文件。\r\n";
            _status.Text = head + r.text;
            Suppress = false;
        }

        string SelectedFile()
        {
            if (_list.SelectedIndex < 0) return "";
            string raw = Convert.ToString(_list.Items[_list.SelectedIndex]);
            int bar = raw.LastIndexOf('|');
            return bar > 0 ? raw.Substring(0, bar) : raw;
        }

        void ImportAudio()
        {
            if (Profile == null) return;
            using (OpenFileDialog dlg = new OpenFileDialog())
            {
                dlg.Title = "导入角色语音（可多选）";
                dlg.Filter = "音频文件|*.mp3;*.wav;*.ogg;*.m4a;*.wma;*.flac|所有文件|*.*";
                dlg.Multiselect = true;
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                int n = 0;
                foreach (string f in dlg.FileNames)
                {
                    try
                    {
                        Utils.CopyFile(f, Path.Combine(CharacterStore.VoicesDir(Profile.id), Path.GetFileName(f)));
                        n++;
                    }
                    catch (Exception ex) { Log.Warn("复制音频失败：" + f + " → " + ex.Message); }
                }
                RefreshList();
                RaiseChanged();
                Info("已导入 " + n + " 个音频文件。\r\n\r\n别忘了点「生成/更新清单模板」，把台词与音频对应起来，避免串词。");
            }
        }

        void PlaySelected()
        {
            if (Profile == null) return;
            string file = SelectedFile();
            if (file.Length == 0) return;
            Audio.Play(Path.Combine(CharacterStore.VoicesDir(Profile.id), file), true);
        }

        void DeleteAudio()
        {
            if (Profile == null) return;
            string file = SelectedFile();
            if (file.Length == 0) return;
            if (!Ask("删除音频「" + file + "」？")) return;
            try { File.Delete(Path.Combine(CharacterStore.VoicesDir(Profile.id), file)); }
            catch (Exception ex) { Log.ErrorDialog("删除音频失败", ex); }
            RefreshList();
            RaiseChanged();
        }

        void MakeManifest()
        {
            if (Profile == null) return;
            // 先把当前台词落盘，保证模板里的台词是最新的
            CharacterStore.Save(Profile);
            string message;
            string path = VoiceBankLoader.WriteManifestTemplate(Profile, out message);
            if (path.Length > 0)
            {
                RefreshList();
                Info(message);
                try { System.Diagnostics.Process.Start("notepad.exe", "\"" + path + "\""); }
                catch { }
            }
            else Info(message);
        }

        void ReloadManifest()
        {
            if (Profile == null) return;
            VoiceBankLoader.Report r = VoiceBankLoader.Load(Profile, "manifest.txt");
            CharacterStore.Save(Profile);
            RefreshList();
            RaiseChanged();
            Info(r.text);
        }

        void WriteSample()
        {
            if (Profile == null) return;
            try
            {
                string path = Path.Combine(CharacterStore.VoicesDir(Profile.id), "manifest-示例.txt");
                string[] lines = new string[]
                {
                    "# 语音清单示例：每行「台词文本|音频文件名」",
                    "# 把本文件另存为 manifest.txt 即可被自动读取（也可直接改名）",
                    "你好|v01.mp3",
                    "你终于回来了|v02.mp3",
                    "你怎么不理我|v03.mp3"
                };
                File.WriteAllLines(path, lines, new System.Text.UTF8Encoding(true));
                Info("已写入示例清单：\r\n" + path);
            }
            catch (Exception ex) { Log.ErrorDialog("写入示例清单失败", ex); }
        }
    }
}
