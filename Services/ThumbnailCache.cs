using System.Windows.Media.Imaging;
using PhotoView.Models;

namespace PhotoView.Services;

/// <summary>
/// サムネイルストリップ用の小サイズ画像キャッシュ。
/// </summary>
public sealed class ThumbnailCache
{
    private const int ThumbWidth = 160;

    private readonly object _lock = new();
    private readonly Dictionary<int, BitmapSource> _cache = new();
    private readonly Dictionary<int, Task<BitmapSource?>> _inflight = new();
    private readonly SemaphoreSlim _gate = new(3);

    private MediaCollection? _collection;

    public void SetCollection(MediaCollection? collection)
    {
        lock (_lock)
        {
            _collection = collection;
            _cache.Clear();
            _inflight.Clear();
        }
    }

    public async Task<BitmapSource?> GetAsync(int index)
    {
        lock (_lock)
        {
            if (_cache.TryGetValue(index, out var cached))
                return cached;
        }

        Task<BitmapSource?> task;
        lock (_lock)
        {
            if (_inflight.TryGetValue(index, out var existing))
                task = existing;
            else
            {
                task = LoadAsync(index);
                _inflight[index] = task;
            }
        }

        try
        {
            var bmp = await task;
            if (bmp != null)
            {
                lock (_lock) _cache[index] = bmp;
            }
            return bmp;
        }
        finally
        {
            lock (_lock) _inflight.Remove(index);
        }
    }

    private async Task<BitmapSource?> LoadAsync(int index)
    {
        MediaCollection? col;
        lock (_lock) col = _collection;
        if (col == null || index < 0 || index >= col.Count) return null;

        await _gate.WaitAsync();
        try
        {
            var item = col[index];
            return await Task.Run(() =>
            {
                if (col.ReadLock != null)
                {
                    lock (col.ReadLock)
                    {
                        using var s = item.Open();
                        return ImageDecoder.Decode(s, ThumbWidth);
                    }
                }
                using var s2 = item.Open();
                return ImageDecoder.Decode(s2, ThumbWidth);
            });
        }
        catch
        {
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }
}
