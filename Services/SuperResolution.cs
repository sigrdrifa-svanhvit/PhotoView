using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace PhotoView.Services;

/// <summary>
/// ONNX Runtime による Real-ESRGAN（4倍）超解像。モデルはアプリフォルダに同梱します。
/// 入力のサイズは4の倍数に複製パディングし、実行後に元サイズの4倍へクロップします。
/// </summary>
public static class SuperResolution
{
    public const string ModelFileName = "RealESRGAN_x4plus_anime_4B32F.onnx";
    private const int Scale = 4;

    private static readonly object _lock = new();
    private static InferenceSession? _session;
    private static string? _sessionPath;
    private static string? _inputName;
    private static string? _outputName;
    private static bool _nchw = true;

    /// <summary>モデルファイルがアプリフォルダに存在するか。</summary>
    public static bool IsAvailable
        => File.Exists(Path.Combine(AppContext.BaseDirectory, ModelFileName));

    private static void EnsureSession()
    {
        string path = Path.Combine(AppContext.BaseDirectory, ModelFileName);
        if (_session != null && _sessionPath == path) return;
        lock (_lock)
        {
            if (_session != null && _sessionPath == path) return;

            var s = new InferenceSession(path);
            _sessionPath = path;
            _session = s;
            _inputName = s.InputMetadata.Keys.First();
            _outputName = s.OutputMetadata.Keys.First();
            var dims = s.InputMetadata[_inputName].Dimensions;
            _nchw = dims.Length == 4 && dims[1] == 3;
        }
    }

    /// <summary>画像を4倍の超解像へ。元画像は不変で、新しい BitmapSource を返します。</summary>
    public static BitmapSource Enhance(BitmapSource src)
    {
        EnsureSession();
        var session = _session!;
        string inName = _inputName!;
        string outName = _outputName!;

        int W = src.PixelWidth, H = src.PixelHeight;
        int pw = ((W + Scale - 1) / Scale) * Scale;
        int ph = ((H + Scale - 1) / Scale) * Scale;

        byte[] px = ImageEditor.GetPixels(src); // BGRA
        var r = new float[ph, pw];
        var g = new float[ph, pw];
        var b = new float[ph, pw];
        for (int y = 0; y < H; y++)
        {
            for (int x = 0; x < W; x++)
            {
                int i = (y * W + x) * 4;
                r[y, x] = px[i + 2] / 255f; // B
                g[y, x] = px[i + 1] / 255f;
                b[y, x] = px[i] / 255f;     // R（BGRA→RGB）
            }
        }
        // 右・下端を複製パディング
        for (int y = 0; y < ph; y++)
        {
            int sy = y < H ? y : H - 1;
            for (int x = W; x < pw; x++) { r[y, x] = r[sy, W - 1]; g[y, x] = g[sy, W - 1]; b[y, x] = b[sy, W - 1]; }
        }
        for (int x = 0; x < pw; x++)
        {
            int sx = x < W ? x : W - 1;
            for (int y = H; y < ph; y++) { r[y, x] = r[H - 1, sx]; g[y, x] = g[H - 1, sx]; b[y, x] = b[H - 1, sx]; }
        }

        if (!_nchw)
            throw new NotSupportedException("このモデルの入力レイアウト（NHWC）には未対応です。");

        float[] data = new float[pw * ph * 3];
        int di = 0;
        for (int c = 0; c < 3; c++)
            for (int y = 0; y < ph; y++)
                for (int x = 0; x < pw; x++)
                    data[di++] = c == 0 ? r[y, x] : c == 1 ? g[y, x] : b[y, x];

        var tensor = new DenseTensor<float>(data, new[] { 1, 3, ph, pw });
        var input = NamedOnnxValue.CreateFromTensor(inName, tensor);

        using var results = session.Run(new[] { input });
        var outTensor = results.First(x => x.Name == outName).AsTensor<float>();
        var od = outTensor.Dimensions.ToArray();
        int oh = od[2], ow = od[3];

        byte[] outp = new byte[ow * oh * 4];
        for (int y = 0; y < oh; y++)
        {
            for (int x = 0; x < ow; x++)
            {
                byte rr = (byte)Math.Clamp((int)(outTensor[0, 0, y, x] * 255), 0, 255);
                byte gg = (byte)Math.Clamp((int)(outTensor[0, 1, y, x] * 255), 0, 255);
                byte bb = (byte)Math.Clamp((int)(outTensor[0, 2, y, x] * 255), 0, 255);
                int j = (y * ow + x) * 4;
                outp[j] = bb; outp[j + 1] = gg; outp[j + 2] = rr; outp[j + 3] = 255; // RGB→BGRA
            }
        }

        var bmp = BitmapSource.Create(ow, oh, 96, 96, PixelFormats.Bgra32, null, outp, ow * 4);
        bmp.Freeze();
        return ImageEditor.Crop(bmp, 0, 0, W * Scale, H * Scale);
    }

    /// <summary>同サイズ超解像：4倍へ超解像し、元のサイズへバイキュービックで縮小します（画質向上・寸法不変）。</summary>
    public static BitmapSource EnhanceSameSize(BitmapSource src)
    {
        int w = src.PixelWidth, h = src.PixelHeight;
        var big = Enhance(src);
        return ImageEditor.ResizeBicubic(big, w, h);
    }

    /// <summary>指定した矩形領域のみ同サイズ超解像し、元画像へ貼り戻します。</summary>
    public static BitmapSource EnhanceSameSizeRegion(BitmapSource src, System.Windows.Int32Rect rect)
    {
        int iw = src.PixelWidth, ih = src.PixelHeight;
        int rx = Math.Clamp(rect.X, 0, iw - 1);
        int ry = Math.Clamp(rect.Y, 0, ih - 1);
        int rw = Math.Clamp(rect.Width, 1, iw - rx);
        int rh = Math.Clamp(rect.Height, 1, ih - ry);

        var region = ImageEditor.Crop(src, rx, ry, rw, rh);
        var enhanced = EnhanceSameSize(region);

        byte[] outPx = ImageEditor.GetPixels(src);
        byte[] enhPx = ImageEditor.GetPixels(enhanced);
        for (int y = 0; y < rh; y++)
        {
            for (int x = 0; x < rw; x++)
            {
                int si = ((ry + y) * iw + (rx + x)) * 4;
                int ei = (y * rw + x) * 4;
                outPx[si] = enhPx[ei];
                outPx[si + 1] = enhPx[ei + 1];
                outPx[si + 2] = enhPx[ei + 2];
                outPx[si + 3] = enhPx[ei + 3];
            }
        }
        return ImageEditor.FromPixels(iw, ih, outPx);
    }
}
