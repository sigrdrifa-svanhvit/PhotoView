using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using SharpCompress.Archives;

namespace PhotoView.Models;

/// <summary>
/// 画像の集合。フォルダ・圧縮アーカイブ・EPUB を透過的に扱い、画像一覧を提供します。
/// </summary>
public sealed class MediaCollection : IDisposable
{
    private readonly IArchive? _archive;
    private readonly ZipArchive? _zipArchive;

    /// <summary>
    /// アーカイブのエントリ読み取り用ロック。
    /// 7z/RAR等のソリッドアーカイブは並行読み取り不可のため、アーカイブ時のみ使用。
    /// フォルダの場合は null（並行デコード可）。
    /// </summary>
    internal object? ReadLock { get; }

    private MediaCollection(string sourceName, IReadOnlyList<MediaItem> items, IArchive? archive, ZipArchive? zipArchive)
    {
        SourceName = sourceName;
        Items = items;
        _archive = archive;
        _zipArchive = zipArchive;
        ReadLock = archive != null || zipArchive != null ? new object() : null;
    }

    /// <summary>
    /// 呼び出し側で用意したアイテム列からコレクションを生成します。
    /// （PDF のページ化や複数ページ TIFF の分割などで使用）
    /// </summary>
    public static MediaCollection FromItems(string sourceName, IReadOnlyList<MediaItem> items)
        => new(sourceName, items, null, null);

    /// <summary>コレクションの出所（フォルダパスまたはアーカイブファイル名）。</summary>
    public string SourceName { get; }

    /// <summary>自然順でソート済みの画像リスト。</summary>
    public IReadOnlyList<MediaItem> Items { get; }

    public int Count => Items.Count;

    public MediaItem this[int index] => Items[index];

    public void Dispose()
    {
        _archive?.Dispose();
        _zipArchive?.Dispose();
    }

    /// <summary>
    /// パスを開きます。フォルダ・画像ファイル・圧縮アーカイブ・EPUB のいずれにも対応。
    /// （PDF は MainWindow 側で Windows.Data.Pdf により別途処理されます）
    /// </summary>
    /// <param name="path">開くパス。</param>
    /// <param name="startIndex">初期表示位置（単一画像ファイルの場合はそのインデックス）。</param>
    public static MediaCollection Open(string path, out int startIndex)
    {
        if (Directory.Exists(path))
            return OpenFolder(path, null, out startIndex);

        if (File.Exists(path))
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext == ".epub")
                return OpenEpub(path, out startIndex);

            if (IsImageExt(ext))
            {
                string? dir = Path.GetDirectoryName(path);
                if (dir != null)
                    return OpenFolder(dir, path, out startIndex);
            }
            if (IsArchiveExt(ext) || LooksLikeArchive(path))
                return OpenArchive(path, out startIndex);

