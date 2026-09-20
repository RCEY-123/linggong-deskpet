// ============================================================================
// ImageFx.cs —— 图片加载 / 缩放 / 抠图 / 帧预渲染缓存（性能与内存管理）
// ============================================================================
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

namespace AlDeskPet
{
    public static class ImageFx
    {
        /// <summary>从文件加载图片，不锁定文件（读进内存流后立即释放句柄）。</summary>
        public static Bitmap LoadUnlocked(string path)
        {
            byte[] bytes = File.ReadAllBytes(path);
            using (MemoryStream ms = new MemoryStream(bytes))
            {
                using (Image img = Image.FromStream(ms, true, true))
                {
                    Bitmap bmp = new Bitmap(img.Width, img.Height, PixelFormat.Format32bppArgb);
                    using (Graphics g = Graphics.FromImage(bmp))
                    {
                        g.CompositingMode = CompositingMode.SourceCopy;
                        g.InterpolationMode = InterpolationMode.NearestNeighbor;
                        g.DrawImage(img, 0, 0, img.Width, img.Height);
                    }
                    return bmp;
                }
            }
        }

        public static Bitmap ScaleToHeight(Bitmap src, int targetHeight)
        {
            if (src == null || targetHeight <= 0 || src.Height == targetHeight) return src;
            double scale = (double)targetHeight / src.Height;
            int w = Math.Max(1, (int)Math.Round(src.Width * scale));
            Bitmap dst = new Bitmap(w, targetHeight, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(dst))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.SmoothingMode = SmoothingMode.HighQuality;
                g.CompositingMode = CompositingMode.SourceCopy;
                g.DrawImage(src, new Rectangle(0, 0, w, targetHeight));
            }
            return dst;
        }

