using System;
using Tokenfall.Art;
using UnityEngine;

namespace Conveer
{
    /// <summary>Кадр видео: RGB-байты, смешивание по альфе, спрайты, пиксельный текст 3×5 игры.</summary>
    public sealed class Frame
    {
        public readonly int W, H;
        public readonly byte[] Rgb;

        public Frame(int w, int h)
        {
            W = w;
            H = h;
            Rgb = new byte[w * h * 3];
        }

        public void CopyFrom(Frame f) => Buffer.BlockCopy(f.Rgb, 0, Rgb, 0, Rgb.Length);

        public void Clear(float r, float g, float b)
        {
            byte R = B8(r), G = B8(g), Bb = B8(b);
            for (int i = 0; i < Rgb.Length; i += 3)
            {
                Rgb[i] = R;
                Rgb[i + 1] = G;
                Rgb[i + 2] = Bb;
            }
        }

        private static byte B8(float v) => (byte)(v <= 0f ? 0 : v >= 1f ? 255 : (int)(v * 255f + 0.5f));

        public void Blend(int x, int y, float r, float g, float b, float a)
        {
            if ((uint)x >= (uint)W || (uint)y >= (uint)H || a <= 0.004f) return;
            int i = (y * W + x) * 3;
            if (a >= 0.996f)
            {
                Rgb[i] = B8(r);
                Rgb[i + 1] = B8(g);
                Rgb[i + 2] = B8(b);
                return;
            }
            Rgb[i] = (byte)(Rgb[i] + (B8(r) - Rgb[i]) * a);
            Rgb[i + 1] = (byte)(Rgb[i + 1] + (B8(g) - Rgb[i + 1]) * a);
            Rgb[i + 2] = (byte)(Rgb[i + 2] + (B8(b) - Rgb[i + 2]) * a);
        }

        public void Fill(int x0, int y0, int w, int h, float r, float g, float b, float a = 1f)
        {
            int x1 = Math.Min(W, x0 + w), y1 = Math.Min(H, y0 + h);
            for (int y = Math.Max(0, y0); y < y1; y++)
                for (int x = Math.Max(0, x0); x < x1; x++)
                    Blend(x, y, r, g, b, a);
        }

        /// <summary>Затемнение всего кадра (экран итогов, титул поверх демо).</summary>
        public void Dim(float k)
        {
            for (int i = 0; i < Rgb.Length; i++) Rgb[i] = (byte)(Rgb[i] * k);
        }

