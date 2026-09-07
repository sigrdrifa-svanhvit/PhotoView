using System.IO;
using System.IO.Compression;
using SharpCompress.Archives;

namespace PhotoView.Models;

/// <summary>
/// 1つの画像を表します。フォルダ内ファイルまたは圧縮アーカイブ内のエントリのどちらかです。
/// </summary>
public abstract class MediaItem
{
    protected MediaItem(string name) => Name = name;

    /// <summary>表示用の名前（エントリパス含む）。</summary>
    public string Name { get; }

    /// <summary>画像データのストリームを開きます。呼び出し側が破棄すること。</summary>
    public abstract Stream Open();

    public override string ToString() => Name;
}

/// <summary>通常のファイルシステム上の画像ファイル。</summary>
public sealed class FileMediaItem : MediaItem
{
    private readonly string _path;

    public FileMediaItem(string path) : base(path)
        => _path = path;

    public override Stream Open()
        => new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read);
}

/// <summary>圧縮アーカイブ（zip/rar/7z等）内の画像エントリ。</summary>
public sealed class ArchiveMediaItem : MediaItem
{
    private readonly IArchiveEntry _entry;

    public ArchiveMediaItem(IArchiveEntry entry, string displayName) : base(displayName)
        => _entry = entry;

    public override Stream Open()
        => _entry.OpenEntryStream();
}

/// <summary>.NET標準のZIPとして開いた場合の画像エントリ。</summary>
public sealed class BuiltInZipItem : MediaItem
{
    private readonly ZipArchive _archive;
    private readonly string _entryName;

    public BuiltInZipItem(ZipArchive archive, string entryName) : base(entryName)
    {
        _archive = archive;
        _entryName = entryName;
    }

    public override Stream Open()
        => _archive.GetEntry(_entryName)?.Open() ?? Stream.Null;
}
