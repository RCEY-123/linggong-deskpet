// ============================================================================
// PetForm.cs —— 桌面上的桌宠本体 + 对话气泡 + 输入框 + 运行管理
// ----------------------------------------------------------------------------
// 桌宠窗口用「分层窗口（UpdateLayeredWindow）」实现真正的逐像素透明：
//   · 只有立绘像素接收点击，透明区域鼠标直接穿透到桌面
//   · Q 弹动画用双缓冲画布复用 + 30fps 局部重绘，空闲时完全不动（0% CPU）
//   · 不缓存多份缩放帧，避免大立绘把内存吃满
// ============================================================================
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Timer = System.Windows.Forms.Timer;

namespace AlDeskPet
{
    // ========================================================================
    // 分层窗口基础设施
    // ========================================================================
    public static class Layered
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct POINT { public int x; public int y; public POINT(int x, int y) { this.x = x; this.y = y; } }

        [StructLayout(LayoutKind.Sequential)]
        public struct SIZE { public int cx; public int cy; public SIZE(int w, int h) { cx = w; cy = h; } }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct BLENDFUNCTION
        {
            public byte BlendOp;
            public byte BlendFlags;
            public byte SourceConstantAlpha;
            public byte AlphaFormat;
        }

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize,
            IntPtr hdcSrc, ref POINT pptSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);

        [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hWnd);
        [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
        [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr hDC);
        [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr hdc);
        [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr hdc, IntPtr hObject);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr hObject);

        const int ULW_ALPHA = 2;
        const byte AC_SRC_OVER = 0;
        const byte AC_SRC_ALPHA = 1;

        /// <summary>把画布贴到分层窗口上（画布必须是 32bppPArgb 预乘格式）。</summary>
        /// <param name="screenX">窗口在屏幕坐标里的位置：UpdateLayeredWindow 的 pptDst 是屏幕坐标，
        /// 传 (0,0) 会把窗口挪到屏幕左上角——必须传窗口自身位置。</param>
        public static bool Apply(IntPtr hwnd, Bitmap canvas, int screenX, int screenY)
        {
            IntPtr screenDc = GetDC(IntPtr.Zero);
            IntPtr memDc = CreateCompatibleDC(screenDc);
            IntPtr hBitmap = IntPtr.Zero;
            IntPtr oldBitmap = IntPtr.Zero;
            try
            {
                hBitmap = canvas.GetHbitmap(Color.FromArgb(0));
                oldBitmap = SelectObject(memDc, hBitmap);
                SIZE size = new SIZE(canvas.Width, canvas.Height);
                POINT src = new POINT(0, 0);
                POINT dst = new POINT(screenX, screenY);
                BLENDFUNCTION blend = new BLENDFUNCTION();
                blend.BlendOp = AC_SRC_OVER;
                blend.BlendFlags = 0;
                blend.SourceConstantAlpha = 255;
                blend.AlphaFormat = AC_SRC_ALPHA;
                bool ok = UpdateLayeredWindow(hwnd, screenDc, ref dst, ref size, memDc, ref src, 0, ref blend, ULW_ALPHA);
                if (!ok)
                {
                    Log.Warn("UpdateLayeredWindow 失败（Win32 err=" + Marshal.GetLastWin32Error()
                        + "，" + canvas.Width + "×" + canvas.Height + " @ " + screenX + "," + screenY + "）");
                }
                else Log.Debug("分层窗口贴图：" + canvas.Width + "×" + canvas.Height + " @ " + screenX + "," + screenY);
                return ok;
            }
            finally
            {
                ReleaseDC(IntPtr.Zero, screenDc);
                if (hBitmap != IntPtr.Zero)
                {
                    SelectObject(memDc, oldBitmap);
                    DeleteObject(hBitmap);
                }
                DeleteDC(memDc);
            }
        }
    }

    // ========================================================================
    // 对话气泡（分层窗口，柔和阴影 + 圆角柔和对话框）
    // ========================================================================
    public class BubbleForm : Form
    {
        const int WS_EX_LAYERED = 0x00080000;

        string _text = "";
        string _title = "";
        int _fontSize = 16;
        Color _textColor = Theme.TextMain;
        bool _bold;
        Bitmap _canvas;
        Bitmap _frame;
        int _sl, _st, _sr, _sb;
        Rectangle _closeRect;
        Timer _lifeTimer;
        Timer _dotTimer;
        int _dots;
        bool _thinking;
        public event EventHandler BubbleClosed;

        /// <summary>
        /// 由桌宠注入：返回 true 表示「现在还不能收气泡」（例如这句台词的语音还在播）。
        /// 注意：WinForms 里 Form.Close() 会 **销毁** 窗口，之后再用就抛 ObjectDisposedException，
        /// 所以气泡的关闭一律走 HideBubble()（只隐藏、不销毁），实例长期复用。
        /// </summary>
        public Func<bool> HoldOpen;

        public BubbleForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            BackColor = Theme.Navy;
            try { LoadFrame(); } catch { }
            Click += delegate (object s, EventArgs e) { HideBubble(); };
        }

        /// <summary>收起气泡（只隐藏，可反复复用）。</summary>
        public void HideBubble()
        {
            try
            {
                if (_lifeTimer != null) _lifeTimer.Stop();
                StopDotTimer();
                if (!IsDisposed && Visible) Hide();
                if (BubbleClosed != null) BubbleClosed(this, EventArgs.Empty);
            }
            catch (Exception ex) { Log.Debug("收起气泡异常：" + ex.Message); }
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= WS_EX_LAYERED | 0x00000080; // 分层 + 工具窗口（不进 Alt-Tab）
                return cp;
            }
        }

        void LoadFrame()
        {
            _frame = Theme.LoadUiAsset("dialog-frame.png");
            if (_frame == null) return;
            try
            {
                string json = Path.Combine(AppPaths.BuiltinDir(), "ui", "dialog-frame.json");
                if (File.Exists(json))
                {
                    Dictionary<string, object> d = Json.AsObj(Json.TryParse(File.ReadAllText(json, Encoding.UTF8)));
                    if (d != null)
                    {
                        _sl = Json.I(d, "left", 60);
                        _st = Json.I(d, "top", 60);
                        _sr = Json.I(d, "right", 60);
                        _sb = Json.I(d, "bottom", 60);
                    }
                }
            }
            catch { }
            if (_sl <= 0) _sl = 60;
            if (_st <= 0) _st = 60;
            if (_sr <= 0) _sr = 60;
            if (_sb <= 0) _sb = 60;
        }

        public bool IsShowing { get { return Visible; } }

        /// <summary>当前气泡里的文字（自检用；命名避开 Form.Text）。</summary>
        public string BubbleText { get { return _text; } }

        public void ShowText(string title, string text, int fontSize, Color color, bool bold, int autoCloseSeconds)
        {
            _title = title ?? "";
            _text = text ?? "";
            _fontSize = fontSize <= 0 ? 16 : fontSize;
            _textColor = color;
            _bold = bold;
            _thinking = false;
            StopDotTimer();
            Relayout();
            if (!Visible) Show();
            BringToFront();
            if (autoCloseSeconds > 0)
            {
                if (_lifeTimer == null)
                {
                    _lifeTimer = new Timer();
                    _lifeTimer.Tick += delegate
                    {
                        // 正在等大模型回复时不要把气泡收掉
                        if (_thinking) return;
                        // 语音还在播就先留着：每 600ms 复查一次，播完再按设定时间收
                        if (HoldOpen != null && HoldOpen())
                        {
                            _lifeTimer.Interval = 600;
                            return;
                        }
                        HideBubble();
                    };
                }
                _lifeTimer.Stop();
                _lifeTimer.Interval = Math.Max(1200, autoCloseSeconds * 1000);
                _lifeTimer.Start();
            }
            else if (_lifeTimer != null) _lifeTimer.Stop();
        }

        public void ShowThinking(string title, int autoCloseSeconds)
        {
            _title = title ?? "";
            _text = "……";
            _thinking = true;
            _dots = 1;
            Relayout();
            if (!Visible) Show();
            BringToFront();
            if (_dotTimer == null)
            {
                _dotTimer = new Timer();
                _dotTimer.Interval = 380;
                _dotTimer.Tick += delegate
                {
                    _dots = _dots % 5 + 1;
                    _text = new string('·', _dots) + "…";
                    Relayout();
                };
            }
            _dotTimer.Start();
            if (autoCloseSeconds > 0 && _lifeTimer != null)
            {
                _lifeTimer.Stop();
                _lifeTimer.Interval = Math.Max(5000, autoCloseSeconds * 1000);
                _lifeTimer.Start();
            }
        }

        void StopDotTimer()
        {
            if (_dotTimer != null) _dotTimer.Stop();
        }

        /// <summary>按文本长度算气泡大小并重新贴图（九宫格按框体固有内边距排版）。</summary>
        public void Relayout()
        {
            try
            {
                int maxTextWidth = 420;
                Font font = Theme.BodySized(_fontSize, _bold);

                // 框体固定区不可拉伸，正文必须让开这些区域
                int topInset = _frame != null ? Math.Max(_st, 52) : 30;
                int bottomInset = _frame != null ? (int)Math.Round(_sb * 0.85) : 20;
                int leftInset = _frame != null ? Math.Max(26, (int)Math.Round(_sl * 0.6)) : 18;
                int rightPad = 34;
                int tailH = 20;

                List<string> lines;
                using (Bitmap probe = new Bitmap(1, 1))
                using (Graphics pg = Graphics.FromImage(probe))
                    lines = Utils.WrapText(pg, _text, font, maxTextWidth);

                int textW = 0;
                using (Bitmap probe2 = new Bitmap(1, 1))
                using (Graphics pg2 = Graphics.FromImage(probe2))
                {
                    foreach (string line in lines)
                    {
                        int w = (int)Math.Ceiling(pg2.MeasureString(line, font, int.MaxValue, StringFormat.GenericTypographic).Width);
                        if (w > textW) textW = w;
                    }
                }

                int width = textW + leftInset + rightPad + 10;
                if (width < 300) width = 300;
                int maxW = maxTextWidth + leftInset + rightPad + 10;
                if (width > maxW) width = maxW;

                int textH = Math.Max(font.Height + 6, lines.Count * (font.Height + 7));
                int height = topInset + textH + bottomInset + 14 + tailH;

                int oldW = Width, oldH = Height;
                Size = new Size(width, height);
                if (_canvas == null || _canvas.Width != width || _canvas.Height != height)
                {
                    if (_canvas != null) _canvas.Dispose();
                    _canvas = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
                }
                Render(lines, font, leftInset, topInset, tailH);
                PositionNear(oldW, oldH);
                if (Handle != IntPtr.Zero) Layered.Apply(Handle, _canvas, Left, Top);
            }
            catch (Exception ex)
            {
                Log.Error("气泡重绘失败", ex);
            }
        }

        void Render(List<string> lines, Font font, int leftInset, int topInset, int tailH)
        {
            using (Graphics g = Graphics.FromImage(_canvas))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.CompositingMode = CompositingMode.SourceCopy;
                g.Clear(Color.Transparent);
                g.CompositingMode = CompositingMode.SourceOver;

                Rectangle body = new Rectangle(0, 0, _canvas.Width, _canvas.Height - tailH);
                if (_frame != null)
                {
                    // 框体本身是半透明的（原本贴在游戏的亮背景上）。
                    // 桌面上背景可能是深色的，所以先在面板内区铺一层浅色底，
                    // 保证深色正文在任何壁纸上都读得清。
                    int bx = Math.Max(6, (int)(_sl * 0.45));
                    int by = Math.Max(8, topInset - 14);
                    int bw = Math.Max(10, _canvas.Width - bx - 12);
                    int bh = Math.Max(10, body.Height - by - Math.Max(6, (int)(_sb * 0.42)));
                    Rectangle inner = new Rectangle(bx, by, bw, bh);
                    using (GraphicsPath path = Theme.RoundedRect(inner, Theme.RadiusDialog))
                    using (SolidBrush br = new SolidBrush(Color.FromArgb(238, 246, 248, 253)))
                        g.FillPath(br, path);
                    ImageFx.DrawNineSlice(g, _frame, body, _sl, _st, _sr, _sb);
                }
                else
                {
                    Rectangle r = new Rectangle(0, 0, body.Width - 1, body.Height - 1);
                    using (GraphicsPath path = Theme.RoundedRect(r, Theme.RadiusDialog))
                    {
                        using (LinearGradientBrush br = new LinearGradientBrush(r,
                            Color.FromArgb(246, 246, 249, 253), Color.FromArgb(240, 232, 240, 250), 90f))
                            g.FillPath(br, path);
                        using (Pen pen = new Pen(Color.FromArgb(190, 92, 112, 148), 1.6f))
                            g.DrawPath(pen, path);
                    }
                    using (SolidBrush br = new SolidBrush(Color.FromArgb(235, 26, 52, 94)))
                        g.FillRectangle(br, new Rectangle(r.X + 10, r.Bottom - 12, r.Width - 20, 8));
                }

                // 名字牌（盖住框体自带名牌区域，保证每个角色都显示正确的名字）
                if (!string.IsNullOrEmpty(_title))
                {
                    Font nameFont = Theme.BodyBold(13f);
                    int nw = (int)g.MeasureString(_title, nameFont).Width + 28;
                    int plateH = 26;
                    Rectangle plate = new Rectangle(leftInset - 12, Math.Max(8, (topInset - plateH) / 2 + 4),
                        Math.Min(nw, _canvas.Width - leftInset - 60), plateH);
                    using (GraphicsPath path = Theme.RoundedRect(plate, Theme.PillRadius(plateH)))
                    {
                        using (LinearGradientBrush br = new LinearGradientBrush(plate, Theme.Navy3, Theme.Navy, 90f))
                            g.FillPath(br, path);
                        using (Pen pen = new Pen(Theme.Gold, 1.2f))
                            g.DrawPath(pen, path);
                    }
                    using (SolidBrush br = new SolidBrush(Theme.GoldBright))
                        g.DrawString(_title, nameFont, br, new PointF(plate.X + 13, plate.Y + 5));
                }

                // 正文（浅色框体上用深色字，保证可读性）
                Color textColor = _frame != null ? Theme.ForLightPanel(_textColor) : _textColor;
                float y = topInset + 2;
                using (SolidBrush br = new SolidBrush(textColor))
                {
                    foreach (string line in lines)
                    {
                        g.DrawString(line, font, br, new PointF(leftInset, y));
                        y += font.Height + 7;
                    }
                }

                // 关闭 ✕：有框体时直接复用框体自带的 ✕（只做点击热区），没有框体才自己画
                if (_frame != null)
                {
                    _closeRect = new Rectangle(Math.Max(0, _canvas.Width - 96), 6, 52, 44);
                }
                else
                {
                    _closeRect = new Rectangle(_canvas.Width - 34, 8, 24, 24);
                    using (Pen pen = new Pen(Color.FromArgb(220, 60, 90, 140), 2f))
                    {
                        g.DrawLine(pen, _closeRect.X + 6, _closeRect.Y + 6, _closeRect.Right - 6, _closeRect.Bottom - 6);
                        g.DrawLine(pen, _closeRect.Right - 6, _closeRect.Y + 6, _closeRect.X + 6, _closeRect.Bottom - 6);
                    }
                }

                // 尾巴（指向桌宠）
                Point petAnchor = PetAnchorScreen();
                int tailX = Math.Max(30, Math.Min(_canvas.Width - 40, petAnchor.X - Left));
                Point[] tail = new Point[]
                {
                    new Point(tailX - 13, body.Bottom - 2),
                    new Point(tailX + 13, body.Bottom - 2),
                    new Point(tailX, _canvas.Height - 1)
                };
                using (SolidBrush br = new SolidBrush(Color.FromArgb(246, 240, 244, 252)))
                    g.FillPolygon(br, tail);
                using (Pen pen = new Pen(Color.FromArgb(200, 40, 70, 120), 1.4f))
                    g.DrawLines(pen, new Point[] { tail[0], tail[2], tail[1] });
            }
        }

        /// <summary>气泡尾巴要指向的点（屏幕坐标＝立绘头顶中点）。</summary>
        Point PetAnchorScreen()
        {
            Rectangle sprite = Manager != null && !Manager.IsDisposed
                ? Manager.SpriteRect()
                : new Rectangle(Left, Top, Width, Height);
            Rectangle work = Screen.FromPoint(new Point(sprite.Left + sprite.Width / 2, sprite.Top + sprite.Height / 2)).WorkingArea;
            int x = Math.Max(work.Left, Math.Min(work.Right, sprite.Left + sprite.Width / 2));
            return new Point(x, sprite.Top);
        }

        /// <summary>供自检使用：把气泡画布复制一份出来。</summary>
        public Bitmap SnapshotCanvas()
        {
            if (_canvas == null) return null;
            try { return new Bitmap(_canvas); }
            catch { return null; }
        }

        /// <summary>由 PetManager 注入，用于把气泡对准桌宠。</summary>
        public PetForm Manager;

        void PositionNear(int oldW, int oldH)
        {
            if (Manager == null || Manager.IsDisposed) { Left = 100; Top = 100; return; }
            Rectangle sprite = Manager.SpriteRect();
            Rectangle work = Screen.FromPoint(new Point(sprite.Left + sprite.Width / 2, sprite.Top + sprite.Height / 2)).WorkingArea;
            Location = Placement(new Size(Width, Height), sprite, work);
        }

        /// <summary>
        /// 气泡落点（纯函数，便于自检）：默认贴在立绘正上方、尾巴尖刚好落在头顶，
        /// 上方放不下时退到立绘左侧，最后才贴工作区底边——原则是尽量不压住角色。
        /// </summary>
        public static Point Placement(Size bubble, Rectangle sprite, Rectangle work)
        {
            int x = sprite.Left + sprite.Width / 2 - bubble.Width / 2;
            x = Math.Max(work.Left + 4, Math.Min(work.Right - bubble.Width - 4, x));
            int y = sprite.Top - bubble.Height - 2;
            if (y < work.Top + 4)
            {
                int lx = sprite.Left - bubble.Width - 12;
                if (lx >= work.Left + 4)
                {
                    int ly = sprite.Bottom - bubble.Height;
                    ly = Math.Max(work.Top + 4, Math.Min(work.Bottom - bubble.Height - 4, ly));
                    return new Point(lx, ly);
                }
                y = sprite.Bottom + 6;
            }
            y = Math.Max(work.Top + 4, Math.Min(work.Bottom - bubble.Height - 4, y));
            return new Point(x, y);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (_closeRect.Contains(e.Location)) { HideBubble(); return; }
            base.OnMouseDown(e);
        }

        protected override void OnPaintBackground(PaintEventArgs e) { /* 分层窗口自行贴图 */ }

        protected override void OnClosed(EventArgs e)
        {
            StopDotTimer();
            if (_lifeTimer != null) _lifeTimer.Stop();
            base.OnClosed(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_canvas != null) { _canvas.Dispose(); _canvas = null; }
            }
            base.Dispose(disposing);
        }
    }

    // ========================================================================
    // 输入框（大模型模式下的对话输入条）
    // ========================================================================
    public class InputBarForm : Form
    {
        public TextBox Box;
        public AlButton SendButton;
        public event EventHandler Submit;
        public event EventHandler Dismissed;

        public InputBarForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            BackColor = Theme.Navy2;
            ClientSize = new Size(420, 54);
            Padding = new Padding(1);

            Panel shell = new Panel();
            shell.Dock = DockStyle.Fill;
            shell.Paint += delegate (object s, PaintEventArgs e)
            {
                Rectangle r = new Rectangle(0, 0, shell.Width - 1, shell.Height - 1);
                using (GraphicsPath path = Theme.RoundedRect(r, Theme.RadiusDialog))
                {
                    using (LinearGradientBrush br = new LinearGradientBrush(r, Theme.PanelHi, Theme.Navy, 90f))
                        e.Graphics.FillPath(br, path);
                    using (Pen pen = new Pen(Theme.Gold, 1.4f))
                        e.Graphics.DrawPath(pen, path);
                }
            };
            Controls.Add(shell);

            Box = new TextBox();
            Box.BorderStyle = BorderStyle.FixedSingle;
            Box.BackColor = Color.FromArgb(10, 20, 40);
            Box.ForeColor = Theme.TextMain;
            Box.Font = Theme.Body(14f);
            Box.SetBounds(12, 13, 300, 28);
            Box.KeyDown += delegate (object s, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Enter && !e.Shift && !e.Control)
                {
                    e.SuppressKeyPress = true;
                    if (Submit != null) Submit(this, EventArgs.Empty);
                }
                else if (e.KeyCode == Keys.Escape) Hide();
            };
            shell.Controls.Add(Box);

            SendButton = new AlButton("发送");
            SendButton.Primary = true;
            SendButton.SetBounds(320, 11, 60, 32);
            SendButton.Click += delegate { if (Submit != null) Submit(this, EventArgs.Empty); };
            shell.Controls.Add(SendButton);

            AlButton close = new AlButton("✕");
            close.SetBounds(386, 11, 26, 32);
            close.Click += delegate
            {
                Hide();
                if (Dismissed != null) Dismissed(this, EventArgs.Empty);
            };
            shell.Controls.Add(close);
        }

        public void ShowUnder(PetForm pet)
        {
            try
            {
                Rectangle sprite = pet.SpriteRect();
                Rectangle work = Screen.FromPoint(new Point(sprite.Left + sprite.Width / 2, sprite.Top + sprite.Height / 2)).WorkingArea;
                Location = Placement(new Size(Width, Height), sprite, work);
                if (!Visible) Show();
                BringToFront();
                Box.Focus();
                Box.SelectAll();
            }
            catch (Exception ex) { Log.Error("显示输入框失败", ex); }
        }

        /// <summary>
        /// 输入条落点（纯函数，便于自检）：**永远不压住角色**——
        /// 优先放在立绘正下方；下方放不下就放到立绘左侧、与角色底部对齐；
        /// 再放不下才贴工作区底边。
        /// </summary>
        public static Point Placement(Size bar, Rectangle sprite, Rectangle work)
        {
            int x = sprite.Left + sprite.Width / 2 - bar.Width / 2;
            x = Math.Max(work.Left + 4, Math.Min(work.Right - bar.Width - 4, x));
            int y = sprite.Bottom + 10;
            if (y + bar.Height > work.Bottom - 4)
            {
                int lx = sprite.Left - bar.Width - 12;
                if (lx >= work.Left + 4)
                {
                    int ly = sprite.Bottom - bar.Height;
                    ly = Math.Max(work.Top + 4, Math.Min(work.Bottom - bar.Height - 4, ly));
                    return new Point(lx, ly);
                }
                y = work.Bottom - bar.Height - 6;
            }
            y = Math.Max(work.Top + 4, Math.Min(work.Bottom - bar.Height - 4, y));
            return new Point(x, y);
        }
    }

    // ========================================================================
    // 桌宠本体
    // ========================================================================
    public class PetForm : Form
    {
        const int WS_EX_LAYERED = 0x00080000;
        const int WS_EX_TOOLWINDOW = 0x00000080;

        public string CharacterId = "";
        CharacterProfile _profile;
        Bitmap _sprite;             // 预乘 alpha 的立绘（只保留一份）
        Bitmap _canvasA, _canvasB;  // 双缓冲画布（复用，不每帧分配）
        bool _useA = true;
        int _spriteW, _spriteH;
        int _baseW, _baseH;
        bool _isOpaque;   // 立绘没有透明通道时，按「卡片」样式绘制（圆角 + 金边 + 阴影）
        Bitmap _lastRendered;

        Timer _anim;
        DateTime _animStart;
        int _animMs = 420;
        bool _dragging;
        Point _dragStart;
        Point _formStart;
        bool _moved;
        bool _busy;                 // 正在等大模型
        DateTime _lastInteract = DateTime.Now;
        Timer _proactiveTimer;
        Timer _idleTimer;
        DateTime _lastProactive = DateTime.Now;

        // ---- 显示方式（始终最上层 / 焦点冻结 / 全屏隐藏）----
        PetDisplayState _displayState = PetDisplayState.Normal;
        bool _displayPaused;                 // 冻结或进后台中：停动画、停主动搭话
        DateTime _pausedSince = DateTime.MinValue;
        DateTime _lastUserAction = DateTime.MinValue;
        /// <summary>用户刚碰过桌宠的宽限期：这段时间内不做冻结/隐藏，碰一下就能把它叫回来。</summary>
        public const int UserGraceSeconds = 4;

        BubbleForm _bubble;
        InputBarForm _input;
        string _lastLine = "";
        string _voiceFile = "";
        readonly List<LlmMessage> _history = new List<LlmMessage>();

        public event EventHandler Interacted;

        public PetForm(CharacterProfile profile)
        {
            CharacterId = profile.id;
            _profile = profile;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            BackColor = Theme.Navy;
            Text = AppPaths.ProductName + " · " + profile.DisplayName();
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);

            _bubble = new BubbleForm();
            _bubble.Manager = this;
            _bubble.HoldOpen = delegate { return IsVoicePlaying(); };
            _input = new InputBarForm();
            _input.Submit += delegate { SendFromInput(); };

            LoadSprite();
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= WS_EX_LAYERED | WS_EX_TOOLWINDOW;
                return cp;
            }
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        // ---------------- 立绘加载 ----------------

        void LoadSprite()
        {
            try
            {
                Bitmap src = null;
                string img = _profile.CurrentImage();
                if (!string.IsNullOrEmpty(img))
                {
                    string path = Path.Combine(CharacterStore.ImagesDir(_profile.id), img);
                    src = ImageCache.Get(path);
                }
                if (src == null) src = MakePlaceholder();
                _isOpaque = !ImageFx.HasTransparency(src);

                double scale = _profile.interact.scale;
                if (scale <= 0) scale = 1.0;
                int targetH = (int)Math.Round(320 * scale);
                int maxH = (int)Math.Round(Screen.PrimaryScreen.WorkingArea.Height * 0.62);
                if (targetH > maxH) targetH = maxH;
                if (targetH < 60) targetH = 60;
                if (targetH > src.Height) targetH = src.Height;

                double ratio = (double)targetH / src.Height;
                _spriteW = Math.Max(24, (int)Math.Round(src.Width * ratio));
                _spriteH = targetH;
                int maxW = (int)Math.Round(Screen.PrimaryScreen.WorkingArea.Width * 0.5);
                if (_spriteW > maxW)
                {
                    _spriteW = maxW;
                    _spriteH = Math.Max(24, (int)Math.Round(src.Height * (double)maxW / src.Width));
                }

                // 一次性生成「预乘 alpha」的缩放立绘：之后所有帧都直接用它缩放，避免透明边缘发黑
                if (_baseW == 0) { _baseW = 12; _baseH = 12; }
                int mw = (int)Math.Ceiling(_spriteW * 1.16);
                int mh = (int)Math.Ceiling(_spriteH * 1.16);
                _baseW = Math.Max(24, mw);
                _baseH = Math.Max(24, mh);

                if (_sprite != null) _sprite.Dispose();
                _sprite = new Bitmap(_spriteW, _spriteH, PixelFormat.Format32bppPArgb);
                using (Graphics g = Graphics.FromImage(_sprite))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.CompositingMode = CompositingMode.SourceCopy;
                    g.DrawImage(src, new Rectangle(0, 0, _spriteW, _spriteH));
                }

                ClientSize = new Size(_baseW, _baseH);
                BuildCanvases();
                RestorePosition();
                RenderFrame(1.0, 0);
                Log.Info("桌宠立绘已加载：" + (string.IsNullOrEmpty(img) ? "(占位)" : img) + " → " + _spriteW + "×" + _spriteH);
            }
            catch (Exception ex)
            {
                Log.ErrorDialog("加载桌宠立绘失败", ex);
            }
        }

        Bitmap MakePlaceholder()
        {
            int w = 220, h = 300;
            Bitmap bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                PointF[] hex = new PointF[6];
                for (int i = 0; i < 6; i++)
                {
                    double a = Math.PI / 180 * (60 * i - 30);
                    hex[i] = new PointF((float)(w / 2.0 + (w / 2.0 - 6) * Math.Cos(a)), (float)(h / 2.0 + (h / 2.0 - 6) * Math.Sin(a)));
                }
                using (LinearGradientBrush br = new LinearGradientBrush(new Rectangle(0, 0, w, h), Color.FromArgb(60, 100, 164), Color.FromArgb(16, 32, 60), 90f))
                    g.FillPolygon(br, hex);
                using (Pen pen = new Pen(Theme.Gold, 3f))
                    g.DrawPolygon(pen, hex);
                using (StringFormat sf = new StringFormat())
                {
                    sf.Alignment = StringAlignment.Center;
                    sf.LineAlignment = StringAlignment.Center;
                    using (SolidBrush br = new SolidBrush(Theme.GoldBright))
                        g.DrawString(_profile.DisplayName() + "\r\n(未导入立绘)", Theme.BodyBold(18f), br, new RectangleF(0, 0, w, h), sf);
                }
            }
            return bmp;
        }

        void BuildCanvases()
        {
            if (_canvasA != null && _canvasA.Width == _baseW && _canvasA.Height == _baseH) return;
            if (_canvasA != null) { _canvasA.Dispose(); _canvasA = null; }
            if (_canvasB != null) { _canvasB.Dispose(); _canvasB = null; }
            _canvasA = new Bitmap(_baseW, _baseH, PixelFormat.Format32bppPArgb);
            _canvasB = new Bitmap(_baseW, _baseH, PixelFormat.Format32bppPArgb);
        }

        void RestorePosition()
        {
            Rectangle work = Screen.PrimaryScreen.WorkingArea;
            int x = work.Right - Width - 60;
            int y = work.Bottom - Height - 40;
            string saved = Config.Current.petPosition;
            if (!string.IsNullOrEmpty(saved))
            {
                string[] parts = saved.Split(',');
                int px, py;
                if (parts.Length == 2 && int.TryParse(parts[0], out px) && int.TryParse(parts[1], out py))
                {
                    x = px;
                    y = py;
                }
            }
            // 确保可见
            if (x < work.Left - Width + 40 || x > work.Right - 40) x = work.Right - Width - 60;
            if (y < work.Top - 20 || y > work.Bottom - 40) y = work.Bottom - Height - 40;
            Location = new Point(x, y);
        }

        public void SavePosition()
        {
            try
            {
                Config.Current.petPosition = Left + "," + Top;
                Config.Save();
            }
            catch { }
        }

        // ---------------- 分层绘制 ----------------

        void RenderFrame(double sy, int bobY)
        {
            if (_sprite == null || _canvasA == null) return;
            Bitmap canvas = _useA ? _canvasA : _canvasB;
            _useA = !_useA;

            double sx = 1.0 + (1.0 - sy) * 0.55;
            int w = Math.Max(2, (int)Math.Round(_spriteW * sx));
            int h = Math.Max(2, (int)Math.Round(_spriteH * sy));
            int x = (canvas.Width - w) / 2;
            int y = canvas.Height - h - (int)Math.Round(canvas.Height * 0.02) + bobY;

            using (Graphics g = Graphics.FromImage(canvas))
            {
                g.CompositingMode = CompositingMode.SourceCopy;
                g.Clear(Color.Transparent);
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                if (_isOpaque)
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.CompositingMode = CompositingMode.SourceOver;
                    Rectangle card = new Rectangle(x, y, w, h);
                    // 外阴影（两层，模拟柔和投影）
                    using (GraphicsPath sp = Theme.RoundedRect(new Rectangle(card.X - 4, card.Y - 2, card.Width + 8, card.Height + 8), 16))
                    using (SolidBrush sb = new SolidBrush(Color.FromArgb(46, 0, 0, 0)))
                        g.FillPath(sb, sp);
                    using (GraphicsPath sp = Theme.RoundedRect(new Rectangle(card.X - 2, card.Y - 1, card.Width + 4, card.Height + 5), 14))
                    using (SolidBrush sb = new SolidBrush(Color.FromArgb(70, 0, 0, 0)))
                        g.FillPath(sb, sp);
                    // 卡片本体（圆角裁切）
                    using (GraphicsPath path = Theme.RoundedRect(card, 12))
                    {
                        g.SetClip(path);
                        g.DrawImage(_sprite, card);
                        g.ResetClip();
                        using (Pen pen = new Pen(Color.FromArgb(210, 232, 200, 106), 2f))
                            g.DrawPath(pen, path);
                        using (Pen pen = new Pen(Color.FromArgb(90, 255, 255, 255), 1f))
                            g.DrawPath(pen, Theme.RoundedRect(new Rectangle(card.X + 2, card.Y + 2, card.Width - 4, card.Height - 4), 10));
                    }
                }
                else
                {
                    g.DrawImage(_sprite, new Rectangle(x, y, w, h));
                }
            }
            if (Handle != IntPtr.Zero) Layered.Apply(Handle, canvas, Left, Top);
            _lastRendered = canvas;
        }

        /// <summary>供自检 / 预览使用：把当前帧画布复制一份出来。</summary>
        public Bitmap SnapshotFrame()
        {
            if (_lastRendered == null) return null;
            try { return new Bitmap(_lastRendered); }
            catch { return null; }
        }

        public static double SquashCurve(double p)
        {
            if (p <= 0) return 1.0;
            if (p >= 1) return 1.0;
            if (p < 0.20) return 1.0 - 0.17 * (p / 0.20);                    // 压扁
            if (p < 0.42) return 0.83 + 0.27 * ((p - 0.20) / 0.22);          // 弹起（过冲）
            if (p < 0.70) return 1.10 - 0.10 * ((p - 0.42) / 0.28);          // 回落
            return 1.0 + 0.035 * Math.Sin((p - 0.70) / 0.30 * Math.PI);      // 收尾微颤
        }

        public void PlayBounce()
        {
            if (!_profile.interact.clickAnimation) { RenderFrame(1.0, 0); return; }
            _animMs = Math.Max(140, _profile.interact.animDurationMs);
            _animStart = DateTime.Now;
            if (_anim == null)
            {
                _anim = new Timer();
                _anim.Interval = 33;
                _anim.Tick += delegate
                {
                    double p = (DateTime.Now - _animStart).TotalMilliseconds / _animMs;
                    if (p >= 1)
                    {
                        _anim.Stop();
                        RenderFrame(1.0, 0);
                        return;
                    }
                    double sy = SquashCurve(p);
                    int bob = (int)Math.Round(-8 * Math.Sin(Math.Min(1, Math.Max(0, (p - 0.25) / 0.6)) * Math.PI));
                    RenderFrame(sy, bob);
                };
            }
            _anim.Start();
        }

        /// <summary>按下时立刻压扁（松开后弹起，和参考插件的「按压 Q 弹」一致）。</summary>
        void PressSquash()
        {
            if (!_profile.interact.clickAnimation) return;
            if (_anim != null) _anim.Stop();
            RenderFrame(0.85, 0);
        }

        protected override void OnPaintBackground(PaintEventArgs e) { }
        protected override void OnPaint(PaintEventArgs e) { }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            RenderFrame(1.0, 0);
        }

        // ---------------- 鼠标交互 ----------------

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left)
            {
                _dragging = true;
                _moved = false;
                _lastUserAction = DateTime.Now;   // 用户碰过桌宠 → 显示监听会给它几秒宽限
                _dragStart = Cursor.Position;
                _formStart = Location;
                Audio.Play(Audio.ResolveSfx(_profile, "press"));
                PressSquash();
                Capture = true;
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (!_dragging) return;
            Point now = Cursor.Position;
            int dx = now.X - _dragStart.X;
            int dy = now.Y - _dragStart.Y;
            if (!_moved && (Math.Abs(dx) > 4 || Math.Abs(dy) > 4))
            {
                _moved = true;
                if (_anim != null) _anim.Stop();
                RenderFrame(1.0, 0);
            }
            if (_moved)
            {
                Location = new Point(_formStart.X + dx, _formStart.Y + dy);
                if (_bubble.Visible) _bubble.Relayout();
                if (_input.Visible) _input.ShowUnder(this);
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button != MouseButtons.Left) return;
            _lastUserAction = DateTime.Now;
            Capture = false;
            _dragging = false;
            Audio.Play(Audio.ResolveSfx(_profile, "release"));
            if (_moved)
            {
                SavePosition();
                return;
            }
            PlayBounce();
            // 需求 4：本句台词的语音还没播完时，互动只做立绘变化，不切下一句台词
            if (IsVoicePlaying())
            {
                Log.Debug("语音未播完，本次点击只播放 Q 弹动画");
                return;
            }
            OnPetClicked();
        }

        /// <summary>当前这句台词的语音是否还在播。</summary>
        public bool IsVoicePlaying()
        {
            return !string.IsNullOrEmpty(_voiceFile) && Audio.IsPlaying(_voiceFile);
        }

        /// <summary>记录「当前这句台词对应的语音文件」（内部与自检使用）。</summary>
        public void SetCurrentVoiceFile(string file)
        {
            _voiceFile = file ?? "";
        }

        /// <summary>当前气泡是否可见（自检用）。</summary>
        public bool BubbleVisible()
        {
            return _bubble != null && !_bubble.IsDisposed && _bubble.Visible;
        }

        /// <summary>当前气泡里的文字（自检用）。</summary>
        public string BubbleText()
        {
            return _bubble == null || _bubble.IsDisposed ? "" : _bubble.BubbleText;
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Right) ShowMenu();
            base.OnMouseClick(e);
        }

        void ShowMenu()
        {
            ContextMenuStrip menu = MenuBuilder.BuildPetMenu(this);
            menu.Show(this, new Point(10, 10));
        }

        // ---------------- 互动 ----------------

        public void OnPetClicked()
        {
            _lastInteract = DateTime.Now;
            if (Interacted != null) Interacted(this, EventArgs.Empty);
            try
            {
                if (_profile.interact.mode == "llm" && !string.IsNullOrEmpty(Config.Current.llm.baseUrl))
                {
                    // 正在等模型回复时不要用新气泡把「思考中」顶掉
                    if (_busy) return;
                    if (_profile.interact.showInputBox) EnsureInput().ShowUnder(this);
                    AskModel("（指挥官戳了戳你。用一句话回应，可以问他有什么事。）", true);
                }
                else
                {
                    if (_profile.interact.showInputBox && _profile.interact.mode == "llm")
                        EnsureInput().ShowUnder(this);
                    SayFromPool("click");
                }
            }
            catch (Exception ex)
            {
                Log.ErrorDialog("互动失败", ex);
            }
        }

        /// <summary>从固定台词池里抽一句并显示。</summary>
        public void SayFromPool(string section)
        {
            List<LineItem> pool = _profile.lines.Section(section);
            if (pool == null || pool.Count == 0) pool = _profile.lines.click;
            if (pool == null || pool.Count == 0)
            {
                ShowBubble("（还没有台词）\r\n请到「角色台词」里导入或添加。", 16, Theme.TextDim, false);
                return;
            }
            bool fromSystem = section == "system" && _profile.interact.useSystemContext && Config.Current.systemWatchPermission == "allowed";
            SystemSnapshot snap = fromSystem ? SystemWatch.Snapshot() : null;

            LineItem item = Utils.PickWeighted(pool, _lastLine);
            if (item == null) return;
            string text = item.text;
            if (snap != null) text = SystemWatch.FillPlaceholders(text, snap);

            ShowBubble(text, FontSizeFor(item), Theme.LineColor(item.color, Theme.TextMain), item.bold);

            // 语音
            string voice = ResolveVoiceFile(item);
            if (!string.IsNullOrEmpty(voice) && Config.Current.voiceEnabled)
            {
                _voiceFile = voice;
                Audio.Play(voice, true);
            }
            else _voiceFile = "";
            _lastLine = text;
        }

        int FontSizeFor(LineItem item)
        {
            if (item.size <= 0) return 17;
            // 台词文件里的字号（1~40）映射到 12~26 像素，避免气泡被撑爆
            int px = 12 + (int)Math.Round(item.size * 0.35);
            if (px < 12) px = 12;
            if (px > 26) px = 26;
            return px;
        }

        string ResolveVoiceFile(LineItem item)
        {
            string mode = _profile.interact.fixedVoiceMode;
            if (mode == "off") return "";
            if (mode == "always")
            {
                List<VoiceEntry> entries = _profile.voices.entries;
                if (entries.Count > 0) return Path.Combine(CharacterStore.VoicesDir(_profile.id), entries[Utils.Next(0, entries.Count)].file);
                return "";
            }
            if (!string.IsNullOrEmpty(item.voice))
            {
                if (Path.IsPathRooted(item.voice) && File.Exists(item.voice)) return item.voice;
                string p = Path.Combine(CharacterStore.VoicesDir(_profile.id), item.voice);
                if (File.Exists(p)) return p;
            }
            string f = _profile.voices.Find(item.text);
            if (!string.IsNullOrEmpty(f)) return Path.Combine(CharacterStore.VoicesDir(_profile.id), f);
            return "";
        }

        public void ShowBubble(string text, int fontSize, Color color, bool bold)
        {
            try
            {
                int seconds = _profile.interact.bubbleSeconds;
                if (seconds == 0) seconds = 0;
                Audio.Play(Audio.ResolveSfx(_profile, "bubble"));
                EnsureBubble().ShowText(_profile.DisplayName(), text, fontSize, color, bold, seconds);
            }
            catch (Exception ex)
            {
                Log.Error("显示气泡失败", ex);
            }
        }

        /// <summary>
        /// 气泡窗口长期复用：WinForms 的 Form.Close() 会销毁窗口，
        /// 之后再 ShowText 就会抛 ObjectDisposedException（曾导致「点了报错」「有声音没对话」）。
        /// 这里做一层保险：发现实例被销毁就重建。
        /// </summary>
        BubbleForm EnsureBubble()
        {
            if (_bubble == null || _bubble.IsDisposed)
            {
                _bubble = new BubbleForm();
                _bubble.Manager = this;
                _bubble.HoldOpen = delegate { return IsVoicePlaying(); };
            }
            else _bubble.Manager = this;
            return _bubble;
        }

        /// <summary>输入条同样做一层保险。</summary>
        InputBarForm EnsureInput()
        {
            if (_input == null || _input.IsDisposed)
            {
                _input = new InputBarForm();
                _input.Submit += delegate { SendFromInput(); };
            }
            return _input;
        }

        /// <summary>立绘在屏幕上的实际可见矩形（不含分层窗口的留白），气泡/输入条据此避让。</summary>
        public Rectangle SpriteRect()
        {
            if (_spriteW <= 0 || _spriteH <= 0) return new Rectangle(Left, Top, Math.Max(1, Width), Math.Max(1, Height));
            int x = Left + (Width - _spriteW) / 2;
            int y = Top + Height - _spriteH - (int)Math.Round(Height * 0.02);
            return new Rectangle(x, y, _spriteW, _spriteH);
        }

        // ---------------- 大模型对话 ----------------

        void SendFromInput()
        {
            InputBarForm bar = EnsureInput();
            string text = bar.Box.Text.Trim();
            if (text.Length == 0) return;
            bar.Box.Clear();
            _lastInteract = DateTime.Now;
            _lastUserAction = DateTime.Now;
            _history.Add(new LlmMessage("user", text));
            AskModel(text, false);
        }

        void AskModel(string userText, bool isClick)
        {
            if (_busy)
            {
                // 不打断进行中的「思考中」气泡
                Log.Debug("上一次大模型请求还没回来，忽略本次请求");
                return;
            }
            if (string.IsNullOrEmpty(Config.Current.llm.baseUrl))
            {
                ShowBubble("（还没有配置大模型接口：齿轮 → 大模型 API）", 15, Theme.TextDim, false);
                return;
            }
            _busy = true;
            EnsureBubble().ShowThinking(_profile.DisplayName(), 0);

            string hint = "";
            if (_profile.interact.useSystemContext && Config.Current.systemWatchPermission == "allowed")
                hint = SystemWatch.BuildContextHint(SystemWatch.Snapshot());
            string system = Llm.BuildSystemPrompt(_profile, hint, hint.Length > 0);

            CharacterProfile snapshotProfile = _profile;
            LlmSettings settings = Llm.Clone(Config.Current.llm);
            settings.temperature = _profile.card.temperature > 0 ? _profile.card.temperature : Config.Current.llm.temperature;
            settings.maxTokens = _profile.card.maxTokens > 0 ? _profile.card.maxTokens : Config.Current.llm.maxTokens;
            List<LlmMessage> hist = new List<LlmMessage>(_history);
            if (isClick) hist.Add(new LlmMessage("user", userText));

            ThreadPool.QueueUserWorkItem(delegate
            {
                LlmResult result;
                try
                {
                    result = Llm.Chat(settings, system, hist, isClick ? null : userText);
                }
                catch (Exception ex)
                {
                    result = new LlmResult();
                    result.ok = false;
                    result.error = "请求异常：" + ex.Message;
                    Log.Error("桌宠对话异常", ex);
                }
                try
                {
                    if (IsDisposed) return;
                    BeginInvoke((MethodInvoker)delegate { OnModelReplied(result, snapshotProfile); });
                }
                catch { }
            });
        }

        void OnModelReplied(LlmResult result, CharacterProfile snapshotProfile)
        {
            _busy = false;
            if (_displayPaused)
            {
                // 等回复的过程中桌宠被冻结/收进后台了：不要再弹气泡盖住别人正在用的程序
                Log.Debug("桌宠处于冻结/后台状态，本次大模型回复不弹气泡");
                return;
            }
            if (result.ok)
            {
                _history.Add(new LlmMessage("assistant", result.text));
                int keep = Math.Max(2, Config.Current.llm.historyTurns) * 2;
                while (_history.Count > keep) _history.RemoveAt(0);
                ShowBubble(result.text, 17, Theme.TextMain, false);
                Log.Info("大模型回复（" + result.elapsedMs + " ms）：" + Utils.FirstLine(result.text, 60));
            }
            else
            {
                ShowBubble("（说不出话来…）\r\n" + Utils.FirstLine(result.error, 120), 14, Theme.Rouge, false);
                Log.Warn("大模型回复失败：" + result.error);
            }
        }

        // ---------------- 定时器：主动对话 / 待机 ----------------

        public void StartTimers()
        {
            StopTimers();
            if (_profile.interact.proactive)
            {
                _proactiveTimer = new Timer();
                _proactiveTimer.Tick += delegate { OnProactiveTick(); };
                _proactiveTimer.Interval = NextProactiveDelay();
                _proactiveTimer.Start();
                _lastProactive = DateTime.Now;
            }
            _idleTimer = new Timer();
            _idleTimer.Interval = 30000;
            _idleTimer.Tick += delegate { OnIdleTick(); };
            _idleTimer.Start();
            // 冻结 / 后台中重建计时器的话，先别跑（等回到桌面再恢复）
            if (_displayPaused)
            {
                if (_proactiveTimer != null) _proactiveTimer.Stop();
                _idleTimer.Stop();
            }
        }

        public void StopTimers()
        {
            if (_proactiveTimer != null) { _proactiveTimer.Stop(); _proactiveTimer.Dispose(); _proactiveTimer = null; }
            if (_idleTimer != null) { _idleTimer.Stop(); _idleTimer.Dispose(); _idleTimer = null; }
        }

        int NextProactiveDelay()
        {
            int min = Math.Max(5, _profile.interact.proactiveMinSec) * 1000;
            int max = Math.Max(_profile.interact.proactiveMinSec, _profile.interact.proactiveMaxSec) * 1000;
            if (max <= min) return min;
            return Utils.Next(min, max);
        }

        void OnProactiveTick()
        {
            TriggerProactive();
        }

        /// <summary>主动对话一轮（定时器与自检共用）。</summary>
        public void TriggerProactive()
        {
            try
            {
                // 冻结 / 进后台时不主动开口（用户正在用别的程序，别打扰他）
                if (_displayPaused) { Log.Debug("桌宠处于冻结/后台状态，本次主动对话跳过"); return; }
                if (_proactiveTimer == null) return;
                _proactiveTimer.Interval = NextProactiveDelay();
                _lastProactive = DateTime.Now;

                // 上一句还没说完 / 模型还在回：这一轮跳过，等下一次
                if (IsVoicePlaying() || _busy) return;

                bool llmMode = _profile.interact.mode == "llm" && !string.IsNullOrEmpty(Config.Current.llm.baseUrl);
                bool watch = _profile.interact.useSystemContext && Config.Current.systemWatchPermission == "allowed";

                if (llmMode)
                {
                    if (watch) AskModel("（主动开口：根据上面「当前情况」里玩家刚在做什么，自然地跟他说一句话）", true);
                    else SayFromPool("proactive");
                    return;
                }
                if (watch) SayFromPool(_profile.lines.system.Count > 0 ? "system" : "proactive");
                else SayFromPool("proactive");
            }
            catch (Exception ex)
            {
                Log.Error("主动对话失败", ex);
            }
        }

        void OnIdleTick()
        {
            try
            {
                if (_displayPaused) return;
                if ((DateTime.Now - _lastInteract).TotalSeconds < Math.Max(30, _profile.interact.idleMinSec)) return;
                if (IsVoicePlaying() || _busy) return;
                BubbleForm b = _bubble;
                if (b != null && !b.IsDisposed && b.Visible) return;
                SayFromPool("idle");
                _lastInteract = DateTime.Now;
            }
            catch (Exception ex)
            {
                Log.Error("待机台词失败", ex);
            }
        }

        /// <summary>打开桌宠时的问候（需求 5）。</summary>
        public void SayGreeting()
        {
            try
            {
                // 冻结 / 后台状态（例如开机自启时正有程序全屏）就别开口了：气泡看不见，语音却会响
                if (_displayPaused) { Log.Debug("桌宠处于冻结/后台状态，本次问候跳过"); return; }
                SystemSnapshot snap = SystemWatch.Enabled ? SystemWatch.Snapshot() : null;
                if (_profile.interact.mode == "llm" && !string.IsNullOrEmpty(Config.Current.llm.baseUrl)
                    && !string.IsNullOrEmpty(_profile.card.greeting))
                {
                    ShowBubble(_profile.card.greeting, 17, Theme.TextMain, false);
                    return;
                }
                if (_profile.lines.greet.Count > 0)
                {
                    LineItem item = Utils.PickWeighted(_profile.lines.greet, "");
                    string text = snap != null ? SystemWatch.FillPlaceholders(item.text, snap) : item.text;
                    ShowBubble(text, FontSizeFor(item), Theme.LineColor(item.color, Theme.TextMain), item.bold);
                    string voice = ResolveVoiceFile(item);
                    if (!string.IsNullOrEmpty(voice) && Config.Current.voiceEnabled)
                    {
                        _voiceFile = voice;
                        Audio.Play(voice, true);
                    }
                    else _voiceFile = "";
                    return;
                }
                SayFromPool("click");
            }
            catch (Exception ex)
            {
                Log.Error("问候失败", ex);
            }
        }

        public void InteractNow()
        {
            _lastUserAction = DateTime.Now;   // 用户主动找桌宠 → 冻结/后台状态立刻解除
            PlayBounce();
            if (IsVoicePlaying()) return;
            OnPetClicked();
        }

        public CharacterProfile Profile { get { return _profile; } }

        public int PetCenterX() { return Left + Width / 2; }
        public int PetTop() { return Top + (int)Math.Round(Height * 0.02); }

        public void ApplyProfile(CharacterProfile p)
        {
            _profile = p;
            LoadSprite();
            StartTimers();
        }

        public void CloseAll()
        {
            try
            {
                StopTimers();
                if (_bubble != null && !_bubble.IsDisposed) { _bubble.HideBubble(); _bubble.Dispose(); }
                if (_input != null && !_input.IsDisposed) { _input.Hide(); _input.Dispose(); }
                _bubble = null;
                _input = null;
            }
            catch { }
        }

        /// <summary>收起气泡与输入条（不销毁实例，便于下次复用）。</summary>
        public void HidePopups()
        {
            try
            {
                if (_bubble != null && !_bubble.IsDisposed) _bubble.HideBubble();
                if (_input != null && !_input.IsDisposed) _input.Hide();
            }
            catch { }
        }

        // ---------------- 显示方式（置顶 / 冻结 / 后台） ----------------

        /// <summary>当前显示状态。</summary>
        public PetDisplayState DisplayState { get { return _displayState; } }

        /// <summary>是不是处于「冻结 / 进后台」状态（动画与主动搭话都停了）。</summary>
        public bool DisplayPaused { get { return _displayPaused; } }

        /// <summary>用户刚刚碰过桌宠（点击 / 拖拽 / 发消息）→ 宽限期内不做冻结或隐藏。</summary>
        public bool UserActedRecently(DateTime now)
        {
            if (_lastUserAction == DateTime.MinValue) return false;
            return (now - _lastUserAction).TotalSeconds < UserGraceSeconds;
        }

        /// <summary>桌宠自己那几个窗口（本体 / 气泡 / 输入条），用于前台窗口判定。</summary>
        public IntPtr[] OwnWindowHandles()
        {
            List<IntPtr> list = new List<IntPtr>();
            try
            {
                if (IsHandleCreated && Handle != IntPtr.Zero) list.Add(Handle);
                if (_bubble != null && !_bubble.IsDisposed && _bubble.IsHandleCreated) list.Add(_bubble.Handle);
                if (_input != null && !_input.IsDisposed && _input.IsHandleCreated) list.Add(_input.Handle);
            }
            catch (Exception ex) { Log.Debug("取桌宠窗口句柄失败：" + ex.Message); }
            return list.ToArray();
        }

        /// <summary>前台的全屏窗口是不是和这只桌宠在同一块屏幕上。</summary>
        public bool OnSameScreen(ForegroundInfo fg)
        {
            try
            {
                Screen s = Screen.FromPoint(new Point(Left + Width / 2, Top + Height / 2));
                Rectangle b = s.Bounds;
                return DisplayWatch.SameScreen(fg, s.DeviceName, new WinRect(b.Left, b.Top, b.Right, b.Bottom));
            }
            catch { return false; }
        }

        /// <summary>
        /// 应用显示状态（由 PetManager 的显示监听调用；幂等，状态没变就什么都不做）。
        ///   Normal —— 正常显示，按设置回到最上层
        ///   Sunk   —— 冻结：沉到窗口最底层 + 停动画 + 停主动搭话（窗口还在桌面上）
        ///   Hidden —— 进后台：隐藏窗口（回到桌面再显示）
        /// </summary>
        public void ApplyDisplayState(PetDisplayState state, string reason)
        {
            if (_displayState == state) return;
            PetDisplayState old = _displayState;
            _displayState = state;
            bool hasHandle = false;
            try { hasHandle = IsHandleCreated && Handle != IntPtr.Zero; } catch { }
            try
            {
                if (state == PetDisplayState.Normal)
                {
                    SetDisplayPaused(false);
                    if (hasHandle) DisplayWatch.SetTopMost(Handle, true);
                    if (!Visible) Show();
                    RenderFrame(1.0, 0);
                }
                else
                {
                    SetDisplayPaused(true);
                    if (Config.Current.hidePopupsWhenInactive) HidePopups();
                    if (state == PetDisplayState.Hidden)
                    {
                        if (hasHandle) DisplayWatch.SetTopMost(Handle, true);   // 回来时直接在最上面
                        if (Visible) Hide();
                    }
                    else
                    {
                        if (hasHandle)
                        {
                            DisplayWatch.SetTopMost(Handle, false);
                            DisplayWatch.PushToBottom(Handle);
                        }
                    }
                }
                // 进入冻结 / 后台记 Info（用户问「桌宠怎么不见了」时能在日志里看到原因），
                // 回到正常显示记 Debug（切窗口太频繁，不刷日志）。
                string message = "桌宠显示状态：" + DescribeState(old) + " → " + DescribeState(state)
                    + "（" + reason + "）";
                if (state == PetDisplayState.Normal) Log.Debug(message);
                else Log.Info(message);
            }
            catch (Exception ex)
            {
                Log.Warn("应用桌宠显示状态失败：" + ex.Message);
            }
        }

        /// <summary>冻结 / 进后台时暂停动画与自动搭话；恢复时把暂停的时间从计时里扣掉。</summary>
        void SetDisplayPaused(bool paused)
        {
            if (_displayPaused == paused) return;
            _displayPaused = paused;
            try
            {
                if (paused)
                {
                    _pausedSince = DateTime.Now;
                    if (_anim != null) _anim.Stop();
                    RenderFrame(1.0, 0);
                    if (_proactiveTimer != null) _proactiveTimer.Stop();
                    if (_idleTimer != null) _idleTimer.Stop();
                    if (!string.IsNullOrEmpty(_voiceFile)) Audio.StopFile(_voiceFile);
                    if (_bubble != null && !_bubble.IsDisposed) _bubble.HideBubble();
                    Log.Debug("桌宠已冻结：暂停动画 / 主动对话 / 待机台词");
                }
                else
                {
                    if (_pausedSince != DateTime.MinValue)
                    {
                        TimeSpan span = DateTime.Now - _pausedSince;
                        if (span > TimeSpan.Zero)
                        {
                            _lastInteract = _lastInteract.Add(span);
                            _lastProactive = _lastProactive.Add(span);
                        }
                        _pausedSince = DateTime.MinValue;
                    }
                    if (_proactiveTimer != null)
                    {
                        _proactiveTimer.Interval = NextProactiveDelay();
                        _proactiveTimer.Start();
                    }
                    if (_idleTimer != null) _idleTimer.Start();
                    Log.Debug("桌宠已恢复：动画与自动搭话重新开始");
                }
            }
            catch (Exception ex)
            {
                Log.Debug("切换桌宠冻结状态异常：" + ex.Message);
            }
        }

        public static string DescribeState(PetDisplayState state)
        {
            if (state == PetDisplayState.Sunk) return "冻结（沉到最底层）";
            if (state == PetDisplayState.Hidden) return "后台运行（已隐藏）";
            return "正常显示";
        }

        /// <summary>把当前角色的一句台词换成「本句还没说完」的提示（不便打断语音时使用）。</summary>
        public void NoteVoiceBusy()
        {
            Log.Debug("语音未播完，暂不切换台词");
        }


        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            SavePosition();
            base.OnFormClosing(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try { StopTimers(); } catch { }
                if (_sprite != null) { _sprite.Dispose(); _sprite = null; }
                if (_canvasA != null) { _canvasA.Dispose(); _canvasA = null; }
                if (_canvasB != null) { _canvasB.Dispose(); _canvasB = null; }
                if (_bubble != null && !_bubble.IsDisposed) _bubble.Dispose();
                if (_input != null && !_input.IsDisposed) _input.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    // ========================================================================
    // 右键菜单
    // ========================================================================
    public static class MenuBuilder
    {
        public class DarkRenderer : ToolStripProfessionalRenderer
        {
            public DarkRenderer() : base(new DarkColors()) { }
            protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
            {
                e.TextColor = e.Item.Selected ? Theme.GoldBright : Theme.TextMain;
                base.OnRenderItemText(e);
            }
        }

        public class DarkColors : ProfessionalColorTable
        {
            public override Color MenuItemSelected { get { return Color.FromArgb(52, 88, 146); } }
            public override Color MenuItemBorder { get { return Theme.GoldDim; } }
            public override Color MenuBorder { get { return Theme.GoldDim; } }
            public override Color ToolStripDropDownBackground { get { return Color.FromArgb(18, 34, 62); } }
            public override Color ImageMarginGradientBegin { get { return Color.FromArgb(18, 34, 62); } }
            public override Color ImageMarginGradientMiddle { get { return Color.FromArgb(18, 34, 62); } }
            public override Color ImageMarginGradientEnd { get { return Color.FromArgb(18, 34, 62); } }
            public override Color MenuItemSelectedGradientBegin { get { return Color.FromArgb(52, 88, 146); } }
            public override Color MenuItemSelectedGradientEnd { get { return Color.FromArgb(52, 88, 146); } }
            public override Color SeparatorDark { get { return Color.FromArgb(70, 104, 160); } }
            public override Color SeparatorLight { get { return Color.FromArgb(24, 44, 80); } }
        }

        static ContextMenuStrip _active;

        public static ContextMenuStrip BuildPetMenu(PetForm pet)
        {
            if (_active != null) { try { _active.Dispose(); } catch { } }
            ContextMenuStrip menu = new ContextMenuStrip();
            _active = menu;
            menu.Renderer = new DarkRenderer();
            menu.BackColor = Color.FromArgb(18, 34, 62);
            menu.ForeColor = Theme.TextMain;
            menu.Font = Theme.Body(13f);
            menu.ShowImageMargin = false;

            AddItem(menu, "互动一下", delegate { pet.InteractNow(); });
            AddItem(menu, "打开设置界面", delegate { PetManager.ShowMainUi(); });
            AddItem(menu, "关闭桌宠并打开设置界面", delegate
            {
                // 只关掉桌面上的桌宠，回到设置界面继续改（不会退出程序）
                try { pet.HidePopups(); } catch { }
                PetManager.StopAllPets();
                PetManager.ShowMainUi();
                PetManager.Balloon("桌宠已关闭", "设置界面已经打开，改完再点「开启桌宠」就好。");
            });

            ToolStripMenuItem chars = new ToolStripMenuItem("切换 / 添加角色");
            chars.ForeColor = Theme.TextMain;
            List<CharacterProfile> all = CharacterStore.ListAll();
            foreach (CharacterProfile p in all)
            {
                CharacterProfile captured = p;
                ToolStripMenuItem item = new ToolStripMenuItem();
                bool showing = PetManager.IsShowing(p.id);
                item.Text = (showing ? "● " : "○ ") + p.DisplayName() + (showing ? "（桌面上）" : "");
                item.Click += delegate
                {
                    if (PetManager.IsShowing(captured.id))
                    {
                        if (PetManager.CountOfPets() > 1) PetManager.StopPets(captured.id);
                        else pet.ShowBubble("（只剩我一个了，别关我…）", 15, Theme.TextDim, false);
                    }
                    else
                    {
                        string err;
                        if (!PetManager.Start(captured, out err)) pet.ShowBubble("启动失败：" + err, 14, Theme.Rouge, false);
                    }
                };
                chars.DropDownItems.Add(item);
            }
            menu.Items.Add(chars);

            // ---- 桌宠显示方式：右键就能换，不用进设置界面 ----
            ToolStripMenuItem display = new ToolStripMenuItem("桌宠显示");
            display.ForeColor = Theme.TextMain;
            string current = PetDisplayMode.Normalize(Config.Current.petDisplayMode);
            string[] ids = PetDisplayMode.Ids();
            string[] titles = PetDisplayMode.Titles();
            for (int i = 0; i < ids.Length; i++)
            {
                string captured = ids[i];
                ToolStripMenuItem item = new ToolStripMenuItem();
                item.Text = (current == captured ? "● " : "○ ") + titles[i];
                item.Click += delegate
                {
                    Config.Current.petDisplayMode = captured;
                    Config.Save();
                    PetManager.ApplyDisplaySettings();
                    pet.ShowBubble("桌宠显示方式已改为：" + PetDisplayMode.Title(captured), 14, Theme.TextDim, false);
                };
                display.DropDownItems.Add(item);
            }
            display.DropDownItems.Add(new ToolStripSeparator());
            ToolStripMenuItem fullscreenItem = new ToolStripMenuItem();
            fullscreenItem.Text = (Config.Current.hideOnFullscreen ? "● " : "○ ") + "有程序全屏时自动隐藏（进后台）";
            fullscreenItem.Click += delegate
            {
                Config.Current.hideOnFullscreen = !Config.Current.hideOnFullscreen;
                Config.Save();
                PetManager.ApplyDisplaySettings();
                pet.ShowBubble(Config.Current.hideOnFullscreen
                    ? "有程序全屏时会把桌宠收起来，回到桌面自动回来。"
                    : "已关闭全屏自动隐藏。", 14, Theme.TextDim, false);
            };
            display.DropDownItems.Add(fullscreenItem);
            display.DropDownItems.Add(new ToolStripSeparator());
            ToolStripMenuItem displaySettings = new ToolStripMenuItem("打开显示设置…");
            displaySettings.ForeColor = Theme.TextMain;
            displaySettings.Click += delegate
            {
                PetManager.ShowMainUi();
                MainForm ui = Program.MainUi;
                if (ui != null && !ui.IsDisposed) ui.OpenSettings("display");
            };
            display.DropDownItems.Add(displaySettings);
            menu.Items.Add(display);

            AddItem(menu, "重置位置（回到右下角）", delegate
            {
                Config.Current.petPosition = "";
                Config.Save();
                Rectangle work = Screen.PrimaryScreen.WorkingArea;
                pet.Location = new Point(work.Right - pet.Width - 60, work.Bottom - pet.Height - 40);
                pet.SavePosition();
            });

            AddItem(menu, "隐藏这个角色", delegate { PetManager.StopPets(pet.CharacterId); });
            menu.Items.Add(new ToolStripSeparator());
            AddItem(menu, "退出程序（桌宠一起关掉）", delegate { Program.ShutdownApp(); });
            return menu;
        }

        public static ContextMenuStrip BuildTrayMenu()
        {
            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Renderer = new DarkRenderer();
            menu.BackColor = Color.FromArgb(18, 34, 62);
            menu.ForeColor = Theme.TextMain;
            menu.Font = Theme.Body(13f);
            menu.ShowImageMargin = false;

            AddItem(menu, "打开设置界面", delegate { PetManager.ShowMainUi(); });
            AddItem(menu, "显示 / 切换角色", delegate { PetManager.ShowActivePet(); });
            AddItem(menu, "隐藏所有桌宠", delegate { PetManager.StopAllPets(); });
            menu.Items.Add(new ToolStripSeparator());
            AddItem(menu, "打开数据目录", delegate { ErrorDialogForm.OpenFolder(AppPaths.DataDir()); });
            AddItem(menu, "打开日志", delegate
            {
                try { System.Diagnostics.Process.Start("notepad.exe", "\"" + Log.CurrentLogFile() + "\""); }
                catch { }
            });
            menu.Items.Add(new ToolStripSeparator());
            AddItem(menu, "退出", delegate { Program.ShutdownApp(); });
            return menu;
        }

        static void AddItem(ContextMenuStrip menu, string text, EventHandler handler)
        {
            ToolStripMenuItem item = new ToolStripMenuItem(text);
            item.ForeColor = Theme.TextMain;
            item.Click += handler;
            menu.Items.Add(item);
        }
    }

    // ========================================================================
    // 桌宠运行管理（支持同时显示多个角色，各自独立）
    // ========================================================================
    public static class PetManager
    {
        static readonly List<PetForm> Pets = new List<PetForm>();
        static NotifyIcon _tray;
        static bool _greeted;

        public static bool IsRunning
        {
            get
            {
                Prune();
                return Pets.Count > 0;
            }
        }

        public static int CountOfPets() { Prune(); return Pets.Count; }

        public static bool IsShowing(string characterId)
        {
            Prune();
            foreach (PetForm p in Pets) if (p.CharacterId == characterId) return true;
            return false;
        }

        /// <summary>按角色 id 取桌面上的桌宠窗口（没有就返回 null）。</summary>
        public static PetForm Find(string characterId)
        {
            Prune();
            foreach (PetForm p in Pets) if (p.CharacterId == characterId) return p;
            return null;
        }

        static void Prune()
        {
            for (int i = Pets.Count - 1; i >= 0; i--)
            {
                if (Pets[i].IsDisposed)
                {
                    try { Pets[i].CloseAll(); } catch { }
                    Pets.RemoveAt(i);
                }
            }
        }

        public static bool Start(CharacterProfile profile, out string error)
        {
            error = "";
            try
            {
                if (profile == null) { error = "没有角色。"; return false; }
                Prune();
                foreach (PetForm p in Pets)
                {
                    if (p.CharacterId == profile.id)
                    {
                        p.Show();
                        p.BringToFront();
                        EnsureTray();
                        return true;
                    }
                }

                CharacterStore.Save(profile);
                Audio.Configure(Config.Current.volume, Config.Current.sfxEnabled);

                PetForm pet = new PetForm(profile);
                Pets.Add(pet);
                pet.FormClosed += delegate { Prune(); };
                pet.Show();
                pet.StartTimers();
                // 先按显示方式摆好状态（万一启动时正有程序全屏，问候就该跳过）
                ApplyDisplaySettings();
                if (profile.interact.showOnStart || _greeted) pet.SayGreeting();
                _greeted = true;

                if (profile.interact.useSystemContext && Config.Current.systemWatchPermission == "allowed" && !SystemWatch.Enabled)
                    SystemWatch.Start(5000);

                EnsureTray();
                Log.Info("桌宠已启动：" + profile.DisplayName());
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                Log.ErrorDialog("启动桌宠失败", ex);
                return false;
            }
        }

        public static void StopPets(string characterId)
        {
            Prune();
            for (int i = Pets.Count - 1; i >= 0; i--)
            {
                if (Pets[i].CharacterId != characterId) continue;
                PetForm p = Pets[i];
                Pets.RemoveAt(i);
                try
                {
                    p.CloseAll();
                    p.Close();
                    p.Dispose();
                }
                catch (Exception ex) { Log.Warn("关闭桌宠异常：" + ex.Message); }
            }
            if (Pets.Count == 0) PetDisplayWatcher.Stop();
        }

        public static void StopAllPets()
        {
            Prune();
            foreach (PetForm p in new List<PetForm>(Pets))
            {
                try
                {
                    p.CloseAll();
                    p.Close();
                    p.Dispose();
                }
                catch { }
            }
            Pets.Clear();
            PetDisplayWatcher.Stop();
        }

        // ---------------- 显示方式（置顶 / 冻结 / 后台） ----------------

        /// <summary>桌宠自己那几个窗口句柄的合集（前台窗口判定用）。</summary>
        public static IntPtr[] SelfWindowHandles()
        {
            List<IntPtr> list = new List<IntPtr>();
            Prune();
            foreach (PetForm p in Pets)
            {
                try
                {
                    IntPtr[] own = p.OwnWindowHandles();
                    if (own != null) list.AddRange(own);
                }
                catch { }
            }
            return list.ToArray();
        }

        /// <summary>
        /// 采一次前台窗口并按设置摆好每一只桌宠的层级 / 可见性。
        /// 状态没变化时什么都不做（不产生无谓的窗口操作）。
        /// </summary>
        public static void RefreshDisplay()
        {
            Prune();
            if (Pets.Count == 0)
            {
                PetDisplayWatcher.Stop();
                return;
            }
            string mode = PetDisplayMode.Normalize(Config.Current.petDisplayMode);
            bool hideFullscreen = Config.Current.hideOnFullscreen;
            ForegroundInfo fg = DisplayWatch.Sample(SelfWindowHandles());
            DateTime now = DateTime.Now;
            foreach (PetForm pet in Pets)
            {
                try
                {
                    if (pet.IsDisposed || !pet.IsHandleCreated) continue;
                    PetDisplayState state = DisplayWatch.Decide(mode, hideFullscreen, fg, pet.OnSameScreen(fg));
                    // 用户刚碰过桌宠（点击 / 拖拽 / 发消息）→ 先别冻结也别藏，把它叫回最上层
                    if (state != PetDisplayState.Normal && pet.UserActedRecently(now))
                        state = PetDisplayState.Normal;
                    pet.ApplyDisplayState(state, fg.describe);
                }
                catch (Exception ex)
                {
                    Log.Debug("刷新桌宠显示状态失败：" + ex.Message);
                }
            }
        }

        /// <summary>
        /// 设置里改了显示方式（或开关）后调用：按新设置起停监听，并立刻重算一次。
        /// 「始终最上层 + 不隐藏全屏」这种组合完全不需要监听，直接停掉、零轮询。
        /// </summary>
        public static void ApplyDisplaySettings()
        {
            try
            {
                Prune();
                if (Pets.Count == 0)
                {
                    PetDisplayWatcher.Stop();
                    return;
                }
                bool need = PetDisplayMode.NeedsWatch(Config.Current.petDisplayMode, Config.Current.hideOnFullscreen);
                if (need)
                {
                    PetDisplayWatcher.Start();   // 已在跑就什么都不做
                    PetDisplayWatcher.Tick();
                    return;
                }
                PetDisplayWatcher.Stop();
                foreach (PetForm pet in Pets)
                {
                    try { pet.ApplyDisplayState(PetDisplayState.Normal, "始终保持在最上层"); }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                Log.Warn("应用桌宠显示设置失败：" + ex.Message);
            }
        }

        /// <summary>当前显示状态的文字描述（设置界面 / 托盘提示用）。</summary>
        public static string DescribeDisplaySettings()
        {
            string mode = PetDisplayMode.Normalize(Config.Current.petDisplayMode);
            string text = PetDisplayMode.Title(mode);
            if (mode != PetDisplayMode.FullscreenHide && Config.Current.hideOnFullscreen)
                text += "；有程序全屏时自动隐藏";
            return text;
        }

        public static void StopIfShowing(string characterId)
        {
            if (IsShowing(characterId)) StopPets(characterId);
        }

        public static void NotifyProfileUpdated(CharacterProfile p)
        {
            if (p == null) return;
            Prune();
            foreach (PetForm pet in Pets)
            {
                if (pet.CharacterId == p.id)
                {
                    try { pet.ApplyProfile(p); } catch (Exception ex) { Log.Warn("刷新桌宠失败：" + ex.Message); }
                }
            }
        }

        public static void NotifySettingsUpdated()
        {
            Audio.Configure(Config.Current.volume, Config.Current.sfxEnabled);
            bool allow = Config.Current.systemWatchPermission == "allowed";
            if (allow)
            {
                bool wanted = false;
                Prune();
                foreach (PetForm p in Pets) if (p.Profile.interact.useSystemContext) wanted = true;
                if (wanted && !SystemWatch.Enabled) SystemWatch.Start(5000);
                if (!wanted && SystemWatch.Enabled) SystemWatch.Stop();
            }
            else if (SystemWatch.Enabled) SystemWatch.Stop();
        }

        public static void ShowActivePet()
        {
            string id = Config.Current.activeCharacter;
            if (string.IsNullOrEmpty(id))
            {
                List<CharacterProfile> all = CharacterStore.ListAll();
                if (all.Count == 0) { ShowMainUi(); return; }
                id = all[0].id;
            }
            CharacterProfile p = CharacterStore.Load(id);
            if (p == null) { ShowMainUi(); return; }
            string err;
            if (!Start(p, out err)) ShowMainUi();
        }

        public static void ShowMainUi()
        {
            try
            {
                MainForm f = Program.MainUi;
                if (f == null || f.IsDisposed)
                {
                    f = new MainForm();
                    Program.MainUi = f;
                }
                f.Show();
                f.WindowState = FormWindowState.Normal;
                f.Activate();
                f.RefreshList();
            }
            catch (Exception ex)
            {
                Log.ErrorDialog("打开主界面失败", ex);
            }
        }

        public static void EnsureTray()
        {
            try
            {
                if (!Config.Current.showTray) return;
                if (_tray != null) return;
                _tray = new NotifyIcon();
                try { _tray.Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
                catch { _tray.Icon = SystemIcons.Application; }
                _tray.Text = AppPaths.ProductName + " · v" + AppPaths.Version;
                _tray.Visible = true;
                _tray.ContextMenuStrip = MenuBuilder.BuildTrayMenu();
                _tray.DoubleClick += delegate { ShowMainUi(); };
            }
            catch (Exception ex)
            {
                Log.Warn("创建托盘图标失败：" + ex.Message);
            }
        }

        public static void Balloon(string title, string text)
        {
            try
            {
                if (_tray == null) return;
                _tray.BalloonTipTitle = title;
                _tray.BalloonTipText = text;
                _tray.ShowBalloonTip(3000);
            }
            catch { }
        }

        public static void DisposeTray()
        {
            try
            {
                if (_tray != null)
                {
                    _tray.Visible = false;
                    _tray.Dispose();
                    _tray = null;
                }
            }
            catch { }
        }
    }
}
