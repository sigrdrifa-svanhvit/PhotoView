using System.IO;
using PhotoView.Models;
using Windows.Data.Pdf;
using Windows.Storage;
using Windows.Storage.Streams;

namespace PhotoView.Services;

/// <summary>
/// PDF を Windows 標準の Windows.Data.Pdf（WinRT）でラスタライズして閲覧するためのサポート。
/// 追加のネイティブ DLL は不要です（Windows 10/11 の PDF コンポーネントを使用）。
/// </summary>
public static class PdfSource
{
    /// <summary>PDF を開き、1ページ1アイテムのコレクションを返します。</summary>
    public static MediaCollection Open(string path, out int startIndex)
    {
        int pages;
        try
        {
            var file = StorageFile.GetFileFromPathAsync(path).AsTask().GetAwaiter().GetResult();
            var doc = PdfDocument.LoadFromFileAsync(file).AsTask().GetAwaiter().GetResult();
            pages = (int)doc.PageCount;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("この PDF を開けませんでした。" + ex.Message, ex);
        }

        if (pages <= 0)
            throw new InvalidOperationException("この PDF にページがありません。");

        startIndex = 0;
        var items = new List<MediaItem>(pages);
        for (int i = 0; i < pages; i++)
            items.Add(new PdfPageItem(path, i, $"ページ {i + 1} / {pages}"));

        return MediaCollection.FromItems(Path.GetFileName(path), items);
    }
}

/// <summary>PDF の1ページ。開くたびに該当ページをラスタライズして画像ストリームを返します。</summary>
public sealed class PdfPageItem : MediaItem
{
    // 表示/サムネイルごとにデコードされるため、ページ全体のラスタライズ解像度はこれに従います。
    private const double TargetDpi = 180.0;
    private const uint MaxEdge = 4096;

    private readonly string _path;
    private readonly int _pageIndex;

    public PdfPageItem(string path, int pageIndex, string name) : base(name)
    {
        _path = path;
        _pageIndex = pageIndex;
    }

    public override Stream Open()
    {
        try
        {
            var file = StorageFile.GetFileFromPathAsync(_path).AsTask().GetAwaiter().GetResult();
            var doc = PdfDocument.LoadFromFileAsync(file).AsTask().GetAwaiter().GetResult();
            var page = doc.GetPage((uint)_pageIndex);
            if (page == null) return Stream.Null;

            var opt = new PdfPageRenderOptions
            {
                // ページの DIP サイズから TargetDpi 相当の出力幅を計算（上限つき）
                DestinationWidth = (uint)Math.Clamp(
                    (int)Math.Round(page.Size.Width * TargetDpi / 96.0),
                    16, MaxEdge),
            };

            using var ras = new InMemoryRandomAccessStream();
            page.RenderToStreamAsync(ras, opt).AsTask().GetAwaiter().GetResult();
            ras.Seek(0);

            var ms = new MemoryStream();
            using (var read = ras.AsStreamForRead())
                read.CopyTo(ms);
            ms.Position = 0;
            return ms;
        }
        catch
        {
            return Stream.Null;
        }
    }
}
