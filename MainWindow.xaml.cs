using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using PhotoView.Models;
using PhotoView.Services;

namespace PhotoView;

/// <summary>サムネイル1枚分のVM。</summary>
public sealed class ThumbItem : INotifyPropertyChanged
{
    private BitmapSource? _thumb;
    public int Index { get; }
    public string Name { get; }

    public ThumbItem(int index, string name)
    {
        Index = index;
        Name = name;
    }

    public BitmapSource? Thumb
    {
        get => _thumb;
        set
        {
            if (ReferenceEquals(_thumb, value)) return;
            _thumb = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Thumb)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public partial class MainWindow : Window
{
    private const int ThumbWindow = 40;
    private const double TopHoverZone = 90;
    private const double BottomHoverZone = 170;
    private const double TopCornerZoneWidth = 90;

    private readonly ImageCache _imageCache = new();
    private readonly ThumbnailCache _thumbCache = new();
    private readonly ObservableCollection<ThumbItem> _thumbItems = new();
    private readonly DispatcherTimer _chromeHideTimer;

    private MediaCollection? _collection;
    private int _currentIndex;
    private bool _chromePinned;
    private bool _suppressThumbSelection;

    // サムネイルストリップの現在のウィンドウ範囲（スクロールに応じて拡張）
    private int _thumbStart;
    private int _thumbEnd;
    private double _thumbItemWidth = 110;

    // ズーム/パン状態
    private double _scale = 1;
    private double _offsetX, _offsetY;
    private bool _isFit = true;
    private Point _downPos;
    private bool _dragging;

    // クリックとダブルクリックの判別
    private Point _pendingClickPos;
    private bool _pendingClick;
    private bool _pendingClickShift;
    private readonly DispatcherTimer _clickTimer;

    // 2ページ表示モード
    private bool _twoPageMode;
    private bool _swapPages = true; // true: 現在ページが右、次ページが左（デフォルト）

    // アニメーション GIF の再生状態
    private DispatcherTimer? _gifTimer;
    private IReadOnlyList<BitmapSource>? _gifFrames;
    private IReadOnlyList<int> _gifDelays = Array.Empty<int>();
    private MediaCollection? _gifCol;
    private int _gifItemIndex = -1;
    private int _gifIndex;

    // カーソルがホバーしている領域
    private bool _hoverTop, _hoverBottom;

    public MainWindow(string? initialPath = null)
    {
        InitializeComponent();
        ThumbList.ItemsSource = _thumbItems;

        // プライマリディスプレイのワークエリア中央に配置
        CenterOnPrimaryDisplay();

        _chromeHideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _chromeHideTimer.Tick += (_, _) =>
        {
            _chromeHideTimer.Stop();
            if (_chromePinned) return;
            if (!_hoverTop) TopBar.Visibility = Visibility.Collapsed;
            if (!_hoverBottom) ThumbPanel.Visibility = Visibility.Collapsed;
        };

        _clickTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(220) };
        _clickTimer.Tick += (_, _) =>
        {
            _clickTimer.Stop();
            if (_pendingClick)
            {
                _pendingClick = false;
                HandleViewportClick(_pendingClickPos);
            }
        };

        SwapPagesMenuItem.IsChecked = _swapPages;

        if (initialPath != null)
            OpenPath(initialPath);
        else
            DropHint.Visibility = Visibility.Visible;
    }

    private void CenterOnPrimaryDisplay()
    {
        var wa = SystemParameters.WorkArea; // プライマリディスプレイのワークエリア
        Width = Math.Min(Width, wa.Width);
        Height = Math.Min(Height, wa.Height);
        Left = wa.Left + (wa.Width - Width) / 2;
        Top = wa.Top + (wa.Height - Height) / 2;
    }

    // ---------- 開く ----------

    private void OpenPath(string path)
    {
        try
        {
            // PDF は専用経路、それ以外は MediaCollection（必要なら複数ページ TIFF を展開）
            string ext = Path.GetExtension(path);
            MediaCollection col;
            int startIndex;
            if (ext.Equals(".pdf", StringComparison.OrdinalIgnoreCase))
            {
                col = PdfSource.Open(path, out startIndex);
                col = TiffPageSplitter.Expand(col, ref startIndex);
            }
            else
            {
                col = MediaCollection.Open(path, out startIndex);
                col = TiffPageSplitter.Expand(col, ref startIndex);
            }

            if (col.Count == 0)
            {
                col.Dispose();
                MessageBox.Show(this, "画像が見つかりません。", "PhotoView",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            _collection?.Dispose();
            _collection = col;
            _imageCache.SetCollection(col);
            _thumbCache.SetCollection(col);
            _currentIndex = startIndex;
            _isFit = true;
            DropHint.Visibility = Visibility.Collapsed;
            ShowChrome();

            _imageCache.SetCurrent(_currentIndex);
            _ = DisplayImagesAsync(_currentIndex);
            RebuildThumbs();
            UpdateCaption();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "PhotoView",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ---------- ナビゲーション ----------

    private void GoTo(int index, bool scrollThumb = true)
    {
        if (_collection == null) return;
        int count = _collection.Count;
        index = Math.Clamp(index, 0, count - 1);
        if (index == _currentIndex) return;

        _currentIndex = index;
        _imageCache.SetCurrent(index);
        _ = DisplayImagesAsync(index);
        UpdateCaption();
        if (scrollThumb) RebuildThumbs();
    }

    private void Prev(bool shift = false) => GoTo(_currentIndex - (Step(shift)));
    private void Next(bool shift = false) => GoTo(_currentIndex + Step(shift));

    /// <summary>送り戻しのステップ数。2ページ表示では2枚、Shift押下時は1枚。</summary>
    private int Step(bool shift)
        => _twoPageMode && !shift ? 2 : 1;

    private async Task DisplayImagesAsync(int index)
    {
        MediaCollection? col = _collection;
        if (col == null || index < 0 || index >= col.Count) return;

        // 表示切り替えでは GIF 再生を止める
        StopGifAnimation();

        // 2ページ表示モードの場合は index と index+1 を読み込む
        bool twoPage = _twoPageMode;
        int next = twoPage ? index + 1 : -1;
        bool needNext = next >= 0 && next < col.Count;

        bool anyUncached = !_imageCache.Contains(index) || (needNext && !_imageCache.Contains(next));
        if (anyUncached) LoadingOverlay.Visibility = Visibility.Visible;

        var bmp = await _imageCache.GetAsync(index);
        var bmpNext = needNext ? await _imageCache.GetAsync(next) : null;

        // 結果が古くなっていたら無視（コレクション変更 or 別の画像へ移動済み or モード切替）
        if (_collection != col || _currentIndex != index || _twoPageMode != twoPage)
        {
            if (_collection == col)
                LoadingOverlay.Visibility = Visibility.Collapsed;
            return;
        }

        LoadingOverlay.Visibility = Visibility.Collapsed;

        if (bmp != null)
        {
            ErrorText.Visibility = Visibility.Collapsed;
        }
        else
        {
            ErrorText.Text = "この画像を読み込めませんでした。";
            ErrorText.Visibility = Visibility.Visible;
        }

        SetImages(bmp, bmpNext);

        // 1ページ表示で GIF ならアニメーション再生
        if (!twoPage && bmp != null)
            TryStartGifAnimation(col, index);
    }

    private void StopGifAnimation()
    {
        _gifTimer?.Stop();
        _gifTimer = null;
        _gifFrames = null;
        _gifDelays = Array.Empty<int>();
        _gifCol = null;
        _gifItemIndex = -1;
    }

    private async void TryStartGifAnimation(MediaCollection col, int index)
    {
        StopGifAnimation();
        string name = col[index].Name;
        if (!name.EndsWith(".gif", StringComparison.OrdinalIgnoreCase)) return;

        (BitmapSource[] Frames, int[] Delays)? result;
        try
        {
            result = await Task.Run(() => DecodeGifFrames(col[index]));
        }
        catch
        {
            result = null;
        }
        if (result == null || result.Value.Frames.Length <= 1) return;

        // デコード中に移動・モード切替されていたら中断
        if (_collection != col || _currentIndex != index || _twoPageMode) return;

        _gifCol = col;
        _gifItemIndex = index;
        _gifFrames = result.Value.Frames;
        _gifDelays = result.Value.Delays;
        _gifIndex = 1;

        _gifTimer = new DispatcherTimer();
        _gifTimer.Tick += (_, _) =>
        {
            if (_collection != _gifCol || _currentIndex != _gifItemIndex)
            {
                StopGifAnimation();
                return;
            }
            if (_gifFrames is null || _gifIndex >= _gifFrames.Count)
            {
                StopGifAnimation();
                return;
            }

            MainImage.Source = _gifFrames[_gifIndex];
            int delay = _gifIndex < _gifDelays.Count ? _gifDelays[_gifIndex] : 100;
            _gifTimer!.Interval = TimeSpan.FromMilliseconds(Math.Max(10, delay));
            _gifIndex = (_gifIndex + 1) % _gifFrames.Count;
        };

        MainImage.Source = result.Value.Frames[0];
        int d0 = result.Value.Delays.Length > 0 ? result.Value.Delays[0] : 100;
        _gifTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(10, d0));
        _gifTimer.Start();
    }

    private static (BitmapSource[] Frames, int[] Delays)? DecodeGifFrames(MediaItem item)
    {
        byte[] bytes;
        using (var s = item.Open())
        using (var ms = new MemoryStream())
        {
            s.CopyTo(ms);
            bytes = ms.ToArray();
        }
        if (bytes.Length < 16) return null;

        var dec = new GifBitmapDecoder(new MemoryStream(bytes),
            BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        if (dec.Frames.Count <= 1) return null;

        // 巨大な GIF（メモリ圧迫）は再生しない
        long totalBytes = 0;
        foreach (var f in dec.Frames)
            totalBytes += (long)f.PixelWidth * f.PixelHeight * 4;
        if (totalBytes > 250L * 1024 * 1024) return null;

        var frames = new BitmapSource[dec.Frames.Count];
        var delays = new int[dec.Frames.Count];
        for (int i = 0; i < dec.Frames.Count; i++)
        {
            var frame = dec.Frames[i];
            frame.Freeze();
            frames[i] = frame;
            delays[i] = GetGifDelayMs(frame);
        }
        return (frames, delays);
    }

    private static int GetGifDelayMs(BitmapFrame frame)
    {
        try
        {
            if (frame.Metadata is BitmapMetadata md)
            {
                object? v = md.GetQuery("/grctlext/Delay");
                if (v is ushort u) return u * 10;
                if (v is byte b) return b * 10;
                if (v is int i) return i * 10;
                if (v is short s) return s * 10;
            }
        }
        catch { }
        return 100;
    }

    private void SetImages(BitmapSource? current, BitmapSource? next)
    {
        // 左右入れ替えモードでは「現在ページ＝右、次ページ＝左」
        BitmapSource? left, right;
        if (_twoPageMode && _swapPages)
        {
            left = next;
            right = current;
        }
        else
        {
            left = current;
            right = next;
        }

        // 2ページモードでも片方しかない場合は1枚を中央表示する
        if (left == null && right != null)
        {
            left = right;
            right = null;
        }

        MainImage.Source = left;
        MainImage2.Source = _twoPageMode && right != null ? right : null;
        MainImage2.Visibility = _twoPageMode && right != null ? Visibility.Visible : Visibility.Collapsed;
        if (_isFit) FitToWindow();
        else ApplyTransform();
    }

    // ---------- キャプション ----------

    private void UpdateCaption()
    {
        if (_collection == null) return;
        var item = _collection[_currentIndex];
        FileNameText.Text = item.Name;
        if (_twoPageMode && _currentIndex + 1 < _collection.Count)
        {
            string mode = _swapPages ? "右始まり" : "左始まり";
            IndexText.Text = $"{_currentIndex + 1}-{_currentIndex + 2} / {_collection.Count}  (2ページ・{mode})";
        }
        else
        {
            IndexText.Text = $"{_currentIndex + 1} / {_collection.Count}";
        }
        SourceText.Text = "ソース: " + _collection.SourceName;
        Title = $"{item.Name} - PhotoView";
    }

    // ---------- サムネイル ----------

    private void RebuildThumbs()
    {
        if (_collection == null) return;

        int count = _collection.Count;
        _thumbStart = Math.Max(0, _currentIndex - ThumbWindow);
        _thumbEnd = Math.Min(count - 1, _currentIndex + ThumbWindow);

        _suppressThumbSelection = true;
        _thumbItems.Clear();
        for (int i = _thumbStart; i <= _thumbEnd; i++)
        {
            var ti = new ThumbItem(i, _collection[i].Name);
            _thumbItems.Add(ti);
            _ = LoadThumbAsync(ti);
        }

        ThumbList.SelectedItem = _thumbItems.FirstOrDefault(t => t.Index == _currentIndex);
        ThumbList.ScrollIntoView(ThumbList.SelectedItem);
        _suppressThumbSelection = false;
    }

    private async Task LoadThumbAsync(ThumbItem item)
    {
        var bmp = await _thumbCache.GetAsync(item.Index);
        if (bmp == null) return;
        // リストに残っているか確認
        if (_thumbItems.Contains(item))
            item.Thumb = bmp;
    }

    private void ThumbList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressThumbSelection) return;
        if (ThumbList.SelectedItem is ThumbItem ti && ti.Index != _currentIndex)
        {
            GoTo(ti.Index, scrollThumb: false);
            ThumbList.ScrollIntoView(ti);
        }
    }

    private void ThumbList_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        // サムネイルストリップ上でのホイールは横スクロールにする
        // （ScrollViewer が先に処理するのを防ぐためトンネリングイベントで受ける）
        if (FindDescendant<ScrollViewer>(ThumbList) is not ScrollViewer sv) return;

        double newOffset = sv.HorizontalOffset + e.Delta;

        // ストリップの端に近づいたらウィンドウを拡張してスクロールを継続させる
        if (_collection is MediaCollection col && _thumbItems.Count > 0)
        {
            if (e.Delta > 0 && newOffset >= sv.ScrollableWidth - 200)
                ExpandThumbWindowForward(col);
            else if (e.Delta < 0 && newOffset <= 200)
                ExpandThumbWindowBackward(col, ref newOffset);
        }

        ThumbList.UpdateLayout();
        sv.ScrollToHorizontalOffset(newOffset);
        e.Handled = true;
    }

    private void ExpandThumbWindowForward(MediaCollection col)
    {
        if (_thumbEnd >= col.Count - 1) return;
        int end = Math.Min(_thumbEnd + ThumbWindow, col.Count - 1);
        for (int i = _thumbEnd + 1; i <= end; i++)
        {
            var ti = new ThumbItem(i, col[i].Name);
            _thumbItems.Add(ti);
            _ = LoadThumbAsync(ti);
        }
        _thumbEnd = end;
    }

    private void ExpandThumbWindowBackward(MediaCollection col, ref double offset)
    {
        if (_thumbStart <= 0) return;
        int start = Math.Max(_thumbStart - ThumbWindow, 0);

        var prepend = new List<ThumbItem>();
        for (int i = start; i < _thumbStart; i++)
            prepend.Add(new ThumbItem(i, col[i].Name));

        for (int i = 0; i < prepend.Count; i++)
        {
            _thumbItems.Insert(i, prepend[i]);
            _ = LoadThumbAsync(prepend[i]);
        }

        // 左側に増えた分だけスクロールオフセットを補正して表示位置を保つ
        offset += prepend.Count * ThumbItemWidth;
        _thumbStart = start;
    }

    private double ThumbItemWidth
    {
        get
        {
            if (ThumbList.Items.Count > 0 &&
                ThumbList.ItemContainerGenerator.ContainerFromIndex(0) is FrameworkElement fe &&
                fe.ActualWidth > 0)
                _thumbItemWidth = fe.ActualWidth;
            return _thumbItemWidth;
        }
    }

    private static T? FindDescendant<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) return match;
            if (FindDescendant<T>(child) is T inner) return inner;
        }
        return null;
    }

    // ---------- ズーム / パン ----------

    /// <summary>コンテンツ全体（1枚 or 2枚）のピクセル幅・高さを返す。</summary>
    private void GetContentSize(out double width, out double height)
    {
        if (MainImage.Source is not BitmapSource left)
        {
            width = 0;
            height = 0;
            return;
        }

        if (_twoPageMode && MainImage2.Source is BitmapSource right)
        {
            width = left.PixelWidth + right.PixelWidth;
            height = Math.Max(left.PixelHeight, right.PixelHeight);
        }
        else
        {
            width = left.PixelWidth;
            height = left.PixelHeight;
        }
    }

    private void ApplyTransform()
    {
        if (MainImage.Source is not BitmapSource left) return;

        // 表示サイズを直接設定（RenderTransform は使わない）。
        // ClipToBounds との相互作用で下半分が欠ける問題を回避する。
        if (_twoPageMode && MainImage2.Source is BitmapSource right)
        {
            // 左画像と右画像を横に並べ、それぞれ自分の半分の領域の中央に配置
            double totalW = (left.PixelWidth + right.PixelWidth) * _scale;
            double maxH = Math.Max(left.PixelHeight, right.PixelHeight) * _scale;
            double halfW = totalW / 2;

            MainImage.Width = left.PixelWidth * _scale;
            MainImage.Height = left.PixelHeight * _scale;
            Canvas.SetLeft(MainImage, _offsetX + (halfW - MainImage.Width) / 2);
            Canvas.SetTop(MainImage, _offsetY + (maxH - MainImage.Height) / 2);

            MainImage2.Width = right.PixelWidth * _scale;
            MainImage2.Height = right.PixelHeight * _scale;
            Canvas.SetLeft(MainImage2, _offsetX + halfW + (halfW - MainImage2.Width) / 2);
            Canvas.SetTop(MainImage2, _offsetY + (maxH - MainImage2.Height) / 2);
        }
        else
        {
            MainImage.Width = left.PixelWidth * _scale;
            MainImage.Height = left.PixelHeight * _scale;
            Canvas.SetLeft(MainImage, _offsetX);
            Canvas.SetTop(MainImage, _offsetY);
        }
    }

    private void FitToWindow()
    {
        if (MainImage.Source is not BitmapSource) return;
        double vw = Viewport.ActualWidth, vh = Viewport.ActualHeight;
        if (vw < 1 || vh < 1) return;

        GetContentSize(out double cw, out double ch);
        if (cw < 1 || ch < 1) return;

        double sx = vw / cw;
        double sy = vh / ch;
        _scale = Math.Min(sx, sy);
        _offsetX = (vw - cw * _scale) / 2;
        _offsetY = (vh - ch * _scale) / 2;
        _isFit = true;
        ApplyTransform();
    }

    private void ZoomAt(Point p, double factor)
    {
        if (MainImage.Source is not BitmapSource) return;
        double newScale = Math.Clamp(_scale * factor, 0.02, 16.0);
        if (Math.Abs(newScale - _scale) < 1e-9) return;

        // カーソル位置の画像座標（コンテンツ全体座標）
        GetContentSize(out double cw, out double ch);
        double imgX = (p.X - _offsetX) / _scale;
        double imgY = (p.Y - _offsetY) / _scale;

        _scale = newScale;
        _offsetX = p.X - imgX * _scale;
        _offsetY = p.Y - imgY * _scale;
        _isFit = false;
        ApplyTransform();
    }

    private void Viewport_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_isFit && MainImage.Source != null)
            FitToWindow();
    }

    // ---------- マウス操作 ----------

    private void Viewport_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount >= 2)
        {
            _clickTimer.Stop();
            _pendingClick = false;

            if (_isFit)
            {
                if (MainImage.Source is BitmapSource)
                {
                    GetContentSize(out double cw, out double ch);
                    _scale = 1;
                    _offsetX = (Viewport.ActualWidth - cw) / 2;
                    _offsetY = (Viewport.ActualHeight - ch) / 2;
                    _isFit = false;
                    ApplyTransform();
                }
            }
            else
            {
                FitToWindow();
            }
            e.Handled = true;
            return;
        }

        _downPos = e.GetPosition(Viewport);
        _dragging = false;
        _pendingClickPos = _downPos;
        _pendingClick = true;
        _pendingClickShift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);

        // ダブルクリックとの判別用タイマー（一定時間後にクリックとして確定）
        _clickTimer.Stop();
        _clickTimer.Start();

        Viewport.CaptureMouse();
        e.Handled = true;
    }

    private void Viewport_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed && Viewport.IsMouseCaptured)
        {
            var p = e.GetPosition(Viewport);
            double dx = p.X - _downPos.X;
            double dy = p.Y - _downPos.Y;

            // 4px 以上動いたらパン（ズーム中のみ）扱い
            if (!_dragging && (Math.Abs(dx) > 4 || Math.Abs(dy) > 4))
            {
                _dragging = true;
                _pendingClick = false; // ドラッグになったのでクリック扱いしない
            }

            if (_dragging)
            {
                _offsetX += dx;
                _offsetY += dy;
                _isFit = false;
                _downPos = p;
                ApplyTransform();
            }
        }
    }

    private void Viewport_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!Viewport.IsMouseCaptured) return;
        Viewport.ReleaseMouseCapture();
        _dragging = false;
        e.Handled = true;
    }
    /// <summary>確定したクリックの処理。左端・右端で送り戻し、中央でUIトグル。</summary>
    private void HandleViewportClick(Point p)
    {
        double w = Viewport.ActualWidth;
        if (w <= 0) return;
        double rel = p.X / w;
        if (rel < 0.33) Prev(_pendingClickShift);
        else if (rel > 0.67) Next(_pendingClickShift);
        else ToggleChrome();
    }

    private void Viewport_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        var p = e.GetPosition(Viewport);
        double factor = e.Delta > 0 ? 1.15 : 1 / 1.15;
        ZoomAt(p, factor);
        e.Handled = true;
    }

    // ---------- キーボード ----------

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Left:
            case Key.Back:
                Prev(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
                e.Handled = true;
                break;
            case Key.Right:
            case Key.Space:
                Next(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
                e.Handled = true;
                break;
            case Key.PageUp:
                GoTo(_currentIndex - 10);
                e.Handled = true;
                break;
            case Key.PageDown:
                GoTo(_currentIndex + 10);
                e.Handled = true;
                break;
            case Key.Home:
                GoTo(0);
                e.Handled = true;
                break;
            case Key.End:
                GoTo(_collection?.Count - 1 ?? 0);
                e.Handled = true;
                break;
            case Key.Add:
            case Key.OemPlus:
                if (MainImage.Source != null)
                    ZoomAt(new Point(Viewport.ActualWidth / 2, Viewport.ActualHeight / 2), 1.2);
                e.Handled = true;
                break;
            case Key.Subtract:
            case Key.OemMinus:
                if (MainImage.Source != null)
                    ZoomAt(new Point(Viewport.ActualWidth / 2, Viewport.ActualHeight / 2), 1 / 1.2);
                e.Handled = true;
                break;
            case Key.D0:
                FitToWindow();
                e.Handled = true;
                break;
            case Key.P:
                ToggleTwoPage();
                e.Handled = true;
                break;
            case Key.S:
                ToggleSwapPages();
                e.Handled = true;
                break;
            case Key.O:
                if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
                {
                    if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) OpenFolderBrowser();
                    else OpenFileBrowser();
                    e.Handled = true;
                }
                break;
            case Key.F11:
                ToggleFullscreen();
                e.Handled = true;
                break;
            case Key.Escape:
                if (_fullscreen)
                    ToggleFullscreen();
                else if (_chromePinned || TopBar.Visibility == Visibility.Visible)
                    HideChrome(force: true);
                e.Handled = true;
                break;
        }
    }

    // ---------- UI 表示 ----------

    /// <summary>2ページ表示モードの切り替え。</summary>
    private void ToggleTwoPage()
    {
        _twoPageMode = !_twoPageMode;
        TwoPageMenuItem.IsChecked = _twoPageMode;

        _isFit = true;
        _ = DisplayImagesAsync(_currentIndex);
        UpdateCaption();
        ShowChrome();
    }

    /// <summary>2ページの左右入れ替え（現在ページを右/左のどちらに置くか）。</summary>
    private void ToggleSwapPages()
    {
        _swapPages = !_swapPages;
        SwapPagesMenuItem.IsChecked = _swapPages;

        if (_twoPageMode)
        {
            _isFit = true;
            _ = DisplayImagesAsync(_currentIndex);
            UpdateCaption();
            ShowChrome();
        }
    }

    private void ShowChrome()
    {
        TopBar.Visibility = Visibility.Visible;
        ThumbPanel.Visibility = Visibility.Visible;
        _chromeHideTimer.Stop();
        _chromeHideTimer.Start();
    }

    private void HideChrome(bool force)
    {
        _chromeHideTimer.Stop();
        TopBar.Visibility = Visibility.Collapsed;
        ThumbPanel.Visibility = Visibility.Collapsed;
        if (force) _chromePinned = false;
    }

    private void ToggleChrome()
    {
        _chromePinned = !_chromePinned;
        if (_chromePinned)
        {
            _chromeHideTimer.Stop();
            TopBar.Visibility = Visibility.Visible;
            ThumbPanel.Visibility = Visibility.Visible;
        }
        else
        {
            TopBar.Visibility = Visibility.Collapsed;
            ThumbPanel.Visibility = Visibility.Collapsed;
        }
    }

    private void Window_MouseMove(object sender, MouseEventArgs e)
    {
        var pos = e.GetPosition(Root);

        // 全画面時に右上コーナーへカーソルを置くと閉じるボタンを表示
        bool hoverTopRight = pos.X >= Root.ActualWidth - TopCornerZoneWidth
                             && pos.Y <= TopHoverZone;
        CloseFullscreenButton.Visibility =
            _fullscreen && hoverTopRight ? Visibility.Visible : Visibility.Collapsed;

        // 左上コーナーへカーソルを置くと全画面⇔通常をトグルするボタンを表示（どちらの状態でも）
        bool hoverTopLeft = pos.X <= TopCornerZoneWidth && pos.Y <= TopHoverZone;
        FullscreenButton.Visibility = hoverTopLeft ? Visibility.Visible : Visibility.Collapsed;

        // 画面の左/右端（クリックで送り戻しできる位置）にカーソルを置くと送り/戻しボタンを表示
        bool navAvailable = _collection != null && _collection.Count > 1;
        bool hoverLeftNav = navAvailable && pos.X < Root.ActualWidth * 0.33;
        bool hoverRightNav = navAvailable && pos.X > Root.ActualWidth * 0.67;
        PrevButton.Visibility = hoverLeftNav ? Visibility.Visible : Visibility.Collapsed;
        NextButton.Visibility = hoverRightNav ? Visibility.Visible : Visibility.Collapsed;

        // コレクションが開いていて、ドラッグ中でなければマウス位置でUI表示切替
        if (_collection == null) return;
        if (e.LeftButton == MouseButtonState.Pressed) return;

        _hoverTop = pos.Y <= TopHoverZone;
        _hoverBottom = pos.Y >= Root.ActualHeight - BottomHoverZone;

        // カーソルがエリア内にある要素だけ表示する
        if (_hoverTop) TopBar.Visibility = Visibility.Visible;
        if (_hoverBottom) ThumbPanel.Visibility = Visibility.Visible;

        // エリア外なら短い遅延後に隠す
        _chromeHideTimer.Stop();
        _chromeHideTimer.Start();
    }

    private void Window_MouseLeave(object sender, MouseEventArgs e)
    {
        _hoverTop = false;
        _hoverBottom = false;
        CloseFullscreenButton.Visibility = Visibility.Collapsed;
        FullscreenButton.Visibility = Visibility.Collapsed;
        PrevButton.Visibility = Visibility.Collapsed;
        NextButton.Visibility = Visibility.Collapsed;
        if (_chromePinned) return;
        TopBar.Visibility = Visibility.Collapsed;
        ThumbPanel.Visibility = Visibility.Collapsed;
    }

    private void PrevButton_Click(object sender, RoutedEventArgs e) => Prev();
    private void NextButton_Click(object sender, RoutedEventArgs e) => Next();

    private bool _fullscreen;
    private void ToggleFullscreen()
    {
        _fullscreen = !_fullscreen;
        if (_fullscreen)
        {
            WindowStyle = WindowStyle.None;
            WindowState = WindowState.Maximized;
            Topmost = true;
        }
        else
        {
            WindowStyle = WindowStyle.SingleBorderWindow;
            WindowState = WindowState.Normal;
            Topmost = false;
            CloseFullscreenButton.Visibility = Visibility.Collapsed;
        }
        if (_isFit) FitToWindow();
    }

    private void CloseFullscreenButton_Click(object sender, RoutedEventArgs e)
        => Close();

    private void FullscreenButton_Click(object sender, RoutedEventArgs e)
        => ToggleFullscreen();

    // ---------- 開くダイアログ / ドラッグ&ドロップ ----------

    private void OpenFileBrowser()
    {
        var dlg = new OpenFileDialog
        {
            Title = "画像・圧縮ファイルを開く",
            Filter = "画像/圧縮ファイル|*.png;*.jpg;*.jpeg;*.webp;*.bmp;*.gif;*.tif;*.tiff;*.zip;*.rar;*.7z;*.tar;*.gz;*.bz2;*.xz;*.cab;*.epub;*.pdf|すべてのファイル|*.*",
            Multiselect = false,
        };
        if (dlg.ShowDialog(this) == true)
            OpenPath(dlg.FileName);
    }

    private void OpenFolderBrowser()
    {
        var dlg = new OpenFolderDialog
        {
            Title = "画像フォルダを開く",
        };
        if (dlg.ShowDialog(this) == true)
            OpenPath(dlg.FolderName);
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
        {
            string path = files[0];
            OpenPath(path);
        }
        e.Handled = true;
    }

    protected override void OnClosed(EventArgs e)
    {
        _collection?.Dispose();
        _collection = null;
        base.OnClosed(e);
    }

    // ---------- コンテキストメニュー ----------

    private void MenuItem_OpenFile_Click(object sender, RoutedEventArgs e)
        => OpenFileBrowser();

    private void MenuItem_OpenFolder_Click(object sender, RoutedEventArgs e)
        => OpenFolderBrowser();

    private void MenuItem_Fit_Click(object sender, RoutedEventArgs e)
        => FitToWindow();

    private void MenuItem_Zoom100_Click(object sender, RoutedEventArgs e)
    {
        if (MainImage.Source is BitmapSource)
        {
            GetContentSize(out double cw, out double ch);
            _scale = 1;
            _offsetX = (Viewport.ActualWidth - cw) / 2;
            _offsetY = (Viewport.ActualHeight - ch) / 2;
            _isFit = false;
            ApplyTransform();
        }
    }

    private void MenuItem_TwoPage_Click(object sender, RoutedEventArgs e)
        => ToggleTwoPage();

    private void MenuItem_SwapPages_Click(object sender, RoutedEventArgs e)
        => ToggleSwapPages();

    private async void MenuItem_Edit_Click(object sender, RoutedEventArgs e)
        => await OpenEditDialog();

    private async Task OpenEditDialog()
    {
        if (_collection == null) return;
        int index = _currentIndex;
        var bmp = await _imageCache.GetAsync(index);
        if (bmp == null)
        {
            MessageBox.Show(this, "画像を読み込んでいないため編集できません。", "編集",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dlg = new EditDialog(bmp, _collection[index].Name) { Owner = this };
        dlg.ShowDialog();
    }

    private void MenuItem_Fullscreen_Click(object sender, RoutedEventArgs e)
        => ToggleFullscreen();

    private void MenuItem_Exit_Click(object sender, RoutedEventArgs e)
        => Close();
}
