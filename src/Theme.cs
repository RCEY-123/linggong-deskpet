// ============================================================================
// Theme.cs —— 主题配色（深蓝 + 柔金 · 圆润简约）/ 字体 / 绘制工具
// ============================================================================
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;

namespace AlDeskPet
{
    public static class Theme
    {
        // ---------------- 圆角规范（v1.0.9 界面改版：一律圆角，不再用斜切角） ----------------
        public const int RadiusControl = 10;   // 按钮 / 复选框 / 输入框 / 下拉
        public const int RadiusPanel   = 14;   // 卡片面板 / 分组框
        public const int RadiusDialog  = 16;   // 气泡 / 输入条 / 弹窗
        public const int RadiusCard    = 14;   // 桌宠卡片式立绘外框
        /// <summary>胶囊圆角：按高度/2 算，页签与胶囊按钮用。</summary>
        public static int PillRadius(int height) { return Math.Max(6, height / 2); }

        // ---------------- 配色（深蓝 + 柔金：降饱和、去"军舰硬朗感"，保持对比度） ----------------
        public static readonly Color Navy = Color.FromArgb(18, 24, 38);
        public static readonly Color Navy2 = Color.FromArgb(30, 42, 66);
        public static readonly Color Navy3 = Color.FromArgb(44, 60, 90);
        public static readonly Color Panel = Color.FromArgb(28, 36, 54);
        public static readonly Color PanelHi = Color.FromArgb(42, 54, 78);
        public static readonly Color Gold = Color.FromArgb(226, 206, 150);
        public static readonly Color GoldDim = Color.FromArgb(146, 132, 96);
        public static readonly Color GoldBright = Color.FromArgb(244, 232, 196);
        public static readonly Color TextMain = Color.FromArgb(236, 240, 246);
        public static readonly Color TextDim = Color.FromArgb(158, 172, 194);
        public static readonly Color Rouge = Color.FromArgb(206, 118, 124);
        public static readonly Color Indigo = Color.FromArgb(132, 156, 220);
        public static readonly Color Candy = Color.FromArgb(232, 168, 198);
        public static readonly Color Cyan = Color.FromArgb(136, 208, 214);
        public static readonly Color Ok = Color.FromArgb(140, 198, 158);

        /// <summary>台词配色名 → 颜色。</summary>
        public static Color LineColor(string name, Color fallback)
        {
            if (string.IsNullOrEmpty(name)) return fallback;
            switch (name.Trim().ToLowerInvariant())
            {
                case "gold": return GoldBright;
                case "white": return TextMain;
                case "rouge": case "red": return Rouge;
                case "indigo": case "blue": return Indigo;
                case "candy": case "pink": return Candy;
                case "cyan": return Cyan;
                case "ok": case "green": return Ok;
                default:
                    try
                    {
                        if (name.StartsWith("#"))
                        {
                            string hex = name.Substring(1);
                            if (hex.Length == 6)
                                return Color.FromArgb(
                                    Convert.ToInt32(hex.Substring(0, 2), 16),
                                    Convert.ToInt32(hex.Substring(2, 2), 16),
                                    Convert.ToInt32(hex.Substring(4, 2), 16));
                        }
                    }
                    catch { }
                    return fallback;
            }
        }

        // ---------------- 字体 ----------------
        static readonly string[] BodyCandidates = new string[] { "微软雅黑", "Microsoft YaHei", "思源黑体", "SimHei", "Segoe UI" };
        static readonly string[] TitleCandidates = new string[] { "宋体", "SimSun", "NSimSun", "Microsoft YaHei" };

        static string _bodyFamily;
        static string _titleFamily;

        public static string BodyFamily()
        {
            if (_bodyFamily == null) _bodyFamily = ResolveFamily(BodyCandidates);
            return _bodyFamily;
        }

        public static string TitleFamily()
        {
            if (_titleFamily == null) _titleFamily = ResolveFamily(TitleCandidates);
            return _titleFamily;
        }

        static string ResolveFamily(string[] candidates)
        {
            try
            {
                using (InstalledFontCollection col = new InstalledFontCollection())
                {
                    List<string> installed = new List<string>();
                    foreach (FontFamily f in col.Families) installed.Add(f.Name);
                    foreach (string c in candidates)
                        foreach (string name in installed)
                            if (string.Equals(name, c, StringComparison.OrdinalIgnoreCase)) return name;
                }
            }
            catch { }
            return FontFamily.GenericSansSerif.Name;
        }

