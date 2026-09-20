// ============================================================================
// UiControls.cs —— 自绘控件与主题化小工具（圆润简约风格）
// ============================================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Windows.Forms;

namespace AlDeskPet
{
    // ========================================================================
    // 按钮
    // ========================================================================
    public class AlButton : Control, IButtonControl
    {
        bool _hover, _down;
        bool _primary, _danger;
        bool _showCheck;
        /// <summary>圆角半径（v1.0.9：统一圆角，取代原来的斜切角）。</summary>
        public int CornerRadius = Theme.RadiusControl;

        /// <summary>
        /// 是否在选中时右侧画一个金色对勾。
        /// 默认关闭：只有「固定台词模式 / 大模型对话模式」这种需要一眼看出当前项的按钮才打开，
        /// 否则窄按钮（例如输入条的「发送」）会和文字重叠。
        /// </summary>
        public bool ShowCheck
        {
            get { return _showCheck; }
            set
            {
                if (_showCheck == value) return;
                _showCheck = value;
                Invalidate();
            }
        }

        /// <summary>
        /// 「当前选中」状态。必须是属性：赋值时要立即重绘，
        /// 否则切换对话模式这类「只改状态、鼠标不动」的操作看不到变化（旧版 bug）。
        /// </summary>
        public bool Primary
        {
            get { return _primary; }
            set
            {
                if (_primary == value) return;
                _primary = value;
                Invalidate();
                Update();
            }
        }

        public bool Danger
        {
            get { return _danger; }
            set
            {
                if (_danger == value) return;
                _danger = value;
                Invalidate();
            }
        }

        public DialogResult DialogResult { get; set; }

        public void NotifyDefault(bool value) { }

        public void PerformClick()
        {
            if (Enabled) OnClick(EventArgs.Empty);
        }

        public AlButton()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                     ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
            Cursor = Cursors.Hand;
            Font = Theme.BodyBold(14f);
            ForeColor = Theme.TextMain;
        }