        /// <summary>
        /// Спрайт шима (как Sprite в Unity): центр в пикселях кадра, unit — пикселей на клетку,
        /// масштаб, поворот, оттенок. Без поворота — быстрый путь ближайшего соседа.
        /// </summary>
        public void Draw(Sprite s, float px, float py, float unit, float sx, float sy, float angle, Color tint, bool flipX = false)
        {
            if (s == null || s.texture == null || tint.a <= 0.004f) return;
            var t = s.texture;
            float pw = t.width / s.pixelsPerUnit * unit * sx;
            float ph = t.height / s.pixelsPerUnit * unit * sy;
            if (Math.Abs(pw) < 0.5f || Math.Abs(ph) < 0.5f) return;
            if (Math.Abs(angle) < 1e-4f && pw > 0f && ph > 0f)
            {
                float left = px - s.pivot.x * pw, top = py - (1f - s.pivot.y) * ph;
                int x0 = Math.Max(0, (int)Math.Floor(left)), x1 = Math.Min(W - 1, (int)Math.Ceiling(left + pw));
                int y0 = Math.Max(0, (int)Math.Floor(top)), y1 = Math.Min(H - 1, (int)Math.Ceiling(top + ph));
                for (int y = y0; y <= y1; y++)
                {
                    float v = (y + 0.5f - top) / ph;
                    if (v < 0f || v >= 1f) continue;
                    int row = (t.height - 1 - (int)(v * t.height)) * t.width;
                    for (int x = x0; x <= x1; x++)
                    {
                        float u = (x + 0.5f - left) / pw;
                        if (u < 0f || u >= 1f) continue;
                        if (flipX) u = 1f - u;
                        var c = t.Pixels[row + Math.Min(t.width - 1, (int)(u * t.width))];
                        if (c.a == 0) continue;
                        Blend(x, y, c.r / 255f * tint.r, c.g / 255f * tint.g, c.b / 255f * tint.b, c.a / 255f * tint.a);
                    }
                }
                return;
            }
            float cos = (float)Math.Cos(angle), sin = (float)Math.Sin(angle);
            float ext = (Math.Abs(pw) + Math.Abs(ph)) * 0.75f + 1f;
            int ax0 = Math.Max(0, (int)(px - ext)), ax1 = Math.Min(W - 1, (int)(px + ext));
            int ay0 = Math.Max(0, (int)(py - ext)), ay1 = Math.Min(H - 1, (int)(py + ext));
            for (int y = ay0; y <= ay1; y++)
                for (int x = ax0; x <= ax1; x++)
                {
                    float dx = x + 0.5f - px, dy = y + 0.5f - py;
                    float lx = dx * cos + dy * sin;
                    float ly = -dx * sin + dy * cos;
                    float u = lx / pw + s.pivot.x;
                    float vTop = ly / ph + (1f - s.pivot.y);
                    if (u < 0 || u >= 1 || vTop < 0 || vTop >= 1) continue;
                    if (flipX) u = 1f - u;
                    int tx = (int)(u * t.width), tyTop = (int)(vTop * t.height);
                    var c = t.Pixels[(t.height - 1 - tyTop) * t.width + tx];
                    if (c.a == 0) continue;
                    Blend(x, y, c.r / 255f * tint.r, c.g / 255f * tint.g, c.b / 255f * tint.b, c.a / 255f * tint.a);
                }
        }

        /// <summary>Картинка студии (кадр ролика 320×180) с целым увеличением.</summary>
        public void Blit(ArtImage img, int ox, int oy, int scale)
        {
            for (int y = 0; y < img.H; y++)
                for (int x = 0; x < img.W; x++)
                {
                    var c = img.Px[y * img.W + x];
                    for (int j = 0; j < scale; j++)
                    {
                        int yy = oy + y * scale + j;
                        if ((uint)yy >= (uint)H) continue;
                        int i = (yy * W + ox + x * scale) * 3;
                        for (int k = 0; k < scale; k++, i += 3)
                        {
                            if ((uint)(ox + x * scale + k) >= (uint)W) continue;
                            Rgb[i] = c.R;
                            Rgb[i + 1] = c.G;
                            Rgb[i + 2] = c.B;
                        }
                    }
                }
        }

        // ───────────── Текст ─────────────

        /// <summary>Ширина текста шрифтом игры 3×5 (4 пикселя на символ).</summary>
        public static int TextWidth(string text, int scale) => string.IsNullOrEmpty(text) ? 0 : text.Length * 4 * scale - scale;

        /// <summary>Пиксельный текст 3×5 (латиница, цифры, знаки) с тёмной подложкой-обводкой.</summary>
        public void Text(string text, int x, int y, int scale, Color c, bool outline = true)
        {
            if (string.IsNullOrEmpty(text)) return;
            if (outline)
                for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                        if (dx != 0 || dy != 0) Glyphs(text, x + dx * scale / 2 + dx, y + dy * scale / 2 + dy, scale, new Color(0.02f, 0.02f, 0.05f, c.a));
            Glyphs(text, x, y, scale, c);
        }

        private void Glyphs(string text, int x, int y, int scale, Color c)
        {
            for (int i = 0; i < text.Length; i++)
            {
                var rows = Kit.GlyphRows(char.ToUpperInvariant(text[i]));
                if (rows == null) continue;
                for (int j = 0; j < 5; j++)
                    for (int k = 0; k < 3; k++)
                        if (rows[j * 3 + k] == '#') Fill(x + (i * 4 + k) * scale, y + j * scale, scale, scale, c.r, c.g, c.b, c.a);
            }
        }
    }
}