        // 字体缓存：避免每次重绘都 new Font（GDI+ 句柄泄漏与 GC 压力的常见来源）
        static readonly Dictionary<string, Font> FontCache = new Dictionary<string, Font>();

        public static Font Cached(string family, float size, FontStyle style)
        {
            string key = family + "|" + size.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + "|" + (int)style;
            Font f;
            if (FontCache.TryGetValue(key, out f)) return f;
            f = new Font(family, size, style, GraphicsUnit.Pixel);
            FontCache[key] = f;
            return f;
        }

        public static Font Body(float size)
        {
            return Cached(BodyFamily(), size, FontStyle.Regular);
        }

        public static Font BodySized(float size, bool bold)
        {
            return Cached(BodyFamily(), size, bold ? FontStyle.Bold : FontStyle.Regular);
        }

        public static Font TitleSized(float size, bool bold)
        {
            return Cached(TitleFamily(), size, bold ? FontStyle.Bold : FontStyle.Regular);
        }

        public static Font BodyBold(float size)
        {
            return Cached(BodyFamily(), size, FontStyle.Bold);
        }

        public static Font Title(float size)
        {
            return Cached(TitleFamily(), size, FontStyle.Regular);
        }

        public static Font TitleBold(float size)
        {
            return Cached(TitleFamily(), size, FontStyle.Bold);
        }

        // ---------------- 绘制助手 ----------------

        public static GraphicsPath RoundedRect(Rectangle r, int radius)
        {
            GraphicsPath path = new GraphicsPath();
            if (radius <= 0) { path.AddRectangle(r); return path; }
            int d = radius * 2;
            if (d > r.Width) d = r.Width;
            if (d > r.Height) d = r.Height;
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        /// <summary>切角矩形（保留给历史调用；新界面一律用 RoundedRect）。</summary>
        public static GraphicsPath CutCornerRect(Rectangle r, int cut)
        {
            GraphicsPath path = new GraphicsPath();
            if (cut <= 0) { path.AddRectangle(r); return path; }
            path.AddLines(new Point[]
            {
                new Point(r.X + cut, r.Y),
                new Point(r.Right, r.Y),
                new Point(r.Right, r.Bottom - cut),
                new Point(r.Right - cut, r.Bottom),
                new Point(r.X, r.Bottom),
                new Point(r.X, r.Y + cut)
            });
            path.CloseFigure();
            return path;
        }

        public static void FillGradient(Graphics g, Rectangle r, Color a, Color b, float angle)
        {
            if (r.Width <= 0 || r.Height <= 0) return;
            using (LinearGradientBrush br = new LinearGradientBrush(r, a, b, angle))
            {
                g.FillRectangle(br, r);
            }
        }

        public static void DrawGlowText(Graphics g, string text, Font font, Color color, Rectangle bounds, StringAlignment align)
        {
            using (StringFormat sf = new StringFormat())
            {
                sf.Alignment = align;
                sf.LineAlignment = StringAlignment.Center;
                sf.FormatFlags = StringFormatFlags.NoWrap;
                using (SolidBrush shadow = new SolidBrush(Color.FromArgb(120, 0, 0, 0)))
                {
                    Rectangle shadowRect = new Rectangle(bounds.X + 1, bounds.Y + 1, bounds.Width, bounds.Height);
                    g.DrawString(text, font, shadow, shadowRect, sf);
                }
                using (SolidBrush br = new SolidBrush(color))
                {
                    g.DrawString(text, font, br, bounds, sf);
                }
            }
        }

        static Bitmap _hexTile;

        /// <summary>
        /// 程序化生成背景纹理（缓存复用）。
        /// v1.0.9：原来的六边形网格换成**极淡的圆点**（柔和、不抢视线）；函数名保持不变，调用方无需改动。
        /// </summary>
        public static Bitmap HexTile()
        {
            if (_hexTile != null) return _hexTile;
            const int size = 96;
            Bitmap bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                using (SolidBrush br = new SolidBrush(Color.FromArgb(18, 190, 212, 236)))
                {
                    g.FillEllipse(br, size / 2 - 2, size / 2 - 2, 4, 4);           // 中心圆点
                    g.FillEllipse(br, 2, 2, 3, 3);                                  // 四角小圆点
                    g.FillEllipse(br, size - 5, 2, 3, 3);
                    g.FillEllipse(br, 2, size - 5, 3, 3);
                    g.FillEllipse(br, size - 5, size - 5, 3, 3);
                }
            }
            _hexTile = bmp;
            return _hexTile;
        }