        public AlButton(string text) : this() { Text = text; }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; _down = false; Invalidate(); base.OnMouseLeave(e); }

        // 按下时抓鼠标：即使点击过程中弹出模态窗口也能收到 MouseUp，
        // 不会留下「一直亮着」的假按下状态。
        protected override void OnMouseDown(MouseEventArgs e)
        {
            _down = true;
            try { Capture = true; } catch { }
            Invalidate();
            base.OnMouseDown(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            _down = false;
            try { Capture = false; } catch { }
            Invalidate();
            base.OnMouseUp(e);
        }

        protected override void OnMouseCaptureChanged(EventArgs e)
        {
            if (!Capture) { _down = false; Invalidate(); }
            base.OnMouseCaptureChanged(e);
        }

        protected override void OnEnabledChanged(EventArgs e) { _hover = false; _down = false; Invalidate(); base.OnEnabledChanged(e); }
        protected override void OnTextChanged(EventArgs e) { Invalidate(); base.OnTextChanged(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
            if (Danger)
            {
                using (GraphicsPath path = Theme.RoundedRect(r, CornerRadius))
                {
                    using (LinearGradientBrush br = new LinearGradientBrush(r,
                        _hover ? Color.FromArgb(120, 44, 52) : Color.FromArgb(80, 30, 38),
                        Color.FromArgb(46, 18, 26), 90f))
                        g.FillPath(br, path);
                    using (Pen pen = new Pen(_hover ? Color.FromArgb(240, 130, 130) : Color.FromArgb(150, 70, 74), 1.4f))
                        g.DrawPath(pen, path);
                }
                Theme.DrawGlowText(g, Text, Font, _hover ? Color.FromArgb(255, 190, 190) : Color.FromArgb(226, 160, 164), r, StringAlignment.Center);
                return;
            }
            if (_down)
            {
                using (GraphicsPath path = Theme.RoundedRect(r, CornerRadius))
                using (SolidBrush br = new SolidBrush(Color.FromArgb(60, 92, 146)))
                    g.FillPath(br, path);
            }
            Theme.DrawButton(g, r, Text, _hover || _down, Enabled, Primary);

            // 选中态再画一个金色对勾（仅对比模式这类需要明确区分的按钮开启），避免和「鼠标悬停」混淆
            if (Primary && Enabled && ShowCheck)
            {
                float cx = 14f, cy = Height / 2f;
                using (Pen pen = new Pen(Theme.GoldBright, 2f))
                {
                    pen.StartCap = LineCap.Round;
                    pen.EndCap = LineCap.Round;
                    g.DrawLines(pen, new PointF[]
                    {
                        new PointF(cx - 4, cy),
                        new PointF(cx - 1, cy + 3),
                        new PointF(cx + 5, cy - 4)
                    });
                }
            }
        }
    }

    // ========================================================================
    // 复选框
    // ========================================================================
    public class AlCheck : Control
    {
        bool _hover;
        public bool Checked = false;
        public event EventHandler CheckedChanged;

        public AlCheck() : this("") { }

        public AlCheck(string text)
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                     ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
            Text = text;
            Cursor = Cursors.Hand;
            Font = Theme.Body(13.5f);
            ForeColor = Theme.TextMain;
            Height = 22;
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnClick(EventArgs e)
        {
            Checked = !Checked;
            Invalidate();
            if (CheckedChanged != null) CheckedChanged(this, EventArgs.Empty);
            base.OnClick(e);
        }

        public void SetCheckedSilent(bool value)
        {
            Checked = value;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle box = new Rectangle(1, (Height - 15) / 2, 15, 15);
            using (GraphicsPath path = Theme.RoundedRect(box, 5))
            {
                using (SolidBrush br = new SolidBrush(Checked ? Color.FromArgb(58, 76, 112) : Color.FromArgb(30, 40, 58)))
                    g.FillPath(br, path);
                using (Pen pen = new Pen(Checked ? Theme.Gold : (_hover ? Theme.GoldDim : Color.FromArgb(96, 112, 142)), 1.2f))
                    g.DrawPath(pen, path);
            }
            if (Checked)
            {
                using (Pen pen = new Pen(Theme.GoldBright, 2f))
                {
                    pen.StartCap = LineCap.Round;
                    pen.EndCap = LineCap.Round;
                    g.DrawLines(pen, new Point[]
                    {
                        new Point(box.X + 3, box.Y + 8),
                        new Point(box.X + 6, box.Y + 11),
                        new Point(box.X + 12, box.Y + 3)
                    });
                }
            }
            if (!string.IsNullOrEmpty(Text))
            {
                Color fg = Enabled ? (_hover ? Theme.GoldBright : ForeColor) : Theme.TextDim;
                using (SolidBrush br = new SolidBrush(fg))
                    g.DrawString(Text, Font, br, new PointF(22, (Height - Font.Height) / 2f));
            }
        }
    }

    // ========================================================================
    // 选项卡条
    // ========================================================================
    public class AlTabStrip : Control
    {
        readonly List<string> _items = new List<string>();
        int _selected = 0;
        int _hover = -1;
        public event EventHandler SelectedIndexChanged;

        public AlTabStrip()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Navy;
            Height = 40;
            Font = Theme.BodyBold(14.5f);
            Cursor = Cursors.Hand;
        }

        public int SelectedIndex
        {
            get { return _selected; }
            set
            {
                if (value < 0 || value >= _items.Count || value == _selected) return;
                _selected = value;
                Invalidate();
                if (SelectedIndexChanged != null) SelectedIndexChanged(this, EventArgs.Empty);
            }
        }

        public void SetItems(params string[] items)
        {
            _items.Clear();
            _items.AddRange(items);
            _selected = 0;
            Invalidate();
        }

        int ItemWidth()
        {
            if (_items.Count == 0) return 0;
            return Math.Max(96, (Width - 8) / _items.Count);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int idx = HitTest(e.X);
            if (idx != _hover) { _hover = idx; Invalidate(); }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e) { _hover = -1; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            int idx = HitTest(e.X);
            if (idx >= 0) SelectedIndex = idx;
            base.OnMouseDown(e);
        }

        int HitTest(int x)
        {
            int w = ItemWidth();
            if (w <= 0) return -1;
            int idx = x / w;
            if (idx < 0 || idx >= _items.Count) return -1;
            return idx;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Theme.Navy);
            int w = ItemWidth();
            for (int i = 0; i < _items.Count; i++)
            {
                Rectangle r = new Rectangle(i * w, 2, w - 4, Height - 6);
                bool sel = i == _selected;
                Color c1 = sel ? Theme.PanelHi : (i == _hover ? Color.FromArgb(44, 56, 80) : Color.FromArgb(30, 40, 58));
                Color c2 = sel ? Color.FromArgb(34, 44, 64) : Color.FromArgb(22, 30, 45);
                using (GraphicsPath path = Theme.RoundedRect(r, Theme.PillRadius(r.Height)))
                {
                    using (LinearGradientBrush br = new LinearGradientBrush(r, c1, c2, 90f))
                        g.FillPath(br, path);
                    using (Pen pen = new Pen(sel ? Theme.Gold : Color.FromArgb(78, 96, 128), sel ? 1.4f : 1f))
                        g.DrawPath(pen, path);
                }
                Theme.DrawGlowText(g, _items[i], Font, sel ? Theme.GoldBright : (i == _hover ? Theme.TextMain : Theme.TextDim), r, StringAlignment.Center);
            }
            using (Pen pen = new Pen(Color.FromArgb(60, 74, 100), 1f))
                g.DrawLine(pen, 0, Height - 2, Width, Height - 2);
        }
    }

    // ========================================================================
    // 面板（渐变 + 斜切角 + 可选标题）
    // ========================================================================
    public class AlPanel : Panel
    {
        public string Title = "";
        public bool Highlight = false;
        public int TitleHeight = 30;

        public AlPanel()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Panel;
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
            Theme.DrawPanel(g, r, Highlight);
            if (!string.IsNullOrEmpty(Title))
            {
                using (SolidBrush br = new SolidBrush(Theme.Gold))
                    g.FillRectangle(br, 12, 12, 3, TitleHeight - 12);
                using (SolidBrush br = new SolidBrush(Theme.GoldBright))
                    g.DrawString(Title, Theme.BodyBold(15f), br, new PointF(22, 8));
            }
        }
    }

    // ========================================================================
    // 下拉框（自绘，深色）
    // ========================================================================
    public class AlCombo : ComboBox
    {
        public AlCombo()
        {
            DrawMode = DrawMode.OwnerDrawFixed;
            DropDownStyle = ComboBoxStyle.DropDownList;
            FlatStyle = FlatStyle.Flat;
            BackColor = Color.FromArgb(24, 44, 80);
            ForeColor = Theme.TextMain;
            Font = Theme.Body(13.5f);
            ItemHeight = 22;
        }

        protected override void OnDrawItem(DrawItemEventArgs e)
        {
            if (e.Index < 0) return;
            bool sel = (e.State & DrawItemState.Selected) == DrawItemState.Selected;
            using (SolidBrush br = new SolidBrush(sel ? Color.FromArgb(52, 88, 146) : Color.FromArgb(24, 44, 80)))
                e.Graphics.FillRectangle(br, e.Bounds);
            using (SolidBrush br = new SolidBrush(sel ? Theme.GoldBright : Theme.TextMain))
                e.Graphics.DrawString(Items[e.Index].ToString(), Font, br,
                    new RectangleF(e.Bounds.X + 3, e.Bounds.Y + 3, e.Bounds.Width - 6, e.Bounds.Height));
        }
    }

    // ========================================================================
    // 主题化小工具
    // ========================================================================
    public static class Ui
    {
        public static Label L(string text, float size, Color color, bool bold)
        {
            Label l = new Label();
            l.Text = text;
            l.AutoSize = true;
            l.BackColor = Color.Transparent;
            l.ForeColor = color;
            l.Font = bold ? Theme.BodyBold(size) : Theme.Body(size);
            return l;
        }

        public static Label LTitle(string text, float size)
        {
            return L(text, size, Theme.GoldBright, true);
        }

        public static TextBox T(string text, bool multiline, bool readOnly)
        {
            TextBox t = new TextBox();
            t.Text = text;
            t.Multiline = multiline;
            t.ReadOnly = readOnly;
            t.BorderStyle = BorderStyle.FixedSingle;
            t.BackColor = readOnly ? Color.FromArgb(16, 30, 54) : Color.FromArgb(12, 24, 46);
            t.ForeColor = readOnly ? Theme.TextDim : Theme.TextMain;
            t.Font = Theme.Body(13.5f);
            if (multiline) t.ScrollBars = ScrollBars.Vertical;
            return t;
        }

        public static AlCombo C(params string[] items)
        {
            AlCombo c = new AlCombo();
            c.Items.AddRange(items);
            if (items.Length > 0) c.SelectedIndex = 0;
            return c;
        }

        public static ListBox LB()
        {
            ListBox lb = new ListBox();
            lb.BorderStyle = BorderStyle.None;
            lb.BackColor = Color.FromArgb(14, 28, 52);
            lb.ForeColor = Theme.TextMain;
            lb.Font = Theme.Body(13.5f);
            lb.DrawMode = DrawMode.OwnerDrawFixed;
            lb.ItemHeight = 24;
            lb.IntegralHeight = false;
            return lb;
        }

        public static NumericUpDown Num(decimal min, decimal max, decimal value, decimal step)
        {
            NumericUpDown n = new NumericUpDown();
            n.Minimum = min;
            n.Maximum = max;
            n.Value = Math.Max(min, Math.Min(max, value));
            n.Increment = step;
            n.BorderStyle = BorderStyle.FixedSingle;
            n.BackColor = Color.FromArgb(12, 24, 46);
            n.ForeColor = Theme.TextMain;
            n.Font = Theme.Body(13.5f);
            n.Width = 78;
            return n;
        }

        public static TrackBar Slider(int min, int max, int value)
        {
            TrackBar t = new TrackBar();
            t.Minimum = min;
            t.Maximum = max;
            t.Value = Math.Max(min, Math.Min(max, value));
            t.TickStyle = TickStyle.None;
            t.AutoSize = false;
            t.Height = 26;
            t.BackColor = Theme.Panel;
            return t;
        }

        /// <summary>把一组控件放进一个带标题的 AlPanel（返回面板，控件坐标相对面板）。</summary>
        public static AlPanel Card(string title, int x, int y, int w, int h)
        {
            AlPanel p = new AlPanel();
            p.Title = title;
            p.TitleHeight = 30;
            p.Location = new Point(x, y);
            p.Size = new Size(w, h);
            return p;
        }

        /// <summary>
        /// 加提示气泡。注意：ToolTip 是独立的本机窗口，宿主控件销毁后必须一起释放，
        /// 否则它的内部定时器会去访问已释放的控件（表现为随机的 ObjectDisposedException / 进程异常退出）。
        /// </summary>
        public static void Tip(Control host, string text)
        {
            ToolTip tip = new ToolTip();
            tip.SetToolTip(host, text);
            tip.AutoPopDelay = 15000;
            tip.InitialDelay = 400;
            tip.ReshowDelay = 200;
            host.Disposed += delegate
            {
                try { tip.Dispose(); }
                catch { }
            };
        }

        /// <summary>列表行绘制：选中/悬停背景 + 主文本 + 右侧次要信息。</summary>
        public static void DrawRow(Graphics g, Rectangle bounds, bool selected, bool hover, string main, string sub, Color mainColor)
        {
            Color back = selected ? Color.FromArgb(52, 88, 146) : (hover ? Color.FromArgb(30, 54, 96) : Color.Transparent);
            if (back.A != 0)
            {
                using (SolidBrush br = new SolidBrush(back)) g.FillRectangle(br, bounds);
            }
            if (selected)
            {
                using (SolidBrush br = new SolidBrush(Theme.Gold))
                    g.FillRectangle(br, new Rectangle(bounds.X, bounds.Y, 3, bounds.Height));
            }
            Rectangle textRect = new Rectangle(bounds.X + 10, bounds.Y, bounds.Width - 20, bounds.Height);
            using (StringFormat sf = new StringFormat())
            {
                sf.LineAlignment = StringAlignment.Center;
                sf.Trimming = StringTrimming.EllipsisCharacter;
                sf.FormatFlags = StringFormatFlags.NoWrap;
                using (SolidBrush br = new SolidBrush(selected ? Theme.GoldBright : mainColor))
                    g.DrawString(main, Theme.Body(13.5f), br, textRect, sf);
                if (!string.IsNullOrEmpty(sub))
                {
                    sf.Alignment = StringAlignment.Far;
                    using (SolidBrush br = new SolidBrush(selected ? Theme.Gold : Theme.TextDim))
                        g.DrawString(sub, Theme.Body(11.5f), br, textRect, sf);
                }
            }
        }
    }

    // ========================================================================
    // 错误弹窗（需求 7：带日志路径 + 「请把日志发给 AI/技术人员」）
    // ========================================================================
    public class ErrorDialogForm : Form
    {
        readonly string _logPath;

        public ErrorDialogForm(string context, Exception ex, string logPath, string message)
        {
            _logPath = logPath;
            Text = AppPaths.ProductName + " · 出错了";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterScreen;
            MaximizeBox = false;
            MinimizeBox = false;
            ClientSize = new Size(620, 400);
            BackColor = Theme.Navy;
            ForeColor = Theme.TextMain;
            Font = Theme.Body(13.5f);
            ShowInTaskbar = true;

            Panel head = new Panel();
            head.Dock = DockStyle.Top;
            head.Height = 52;
            head.Paint += delegate (object s, PaintEventArgs e)
            {
                Theme.FillGradient(e.Graphics, new Rectangle(0, 0, head.Width, head.Height), Theme.Navy2, Theme.Navy, 90f);
                using (Pen pen = new Pen(Theme.Gold, 1.4f)) e.Graphics.DrawLine(pen, 0, head.Height - 1, head.Width, head.Height - 1);
                Theme.DrawGlowText(e.Graphics, "遇到错误 · 日志已生成", Theme.TitleBold(19f), Theme.GoldBright,
                    new Rectangle(16, 0, head.Width - 32, head.Height), StringAlignment.Near);
            };
            Controls.Add(head);

            TextBox body = Ui.T(message, true, true);
            body.SetBounds(16, 66, ClientSize.Width - 32, 200);
            body.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            Controls.Add(body);

            Label tip = Ui.L("提示：日志文件里包含完整的错误原因与代码位置，直接上传给 AI 或技术人员，比截图有效得多。", 12f, Theme.TextDim, false);
            tip.SetBounds(16, 274, ClientSize.Width - 32, 40);
            Controls.Add(tip);

            AlButton copy = new AlButton("复制日志路径");
            copy.SetBounds(16, 330, 150, 36);
            copy.Click += delegate
            {
                try { Clipboard.SetText(_logPath); }
                catch { }
            };
            Controls.Add(copy);

            AlButton openDir = new AlButton("打开日志文件夹");
            openDir.SetBounds(176, 330, 150, 36);
            openDir.Click += delegate { OpenFolder(Path.GetDirectoryName(_logPath)); };
            Controls.Add(openDir);

            AlButton openFile = new AlButton("打开日志文件");
            openFile.SetBounds(336, 330, 140, 36);
            openFile.Click += delegate
            {
                try { Process.Start("notepad.exe", "\"" + _logPath + "\""); }
                catch (Exception e2) { Log.Warn("打开日志失败：" + e2.Message); }
            };
            Controls.Add(openFile);

            AlButton close = new AlButton("关闭");
            close.Primary = true;
            close.SetBounds(ClientSize.Width - 126, 330, 110, 36);
            close.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            close.Click += delegate { Close(); };
            Controls.Add(close);
        }

        public static void OpenFolder(string dir)
        {
            try
            {
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
                Process.Start("explorer.exe", "\"" + dir + "\"");
            }
            catch (Exception ex) { Log.Warn("打开目录失败：" + ex.Message); }
        }
    }

    // ========================================================================
    // 权限申请弹窗（需求 6：拒绝则不启用系统事件监听）
    // ========================================================================
    public class PermissionForm : Form
    {
        public PermissionForm()
        {
            Text = AppPaths.ProductName + " · 需要授权";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false;
            MinimizeBox = false;
            ClientSize = new Size(560, 380);
            BackColor = Theme.Navy;
            ForeColor = Theme.TextMain;
            Font = Theme.Body(13.5f);

            Panel head = new Panel();
            head.Dock = DockStyle.Top;
            head.Height = 52;
            head.Paint += delegate (object s, PaintEventArgs e)
            {
                Theme.FillGradient(e.Graphics, new Rectangle(0, 0, head.Width, head.Height), Theme.Navy2, Theme.Navy, 90f);
                using (Pen pen = new Pen(Theme.Gold, 1.4f)) e.Graphics.DrawLine(pen, 0, head.Height - 1, head.Width, head.Height - 1);
                Theme.DrawGlowText(e.Graphics, "系统事件监听授权", Theme.TitleBold(19f), Theme.GoldBright,
                    new Rectangle(16, 0, head.Width - 32, head.Height), StringAlignment.Near);
            };
            Controls.Add(head);

            string text =
                "为了让桌宠能「看着你在做什么」并主动开口（例如：你刚打完游戏，它问你刚刚在玩什么），" +
                "本软件需要读取以下信息：\r\n\r\n" +
                "  ·  当前前台窗口所属的程序名（如 chrome.exe）\r\n" +
                "  ·  当前窗口标题文本\r\n" +
                "  ·  键盘鼠标的空闲时间\r\n\r\n" +
                "这些信息只在内存中保留最近 30 分钟，用于在开启大模型对话时生成一句人话上下文；" +
                "不会写入磁盘、不会上传到任何服务器（除你自己配置的大模型接口外）。\r\n\r\n" +
                "如果拒绝，桌宠依然可用，只是不能根据你在做什么主动搭话；" +
                "之后可以在「齿轮 → 互动设置」里重新授权。";

            TextBox body = Ui.T(text, true, true);
            body.SetBounds(16, 64, ClientSize.Width - 32, 232);
            Controls.Add(body);

            AlButton deny = new AlButton("拒绝");
            deny.Danger = true;
            deny.SetBounds(ClientSize.Width - 250, 312, 110, 38);
            deny.Click += delegate { DialogResult = DialogResult.Cancel; Close(); };
            Controls.Add(deny);

            AlButton allow = new AlButton("允许并启用");
            allow.Primary = true;
            allow.SetBounds(ClientSize.Width - 130, 312, 114, 38);
            allow.Click += delegate { DialogResult = DialogResult.OK; Close(); };
            Controls.Add(allow);
            AcceptButton = allow;
        }
    }

    // ========================================================================
    // 单行输入弹窗
    // ========================================================================
    public class PromptForm : Form
    {
        public string Value = "";
        readonly TextBox _box;

        public PromptForm(string title, string label, string initial)
        {
            Text = title;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false;
            MinimizeBox = false;
            ClientSize = new Size(460, 176);
            BackColor = Theme.Navy;
            ForeColor = Theme.TextMain;
            Font = Theme.Body(13.5f);

            Label lb = Ui.L(label, 13.5f, Theme.TextMain, false);
            lb.SetBounds(18, 20, 420, 24);
            Controls.Add(lb);

            _box = Ui.T(initial, false, false);
            _box.SetBounds(18, 52, 420, 28);
            _box.SelectAll();
            Controls.Add(_box);

            AlButton cancel = new AlButton("取消");
            cancel.SetBounds(210, 108, 100, 36);
            cancel.Click += delegate { DialogResult = DialogResult.Cancel; Close(); };
            Controls.Add(cancel);

            AlButton ok = new AlButton("确定");
            ok.Primary = true;
            ok.SetBounds(324, 108, 114, 36);
            ok.Click += delegate { Value = _box.Text.Trim(); DialogResult = DialogResult.OK; Close(); };
            Controls.Add(ok);

            AcceptButton = ok;
            CancelButton = cancel;
        }

        public static string Ask(IWin32Window owner, string title, string label, string initial)
        {
            using (PromptForm f = new PromptForm(title, label, initial))
            {
                if (f.ShowDialog(owner) == DialogResult.OK) return f.Value;
                return null;
            }
        }
    }

    // ========================================================================
    // 选择弹窗（列表单选）
    // ========================================================================
    public class ChoiceForm : Form
    {
        public int Selected = -1;
        readonly ListBox _list;

        public ChoiceForm(string title, string label, string[] items, string[] tips)
        {
            Text = title;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false;
            MinimizeBox = false;
            ClientSize = new Size(520, 380);
            BackColor = Theme.Navy;
            ForeColor = Theme.TextMain;
            Font = Theme.Body(13.5f);

            Label lb = Ui.L(label, 13.5f, Theme.TextMain, false);
            lb.SetBounds(18, 16, 480, 24);
            Controls.Add(lb);

            _list = Ui.LB();
            _list.SetBounds(18, 48, 484, 268);
            _list.ItemHeight = 30;
            for (int i = 0; i < items.Length; i++) _list.Items.Add(items[i]);
            _list.DrawItem += delegate (object s, DrawItemEventArgs e)
            {
                if (e.Index < 0) return;
                bool sel = (e.State & DrawItemState.Selected) == DrawItemState.Selected;
                using (SolidBrush br = new SolidBrush(sel ? Color.FromArgb(52, 88, 146) : Color.FromArgb(14, 28, 52)))
                    e.Graphics.FillRectangle(br, e.Bounds);
                string sub = (tips != null && e.Index < tips.Length) ? tips[e.Index] : "";
                Ui.DrawRow(e.Graphics, e.Bounds, sel, false, _list.Items[e.Index].ToString(), sub, Theme.TextMain);
            };
            if (items.Length > 0) _list.SelectedIndex = 0;
            _list.DoubleClick += delegate { Accept(); };
            Controls.Add(_list);

            AlButton cancel = new AlButton("取消");
            cancel.SetBounds(270, 328, 100, 36);
            cancel.Click += delegate { DialogResult = DialogResult.Cancel; Close(); };
            Controls.Add(cancel);

            AlButton ok = new AlButton("确定");
            ok.Primary = true;
            ok.SetBounds(384, 328, 114, 36);
            ok.Click += delegate { Accept(); };
            Controls.Add(ok);

            AcceptButton = ok;
            CancelButton = cancel;
        }

        void Accept()
        {
            Selected = _list.SelectedIndex;
            DialogResult = Selected >= 0 ? DialogResult.OK : DialogResult.Cancel;
            Close();
        }

        public static int Ask(IWin32Window owner, string title, string label, string[] items, string[] tips)
        {
            using (ChoiceForm f = new ChoiceForm(title, label, items, tips))
            {
                if (f.ShowDialog(owner) == DialogResult.OK) return f.Selected;
                return -1;
            }
        }
    }
}
