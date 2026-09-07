using System.IO;
using System.Windows.Media.Imaging;
using PhotoView.Models;

namespace PhotoView.Services;

/// <summary>
/// 複数フレームの TIFF を1ページずつのアイテムに展開します。
/// （ディスク上のファイルのみが対象。アーカイブ内・EPUB 内の TIFF は対象外）
/// </summary>
public static class TiffPageSplitter
{
    /// <summary>
    /// コレクション内の複数ページ TIFF を1ページずつに展開します。
    /// 展開が発生しなければ元のコレクションをそのまま返します（呼び出し側が破棄）。
    /// </summary>
    public static MediaCollection Expand(MediaCollection collection, ref int startIndex)
    {
        if (collection.Count == 0) return collection;

        var items = new List<MediaItem>(collection.Count);
        int newStart = startIndex;
        bool changed = false;

        for (int i = 0; i < collection.Count; i++)
        {
            MediaItem item = collection[i];
            if (item is FileMediaItem file && IsTiff(item.Name))
            {
                int frames = CountFrames(file);
                if (frames > 1)
                {
                    if (i == startIndex) newStart = items.Count;
                    for (int f = 0; f < frames; f++)
                        items.Add(new TiffFrameItem(file.Name, f,
                            $"{Path.GetFileName(item.Name)} ページ {f + 1}"));
                    changed = true;
                    continue;
                }
            }

            if (i == startIndex) newStart = items.Count;
            items.Add(item);
        }

        if (!changed) return collection;

        string sourceName = collection.SourceName;
        collection.Dispose();
        startIndex = newStart;
        return MediaCollection.FromItems(sourceName, items);
    }

    private static bool IsTiff(string name)
    {
        string e = Path.GetExtension(name).ToLowerInvariant();
        return e == ".tif" || e == ".tiff";
    }

    private static int CountFrames(FileMediaItem item)
    {
        try
        {
            using var fs = item.Open();
            var dec = new TiffBitmapDecoder(fs,
                BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.None);
            return dec.Frames.Count;
        }
        catch
        {
            return 1;
        }
    }
}

/// <summary>複数フレーム TIFF の1ページ。開くたびに該当フレームを BMP へ変換して返します。</summary>
public sealed class TiffFrameItem : MediaItem
{
    private readonly string _path;
    private readonly int _frame;

    public TiffFrameItem(string path, int frame, string name) : base(name)
    {
        _path = path;
        _frame = frame;
    }

    public override Stream Open()
    {
        try
        {
            using var fs = File.OpenRead(_path);
            var dec = new TiffBitmapDecoder(fs,
                BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.None);
            if (_frame >= dec.Frames.Count) return Stream.Null;

            var frame = dec.Frames[_frame];
            var outMs = new MemoryStream();
            var enc = new BmpBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(frame));
            enc.Save(outMs);
            outMs.Position = 0;
            return outMs;
        }
        catch
        {
            return Stream.Null;
        }
    }
}
