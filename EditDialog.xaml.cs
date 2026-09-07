using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using PhotoView.Services;

namespace PhotoView;

/// <summary>
/// 簡易画像編集ダイアログ。作業コピーに編集を重ね、「名前を付けて保存」で別ファイルに保存します。
/// 色調は小サイズプレビューにライブ適用し、確定時・保存時・ジオメトリ操作前にフル解像度へ焼き込みます。
/// </summary>
public partial class EditDialog : Window
{
    private const int PreviewMaxDim = 4096;
    private const double RenderMaxElem = 4096; // プレビュー要素の最大寸法（WPF描画上限を回避）

    private readonly BitmapSource _original;
    private BitmapSource _working;
    private BitmapSource? _preview;
    private BitmapSource? _undo;
    private string _baseName;

    // 領域選択（表示座標）と操作種別
    private double _selX, _selY, _selW, _selH;
    private double _angle; // 無回転状態の矩形を中心周りに回転（ラジアン）
    private bool _selecting;
    private Point _dragStart;
    private SelectionOp _op;

    // 画像そのものの任意回転モード（ホイール拡大縮小 ＋ ハンドル回転）
    private bool _imgRotateMode;
    private double _imgRotDeg; // 度
    private double _zoom = 1.0;
    private bool _imgRotating;

    // 任意リサイズモード（フレームドラッグでサイズ変更）
    private bool _resizeMode;
    private double _rzW, _rzH;     // 目標サイズ（表示座標）
    private bool _rzDragging;

    // 一般のビュー（ネイティブ解像度表示 ＋ 拡大縮小・中ボタンパン）。Host の RenderTransform で適用。
    private double _fitScale = 1.0;   // レイアウト毎に求めるフィット倍率（cover）
    private double _viewScale = 1.0;  // ユーザーズーム（1=フィット）
    private double _panX, _panY;      // スクリーン座標での中央からのオフセット
    private bool _panning;
    private Point _lastPan;

    private enum SelectionOp { None, Move, Rotate, ResizeL, ResizeR, ResizeT, ResizeB, ResizeTL, ResizeTR, ResizeBL, ResizeBR, New }

    public EditDialog(BitmapSource image, string baseName)
    {
        InitializeComponent();
        _original = image;
        _working = image;
        _baseName = baseName;

        Brightness.ValueChanged += (_, _) => { BrightnessLabel.Text = ((int)Brightness.Value).ToString(); UpdatePreviewSource(); };
        Contrast.ValueChanged += (_, _) => { ContrastLabel.Text = ((int)Contrast.Value).ToString(); UpdatePreviewSource(); };
        Saturation.ValueChanged += (_, _) => { SaturationLabel.Text = ((int)Saturation.Value).ToString(); UpdatePreviewSource(); };
        Hue.ValueChanged += (_, _) => { HueLabel.Text = ((int)Hue.Value).ToString(); UpdatePreviewSource(); };
        Mosaic.ValueChanged += (_, _) => MosaicLabel.Text = ((int)Mosaic.Value).ToString();
        Blur.ValueChanged += (_, _) => BlurLabel.Text = ((int)Blur.Value).ToString();

        Loaded += (_, _) => { BuildPreview(); RefreshPreview(); };
    }

    // ---------- プレビュー ----------

    private void BuildPreview()
    {
        double scale = Math.Min(1.0, PreviewMaxDim / (double)Math.Max(_working.PixelWidth, _working.PixelHeight));
        int w = Math.Max(1, (int)Math.Round(_working.PixelWidth * scale));
        int h = Math.Max(1, (int)Math.Round(_working.PixelHeight * scale));
        _preview = ImageEditor.Resize(_working, w, h);
    }

    private bool ColorActive()
        => Math.Abs(Brightness.Value) >= 0.5 || Math.Abs(Contrast.Value) >= 0.5
           || Math.Abs(Saturation.Value) >= 0.5 || Math.Abs(Hue.Value) >= 0.5;

    /// <summary>プレビューへ表示。通常は元解像度の作業画像、色調調整中のみ軽量プレビューへ。</summary>
    private void UpdatePreviewSource()
    {
        if (_working == null) return;
        if (ColorActive())
        {
            if (_preview != null)
                PreviewImage.Source = ImageEditor.AdjustColor(_preview, Brightness.Value, Contrast.Value, Saturation.Value, Hue.Value);
        }
        else
        {
            PreviewImage.Source = _working;
        }
    }

    private void RefreshPreview()
    {
        if (_working == null) return;
        FitPreview(Viewport.ActualWidth, Viewport.ActualHeight);
        UpdatePreviewSource();
        ApplyViewTransform(); // 回転モードなら内部で ApplyRotateView も呼ぶ
    }

