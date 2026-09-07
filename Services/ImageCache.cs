using System.Windows.Media.Imaging;
using PhotoView.Models;

namespace PhotoView.Services;

/// <summary>
/// デコード済み画像のインメモリキャッシュ。
/// 現在位置の周辺をプリフェッチし、LRU方式でメモリ・枚数を制限します。
/// </summary>
public sealed class ImageCache
{
    private const int MaxCount = 24;
    private const long MaxBytes = 600L * 1024 * 1024;

    private readonly object _lock = new();
    private readonly Dictionary<int, BitmapSource> _cache = new();
    private readonly Dictionary<int, long> _sizes = new();
    private readonly LinkedList<int> _lru = new();
    private readonly Dictionary<int, Task<BitmapSource?>> _inflight = new();
    private readonly SemaphoreSlim _decodeGate = new(2);

    private MediaCollection? _collection;
    private int _current = -1;
    private long _totalBytes;

    public void SetCollection(MediaCollection? collection)
    {
        lock (_lock)
        {
            _collection = collection;
            _cache.Clear();
            _sizes.Clear();
            _lru.Clear();
            _inflight.Clear();
            _totalBytes = 0;
            _current = -1;
        }
    }

    public bool Contains(int index)
    {
        lock (_lock) return _cache.ContainsKey(index);
    }

    public void SetCurrent(int index)
    {
        lock (_lock) _current = index;
        _ = PrefetchAsync();
    }

    /// <summary>
    /// 指定インデックスの画像を返します。未キャッシュならデコードします。
    /// </summary>
    public async Task<BitmapSource?> GetAsync(int index)
    {
        lock (_lock)
        {
            if (_cache.TryGetValue(index, out var cached))
            {
                Touch(index);
                return cached;
            }
        }

        Task<BitmapSource?> task;
        lock (_lock)
        {
            if (_inflight.TryGetValue(index, out var existing))
            {
                task = existing;
            }
            else
            {
                task = DecodeAsync(index);
                _inflight[index] = task;
            }
        }

        try
        {
            var bmp = await task;
            if (bmp != null)
            {
                Insert(index, bmp);
            }
            return bmp;
        }
        finally
        {
            lock (_lock) _inflight.Remove(index);
        }
    }

    private async Task<BitmapSource?> DecodeAsync(int index)
    {
        MediaCollection? col;
        lock (_lock) col = _collection;
        if (col == null || index < 0 || index >= col.Count) return null;

        await _decodeGate.WaitAsync();
        try
        {
            var item = col[index];
            return await Task.Run(() =>
            {
                // アーカイブのソリッド形式は並行読み取り不可のため直列化
                if (col.ReadLock != null)
                {
                    lock (col.ReadLock)
                    {
                        using var s = item.Open();
                        return ImageDecoder.Decode(s, 0);
                    }
                }
                using var s2 = item.Open();
                return ImageDecoder.Decode(s2, 0);
            });
        }
        catch
        {
            return null;
        }
        finally
        {
            _decodeGate.Release();
        }
    }

    private void Insert(int index, BitmapSource bmp)
    {
        long bytes = (long)bmp.PixelWidth * bmp.PixelHeight * 4;
        lock (_lock)
        {
            if (_cache.ContainsKey(index))
            {
                _cache[index] = bmp;
                Touch(index);
                return;
            }

            _cache[index] = bmp;
            _sizes[index] = bytes;
            _totalBytes += bytes;
            _lru.AddFirst(index);
            Evict();
        }
    }

    private void Touch(int index)
    {
        var node = _lru.Find(index);
        if (node != null)
        {
            _lru.Remove(node);
            _lru.AddFirst(node);
        }
    }

    private void Evict()
    {
        while (_cache.Count > MaxCount || _totalBytes > MaxBytes)
        {
            int? victim = null;
            for (var node = _lru.Last; node != null; node = node.Previous)
            {
                if (Math.Abs(node.Value - _current) <= 2) continue; // 現在位置近傍は残す
                victim = node.Value;
                break;
            }

            if (victim == null) break;

            _totalBytes -= _sizes[victim.Value];
            _sizes.Remove(victim.Value);
            _cache.Remove(victim.Value);
            _lru.Remove(victim.Value);
        }
    }

    private async Task PrefetchAsync()
    {
        MediaCollection? col;
        int cur;
        lock (_lock)
        {
            col = _collection;
            cur = _current;
        }
        if (col == null || cur < 0) return;

        int[] offsets = { 1, -1, 2, -2, 3, -3, 4, -4, 6, -6, 9, -9, 12, -12 };
        var tasks = new List<Task>();
        foreach (int off in offsets)
        {
            int idx = cur + off;
            if (idx < 0 || idx >= col.Count) continue;
            if (Contains(idx)) continue;
            tasks.Add(GetAsync(idx));
        }

        try { await Task.WhenAll(tasks); }
        catch { /* prefetch failures are non-fatal */ }
    }
}
