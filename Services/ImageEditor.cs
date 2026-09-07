using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SkiaSharp;

namespace PhotoView.Services;

/// <summary>
/// 簡易画像編集のピクセル処理（リサイズ / トリミング / カラー変更 / モザイク / ガウシアンブラー /
/// 回転・反転 / バイキュービックリサイズ）。元ビットマップを変更せず、常に新しい BitmapSource を返します。
/// </summary>
public static class ImageEditor
{
    // ---------- 基本 ----------

    public static BitmapSource ToBgra32(BitmapSource src)
    {
        if (src.Format == PixelFormats.Bgra32) return src;
        var f = new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
        f.Freeze();
        return f;
    }

    public static byte[] GetPixels(BitmapSource src)
    {
        var f = ToBgra32(src);
        int w = f.PixelWidth, h = f.PixelHeight;
        byte[] px = new byte[w * h * 4];
        f.CopyPixels(px, w * 4, 0);
        return px;
    }

    public static BitmapSource FromPixels(int w, int h, byte[] px)
    {
        var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, w * 4);
        bmp.Freeze();
        return bmp;
    }

    // ---------- カラー ----------

    /// <summary>明るさ・コントラスト・彩度・色相を調整（明るさ/コントラスト/彩度は -100〜100、色相は -180〜180度、0 で無変更）。</summary>
    public static BitmapSource AdjustColor(BitmapSource src, double brightness, double contrast, double saturation, double hueDeg = 0)
    {
        int w = src.PixelWidth, h = src.PixelHeight;
        byte[] px = GetPixels(src);

        double bOff = brightness * 255.0 / 100.0;
        double c = 255.0 * contrast / 100.0;
        // 標準的なコントラスト式。分母が0になりにくいよう丸める
        double cFactor = 259.0 * (c + 255.0) / (255.0 * (259.0 - Math.Min(258.9, Math.Max(-258.9, c))));
        double s = 1.0 + saturation / 100.0;
        double a = hueDeg * Math.PI / 180.0;
        double cosA = Math.Cos(a), sinA = Math.Sin(a);

        if (Math.Abs(bOff) < 1e-9 && Math.Abs(cFactor - 1.0) < 1e-9 && Math.Abs(s - 1.0) < 1e-9
            && Math.Abs(hueDeg) < 1e-9)
            return src;

        for (int i = 0; i < px.Length; i += 4)
        {
            double r = px[i], g = px[i + 1], b = px[i + 2];

            // 色相回転（YIQ系の回転行列。 hue=0 では恒等）
            if (Math.Abs(hueDeg) >= 0.01)
            {
                double nr = r * (0.213 + 0.787 * cosA - 0.213 * sinA)
                           + g * (0.715 - 0.715 * cosA - 0.715 * sinA)
                           + b * (0.072 - 0.072 * cosA + 0.928 * sinA);
                double ng = r * (0.213 - 0.213 * cosA + 0.143 * sinA)
                           + g * (0.715 + 0.285 * cosA + 0.140 * sinA)
                           + b * (0.072 - 0.072 * cosA - 0.283 * sinA);
                double nb = r * (0.213 - 0.213 * cosA - 0.787 * sinA)
                           + g * (0.715 - 0.715 * cosA + 0.715 * sinA)
                           + b * (0.072 + 0.928 * cosA + 0.072 * sinA);
                r = nr; g = ng; b = nb;
            }

            double r1 = cFactor * (r - 128.0) + 128.0 + bOff;
            double g1 = cFactor * (g - 128.0) + 128.0 + bOff;
            double b1 = cFactor * (b - 128.0) + 128.0 + bOff;

            double gray = 0.299 * r1 + 0.587 * g1 + 0.114 * b1;
            double r2 = gray + s * (r1 - gray);
            double g2 = gray + s * (g1 - gray);
            double b2 = gray + s * (b1 - gray);

            px[i] = ClampByte(r2);
            px[i + 1] = ClampByte(g2);
            px[i + 2] = ClampByte(b2);
        }
        return FromPixels(w, h, px);
    }

    public static BitmapSource Grayscale(BitmapSource src)
    {
        int w = src.PixelWidth, h = src.PixelHeight;
        byte[] px = GetPixels(src);
        for (int i = 0; i < px.Length; i += 4)
        {
            byte g = (byte)Math.Round(0.299 * px[i] + 0.587 * px[i + 1] + 0.114 * px[i + 2]);
            px[i] = px[i + 1] = px[i + 2] = g;
        }
        return FromPixels(w, h, px);
    }

    public static BitmapSource Sepia(BitmapSource src)
    {
        int w = src.PixelWidth, h = src.PixelHeight;
        byte[] px = GetPixels(src);
        for (int i = 0; i < px.Length; i += 4)
        {
            double r = px[i], g = px[i + 1], b = px[i + 2];
            px[i] = ClampByte(0.393 * r + 0.769 * g + 0.189 * b);
            px[i + 1] = ClampByte(0.349 * r + 0.686 * g + 0.168 * b);
            px[i + 2] = ClampByte(0.272 * r + 0.534 * g + 0.131 * b);
        }
        return FromPixels(w, h, px);
    }

    public static BitmapSource Invert(BitmapSource src)
    {
        int w = src.PixelWidth, h = src.PixelHeight;
        byte[] px = GetPixels(src);
        for (int i = 0; i < px.Length; i += 4)
        {
            px[i] = (byte)(255 - px[i]);
            px[i + 1] = (byte)(255 - px[i + 1]);
            px[i + 2] = (byte)(255 - px[i + 2]);
        }
        return FromPixels(w, h, px);
    }

    // ---------- リサイズ / トリミング ----------

    public static BitmapSource Resize(BitmapSource src, int width, int height)
    {
        if (src.PixelWidth == width && src.PixelHeight == height) return src;
        var tb = new TransformedBitmap(src,
            new ScaleTransform(width / (double)src.PixelWidth, height / (double)src.PixelHeight));
        tb.Freeze();
        return tb;
    }

    /// <summary>高品質（ミップマップ/リニア）補間で指定サイズへリサイズします。</summary>
    public static BitmapSource ResizeBicubic(BitmapSource src, int width, int height)
    {
        if (src.PixelWidth == width && src.PixelHeight == height) return src;
        byte[] px = GetPixels(src);
        using var bmp = new SKBitmap(src.PixelWidth, src.PixelHeight, SKColorType.Bgra8888, SKAlphaType.Premul);
        Marshal.Copy(px, 0, bmp.GetPixels(), px.Length);

        using var resized = bmp.Resize(
            new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul),
            new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));

        byte[] outPx = new byte[resized.ByteCount];
        Marshal.Copy(resized.GetPixels(), outPx, 0, outPx.Length);
        return FromPixels(width, height, outPx);
    }

    // ---------- 回転 / 反転 ----------

    public static BitmapSource Rotate90Cw(BitmapSource src)
    {
        int iw = src.PixelWidth, ih = src.PixelHeight;
        byte[] s = GetPixels(src);
        byte[] o = new byte[ih * iw * 4];
        int os = iw * 4;
        for (int y = 0; y < ih; y++)
        {
            for (int x = 0; x < iw; x++)
            {
                int si = (y * iw + x) * 4;
                int oy = x, ox = ih - 1 - y;          // 時計回り
                int di = (oy * ih + ox) * 4;
                o[di] = s[si]; o[di + 1] = s[si + 1]; o[di + 2] = s[si + 2]; o[di + 3] = s[si + 3];
            }
        }
        return FromPixels(ih, iw, o);
    }

    public static BitmapSource Rotate90Ccw(BitmapSource src)
    {
        int iw = src.PixelWidth, ih = src.PixelHeight;
        byte[] s = GetPixels(src);
        byte[] o = new byte[ih * iw * 4];
        for (int y = 0; y < ih; y++)
        {
            for (int x = 0; x < iw; x++)
            {
                int si = (y * iw + x) * 4;
                int ox = y, oy = iw - 1 - x;          // 反時計回り
                int di = (oy * ih + ox) * 4;
                o[di] = s[si]; o[di + 1] = s[si + 1]; o[di + 2] = s[si + 2]; o[di + 3] = s[si + 3];
            }
        }
        return FromPixels(ih, iw, o);
    }

    public static BitmapSource FlipHorizontal(BitmapSource src)
    {
        int iw = src.PixelWidth, ih = src.PixelHeight;
        byte[] s = GetPixels(src);
        byte[] o = new byte[s.Length];
        for (int y = 0; y < ih; y++)
        {
            for (int x = 0; x < iw; x++)
            {
                int si = (y * iw + x) * 4;
                int di = (y * iw + (iw - 1 - x)) * 4;
                o[di] = s[si]; o[di + 1] = s[si + 1]; o[di + 2] = s[si + 2]; o[di + 3] = s[si + 3];
            }
        }
        return FromPixels(iw, ih, o);
    }

    public static BitmapSource FlipVertical(BitmapSource src)
    {
        int iw = src.PixelWidth, ih = src.PixelHeight;
        byte[] s = GetPixels(src);
        byte[] o = new byte[s.Length];
        for (int y = 0; y < ih; y++)
        {
            for (int x = 0; x < iw; x++)
            {
                int si = (y * iw + x) * 4;
                int di = ((ih - 1 - y) * iw + x) * 4;
                o[di] = s[si]; o[di + 1] = s[si + 1]; o[di + 2] = s[si + 2]; o[di + 3] = s[si + 3];
            }
        }
        return FromPixels(iw, ih, o);
    }

    /// <summary>任意角度で回転します。回転後の画像全体を収めるようキャンバスを拡張し、余白は透明。</summary>
    public static BitmapSource Rotate(BitmapSource src, double angleDeg)
    {
        double a = angleDeg * Math.PI / 180.0;
        int iw = src.PixelWidth, ih = src.PixelHeight;
        double cos = Math.Cos(a), sin = Math.Sin(a);

        int ow = Math.Max(1, (int)Math.Ceiling(Math.Abs(iw * cos) + Math.Abs(ih * sin)));
        int oh = Math.Max(1, (int)Math.Ceiling(Math.Abs(iw * sin) + Math.Abs(ih * cos)));

        byte[] s = GetPixels(src);
        byte[] o = new byte[ow * oh * 4]; // 透明

        double scx = iw / 2.0, scy = ih / 2.0, ocx = ow / 2.0, ocy = oh / 2.0;

        for (int oy = 0; oy < oh; oy++)
        {
            for (int ox = 0; ox < ow; ox++)
            {
                double dx = ox - ocx + 0.5;
                double dy = oy - ocy + 0.5;
                // 逆回転で元座標へ
                double lx = cos * dx + sin * dy;
                double ly = -sin * dx + cos * dy;
                double sx = scx + lx;
                double sy = scy + ly;
                if (sx < 0 || sx >= iw || sy < 0 || sy >= ih) continue;

                int si = ((int)Math.Clamp((int)Math.Floor(sy), 0, ih - 1) * iw
                          + (int)Math.Clamp((int)Math.Floor(sx), 0, iw - 1)) * 4;
                int di = (oy * ow + ox) * 4;
                o[di] = s[si]; o[di + 1] = s[si + 1]; o[di + 2] = s[si + 2]; o[di + 3] = s[si + 3];
            }
        }
        return FromPixels(ow, oh, o);
    }

    public static BitmapSource Crop(BitmapSource src, int rx, int ry, int rw, int rh)
    {
        rx = Math.Clamp(rx, 0, src.PixelWidth);
        ry = Math.Clamp(ry, 0, src.PixelHeight);
        rw = Math.Clamp(rw, 1, src.PixelWidth - rx);
        rh = Math.Clamp(rh, 1, src.PixelHeight - ry);
        if (rw <= 0 || rh <= 0) return src;

        var cb = new CroppedBitmap(src, new Int32Rect(rx, ry, rw, rh));
        cb.Freeze();
        return cb;
    }

    /// <summary>指定矩形内をブロック平均のピクセル化（モザイク）します。</summary>
    public static BitmapSource ApplyMosaic(BitmapSource src, int rx, int ry, int rw, int rh, int block)
    {
        int w = src.PixelWidth, h = src.PixelHeight;
        if (block < 1) block = 1;

        rx = Math.Clamp(rx, 0, w - 1);
        ry = Math.Clamp(ry, 0, h - 1);
        rw = Math.Clamp(rw, 1, w - rx);
        rh = Math.Clamp(rh, 1, h - ry);

        byte[] px = GetPixels(src);
        int stride = w * 4;

        for (int by = ry; by < ry + rh; by += block)
        {
            int ey = Math.Min(by + block, ry + rh);
            for (int bx = rx; bx < rx + rw; bx += block)
            {
                int ex = Math.Min(bx + block, rx + rw);
                long sr = 0, sg = 0, sb = 0;
                int cnt = 0;
                for (int y = by; y < ey; y++)
                {
                    int row = y * stride;
                    for (int x = bx; x < ex; x++)
                    {
                        int idx = row + x * 4;
                        sr += px[idx]; sg += px[idx + 1]; sb += px[idx + 2];
                        cnt++;
                    }
                }
                byte r = (byte)(sr / cnt), g = (byte)(sg / cnt), b = (byte)(sb / cnt);
                for (int y = by; y < ey; y++)
                {
                    int row = y * stride;
                    for (int x = bx; x < ex; x++)
                    {
                        int idx = row + x * 4;
                        px[idx] = r; px[idx + 1] = g; px[idx + 2] = b;
                    }
                }
            }
        }
        return FromPixels(w, h, px);
    }

    // ---------- ガウシアンブラー ----------

    /// <summary>分離可能なガウシアンブラー（sigma が強さ）を画像全体へ適用します。</summary>
    public static BitmapSource GaussianBlur(BitmapSource src, double sigma)
    {
        if (sigma < 0.5) return src;
        int w = src.PixelWidth, h = src.PixelHeight;
        byte[] srcPx = GetPixels(src);
        double[] k = BuildGaussKernel(sigma);
        int radius = (k.Length - 1) / 2;

        byte[] tmp = new byte[srcPx.Length];
        byte[] dst = new byte[srcPx.Length];

        // 横方向
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                double r = 0, g = 0, b = 0;
                for (int i = -radius; i <= radius; i++)
                {
                    int xx = Math.Clamp(x + i, 0, w - 1);
                    int idx = (y * w + xx) * 4;
                    double kk = k[i + radius];
                    r += srcPx[idx + 2] * kk; // R
                    g += srcPx[idx + 1] * kk; // G
                    b += srcPx[idx] * kk;     // B
                }
                int o = (y * w + x) * 4;
                tmp[o] = ClampByte(b); tmp[o + 1] = ClampByte(g); tmp[o + 2] = ClampByte(r); tmp[o + 3] = srcPx[o + 3];
            }
        }
        // 縦方向
        for (int x = 0; x < w; x++)
        {
            for (int y = 0; y < h; y++)
            {
                double r = 0, g = 0, b = 0;
                for (int i = -radius; i <= radius; i++)
                {
                    int yy = Math.Clamp(y + i, 0, h - 1);
                    int idx = (yy * w + x) * 4;
                    double kk = k[i + radius];
                    r += tmp[idx + 2] * kk;
                    g += tmp[idx + 1] * kk;
                    b += tmp[idx] * kk;
                }
                int o = (y * w + x) * 4;
                dst[o] = ClampByte(b); dst[o + 1] = ClampByte(g); dst[o + 2] = ClampByte(r); dst[o + 3] = tmp[o + 3];
            }
        }
        return FromPixels(w, h, dst);
    }

    /// <summary>指定した矩形領域のみガウシアンブラーを適用し、元画像へ貼り戻します。</summary>
    public static BitmapSource GaussianBlurRegion(BitmapSource src, System.Windows.Int32Rect rect, double sigma)
    {
        int iw = src.PixelWidth, ih = src.PixelHeight;
        int rx = Math.Clamp(rect.X, 0, iw - 1);
        int ry = Math.Clamp(rect.Y, 0, ih - 1);
        int rw = Math.Clamp(rect.Width, 1, iw - rx);
        int rh = Math.Clamp(rect.Height, 1, ih - ry);

        var region = Crop(src, rx, ry, rw, rh);
        var blurred = GaussianBlur(region, sigma);

        byte[] outPx = GetPixels(src);
        byte[] bPx = GetPixels(blurred);
        for (int y = 0; y < rh; y++)
        {
            for (int x = 0; x < rw; x++)
            {
                int si = ((ry + y) * iw + (rx + x)) * 4;
                int ei = (y * rw + x) * 4;
                outPx[si] = bPx[ei];
                outPx[si + 1] = bPx[ei + 1];
                outPx[si + 2] = bPx[ei + 2];
                outPx[si + 3] = bPx[ei + 3];
            }
        }
        return FromPixels(iw, ih, outPx);
    }

    private static double[] BuildGaussKernel(double sigma)
    {
        int radius = Math.Max(1, (int)Math.Ceiling(sigma * 3));
        int n = radius * 2 + 1;
        double[] k = new double[n];
        double sum = 0;
        for (int i = -radius; i <= radius; i++)
        {
            double v = Math.Exp(-(i * i) / (2.0 * sigma * sigma));
            k[i + radius] = v;
            sum += v;
        }
        for (int i = 0; i < n; i++) k[i] /= sum;
        return k;
    }

    // ---------- 回転選択（角度付きトリミング / モザイク） ----------
    /// <summary>
    /// 回転した選択四角形を抜き出します。出力は選択を含む正立バウンディング矩形で、
    /// 四角形の外側は透明（アルファ0）になります。
    /// </summary>
    public static BitmapSource CropRotated(BitmapSource src, double cx, double cy, double w, double h, double angleRad)
    {
        int iw = src.PixelWidth, ih = src.PixelHeight;
        double hw = Math.Max(1.0, w / 2.0), hh = Math.Max(1.0, h / 2.0);
        double cos = Math.Cos(angleRad), sin = Math.Sin(angleRad);

        // 四隅を回転
        var corners = new (double X, double Y)[]
        {
            (cx + ( -hw * cos - (-hh) * sin), cy + ( -hw * sin + (-hh) * cos)),
            (cx + (  hw * cos - (-hh) * sin), cy + (  hw * sin + (-hh) * cos)),
            (cx + (  hw * cos -  hh * sin),   cy + (  hw * sin +  hh * cos)),
            (cx + ( -hw * cos -  hh * sin),   cy + ( -hw * sin +  hh * cos)),
        };

        double minX = corners.Min(c => c.X), maxX = corners.Max(c => c.X);
        double minY = corners.Min(c => c.Y), maxY = corners.Max(c => c.Y);

        int b0 = Math.Max(0, (int)Math.Floor(minX));
        int b1 = Math.Min(iw, (int)Math.Ceiling(maxX));
        int t0 = Math.Max(0, (int)Math.Floor(minY));
        int t1 = Math.Min(ih, (int)Math.Ceiling(maxY));
        int ow = Math.Max(1, b1 - b0), oh = Math.Max(1, t1 - t0);

        byte[] srcPx = GetPixels(src);
        byte[] outPx = new byte[ow * oh * 4]; // 透明

        for (int oy = 0; oy < oh; oy++)
        {
            for (int ox = 0; ox < ow; ox++)
            {
                double px = b0 + ox + 0.5;
                double py = t0 + oy + 0.5;
                if (!InsideRotatedRect(px, py, cx, cy, hw, hh, angleRad)) continue;

                int sx = (int)Math.Clamp((int)Math.Floor(px), 0, iw - 1);
                int sy = (int)Math.Clamp((int)Math.Floor(py), 0, ih - 1);
                int si = (sy * iw + sx) * 4;
                int di = (oy * ow + ox) * 4;
                outPx[di] = srcPx[si];
                outPx[di + 1] = srcPx[si + 1];
                outPx[di + 2] = srcPx[si + 2];
                outPx[di + 3] = srcPx[si + 3];
            }
        }
        return FromPixels(ow, oh, outPx);
    }

    /// <summary>回転した選択四角形の内側だけにモザイクを適用します。</summary>
    public static BitmapSource ApplyMosaicRotated(BitmapSource src, double cx, double cy, double w, double h, double angleRad, int block)
    {
        int iw = src.PixelWidth, ih = src.PixelHeight;
        double hw = Math.Max(1.0, w / 2.0), hh = Math.Max(1.0, h / 2.0);
        double cos = Math.Cos(angleRad), sin = Math.Sin(angleRad);
        if (block < 1) block = 1;

        var corners = new (double X, double Y)[]
        {
            (cx + ( -hw * cos - (-hh) * sin), cy + ( -hw * sin + (-hh) * cos)),
            (cx + (  hw * cos - (-hh) * sin), cy + (  hw * sin + (-hh) * cos)),
            (cx + (  hw * cos -  hh * sin),   cy + (  hw * sin +  hh * cos)),
            (cx + ( -hw * cos -  hh * sin),   cy + ( -hw * sin +  hh * cos)),
        };
        double minX = corners.Min(c => c.X), maxX = corners.Max(c => c.X);
        double minY = corners.Min(c => c.Y), maxY = corners.Max(c => c.Y);

        int b0 = Math.Max(0, (int)Math.Floor(minX));
        int b1 = Math.Min(iw, (int)Math.Ceiling(maxX));
        int t0 = Math.Max(0, (int)Math.Floor(minY));
        int t1 = Math.Min(ih, (int)Math.Ceiling(maxY));
        if (b1 - b0 <= 0 || t1 - t0 <= 0) return src;

        byte[] px = GetPixels(src);
        int stride = iw * 4;

        for (int by = t0; by < t1; by += block)
        {
            int ey = Math.Min(by + block, t1);
            for (int bx = b0; bx < b1; bx += block)
            {
                int ex = Math.Min(bx + block, b1);
                long sr = 0, sg = 0, sb = 0;
                int cnt = 0;
                for (int y = by; y < ey; y++)
                {
                    for (int x = bx; x < ex; x++)
                    {
                        if (!InsideRotatedRect(x + 0.5, y + 0.5, cx, cy, hw, hh, angleRad)) continue;
                        int idx = y * stride + x * 4;
                        sr += px[idx]; sg += px[idx + 1]; sb += px[idx + 2];
                        cnt++;
                    }
                }
                if (cnt == 0) continue;

                byte r = (byte)(sr / cnt), g = (byte)(sg / cnt), b = (byte)(sb / cnt);
                for (int y = by; y < ey; y++)
                {
                    for (int x = bx; x < ex; x++)
                    {
                        if (!InsideRotatedRect(x + 0.5, y + 0.5, cx, cy, hw, hh, angleRad)) continue;
                        int idx = y * stride + x * 4;
                        px[idx] = r; px[idx + 1] = g; px[idx + 2] = b;
                    }
                }
            }
        }
        return FromPixels(iw, ih, px);
    }

    /// <summary>点が回転四角形（中心,半径hw/hh,角度angleRad）の内側か。</summary>
    public static bool InsideRotatedRect(double px, double py, double cx, double cy, double hw, double hh, double angleRad)
    {
        double cos = Math.Cos(angleRad), sin = Math.Sin(angleRad);
        double dx = px - cx, dy = py - cy;
        double lx = dx * cos + dy * sin;
        double ly = -dx * sin + dy * cos;
        return Math.Abs(lx) <= hw && Math.Abs(ly) <= hh;
    }

    // ---------- 保存 ----------

    /// <summary>形式拡張子（.png/.jpg など）に応じてストリームへエンコードします。</summary>
    public static void Encode(BitmapSource src, string ext, Stream stream)
    {
        switch (ext.ToLowerInvariant())
        {
            case ".png":
                SaveWith(new PngBitmapEncoder(), src, stream);
                break;
            case ".jpg":
            case ".jpeg":
                SaveWith(new JpegBitmapEncoder { QualityLevel = 90 }, src, stream);
                break;
            case ".bmp":
                SaveWith(new BmpBitmapEncoder(), src, stream);
                break;
            case ".gif":
                SaveWith(new GifBitmapEncoder(), src, stream);
                break;
            case ".tif":
            case ".tiff":
                SaveWith(new TiffBitmapEncoder(), src, stream);
                break;
            case ".webp":
                SaveWebp(src, stream);
                break;
            default:
                SaveWith(new PngBitmapEncoder(), src, stream);
                break;
        }
    }

    private static void SaveWith(BitmapEncoder encoder, BitmapSource src, Stream stream)
    {
        encoder.Frames.Add(BitmapFrame.Create(src));
        encoder.Save(stream);
    }

    private static void SaveWebp(BitmapSource src, Stream stream)
    {
        int w = src.PixelWidth, h = src.PixelHeight;
        byte[] px = GetPixels(src);
        using var bmp = new SKBitmap(w, h, SKColorType.Bgra8888, SKAlphaType.Premul);
        Marshal.Copy(px, 0, bmp.GetPixels(), px.Length);
        using var image = SKImage.FromBitmap(bmp);
        using var data = image.Encode(SKEncodedImageFormat.Webp, 90);
        byte[] outBytes = data.ToArray();
        stream.Write(outBytes, 0, outBytes.Length);
    }

    private static byte ClampByte(double v) => (byte)Math.Clamp(v, 0, 255);
}