            throw new InvalidOperationException("対応していないファイル形式です: " + ext);
        }

        throw new FileNotFoundException("パスが見つかりません: " + path);
    }

    public static MediaCollection OpenFolder(string folder, string? anchorFile, out int startIndex)
    {
        var files = Directory.EnumerateFiles(folder)
            .Where(f => IsImageExt(Path.GetExtension(f)))
            .OrderBy(Path.GetFileName, NaturalStringComparer.Instance)
            .ToList();

        var items = files.Select(f => (MediaItem)new FileMediaItem(f)).ToList();
        startIndex = 0;

        if (anchorFile != null)
        {
            int i = items.FindIndex(it => it.Name.Equals(anchorFile, StringComparison.OrdinalIgnoreCase));
            if (i >= 0) startIndex = i;
        }

        return new MediaCollection(folder, items, null, null);
    }

    public static MediaCollection OpenArchive(string path, out int startIndex)
    {
        // SharpCompress で開く（zip/rar/7z/tar/gz など）
        try
        {
            var archive = ArchiveFactory.Open(path);
            var entries = archive.Entries
                .Where(en => !en.IsDirectory)
                .Where(en => !en.IsEncrypted)
                .Where(en => IsImageExt(Path.GetExtension(en.Key)))
                .OrderBy(en => en.Key, NaturalStringComparer.Instance)
                .ToList();

            if (entries.Count == 0)
            {
                bool anyEncrypted = archive.Entries.Any(en => !en.IsDirectory && en.IsEncrypted);
                archive.Dispose();
                throw new InvalidOperationException(
                    anyEncrypted
                        ? "パスワード保護されたアーカイブは対応していません。"
                        : "このアーカイブ内に画像が見つかりません。");
            }

            var items = entries
                .Select(en => (MediaItem)new ArchiveMediaItem(en, en.Key ?? "image"))
                .ToList();

            startIndex = 0;
            return new MediaCollection(Path.GetFileName(path), items, archive, null);
        }
        catch (InvalidOperationException)
        {
            // 画像なし・暗号化など、意図的なエラーはそのまま伝播
            throw;
        }
        catch (Exception)
        {
            // SharpCompress で開けない場合は .NET 標準 ZIP として再試行
            try
            {
                return OpenZipFallback(path, out startIndex);
            }
            catch (InvalidOperationException)
            {
                throw;
            }
            catch (Exception)
            {
                throw new InvalidOperationException(
                    "この圧縮ファイルを開けませんでした。ファイルが破損しているか、対応していない形式です。");
            }
        }
    }

    private static MediaCollection OpenZipFallback(string path, out int startIndex)
    {
        var zip = ZipFile.OpenRead(path);
        var entries = zip.Entries
            .Where(en => !en.FullName.EndsWith("/", StringComparison.Ordinal))
            .Where(en => IsImageExt(Path.GetExtension(en.Name)))
            .OrderBy(en => en.FullName, NaturalStringComparer.Instance)
            .ToList();

        if (entries.Count == 0)
        {
            zip.Dispose();
            throw new InvalidOperationException("このアーカイブ内に画像が見つかりません。");
        }

        var items = entries
            .Select(en => (MediaItem)new BuiltInZipItem(zip, en.FullName))
            .ToList();

        startIndex = 0;
        return new MediaCollection(Path.GetFileName(path), items, null, zip);
    }

    // ---------- EPUB ----------

    /// <summary>
    /// EPUB を開きます。コンテナの OPF を読み、spine（読書順）に沿って
    /// ページ画像を並べます。各ページが XHTML の場合、最初の &lt;img&gt; を画像として扱います。
    /// </summary>
    private static MediaCollection OpenEpub(string path, out int startIndex)
    {
        try
        {
            var zip = ZipFile.OpenRead(path);
            try
            {
                var pages = BuildEpubPages(zip);
                if (pages.Count > 0)
                {
                    var items = pages
                        .Select(n => (MediaItem)new BuiltInZipItem(zip, n))
                        .ToList();
                    startIndex = 0;
                    return new MediaCollection(Path.GetFileName(path), items, null, zip);
                }
            }
            catch
            {
                // 構造解析に失敗した場合は通常の ZIP として開き直す
            }
            zip.Dispose();
            return OpenZipFallback(path, out startIndex);
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception)
        {
            throw new InvalidOperationException(
                "この EPUB を開けませんでした。ファイルが破損しているか、対応していない形式です。");
        }
    }

    /// <summary>spine 順に画像エントリ名（実在名）を返します。取得できなければ空。</summary>
    private static List<string> BuildEpubPages(ZipArchive zip)
    {
        var byName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var en in zip.Entries)
            if (!en.FullName.EndsWith("/", StringComparison.Ordinal))
                byName[en.FullName] = en.FullName;

        // container.xml → OPF の場所
        string opfPath = "";
        if (byName.TryGetValue("META-INF/container.xml", out string? containerName))
        {
            using var s = zip.GetEntry(containerName)!.Open();
            var cdoc = XDocument.Load(s);
            var rootfile = cdoc.Descendants().FirstOrDefault(x => x.Name.LocalName == "rootfile");
            opfPath = (string?)rootfile?.Attribute("full-path") ?? "";
        }
        if (opfPath.Length == 0)
            opfPath = byName.Keys.FirstOrDefault(k => k.EndsWith(".opf", StringComparison.OrdinalIgnoreCase)) ?? "";
        if (opfPath.Length == 0 || !byName.TryGetValue(opfPath, out string? opfActual))
            return new List<string>();

        // manifest / spine を読み取る
        var manifest = new Dictionary<string, (string Href, string Media)>(StringComparer.OrdinalIgnoreCase);
        var spineIds = new List<string>();
        using (var s2 = zip.GetEntry(opfActual)!.Open())
        {
            var odoc = XDocument.Load(s2);
            foreach (var it in odoc.Descendants().Where(x => x.Name.LocalName == "item"))
            {
                string id = (string?)it.Attribute("id") ?? "";
                string href = (string?)it.Attribute("href") ?? "";
                string media = (string?)it.Attribute("media-type") ?? "";
                if (id.Length > 0) manifest[id] = (href, media);
            }
            spineIds = odoc.Descendants()
                .Where(x => x.Name.LocalName == "itemref")
                .Select(x => (string?)x.Attribute("idref") ?? "")
                .Where(x => x.Length > 0)
                .ToList();
        }

        string opfDir = EntryDir(opfActual);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pages = new List<string>();

        foreach (string id in spineIds)
        {
            if (!manifest.TryGetValue(id, out var m)) continue;

            string resolved = NormalizeEntry(opfDir, m.Href);
            if (!byName.TryGetValue(resolved, out string? actual)) continue;

            bool isImage = m.Media.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
                || IsImageExt(Path.GetExtension(m.Href));
            if (isImage)
            {
                AddPage(pages, seen, actual);
            }
            else
            {
                string? imgSrc = ExtractFirstImage(zip, actual);
                if (imgSrc == null) continue;

                string imgResolved = NormalizeEntry(EntryDir(actual), imgSrc);
                if (byName.TryGetValue(imgResolved, out string? imgActual))
                    AddPage(pages, seen, imgActual);
            }
        }
        return pages;
    }

    private static void AddPage(List<string> pages, HashSet<string> seen, string name)
    {
        if (seen.Add(name)) pages.Add(name);
    }

    private static string EntryDir(string entry)
    {
        int i = entry.LastIndexOf('/');
        return i < 0 ? "" : entry.Substring(0, i);
    }

    /// <summary>相対パスを zip 内のエントリ名（/区切り）へ解決して正規化します。</summary>
    private static string NormalizeEntry(string baseDir, string rel)
    {
        string decoded = Uri.UnescapeDataString(rel).Replace('\\', '/');
        string combined = baseDir.Length == 0
            ? decoded.TrimStart('/')
            : baseDir + "/" + decoded.TrimStart('/');

        var parts = new List<string>();
        foreach (var seg in combined.Split('/'))
        {
            if (seg.Length == 0 || seg == ".") continue;
            if (seg == "..")
            {
                if (parts.Count > 0) parts.RemoveAt(parts.Count - 1);
            }
            else
            {
                parts.Add(seg);
            }
        }
        return string.Join("/", parts);
    }

    /// <summary>XHTML ページから最初の &lt;img src&gt; を抽出します。</summary>
    private static string? ExtractFirstImage(ZipArchive zip, string entryName)
    {
        var e = zip.GetEntry(entryName);
        if (e == null) return null;

        string text;
        using (var s = e.Open())
        using (var r = new StreamReader(s, Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
            text = r.ReadToEnd();

        var m = Regex.Match(text,
            "<img[^>]*?\\bsrc\\s*=\\s*[\"'](?<src>[^\"']+)[\"']",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        return m.Success ? m.Groups["src"].Value : null;
    }

    public static bool IsImageExt(string? ext)
        => !string.IsNullOrEmpty(ext) && ImageExts.Contains(ext.ToLowerInvariant());

    public static bool IsArchiveExt(string? ext)
        => !string.IsNullOrEmpty(ext) && ArchiveExts.Contains(ext.ToLowerInvariant());

    /// <summary>拡張子が未知でも、先頭バイトでアーカイブらしいかを判定。</summary>
    private static bool LooksLikeArchive(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            Span<byte> head = stackalloc byte[8];
            int n = fs.Read(head);
            if (n < 4) return false;

            // ZIP: PK\x03\x04 / PK\x05\x06 / PK\x07\x08
            if (head[0] == 0x50 && head[1] == 0x4B) return true;
            // RAR: Rar!
            if (head[0] == 0x52 && head[1] == 0x61 && head[2] == 0x72 && head[3] == 0x21) return true;
            // 7z: 7z\xBC\xAF\x27\x1C
            if (head[0] == 0x37 && head[1] == 0x7A && head[2] == 0xBC && head[3] == 0xAF) return true;
            // gzip: \x1F\x8B
            if (head[0] == 0x1F && head[1] == 0x8B) return true;
            // bzip2: BZh
            if (head[0] == 0x42 && head[1] == 0x5A && head[2] == 0x68) return true;
            // xz: \xFD7zXZ\x00
            if (head[0] == 0xFD && head[1] == 0x37 && head[2] == 0x7A && head[3] == 0x58) return true;
            // CAB: MSCF
            if (head[0] == 0x4D && head[1] == 0x53 && head[2] == 0x43 && head[3] == 0x46) return true;
            return false;
        }
        catch
        {
            return false;
        }
    }

    private static readonly HashSet<string> ImageExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".webp", ".bmp", ".gif", ".tif", ".tiff",
    };

    private static readonly HashSet<string> ArchiveExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".zip", ".rar", ".7z", ".tar", ".gz", ".bz2", ".xz", ".cab",
        ".tgz", ".tbz", ".tbz2", ".txz",
        ".cbz", ".cbr", ".cb7", ".epub",
    };
}