    /// <summary>「画面いっぱい（フィル）」倍率を計算します。余白なし・はみ出しはトリミング。</summary>
    private void FitPreview(double availW, double availH)
    {
        if (_working == null) return;
        double cw = _working.PixelWidth, ch = _working.PixelHeight;

        double aW = Math.Max(1, availW);
        double aH = Math.Max(1, availH);
        // cover：大きい方に合わせて画面を埋める（余白なし）。小さい方ははみ出してトリミング。
        _fitScale = Math.Max(aW / cw, aH / ch);
        if (_fitScale <= 0) _fitScale = 0.01;
    }

    private void Viewport_SizeChanged(object sender, SizeChangedEventArgs e) => RefreshPreview();

    // ---------- 画像の任意回転モード ----------

    private void ImageRotateToggle_Changed(object sender, RoutedEventArgs e)
    {
        ClearSelection();
        ResetView();
        if (ImageRotateToggle.IsChecked == true)
        {
            _imgRotateMode = true;
            _imgRotDeg = 0;
            _zoom = 1.0;
            ApplyImgRotateBtn.IsEnabled = true;
        }
        else
        {
            _imgRotateMode = false;
            _imgRotDeg = 0;
            _zoom = 1.0;
            ApplyImgRotateBtn.IsEnabled = false;
            _imgRotating = false;
        }
        ApplyRotateView();
    }

    /// <summary>現在の角度・ズームをプレビューへ視覚的に反映します（回転・拡大は中心基準）。</summary>
    private void ApplyRotateView()
    {
        if (_imgRotateMode)
        {
            PreviewImage.RenderTransformOrigin = new Point(0.5, 0.5);
            var g = new TransformGroup();
            g.Children.Add(new RotateTransform(_imgRotDeg));
            g.Children.Add(new ScaleTransform(_zoom, _zoom));
            PreviewImage.RenderTransform = g;

            double cx = PreviewImage.Width / 2.0, cy = PreviewImage.Height / 2.0;
            double d = PreviewImage.Height / 2.0 * _zoom + 18;
            Point hp = GetImgHandlePos(cx, cy, d);

            ImageRotateLine.X1 = cx; ImageRotateLine.Y1 = cy;
            ImageRotateLine.X2 = hp.X; ImageRotateLine.Y2 = hp.Y;
            ImageRotateLine.Visibility = Visibility.Visible;

            Canvas.SetLeft(ImageRotateHandle, hp.X - ImageRotateHandle.Width / 2.0);
            Canvas.SetTop(ImageRotateHandle, hp.Y - ImageRotateHandle.Height / 2.0);
            ImageRotateHandle.Visibility = Visibility.Visible;

            StatusText.Text = $"回転: {_imgRotDeg:0.0}°　ズーム: {_zoom:0.00}x";
        }
        else
        {
            PreviewImage.RenderTransform = Transform.Identity;
            ImageRotateLine.Visibility = Visibility.Collapsed;
            ImageRotateHandle.Visibility = Visibility.Collapsed;
        }
    }

    private Point GetImgHandlePos(double cx, double cy, double len)
    {
        double rad = _imgRotDeg * Math.PI / 180.0;
        return new Point(cx + Math.Sin(rad) * len, cy - Math.Cos(rad) * len);
    }

    private bool NearImgHandle(Point p)
    {
        double cx = PreviewImage.Width / 2.0, cy = PreviewImage.Height / 2.0;
        double d = PreviewImage.Height / 2.0 * _zoom + 18;
        return (p - GetImgHandlePos(cx, cy, d)).Length <= 14;
    }

    private void SetImgRotationFromPointer(Point p)
    {
        double cx = PreviewImage.Width / 2.0, cy = PreviewImage.Height / 2.0;
        if (Math.Abs(p.X - cx) < 0.5 && Math.Abs(p.Y - cy) < 0.5) return;
        _imgRotDeg = Math.Atan2(p.X - cx, -(p.Y - cy)) * 180.0 / Math.PI;
    }