        public static Bitmap ScaleToWidth(Bitmap src, int targetWidth)
        {
            if (src == null || targetWidth <= 0 || src.Width == targetWidth) return src;
            double scale = (double)targetWidth / src.Width;
            int h = Math.Max(1, (int)Math.Round(src.Height * scale));
            Bitmap dst = new Bitmap(targetWidth, h, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(dst))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.CompositingMode = CompositingMode.SourceCopy;
                g.DrawImage(src, new Rectangle(0, 0, targetWidth, h));
            }
            return dst;
        }

        /// <summary>图片是否已经有透明像素（用于判断要不要抠图）。</summary>
        public static bool HasTransparency(Bitmap bmp)
        {
            if (bmp == null) return false;
            try
            {
                Rectangle rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
                BitmapData data = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                try
                {
                    int stride = data.Stride;
                    byte[] row = new byte[stride];
                    int transparent = 0;
                    for (int y = 0; y < bmp.Height; y += 1)
                    {
                        System.Runtime.InteropServices.Marshal.Copy(IntPtr.Add(data.Scan0, y * stride), row, 0, stride);
                        for (int x = 3; x < stride; x += 4)
                        {
                            if (row[x] < 250) { transparent++; if (transparent > 64) return true; }
                        }
                    }
                }
                finally { bmp.UnlockBits(data); }
                return false;
            }
            catch { return false; }
        }

        public class BgRemovalResult
        {
            public bool ok;
            public string message = "";
            public int removedPixels;
        }

        /// <summary>
        /// 一键抠图：从四边取背景色，做 8 邻域洪水填充，边缘羽化 1px。
        /// 纯色/近纯色背景的立绘可一键变成透明底桌宠。
        /// </summary>
        public static BgRemovalResult RemoveEdgeBackground(Bitmap bmp, int tolerance, bool feather)
        {
            BgRemovalResult result = new BgRemovalResult();
            if (bmp == null) { result.message = "没有图片。"; return result; }
            int w = bmp.Width, h = bmp.Height;
            Rectangle rect = new Rectangle(0, 0, w, h);
            BitmapData data = bmp.LockBits(rect, ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
            try
            {
                int stride = data.Stride;
                byte[] buf = new byte[stride * h];
                System.Runtime.InteropServices.Marshal.Copy(data.Scan0, buf, 0, buf.Length);

                // 四角 + 四边中点采样背景色
                int[][] samples = new int[][] {
                    new int[]{0,0}, new int[]{w-1,0}, new int[]{0,h-1}, new int[]{w-1,h-1},
                    new int[]{w/2,0}, new int[]{w/2,h-1}, new int[]{0,h/2}, new int[]{w-1,h/2}
                };
                long sr = 0, sg = 0, sb = 0;
                foreach (int[] s in samples)
                {
                    int off = s[1] * stride + s[0] * 4;
                    sb += buf[off]; sg += buf[off + 1]; sr += buf[off + 2];
                }
                int n = samples.Length;
                int br = (int)(sr / n), bg = (int)(sg / n), bb = (int)(sb / n);

                bool[] visited = new bool[w * h];
                Stack<int> stack = new Stack<int>(w * 2);
                for (int x = 0; x < w; x++) { stack.Push(x); stack.Push((h - 1) * w + x); }
                for (int y = 0; y < h; y++) { stack.Push(y * w); stack.Push(y * w + w - 1); }

                int limit = tolerance * tolerance * 3;
                int removed = 0;
                while (stack.Count > 0)
                {
                    int idx = stack.Pop();
                    if (idx < 0 || idx >= w * h) continue;
                    if (visited[idx]) continue;
                    visited[idx] = true;
                    int px = idx % w, py = idx / w;
                    int off = py * stride + px * 4;
                    int db = buf[off] - bb, dg = buf[off + 1] - bg, dr = buf[off + 2] - br;
                    if (dr * dr + dg * dg + db * db > limit) continue;
                    buf[off] = 0; buf[off + 1] = 0; buf[off + 2] = 0; buf[off + 3] = 0;
                    removed++;
                    stack.Push(idx - 1);
                    stack.Push(idx + 1);
                    stack.Push(idx - w);
                    stack.Push(idx + w);
                    stack.Push(idx - w - 1);
                    stack.Push(idx - w + 1);
                    stack.Push(idx + w - 1);
                    stack.Push(idx + w + 1);
                }

                if (feather && removed > 0)
                {
                    // 边缘羽化：与透明像素相邻的不透明像素 alpha 降到 55%
                    byte[] copy = (byte[])buf.Clone();
                    for (int y = 1; y < h - 1; y++)
                    {
                        for (int x = 1; x < w - 1; x++)
                        {
                            int off = y * stride + x * 4;
                            if (copy[off + 3] == 0) continue;
                            bool nearEmpty =
                                copy[off - 4 + 3] == 0 || copy[off + 4 + 3] == 0 ||
                                copy[off - stride + 3] == 0 || copy[off + stride + 3] == 0;
                            if (nearEmpty) buf[off + 3] = (byte)(buf[off + 3] * 55 / 100);
                        }
                    }
                }

                System.Runtime.InteropServices.Marshal.Copy(buf, 0, data.Scan0, buf.Length);
                result.removedPixels = removed;
                result.ok = removed > 0;
                result.message = removed > 0
                    ? ("抠图完成：去除背景像素 " + removed + " 个（背景色 RGB " + br + "," + bg + "," + bb + "）")
                    : "没有找到可去除的纯色背景（背景可能不是纯色），请换一张背景干净的立绘。";
                return result;
            }
            catch (Exception ex)
            {
                result.message = "抠图失败：" + ex.Message;
                Log.Error("抠图失败", ex);
                return result;
            }
            finally
            {
                bmp.UnlockBits(data);
            }
        }

        /// <summary>裁掉四周全透明的边（减少无谓绘制面积）。</summary>
        public static Bitmap TrimTransparent(Bitmap src)
        {
            if (src == null) return null;
            try
            {
                int w = src.Width, h = src.Height;
                Rectangle rect = new Rectangle(0, 0, w, h);
                BitmapData data = src.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                int minX = w, minY = h, maxX = -1, maxY = -1;
                try
                {
                    int stride = data.Stride;
                    byte[] row = new byte[stride];
                    for (int y = 0; y < h; y++)
                    {
                        System.Runtime.InteropServices.Marshal.Copy(IntPtr.Add(data.Scan0, y * stride), row, 0, stride);
                        for (int x = 0; x < w; x++)
                        {
                            if (row[x * 4 + 3] > 8)
                            {
                                if (x < minX) minX = x;
                                if (x > maxX) maxX = x;
                                if (y < minY) minY = y;
                                if (y > maxY) maxY = y;
                            }
                        }
                    }
                }
                finally { src.UnlockBits(data); }
                if (maxX < 0 || maxY < 0) return src;
                if (minX == 0 && minY == 0 && maxX == w - 1 && maxY == h - 1) return src;
                Rectangle crop = new Rectangle(minX, minY, maxX - minX + 1, maxY - minY + 1);
                Bitmap dst = new Bitmap(crop.Width, crop.Height, PixelFormat.Format32bppArgb);
                using (Graphics g = Graphics.FromImage(dst))
                {
                    g.CompositingMode = CompositingMode.SourceCopy;
                    g.DrawImage(src, new Rectangle(0, 0, crop.Width, crop.Height), crop, GraphicsUnit.Pixel);
                }
                return dst;
            }
            catch { return src; }
        }

        /// <summary>九宫格绘制（对话框框体拉伸不变形）。</summary>
        public static void DrawNineSlice(Graphics g, Image img, Rectangle target, int left, int top, int right, int bottom)
        {
            if (img == null) return;
            int sw = img.Width, sh = img.Height;
            left = Math.Max(0, Math.Min(left, sw / 2));
            right = Math.Max(0, Math.Min(right, sw - left - 1));
            top = Math.Max(0, Math.Min(top, sh / 2));
            bottom = Math.Max(0, Math.Min(bottom, sh - top - 1));

            int tw = target.Width, th = target.Height;
            int lw = left, rw = right, th2 = top, bh = bottom;
            int mw = Math.Max(0, tw - lw - rw);
            int mh = Math.Max(0, th - th2 - bh);
            int smw = Math.Max(1, sw - lw - rw);
            int smh = Math.Max(1, sh - th2 - bh);

            if (mw == 0 || mh == 0) { g.DrawImage(img, target); return; }

            // 角
            g.DrawImage(img, new Rectangle(target.X, target.Y, lw, th2), new Rectangle(0, 0, lw, th2), GraphicsUnit.Pixel);
            g.DrawImage(img, new Rectangle(target.Right - rw, target.Y, rw, th2), new Rectangle(sw - rw, 0, rw, th2), GraphicsUnit.Pixel);
            g.DrawImage(img, new Rectangle(target.X, target.Bottom - bh, lw, bh), new Rectangle(0, sh - bh, lw, bh), GraphicsUnit.Pixel);
            g.DrawImage(img, new Rectangle(target.Right - rw, target.Bottom - bh, rw, bh), new Rectangle(sw - rw, sh - bh, rw, bh), GraphicsUnit.Pixel);
            // 边（拉伸）
            g.DrawImage(img, new Rectangle(target.X + lw, target.Y, mw, th2), new Rectangle(lw, 0, smw, th2), GraphicsUnit.Pixel);
            g.DrawImage(img, new Rectangle(target.X + lw, target.Bottom - bh, mw, bh), new Rectangle(lw, sh - bh, smw, bh), GraphicsUnit.Pixel);
            g.DrawImage(img, new Rectangle(target.X, target.Y + th2, lw, mh), new Rectangle(0, th2, lw, smh), GraphicsUnit.Pixel);
            g.DrawImage(img, new Rectangle(target.Right - rw, target.Y + th2, rw, mh), new Rectangle(sw - rw, th2, rw, smh), GraphicsUnit.Pixel);
            // 中
            g.DrawImage(img, new Rectangle(target.X + lw, target.Y + th2, mw, mh), new Rectangle(lw, th2, smw, smh), GraphicsUnit.Pixel);
        }

        /// <summary>圆角遮罩（把立绘裁成圆角矩形时用）。</summary>
        public static Bitmap RoundedMask(Bitmap src, int radius)
        {
            if (src == null || radius <= 0) return src;
            Bitmap dst = new Bitmap(src.Width, src.Height, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(dst))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (GraphicsPath path = Theme.RoundedRect(new Rectangle(0, 0, src.Width, src.Height), radius))
                {
                    g.SetClip(path);
                    g.CompositingMode = CompositingMode.SourceCopy;
                    g.DrawImage(src, 0, 0, src.Width, src.Height);
                }
            }
            return dst;
        }

        /// <summary>棋盘格背景（展示透明区域用）。</summary>
        public static void DrawChecker(Graphics g, Rectangle r, int cell)
        {
            using (SolidBrush a = new SolidBrush(Color.FromArgb(26, 40, 68)))
            using (SolidBrush b = new SolidBrush(Color.FromArgb(20, 32, 56)))
            {
                for (int y = r.Y; y < r.Bottom; y += cell)
                {
                    for (int x = r.X; x < r.Right; x += cell)
                    {
                        bool odd = (((x - r.X) / cell) + ((y - r.Y) / cell)) % 2 == 1;
                        g.FillRectangle(odd ? b : a, x, y, cell, cell);
                    }
                }
            }
        }
    }

    /// <summary>
    /// 图片缓存：同一路径只解码一次（性能优化 —— 列表滚动/重绘不再反复解码大图）。
    /// 内存管理：Clear() 时统一 Dispose；只缓存缩略图与小图，不缓存整张立绘的多个副本。
    /// </summary>
    public static class ImageCache
    {
        class Entry
        {
            public Bitmap bmp;
            public DateTime stamp;
        }

        static readonly Dictionary<string, Entry> Cache = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        static readonly object Gate = new object();

        public static Bitmap Get(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
            DateTime stamp = File.GetLastWriteTimeUtc(path);
            lock (Gate)
            {
                Entry e;
                if (Cache.TryGetValue(path, out e) && e.stamp == stamp) return e.bmp;
                try
                {
                    Bitmap bmp = ImageFx.LoadUnlocked(path);
                    if (e != null && e.bmp != null) e.bmp.Dispose();
                    Entry ne = new Entry();
                    ne.bmp = bmp;
                    ne.stamp = stamp;
                    Cache[path] = ne;
                    return bmp;
                }
                catch (Exception ex)
                {
                    Log.Warn("图片解码失败：" + path + " → " + ex.Message);
                    return null;
                }
            }
        }

        /// <summary>取缩略图（限制高度，减少内存占用）。</summary>
        public static Bitmap GetThumb(string path, int height)
        {
            string key = path + "#thumb" + height;
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
            DateTime stamp = File.GetLastWriteTimeUtc(path);
            lock (Gate)
            {
                Entry e;
                if (Cache.TryGetValue(key, out e) && e.stamp == stamp) return e.bmp;
                try
                {
                    Bitmap src = Get(path);
                    if (src == null) return null;
                    Bitmap thumb = ImageFx.ScaleToHeight(src, height);
                    if (e != null && e.bmp != null) e.bmp.Dispose();
                    Entry ne = new Entry();
                    ne.bmp = thumb;
                    ne.stamp = stamp;
                    Cache[key] = ne;
                    return thumb;
                }
                catch (Exception ex)
                {
                    Log.Warn("生成缩略图失败：" + path + " → " + ex.Message);
                    return null;
                }
            }
        }

        public static void Clear()
        {
            lock (Gate)
            {
                foreach (KeyValuePair<string, Entry> kv in Cache)
                {
                    try { if (kv.Value.bmp != null) kv.Value.bmp.Dispose(); }
                    catch { }
                }
                Cache.Clear();
            }
        }

        public static void Invalidate(string path)
        {
            lock (Gate)
            {
                List<string> keys = new List<string>();
                foreach (KeyValuePair<string, Entry> kv in Cache)
                    if (kv.Key.StartsWith(path, StringComparison.OrdinalIgnoreCase)) keys.Add(kv.Key);
                foreach (string k in keys)
                {
                    try { if (Cache[k].bmp != null) Cache[k].bmp.Dispose(); } catch { }
                    Cache.Remove(k);
                }
            }
        }
    }
}
