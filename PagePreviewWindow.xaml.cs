using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ScanEzi
{
    /// <summary>
    /// Full-size preview of a scanned page with the crop and rotate tools inside it, so the
    /// main window stays a gallery and the editing happens where the page is actually legible.
    /// Edits are held per page and only written to disk when the window is applied.
    /// </summary>
    public partial class PagePreviewWindow : Wpf.Ui.Controls.FluentWindow
    {
        private readonly IReadOnlyList<ScannedImage> _pages;
        private readonly ImageProcessor _imageProcessor = new();
        private readonly Dictionary<int, PageEdit> _edits = new();

        private int _index;
        private BitmapSource? _baseBitmap;
        private BitmapSource? _displayBitmap;

        private bool _cropMode;
        private bool _dragging;
        private Point _dragStart;
        private Rect _selection;

        public PagePreviewWindow(IReadOnlyList<ScannedImage> pages, int startIndex)
        {
            InitializeComponent();

            _pages = pages;
            _index = Math.Clamp(startIndex, 0, Math.Max(0, pages.Count - 1));

            Loaded += (_, _) => LoadPage();
        }


        /// <summary>The pages that were changed, as (index into the supplied list, new file path).</summary>
        public IReadOnlyList<(int Index, string Path)> AppliedEdits { get; private set; } =
            Array.Empty<(int, string)>();

        private PageEdit CurrentEdit
        {
            get
            {
                if (!_edits.TryGetValue(_index, out var edit))
                {
                    edit = new PageEdit();
                    _edits[_index] = edit;
                }
                return edit;
            }
        }

        private void LoadPage()
        {
            if (_pages.Count == 0) return;

            _baseBitmap = LoadBitmap(_pages[_index].ImagePath);
            _cropMode = false;
            _dragging = false;
            _selection = Rect.Empty;

            RefreshDisplay();
            UpdateChrome();
        }

        private static BitmapSource? LoadBitmap(string path)
        {
            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.UriSource = new Uri(path);
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.EndInit();
                bitmap.Freeze();
                return bitmap;
            }
            catch
            {
                return null;
            }
        }

        private static BitmapSource Rotate(BitmapSource source, int degrees)
        {
            if (degrees == 0) return source;

            var rotated = new TransformedBitmap(source, new RotateTransform(degrees));
            rotated.Freeze();
            return rotated;
        }

        /// <summary>Rebuilds the displayed bitmap from the pending edit and re-fits it to the stage.</summary>
        private void RefreshDisplay()
        {
            if (_baseBitmap == null)
            {
                PreviewImage.Source = null;
                return;
            }

            _displayBitmap = Rotate(_baseBitmap, CurrentEdit.Rotation);
            PreviewImage.Source = _displayBitmap;

            FitToStage();
            RenderSelection();
        }

        private void FitToStage()
        {
            if (_displayBitmap == null) return;

            double availWidth = Math.Max(0, StageHost.ActualWidth - 24);
            double availHeight = Math.Max(0, StageHost.ActualHeight - 24);
            if (availWidth < 1 || availHeight < 1) return;

            double scale = Math.Min(availWidth / _displayBitmap.PixelWidth, availHeight / _displayBitmap.PixelHeight);
            scale = Math.Min(scale, 1.0);

            double width = Math.Max(1, _displayBitmap.PixelWidth * scale);
            double height = Math.Max(1, _displayBitmap.PixelHeight * scale);

            PreviewImage.Width = width;
            PreviewImage.Height = height;
            CropLayer.Width = width;
            CropLayer.Height = height;
        }

        private void StageHost_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            FitToStage();
            RenderSelection();
        }

        // ---- crop selection ----

        private void Stage_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (!_cropMode || CropLayer.Width <= 0) return;

            var point = e.GetPosition(CropLayer);
            if (point.X < 0 || point.Y < 0 || point.X > CropLayer.Width || point.Y > CropLayer.Height)
            {
                return;
            }

            _dragging = true;
            _dragStart = Clamp(point);
            _selection = new Rect(_dragStart, new Size(0, 0));

            StageHost.CaptureMouse();
            RenderSelection();
            e.Handled = true;
        }

        private void Stage_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_dragging) return;

            var point = Clamp(e.GetPosition(CropLayer));
            _selection = new Rect(
                Math.Min(_dragStart.X, point.X),
                Math.Min(_dragStart.Y, point.Y),
                Math.Abs(point.X - _dragStart.X),
                Math.Abs(point.Y - _dragStart.Y));

            RenderSelection();
        }

        private void Stage_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!_dragging) return;

            _dragging = false;
            StageHost.ReleaseMouseCapture();
            CommitSelection();
        }

        private void CommitSelection()
        {
            // Ignore an accidental click that produced no meaningful area.
            if (_selection.Width < 12 || _selection.Height < 12)
            {
                _selection = Rect.Empty;
                CurrentEdit.Crop = null;
            }
            else
            {
                CurrentEdit.Crop = ToPixelRect(_selection);
                _cropMode = false;
            }

            UpdateChrome();
            RenderSelection();
        }

        private void Stage_MouseLeave(object sender, MouseEventArgs e)
        {
            if (!_dragging) return;
            _dragging = false;
            CropLayer.ReleaseMouseCapture();
            CommitSelection();
        }

        private Point Clamp(Point point) => new(
            Math.Clamp(point.X, 0, CropLayer.Width),
            Math.Clamp(point.Y, 0, CropLayer.Height));

        /// <summary>Converts a selection in stage coordinates into a rectangle in the page's own pixels.</summary>
        private Int32Rect ToPixelRect(Rect selection)
        {
            if (_displayBitmap == null || CropLayer.Width <= 0 || CropLayer.Height <= 0)
            {
                return new Int32Rect(0, 0, 0, 0);
            }

            double sx = _displayBitmap.PixelWidth / CropLayer.Width;
            double sy = _displayBitmap.PixelHeight / CropLayer.Height;

            int x = (int)Math.Round(selection.X * sx);
            int y = (int)Math.Round(selection.Y * sy);
            int w = (int)Math.Round(selection.Width * sx);
            int h = (int)Math.Round(selection.Height * sy);

            x = Math.Clamp(x, 0, _displayBitmap.PixelWidth - 1);
            y = Math.Clamp(y, 0, _displayBitmap.PixelHeight - 1);
            w = Math.Clamp(w, 1, _displayBitmap.PixelWidth - x);
            h = Math.Clamp(h, 1, _displayBitmap.PixelHeight - y);

            return new Int32Rect(x, y, w, h);
        }

        private void RenderSelection()
        {
            if (CropLayer.Width <= 0 || CropLayer.Height <= 0)
            {
                return;
            }

            var area = new Rect(0, 0, CropLayer.Width, CropLayer.Height);
            var rect = _selection;

            // Fall back to a crop stored on the edit, so reopening the preview shows it again.
            if (rect.IsEmpty && CurrentEdit.Crop is { } saved && _displayBitmap != null)
            {
                double fx = CropLayer.Width / _displayBitmap.PixelWidth;
                double fy = CropLayer.Height / _displayBitmap.PixelHeight;
                rect = new Rect(saved.X * fx, saved.Y * fy, saved.Width * fx, saved.Height * fy);
            }

            if (rect.IsEmpty || rect.Width < 1 || rect.Height < 1)
            {
                CropDimPath.Data = null;
                CropSelectionRect.Visibility = Visibility.Collapsed;
                CropHandle.Visibility = Visibility.Collapsed;
                return;
            }

            rect.Intersect(area);
            if (rect.IsEmpty)
            {
                CropDimPath.Data = null;
                CropSelectionRect.Visibility = Visibility.Collapsed;
                CropHandle.Visibility = Visibility.Collapsed;
                return;
            }

            // Even-odd fill leaves the selection clear and dims everything around it.
            CropDimPath.Data = new GeometryGroup
            {
                FillRule = FillRule.EvenOdd,
                Children =
                {
                    new RectangleGeometry(area),
                    new RectangleGeometry(rect)
                }
            };

            Canvas.SetLeft(CropSelectionRect, rect.X);
            Canvas.SetTop(CropSelectionRect, rect.Y);
            CropSelectionRect.Width = rect.Width;
            CropSelectionRect.Height = rect.Height;
            CropSelectionRect.Visibility = Visibility.Visible;

            Canvas.SetLeft(CropHandle, rect.Right - 5);
            Canvas.SetTop(CropHandle, rect.Bottom - 5);
            CropHandle.Visibility = _dragging ? Visibility.Visible : Visibility.Collapsed;
        }

        // ---- commands ----

        private void RotateLeftButton_Click(object sender, RoutedEventArgs e) => RotateBy(-90);

        private void RotateRightButton_Click(object sender, RoutedEventArgs e) => RotateBy(90);

        private void RotateBy(int delta)
        {
            if (_baseBitmap == null) return;

            // A crop is measured against the current orientation, so it cannot survive a turn.
            CurrentEdit.Rotation = ((CurrentEdit.Rotation + delta) % 360 + 360) % 360;
            CurrentEdit.Crop = null;
            _selection = Rect.Empty;
            _cropMode = false;

            RefreshDisplay();
            UpdateChrome();
        }

        private void CropModeButton_Click(object sender, RoutedEventArgs e)
        {
            if (_baseBitmap == null) return;

            _cropMode = !_cropMode;
            if (!_cropMode)
            {
                _selection = Rect.Empty;
            }

            UpdateChrome();
            RenderSelection();
        }

        private void ResetButton_Click(object sender, RoutedEventArgs e)
        {
            _edits.Remove(_index);
            _selection = Rect.Empty;
            _cropMode = false;

            RefreshDisplay();
            UpdateChrome();
        }

        private void PrevButton_Click(object sender, RoutedEventArgs e) => Navigate(-1);

        private void NextButton_Click(object sender, RoutedEventArgs e) => Navigate(1);

        private void Navigate(int delta)
        {
            int next = _index + delta;
            if (next < 0 || next >= _pages.Count) return;

            _index = next;
            LoadPage();
        }

        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            switch (e.Key)
            {
                case Key.Escape:
                    CancelButton_Click(sender, e);
                    e.Handled = true;
                    break;

                case Key.Left when Keyboard.Modifiers == ModifierKeys.None:
                    Navigate(-1);
                    e.Handled = true;
                    break;

                case Key.Right when Keyboard.Modifiers == ModifierKeys.None:
                    Navigate(1);
                    e.Handled = true;
                    break;

                case Key.Enter when Keyboard.Modifiers == ModifierKeys.Control:
                    ApplyButton_Click(sender, e);
                    e.Handled = true;
                    break;
            }
        }

        private void UpdateChrome()
        {
            var edit = CurrentEdit;

            PreviewTitleBar.Title = _pages.Count > 0
                ? $"Preview \u00B7 {Path.GetFileName(_pages[_index].ImagePath)}"
                : "Preview";

            PageIndicatorText.Text = $"Page {_index + 1} of {Math.Max(1, _pages.Count)}";
            PrevButton.IsEnabled = _index > 0;
            NextButton.IsEnabled = _index < _pages.Count - 1;

            CropModeButton.Appearance = _cropMode
                ? Wpf.Ui.Controls.ControlAppearance.Primary
                : Wpf.Ui.Controls.ControlAppearance.Secondary;

            EmptyHint.Visibility = _cropMode ? Visibility.Visible : Visibility.Collapsed;
            StageHost.Cursor = _cropMode ? Cursors.Cross : Cursors.Arrow;

            var parts = new List<string>();
            if (edit.Rotation != 0) parts.Add($"Rotated {edit.Rotation}\u00B0");
            if (edit.Crop != null) parts.Add("Cropped");

            int editedPages = _edits.Count(kv => kv.Value.HasChanges);

            EditSummaryText.Text = parts.Count > 0
                ? "This page: " + string.Join(" \u00B7 ", parts) +
                  (editedPages > 1 ? $" \u00B7 {editedPages} pages edited" : string.Empty)
                : editedPages > 1
                    ? $"{editedPages} pages edited"
                    : "No changes yet";
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void ApplyButton_Click(object sender, RoutedEventArgs e)
        {
            var applied = new List<(int, string)>();

            try
            {
                foreach (var pair in _edits.Where(kv => kv.Value.HasChanges).OrderBy(kv => kv.Key))
                {
                    string path = _pages[pair.Key].ImagePath;

                    if (pair.Value.Rotation != 0)
                    {
                        path = _imageProcessor.RotateImage(path, pair.Value.Rotation);
                    }

                    if (pair.Value.Crop is { } crop)
                    {
                        path = _imageProcessor.CropImage(path, crop);
                    }

                    applied.Add((pair.Key, path));
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Could not apply changes",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            AppliedEdits = applied;
            DialogResult = true;
            Close();
        }

        private sealed class PageEdit
        {
            public int Rotation { get; set; }
            public Int32Rect? Crop { get; set; }
            public bool HasChanges => Rotation != 0 || Crop != null;
        }
    }
}