    private void Preview_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_imgRotateMode)
        {
            _zoom = Math.Clamp(_zoom * (e.Delta > 0 ? 1.15 : 1 / 1.15), 0.1, 8.0);
            ApplyRotateView();
            e.Handled = true;
            return;
        }

        // 一般表示のホイール拡大縮小（カーソル位置基準、ネイティブ画素）
        Point p = e.GetPosition(PreviewImage);
        double factor = e.Delta > 0 ? 1.15 : 1 / 1.15;
        ZoomAt(p, factor);
        e.Handled = true;
    }

    private void ZoomAt(Point local, double factor)
    {
        if (_working == null) return;
        // 中央基準で拡大縮小（片側へ流れるのを防ぎ、中央を維持）。細部は中ドラッグでパンして確認。
        double newScale = Math.Clamp(_viewScale * factor, 0.1, Math.Max(0.1, MaxView));
        if (Math.Abs(newScale - _viewScale) < 1e-9) return;
        _viewScale = newScale;
        ApplyViewTransform();
    }

    private double MaxView
        => _working == null ? 1.0 : Math.Min(
            RenderMaxElem / (_working.PixelWidth * _fitScale),
            RenderMaxElem / (_working.PixelHeight * _fitScale));

    private void ApplyViewTransform()
    {
        if (_working == null) return;
        double cw = _working.PixelWidth, ch = _working.PixelHeight;

        // 描画上限(4096)を超えないようビュー倍率を制限（巨大画像は1未満も可）
        _viewScale = Math.Clamp(_viewScale, 0.1, Math.Max(0.1, MaxView));
        double ew = cw * _fitScale * _viewScale;
        double eh = ch * _fitScale * _viewScale;

        // 要素を表示サイズにし、Canvas 配置（RenderTransform 不使用＝メインビューアと同方式）
        PreviewImage.Width = ew;
        PreviewImage.Height = eh;
        PreviewArea.Width = ew;
        PreviewArea.Height = eh;

        double tx = Viewport.ActualWidth / 2.0 - ew / 2.0 + _panX;
        double ty = Viewport.ActualHeight / 2.0 - eh / 2.0 + _panY;
        PreviewArea.Margin = new Thickness(tx, ty, 0, 0);

        UpdateSelectionDisplay();
        if (_imgRotateMode) ApplyRotateView();
    }

    private void ResetView()
    {
        _viewScale = 1.0;
        _panX = 0;
        _panY = 0;
        _panning = false;
        ApplyViewTransform();
    }

    // ---------- マウス操作（パン・回転・リサイズ・選択） ----------

    private double DispW => PreviewImage.Width;
    private double DispH => PreviewImage.Height;

    private void Preview_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Middle)
        {
            _panning = true;
            _lastPan = e.GetPosition(Viewport);
            Viewport.CaptureMouse();
            e.Handled = true;
        }
    }

    private void Preview_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Middle && _panning)
        {
            _panning = false;
            Viewport.ReleaseMouseCapture();
            e.Handled = true;
        }
    }

    private void Preview_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        Point p = e.GetPosition(PreviewImage);

        // 画像回転モード: ハンドルを掴んで任意回転
        if (_imgRotateMode)
        {
            if (NearImgHandle(p))
            {
                _imgRotating = true;
                PreviewImage.CaptureMouse();
                e.Handled = true;
            }
            return;
        }

        // 任意リサイズモード: フレームの辺/角を掴んでサイズ変更
        if (_resizeMode)
        {
            var op = ResizeFrameHitTest(p);
            if (op != SelectionOp.None)
            {
                _rzDragging = true;
                PreviewImage.CaptureMouse();
                e.Handled = true;
            }
            return;
        }

        _selecting = true;
        _dragStart = p;

        if (_hasSelection)
        {
            if (NearHandle(p))
            {
                _op = SelectionOp.Rotate;
                PreviewImage.CaptureMouse();
                e.Handled = true;
                return;
            }

            _op = HitTest(p);
            if (_op != SelectionOp.None && _op != SelectionOp.New)
            {
                SelRect.Visibility = Visibility.Visible;
                PreviewImage.CaptureMouse();
                e.Handled = true;
                return;
            }
        }

        // 新規選択
        _op = SelectionOp.New;
        _selX = p.X; _selY = p.Y; _selW = 0; _selH = 0;
        _angle = 0;
        SelRect.Visibility = Visibility.Visible;
        UpdateSelectionDisplay();
        PreviewImage.CaptureMouse();
        e.Handled = true;
    }

    private void Preview_MouseMove(object sender, MouseEventArgs e)
    {
        // 中ボタンドラッグ＝パン
        if (_panning && e.MiddleButton == MouseButtonState.Pressed)
        {
            Point panPos = e.GetPosition(Viewport);
            _panX += panPos.X - _lastPan.X;
            _panY += panPos.Y - _lastPan.Y;
            _lastPan = panPos;
            ApplyViewTransform();
            e.Handled = true;
            return;
        }

        Point p = e.GetPosition(PreviewImage);

        if (_imgRotateMode)
        {
            if (_imgRotating && e.LeftButton == MouseButtonState.Pressed)
            {
                SetImgRotationFromPointer(p);
                ApplyRotateView();
            }
            else
            {
                PreviewImage.Cursor = NearImgHandle(p) ? Cursors.Hand : Cursors.Arrow;
            }
            return;
        }

        // 任意リサイズモード
        if (_resizeMode)
        {
            if (_rzDragging && e.LeftButton == MouseButtonState.Pressed)
            {
                ResizeFrameDrag(p, ResizeFrameHitTest(p));
            }
            else if (!_rzDragging)
            {
                PreviewImage.Cursor = ResizeFrameHitTest(p) != SelectionOp.None ? Cursors.SizeNWSE : Cursors.Arrow;
            }
            return;
        }

        if (_selecting && e.LeftButton == MouseButtonState.Pressed)
        {
            switch (_op)
            {
                case SelectionOp.New:
                    _selX = Math.Min(_dragStart.X, p.X);
                    _selY = Math.Min(_dragStart.Y, p.Y);
                    _selW = Math.Abs(p.X - _dragStart.X);
                    _selH = Math.Abs(p.Y - _dragStart.Y);
                    break;
                case SelectionOp.Move:
                    MoveSelection(p.X - _dragStart.X, p.Y - _dragStart.Y);
                    break;
                case SelectionOp.Rotate:
                    double cx = _selX + _selW / 2.0, cy = _selY + _selH / 2.0;
                    if (Math.Abs(p.X - cx) < 0.5 && Math.Abs(p.Y - cy) < 0.5) break;
                    _angle = Math.Atan2(p.X - cx, -(p.Y - cy));
                    break;
                default:
                    ResizeSelection(p);
                    break;
            }

            if (_op != SelectionOp.New) _dragStart = p;
            UpdateSelectionDisplay();
            return;
        }

        // ホバー時のカーソル
        if (_hasSelection)
        {
            if (NearHandle(p))
            {
                PreviewImage.Cursor = Cursors.Hand;
            }
            else
            {
                PreviewImage.Cursor = HitTest(p) switch
                {
                    SelectionOp.Move => Cursors.SizeAll,
                    SelectionOp.ResizeL or SelectionOp.ResizeR => Cursors.SizeWE,
                    SelectionOp.ResizeT or SelectionOp.ResizeB => Cursors.SizeNS,
                    SelectionOp.ResizeTL or SelectionOp.ResizeBR => Cursors.SizeNWSE,
                    SelectionOp.ResizeTR or SelectionOp.ResizeBL => Cursors.SizeNESW,
                    _ => Cursors.Cross,
                };
            }
        }
        else
        {
            PreviewImage.Cursor = Cursors.Cross;
        }
    }

    private void Preview_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_imgRotateMode)
        {
            if (_imgRotating)
            {
                _imgRotating = false;
                PreviewImage.ReleaseMouseCapture();
                ApplyRotateView();
            }
            e.Handled = true;
            return;
        }

        // 任意リサイズモード
        if (_resizeMode)
        {
            if (_rzDragging)
            {
                _rzDragging = false;
                PreviewImage.ReleaseMouseCapture();
                UpdateResizeFrame();
            }
            e.Handled = true;
            return;
        }

        if (!_selecting) return;
        _selecting = false;
        PreviewImage.ReleaseMouseCapture();

        if (_op == SelectionOp.New && (_selW < 3 || _selH < 3))
        {
            _selW = _selH = 0;
        }
        _op = SelectionOp.None;
        UpdateSelectionDisplay();
        e.Handled = true;
    }

    // ---------- 選択操作 ----------

    private void MoveSelection(double dx, double dy)
    {
        double cx = Math.Clamp(_selX + _selW / 2.0 + dx, 0, DispW);
        double cy = Math.Clamp(_selY + _selH / 2.0 + dy, 0, DispH);
        _selX = cx - _selW / 2.0;
        _selY = cy - _selH / 2.0;
    }

    private void ResizeSelection(Point p)
    {
        double cx = _selX + _selW / 2.0, cy = _selY + _selH / 2.0;
        double cos = Math.Cos(_angle), sin = Math.Sin(_angle);
        double dx = p.X - cx, dy = p.Y - cy;
        double lx = dx * cos + dy * sin;
        double ly = -dx * sin + dy * cos;

        double L = -_selW / 2.0, R = _selW / 2.0, T = -_selH / 2.0, B = _selH / 2.0;

        switch (_op)
        {
            case SelectionOp.ResizeL: L = Math.Min(lx, R - 2); break;
            case SelectionOp.ResizeR: R = Math.Max(lx, L + 2); break;
            case SelectionOp.ResizeT: T = Math.Min(ly, B - 2); break;
            case SelectionOp.ResizeB: B = Math.Max(ly, T + 2); break;
            case SelectionOp.ResizeTL: L = Math.Min(lx, R - 2); T = Math.Min(ly, B - 2); break;
            case SelectionOp.ResizeTR: R = Math.Max(lx, L + 2); T = Math.Min(ly, B - 2); break;
            case SelectionOp.ResizeBL: L = Math.Min(lx, R - 2); B = Math.Max(ly, T + 2); break;
            case SelectionOp.ResizeBR: R = Math.Max(lx, L + 2); B = Math.Max(ly, T + 2); break;
            default: return;
        }

        double newW = Math.Max(2, R - L);
        double newH = Math.Max(2, B - T);
        double mx = (L + R) / 2.0, my = (T + B) / 2.0;
        double ncx = cx + mx * cos - my * sin;
        double ncy = cy + mx * sin + my * cos;

        _selX = ncx - newW / 2.0;
        _selY = ncy - newH / 2.0;
        _selW = newW;
        _selH = newH;
    }

    private SelectionOp HitTest(Point p)
    {
        const double th = HandleThreshold;
        double cx = _selX + _selW / 2.0, cy = _selY + _selH / 2.0;
        double cos = Math.Cos(_angle), sin = Math.Sin(_angle);
        double dx = p.X - cx, dy = p.Y - cy;
        double lx = dx * cos + dy * sin;
        double ly = -dx * sin + dy * cos;
        double hl = _selW / 2.0, hh = _selH / 2.0;

        bool nearL = Math.Abs(lx + hl) <= th;
        bool nearR = Math.Abs(lx - hl) <= th;
        bool nearT = Math.Abs(ly + hh) <= th;
        bool nearB = Math.Abs(ly - hh) <= th;

        if (nearL && nearT) return SelectionOp.ResizeTL;
        if (nearR && nearT) return SelectionOp.ResizeTR;
        if (nearL && nearB) return SelectionOp.ResizeBL;
        if (nearR && nearB) return SelectionOp.ResizeBR;
        if (nearL) return SelectionOp.ResizeL;
        if (nearR) return SelectionOp.ResizeR;
        if (nearT) return SelectionOp.ResizeT;
        if (nearB) return SelectionOp.ResizeB;

        if (Math.Abs(lx) <= hl && Math.Abs(ly) <= hh) return SelectionOp.Move;
        return SelectionOp.New;
    }

    private const double HandleThreshold = 10;

    private bool NearHandle(Point p)
    {
        double cx = _selX + _selW / 2.0, cy = _selY + _selH / 2.0;
        Point hp = GetHandlePos(cx, cy, _selH / 2.0 + 14);
        return (p - hp).Length <= 12;
    }

    private void UpdateSelectionDisplay()
    {
        if (_selW <= 0 || _selH <= 0)
        {
            SelRect.Visibility = Visibility.Collapsed;
            RotateHandle.Visibility = Visibility.Collapsed;
            return;
        }

        SelRect.Visibility = Visibility.Visible;
        Canvas.SetLeft(SelRect, _selX);
        Canvas.SetTop(SelRect, _selY);
        SelRect.Width = _selW;
        SelRect.Height = _selH;
        SelRect.RenderTransform = new RotateTransform(_angle * 180.0 / Math.PI, _selW / 2.0, _selH / 2.0);

        double cx = _selX + _selW / 2.0, cy = _selY + _selH / 2.0;
        double upLen = _selH / 2.0 + 14;
        Point hp = GetHandlePos(cx, cy, upLen);
        RotateHandle.Visibility = Visibility.Visible;
        Canvas.SetLeft(RotateHandle, hp.X - RotateHandle.Width / 2.0);
        Canvas.SetTop(RotateHandle, hp.Y - RotateHandle.Height / 2.0);
    }

    private Point GetHandlePos(double cx, double cy, double upLen)
        => new(cx + Math.Sin(_angle) * upLen, cy - Math.Cos(_angle) * upLen);

    private bool _hasSelection => _selW > 0 && _selH > 0 && SelRect.Visibility == Visibility.Visible;

    private (double Cx, double Cy, double W, double H, double Angle)? GetSelectionImage()
    {
        if (!_hasSelection || _working == null || DispW < 1) return null;

        double sx = _working.PixelWidth / DispW;
        double sy = _working.PixelHeight / DispH;

        double cx = (_selX + _selW / 2.0) * sx;
        double cy = (_selY + _selH / 2.0) * sy;
        double w = _selW * sx;
        double h = _selH * sy;
        return (cx, cy, w, h, _angle);
    }

    private void ClearSelection()
    {
        _selW = _selH = 0;
        _angle = 0;
        UpdateSelectionDisplay();
    }

    private Int32Rect? GetSelectionRect()
    {
        var sel = GetSelectionImage();
        if (sel == null || _working == null) return null;
        var s = sel.Value;
        int iw = _working.PixelWidth, ih = _working.PixelHeight;

        if (Math.Abs(s.Angle) < 1e-3)
        {
            int x = (int)Math.Round(s.Cx - s.W / 2.0);
            int y = (int)Math.Round(s.Cy - s.H / 2.0);
            return ClampRect(x, y, (int)Math.Round(s.W), (int)Math.Round(s.H), iw, ih);
        }

        double hw = s.W / 2.0, hh = s.H / 2.0;
        double cos = Math.Cos(s.Angle), sin = Math.Sin(s.Angle);
        int minX = (int)Math.Floor(s.Cx - (hw * cos + hh * sin));
        int minY = (int)Math.Floor(s.Cy - (hw * sin + hh * cos));
        int maxX = (int)Math.Ceiling(s.Cx + (hw * cos + hh * sin));
        int maxY = (int)Math.Ceiling(s.Cy + (hw * sin + hh * cos));
        return ClampRect(minX, minY, maxX - minX, maxY - minY, iw, ih);
    }

    private static Int32Rect ClampRect(int x, int y, int w, int h, int iw, int ih)
    {
        x = Math.Clamp(x, 0, iw - 1);
        y = Math.Clamp(y, 0, ih - 1);
        w = Math.Clamp(w, 1, iw - x);
        h = Math.Clamp(h, 1, ih - y);
        return new Int32Rect(x, y, w, h);
    }

    // ---------- 任意リサイズモード ----------

    private void ResizeMode_Changed(object sender, RoutedEventArgs e)
    {
        ClearSelection();
        if (ResizeModeToggle.IsChecked == true)
        {
            _resizeMode = true;
            _rzDragging = false;
            _rzW = PreviewImage.Width;
            _rzH = PreviewImage.Height;
            ResizeApplyBtn.IsEnabled = true;
            UpdateResizeFrame();
        }
        else
        {
            _resizeMode = false;
            _rzDragging = false;
            ResizeFrame.Visibility = Visibility.Collapsed;
            ResizeApplyBtn.IsEnabled = false;
        }
    }

    private int TargetResizeW
        => (int)Math.Round(_rzW * _working.PixelWidth / Math.Max(1, PreviewImage.Width));

    private int TargetResizeH
        => (int)Math.Round(_rzH * _working.PixelHeight / Math.Max(1, PreviewImage.Height));

    private void UpdateResizeFrame()
    {
        ResizeFrame.Visibility = Visibility.Visible;
        ResizeFrame.Width = _rzW;
        ResizeFrame.Height = _rzH;
        StatusText.Text = $"リサイズ: {TargetResizeW} x {TargetResizeH}px";
    }

    private SelectionOp ResizeFrameHitTest(Point p)
    {
        const double th = 10;
        bool nearR = Math.Abs(p.X - _rzW) <= th;
        bool nearB = Math.Abs(p.Y - _rzH) <= th;
        if (nearR && nearB) return SelectionOp.ResizeBR;
        if (nearR) return SelectionOp.ResizeR;
        if (nearB) return SelectionOp.ResizeB;
        if (p.X >= 0 && p.X <= _rzW && p.Y >= 0 && p.Y <= _rzH)
            return SelectionOp.ResizeBR;
        return SelectionOp.None;
    }

    private void ResizeFrameDrag(Point p, SelectionOp op)
    {
        double imgAspect = (double)_working.PixelWidth / _working.PixelHeight;
        bool lockAspect = KeepRatio.IsChecked == true;
        double newW = _rzW, newH = _rzH;

        bool widthOp = op is SelectionOp.ResizeR or SelectionOp.ResizeBR;
        bool heightOp = op is SelectionOp.ResizeB or SelectionOp.ResizeBR;
        if (widthOp) newW = Math.Max(2, p.X);
        if (heightOp) newH = Math.Max(2, p.Y);

        if (lockAspect)
        {
            if (widthOp) newH = newW / imgAspect;
            else if (heightOp) newW = newH * imgAspect;
        }

        _rzW = newW;
        _rzH = newH;
        UpdateResizeFrame();
    }

    // ---------- 色調の確定（フル解像度へ焼き込み） ----------

    private void ResetColorControls()
    {
        Brightness.Value = 0; Contrast.Value = 0; Saturation.Value = 0; Hue.Value = 0;
    }

    private async Task EnsureColorCommittedAsync()
    {
        double b = Brightness.Value, c = Contrast.Value, s = Saturation.Value, h = Hue.Value;
        if (Math.Abs(b) < 0.5 && Math.Abs(c) < 0.5 && Math.Abs(s) < 0.5 && Math.Abs(h) < 0.5) return;

        var src = _working;
        ShowBusy(true);
        BitmapSource? res = null;
        try { res = await System.Threading.Tasks.Task.Run(() => ImageEditor.AdjustColor(src, b, c, s, h)); }
        catch { res = null; }
        ShowBusy(false);

        if (res == null)
        {
            StatusText.Text = "色調の適用に失敗しました。";
            return;
        }

        _undo = _working;
        UndoBtn.IsEnabled = true;
        _working = res;
        ResetColorControls();
        BuildPreview();
        RefreshPreview();
        StatusText.Text = "色調調整を反映しました。";
    }

    // ---------- 編集適用（バックグラウンド） ----------

    private void ShowBusy(bool on) => BusyOverlay.Visibility = on ? Visibility.Visible : Visibility.Collapsed;

    private async void ApplyEdit(Func<BitmapSource, BitmapSource> op, string message)
    {
        if (_working == null) return;
        ShowBusy(true);
        StatusText.Text = message + "...";
        var src = _working;
        BitmapSource? result = null;
        try
        {
            result = await System.Threading.Tasks.Task.Run(() =>
            {
                try { return op(src); }
                catch { return null; }
            });
        }
        catch { result = null; }
        ShowBusy(false);

        if (result == null)
        {
            StatusText.Text = "処理に失敗しました。";
            return;
        }

        _undo = _working;
        UndoBtn.IsEnabled = true;
        _working = result;
        BuildPreview();
        RefreshPreview();
        ResetView();
        StatusText.Text = message + "を適用しました。";
    }

    private void ApplyColor_Click(object sender, RoutedEventArgs e)
    {
        if (!ColorActive())
        {
            StatusText.Text = "色調を変更していません。";
            return;
        }
        _ = EnsureColorCommittedAsync();
    }

    private async void Grayscale_Click(object sender, RoutedEventArgs e)
    {
        await EnsureColorCommittedAsync();
        ApplyEdit(ImageEditor.Grayscale, "グレースケール");
    }

    private async void Sepia_Click(object sender, RoutedEventArgs e)
    {
        await EnsureColorCommittedAsync();
        ApplyEdit(ImageEditor.Sepia, "セピア");
    }

    private async void Invert_Click(object sender, RoutedEventArgs e)
    {
        await EnsureColorCommittedAsync();
        ApplyEdit(ImageEditor.Invert, "反転");
    }

    private async void ResizeApply_Click(object sender, RoutedEventArgs e)
    {
        if (!_resizeMode || _working == null) return;
        int tw = Math.Clamp(TargetResizeW, 1, 32768);
        int th = Math.Clamp(TargetResizeH, 1, 32768);
        await EnsureColorCommittedAsync();
        ApplyEdit(img => ImageEditor.Resize(img, tw, th), "リサイズ");
        ResizeModeToggle.IsChecked = false; // モード終了
    }

    private static int ClampResize(int v) => Math.Clamp(v, 1, 32768);

    private async void Rotate90Left_Click(object sender, RoutedEventArgs e)
    {
        await EnsureColorCommittedAsync();
        ApplyEdit(ImageEditor.Rotate90Ccw, "回転");
        ClearSelection();
    }

    private async void Rotate90Right_Click(object sender, RoutedEventArgs e)
    {
        await EnsureColorCommittedAsync();
        ApplyEdit(ImageEditor.Rotate90Cw, "回転");
        ClearSelection();
    }

    private async void FlipH_Click(object sender, RoutedEventArgs e)
    {
        await EnsureColorCommittedAsync();
        ApplyEdit(ImageEditor.FlipHorizontal, "左右反転");
        ClearSelection();
    }

    private async void FlipV_Click(object sender, RoutedEventArgs e)
    {
        await EnsureColorCommittedAsync();
        ApplyEdit(ImageEditor.FlipVertical, "上下反転");
        ClearSelection();
    }

    private async void RotateFree_Click(object sender, RoutedEventArgs e)
    {
        if (!double.TryParse(RotateAngle.Text, out double deg))
        {
            StatusText.Text = "角度に数値を入力してください。";
            return;
        }
        deg %= 360.0;
        if (Math.Abs(deg) < 0.01)
        {
            StatusText.Text = "回転角度が0です。";
            return;
        }
        await EnsureColorCommittedAsync();
        double d = deg;
        ApplyEdit(img => ImageEditor.Rotate(img, d), "回転");
        ClearSelection();
    }

    private async void Crop_Click(object sender, RoutedEventArgs e)
    {
        await EnsureColorCommittedAsync();
        var sel = GetSelectionImage();
        if (sel == null)
        {
            StatusText.Text = "まず画像上で範囲を選択してください。";
            return;
        }
        var s = sel.Value;
        ApplyEdit(img => ImageEditor.CropRotated(img, s.Cx, s.Cy, s.W, s.H, s.Angle), "トリミング");
        ClearSelection();
    }

    private async void CopySelection_Click(object sender, RoutedEventArgs e)
    {
        if (_working == null) return;
        await EnsureColorCommittedAsync();
        var sel = GetSelectionImage();
        BitmapSource bmp;
        if (sel != null)
        {
            var s = sel.Value;
            // 選択範囲（回転対応）をコピー
            bmp = ImageEditor.CropRotated(_working, s.Cx, s.Cy, s.W, s.H, s.Angle);
        }
        else
        {
            // 選択がなければ作業画像全体をコピー
            bmp = _working;
        }
        try
        {
            Clipboard.SetImage(bmp);
            StatusText.Text = sel != null ? "選択範囲をコピーしました。" : "画像全体をコピーしました。";
        }
        catch (Exception ex)
        {
            StatusText.Text = "コピーに失敗しました。" + ex.Message;
        }
    }

    private async void Mosaic_Click(object sender, RoutedEventArgs e)
    {
        await EnsureColorCommittedAsync();
        var sel = GetSelectionImage();
        if (sel == null)
        {
            StatusText.Text = "まず画像上で範囲を選択してください。";
            return;
        }
        var s = sel.Value;
        int block = (int)Mosaic.Value;
        ApplyEdit(img => ImageEditor.ApplyMosaicRotated(img, s.Cx, s.Cy, s.W, s.H, s.Angle, block), "モザイク");
    }

    private async void Blur_Click(object sender, RoutedEventArgs e)
    {
        await EnsureColorCommittedAsync();
        var sel = GetSelectionRect();
        double sigma = Blur.Value;
        if (sel != null)
        {
            var r = sel.Value;
            ApplyEdit(img => ImageEditor.GaussianBlurRegion(img, r, sigma), "ガウシアンブラー（選択領域 " + r.Width + "x" + r.Height + "）");
        }
        else
        {
            ApplyEdit(img => ImageEditor.GaussianBlur(img, sigma), "ガウシアンブラー（全体）");
        }
        ClearSelection();
    }

    private async void SuperRes_Click(object sender, RoutedEventArgs e)
    {
        if (_working == null) return;
        if (!SuperResolution.IsAvailable)
        {
            StatusText.Text = "超解像モデル（" + SuperResolution.ModelFileName + "）が見つかりません。";
            return;
        }
        await EnsureColorCommittedAsync();
        ApplyEdit(img => SuperResolution.Enhance(img), "超解像");
        ClearSelection();
    }

    private async void SuperResSameSize_Click(object sender, RoutedEventArgs e)
    {
        if (_working == null) return;
        if (!SuperResolution.IsAvailable)
        {
            StatusText.Text = "超解像モデル（" + SuperResolution.ModelFileName + "）が見つかりません。";
            return;
        }
        await EnsureColorCommittedAsync();
        var sel = GetSelectionRect();
        if (sel != null)
        {
            var r = sel.Value;
            ApplyEdit(img => SuperResolution.EnhanceSameSizeRegion(img, r), "同サイズ超解像（選択領域 " + r.Width + "x" + r.Height + "）");
        }
        else
        {
            ApplyEdit(img => SuperResolution.EnhanceSameSize(img), "同サイズ超解像（全体）");
        }
        ClearSelection();
    }

    private async void ApplyImgRotate_Click(object sender, RoutedEventArgs e)
    {
        if (!_imgRotateMode) return;
        double deg = _imgRotDeg % 360.0;
        if (Math.Abs(deg) < 0.01)
        {
            StatusText.Text = "回転角度が0のため適用しません。";
            return;
        }
        await EnsureColorCommittedAsync();
        ApplyEdit(img => ImageEditor.Rotate(img, deg), "画像回転");
        ImageRotateToggle.IsChecked = false;
        StatusText.Text = $"画像を {deg:0.0}° 回転しました。";
    }

    private void Undo_Click(object sender, RoutedEventArgs e)
    {
        if (_undo == null) return;
        _working = _undo;
        _undo = null;
        UndoBtn.IsEnabled = false;
        ClearSelection();
        ResetColorControls();
        BuildPreview();
        RefreshPreview();
        StatusText.Text = "直前の操作を取り消しました。";
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        _working = _original;
        _undo = null;
        UndoBtn.IsEnabled = false;
        ClearSelection();
        ResetColorControls();
        BuildPreview();
        RefreshPreview();
        StatusText.Text = "元の画像に戻しました。";
    }

    // ---------- 保存 ----------

    private readonly (string Label, string Ext, string Filter)[] Formats =
    {
        ("PNG", ".png", "PNG 画像|*.png"),
        ("JPEG", ".jpg", "JPEG 画像|*.jpg;*.jpeg"),
        ("GIF", ".gif", "GIF 画像|*.gif"),
        ("BMP", ".bmp", "BMP 画像|*.bmp"),
        ("TIFF", ".tiff", "TIFF 画像|*.tif;*.tiff"),
        ("WebP", ".webp", "WebP 画像|*.webp"),
    };

    private async void SaveAs_Click(object sender, RoutedEventArgs e)
    {
        if (_working == null) return;
        await EnsureColorCommittedAsync();

        var fmt = Formats[Math.Max(0, Math.Min(Formats.Length - 1, FormatCombo.SelectedIndex))];
        var dlg = new SaveFileDialog
        {
            Title = "画像を保存",
            Filter = fmt.Filter,
            FileName = Path.ChangeExtension(Path.GetFileName(_baseName), fmt.Ext),
            DefaultExt = fmt.Ext,
            AddExtension = true,
            OverwritePrompt = true,
        };
        if (dlg.ShowDialog(this) != true) return;

        RasterizeSave(_working, dlg.FileName, fmt.Ext);
    }

    private void RasterizeSave(BitmapSource src, string path, string ext)
    {
        try
        {
            using var fs = File.Create(path);
            ImageEditor.Encode(src, ext, fs);
            StatusText.Text = "保存しました: " + path;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "保存に失敗しました。\n" + ex.Message, "編集",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete)
        {
            ClearSelection();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
    }
}