        public static void FillHexBackground(Graphics g, Rectangle rect)
        {
            using (TextureBrush tb = new TextureBrush(HexTile()))
            {
                tb.WrapMode = WrapMode.Tile;
                g.FillRectangle(tb, rect);
            }
        }

        /// <summary>标准面板：圆角 + 柔和渐变底 + 低饱和描边。</summary>
        public static void DrawPanel(Graphics g, Rectangle r, bool highlight)
        {
            Color top = highlight ? PanelHi : Panel;
            Color bottom = highlight ? Color.FromArgb(34, 44, 64) : Color.FromArgb(22, 29, 44);
            using (GraphicsPath path = RoundedRect(r, RadiusPanel))
            {
                using (LinearGradientBrush br = new LinearGradientBrush(r, top, bottom, 90f))
                    g.FillPath(br, path);
                using (Pen pen = new Pen(highlight ? Gold : Color.FromArgb(66, 82, 108), highlight ? 1.4f : 1f))
                    g.DrawPath(pen, path);
            }
        }

        /// <summary>金色按钮。</summary>
        public static void DrawButton(Graphics g, Rectangle r, string text, bool hover, bool enabled, bool primary)
        {
            Color c1, c2, border, fg;
            if (!enabled)
            {
                c1 = Color.FromArgb(38, 48, 66); c2 = Color.FromArgb(26, 34, 50);
                border = Color.FromArgb(60, 70, 90); fg = Color.FromArgb(110, 122, 142);
            }
            else if (primary)
            {
                c1 = hover ? Color.FromArgb(58, 96, 158) : Color.FromArgb(40, 72, 126);
                c2 = hover ? Color.FromArgb(30, 56, 100) : Color.FromArgb(22, 44, 82);
                border = hover ? GoldBright : Gold;
                fg = hover ? GoldBright : Gold;
            }
            else
            {
                // 未选中：只有一点点蓝色高亮，绝不能和「已选中（柔金）」混淆
                c1 = hover ? Color.FromArgb(44, 60, 86) : Color.FromArgb(34, 46, 68);
                c2 = hover ? Color.FromArgb(34, 44, 64) : Color.FromArgb(27, 36, 53);
                border = hover ? Color.FromArgb(150, 172, 205) : Color.FromArgb(92, 112, 146);
                fg = TextMain;
            }
            using (GraphicsPath path = RoundedRect(r, RadiusControl))
            {
                using (LinearGradientBrush br = new LinearGradientBrush(r, c1, c2, 90f))
                    g.FillPath(br, path);
                using (Pen pen = new Pen(border, 1.2f))
                    g.DrawPath(pen, path);
            }
            DrawGlowText(g, text, BodyBold(14f), fg, r, StringAlignment.Center);
        }

        /// <summary>
        /// 气泡用的是「浅色面板」对话框，
        /// 台词配色（本来是按深色底设计的）需要转成浅底上可读的深色。
        /// </summary>
        public static Color ForLightPanel(Color c)
        {
            int lum = (c.R * 299 + c.G * 587 + c.B * 114) / 1000;
            if (lum > 150) return Color.FromArgb(23, 36, 62);                 // 近白/浅金 → 深海军蓝
            if (lum > 110) return Color.FromArgb(30, 48, 80);
            // 有彩色（rouge / indigo / candy…）：压暗但保留色相
            int r = (int)(c.R * 0.62), g = (int)(c.G * 0.62), b = (int)(c.B * 0.62);
            if (r + g + b < 120) return Color.FromArgb(24, 38, 66);
            return Color.FromArgb(r, g, b);
        }

        /// <summary>读取内置 UI 素材（对话框框体等），失败返回 null。</summary>
        public static Bitmap LoadUiAsset(string name)
        {
            try
            {
                string path = Path.Combine(AppPaths.BuiltinDir(), "ui", name);
                if (!File.Exists(path)) return null;
                return ImageFx.LoadUnlocked(path);
            }
            catch (Exception ex)
            {
                Log.Warn("读取 UI 素材失败：" + name + " → " + ex.Message);
                return null;
            }
        }
    }
}
