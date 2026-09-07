using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SkiaSharp;

namespace PhotoView.Services;

/// <summary>
/// ストリームをフリーズ済みの BitmapSource にデコードします。
/// WPF標準（png/jpg/bmp/gif/tiff）と SkiaSharp フォールバック（webp等）に対応。
/// </summary>
public static class ImageDecoder
{
    /// <summary>
    /// ストリームをデコードしてフリーズ済み BitmapSource を返します。失敗時は null。
    /// </summary>
    /// <param name="stream">画像データ。</param>
    /// <param name="maxWidth">0以外の場合、サムネイル用にこの幅以下に縮小してデコード。</param>
    public static BitmapSource? Decode(Stream stream, int maxWidth = 0)
    {
        // WPF の BitmapImage は StreamSource を非同期で読み込むため、
        // 呼び出し側のストリームを後で破棄すると競合が起きる。
        // 事前にバイト配列へ完全コピーし、専用の MemoryStream から読む。
        byte[] bytes;
        using (var ms = new MemoryStream())
        {
            stream.CopyTo(ms);
            bytes = ms.ToArray();
        }

        try
        {
            return DecodeWpf(bytes, maxWidth);
        }
        catch
        {
            try
            {
                return DecodeSkia(bytes, maxWidth);
            }
            catch
            {
                return null;
            }
        }
    }

    private static BitmapSource? DecodeWpf(byte[] bytes, int maxWidth)
    {
        var bi = new BitmapImage();
        bi.BeginInit();
        bi.CacheOption = BitmapCacheOption.OnLoad;
        bi.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
        if (maxWidth > 0)
            bi.DecodePixelWidth = maxWidth;
        bi.StreamSource = new MemoryStream(bytes);
        bi.EndInit();
        bi.Freeze();
        return bi;
    }

    private static BitmapSource? DecodeSkia(byte[] bytes, int maxWidth)
    {
        using var ms = new MemoryStream(bytes);
        using var codec = SKCodec.Create(ms);
        if (codec == null) return null;

        var info = codec.Info;
        var decodeInfo = new SKImageInfo(info.Width, info.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var bmp = new SKBitmap(decodeInfo);
        if (codec.GetPixels(decodeInfo, bmp.GetPixels()) != SKCodecResult.Success)
            return null;

        // サムネイル用に縮小（高品質ダウンスケール）
        SKBitmap? resized = null;
        if (maxWidth > 0 && info.Width > maxWidth)
        {
            int newH = Math.Max(1, (int)Math.Round(info.Height * (maxWidth / (double)info.Width)));
            resized = bmp.Resize(
                new SKImageInfo(maxWidth, newH, SKColorType.Bgra8888, SKAlphaType.Premul),
                new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
        }

        var src = resized ?? bmp;
        byte[] px = new byte[src.ByteCount];
        Marshal.Copy(src.GetPixels(), px, 0, px.Length);

        var bs = BitmapSource.Create(src.Width, src.Height, 96, 96, PixelFormats.Bgra32, null, px, src.RowBytes);
        bs.Freeze();

        if (resized != null) resized.Dispose();
        return bs;
    }
}
