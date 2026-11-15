using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;
using LandingRecorder.Services;
using LandingRecorder.ViewModels;

namespace LandingRecorder;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly DispatcherTimer _videoTimer;
    // Scrubbing: MediaElement stalls (frame stops updating until Pause/Play) when flooded with a seek per
    // MouseMove, so drag positions are coalesced into _pendingSeek and applied at most once per _seekTimer tick.
    private readonly DispatcherTimer _seekTimer;
    private TimeSpan? _pendingSeek;
    private bool _isScrubbing;
    private bool _wasMutedBeforeScrub;
    private Point? _magnifierDragStart;
    private Rectangle? _magnifierSelectionOutline;
    private Rectangle? _magnifierInset;
    private Point? _magnifierMoveOffset;
    private bool _legendPositioned;
    private Point? _legendDragOffset;
    private LineDrag? _lineDrag;
    private double _graphPlotHeight;

    /// <summary>An in-progress drag of a plot line's handle: the line and its range/mouse Y at drag start.</summary>
    private sealed record LineDrag(PlotLineViewModel Line, double StartY, double StartMin, double StartMax);

    public MainWindow()
    {
        InitializeComponent();

        _viewModel = new MainViewModel(new SimConnectService(), new JoystickInputService(), new VideoCaptureService(), new SettingsService());
        DataContext = _viewModel;
        _viewModel.GraphInvalidated += (_, _) => DrawGraph();
        _viewModel.PropertyChanged += ViewModel_PropertyChanged;
        GraphCanvas.SizeChanged += (_, _) => DrawGraph();
        GraphCanvas.LostMouseCapture += (_, _) =>
        {
            // e.g. Alt+Tab mid-drag: no MouseUp arrives, so end the drag/scrub here or playback stays paused.
            _lineDrag = null;
            EndScrub();
        };

        _videoTimer = new DispatcherTimer(DispatcherPriority.Render, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(16)
        };
        _videoTimer.Tick += VideoTimer_Tick;

        _seekTimer = new DispatcherTimer(DispatcherPriority.Render, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(50)
        };
        _seekTimer.Tick += (_, _) => ApplyPendingSeek();

        Closed += MainWindow_Closed;
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        var windowHandle = new WindowInteropHelper(this).EnsureHandle();
        _viewModel.Initialize(windowHandle);
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainViewModel.IsVideoPlaying):
                if (_viewModel.IsVideoPlaying)
                {
                    if (VideoPlayer.NaturalDuration.HasTimeSpan &&
                        VideoPlayer.Position >= VideoPlayer.NaturalDuration.TimeSpan - TimeSpan.FromMilliseconds(200))
                    {
                        VideoPlayer.Position = TimeSpan.Zero;
                        _viewModel.SelectNearestPointByTime(0);
                    }
                    VideoPlayer.Play();
                    _videoTimer.Start();
                }
                else
                {
                    VideoPlayer.Pause();
                    _videoTimer.Stop();
                }
                break;

            case nameof(MainViewModel.VideoSource):
                if (_viewModel.VideoSource is null)
                {
                    VideoPlayer.Stop();
                    _videoTimer.Stop();
                }
                ClearMagnifier();
                break;
        }
    }

    private void VideoPlayer_MediaOpened(object sender, RoutedEventArgs e)
    {
        VideoPlayer.Position = TimeSpan.Zero;
        if (_viewModel.IsVideoPlaying)
        {
            VideoPlayer.Play();
            _videoTimer.Start();
        }
        else
        {
            // LoadedBehavior="Manual" means MediaElement never decodes/displays a frame until
            // Play() is called at least once - Play() then immediately Pause() renders frame
            // zero without actually starting playback, so a freshly opened video shows its
            // first frame instead of a blank player.
            VideoPlayer.Play();
            VideoPlayer.Pause();
        }
        DrawGraph();
    }

    private void VideoPlayer_MediaEnded(object sender, RoutedEventArgs e)
    {
        _viewModel.PauseVideoCommand.Execute(null);
        DrawGraph();
    }

    private void VideoTimer_Tick(object? sender, EventArgs e)
    {
        if (VideoPlayer.Source is null || _viewModel.Points.Count == 0)
        {
            return;
        }

        _viewModel.SelectNearestPointByTime(VideoPlayer.Position.TotalSeconds);
    }

    private void MainWindow_Closed(object? sender, EventArgs e)
    {
        _videoTimer.Stop();
        _seekTimer.Stop();
        _viewModel.Dispose();
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*",
            DefaultExt = "json",
            FileName = "pitch-data.json",
            Title = "Save pitch data"
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            _viewModel.SavePoints(dialog.FileName);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Failed to save file: {ex.Message}", "Save Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        var settingsWindow = new SettingsWindow(_viewModel.CreateSettingsEditor()) { Owner = this };
        if (settingsWindow.ShowDialog() != true || settingsWindow.DataContext is not SettingsViewModel editor)
        {
            return;
        }

        try
        {
            _viewModel.ApplyAndSaveSettings(editor.ToSettings());
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Failed to save settings: {ex.Message}", "Settings Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void LoadButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*",
            DefaultExt = "json",
            Title = "Load pitch data"
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            _viewModel.LoadPoints(dialog.FileName);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Failed to load file: {ex.Message}", "Load Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void PlotLineRange_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: PlotLineViewModel line })
        {
            _viewModel.ZoomPlotLine(line, e.Delta);
            e.Handled = true;
        }
    }

    private void GraphCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_viewModel.Points.Count == 0)
        {
            return;
        }
        BeginScrub();
        UpdateGraphSelection(e.GetPosition(GraphCanvas).X);
        GraphCanvas.CaptureMouse();
        e.Handled = true;
    }

    private void GraphCanvas_MouseMove(object sender, MouseEventArgs e)
    {
        if (_lineDrag is not null && GraphCanvas.IsMouseCaptured && e.LeftButton == MouseButtonState.Pressed)
        {
            UpdateLineDrag(e.GetPosition(GraphCanvas).Y);
        }
        else if (GraphCanvas.IsMouseCaptured && e.LeftButton == MouseButtonState.Pressed)
        {
            UpdateGraphSelection(e.GetPosition(GraphCanvas).X);
        }
        else if (GraphCanvas.IsMouseCaptured && e.RightButton == MouseButtonState.Pressed
                 && _magnifierDragStart is Point dragStart && _magnifierSelectionOutline is not null)
        {
            var rect = new Rect(dragStart, e.GetPosition(GraphCanvas));
            Canvas.SetLeft(_magnifierSelectionOutline, rect.X);
            Canvas.SetTop(_magnifierSelectionOutline, rect.Y);
            _magnifierSelectionOutline.Width = rect.Width;
            _magnifierSelectionOutline.Height = rect.Height;
        }
    }

    private void GraphCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_lineDrag is not null)
        {
            _lineDrag = null;
            GraphCanvas.ReleaseMouseCapture();
            e.Handled = true;
            DrawGraph(); // Restore the handle's normal outline.
            return; // A handle drag never scrubbed the video, so there's no playback to resume.
        }

        EndScrub();
        if (GraphCanvas.IsMouseCaptured)
        {
            GraphCanvas.ReleaseMouseCapture();
            e.Handled = true;
        }
    }

    private void BeginScrub()
    {
        if (VideoPlayer.Source is null)
        {
            return;
        }

        _isScrubbing = true;
        _videoTimer.Stop(); // Otherwise it keeps moving the selection to the (paused) video position mid-drag.
        VideoPlayer.Pause();
        // ScrubbingEnabled plays a short audio snippet on every seek, which sounds like chopping while dragging.
        _wasMutedBeforeScrub = VideoPlayer.IsMuted;
        VideoPlayer.IsMuted = true;
        _seekTimer.Start();
    }

    /// <summary>Applies the final drag position and resumes playback if it was playing. Safe to call twice.</summary>
    private void EndScrub()
    {
        if (!_isScrubbing)
        {
            return;
        }

        _isScrubbing = false;
        _seekTimer.Stop();
        ApplyPendingSeek();
        VideoPlayer.IsMuted = _wasMutedBeforeScrub;
        if (VideoPlayer.Source is not null && _viewModel.IsVideoPlaying)
        {
            VideoPlayer.Play();
            _videoTimer.Start();
        }
    }

    private void ApplyPendingSeek()
    {
        if (_pendingSeek is TimeSpan target && VideoPlayer.Source is not null)
        {
            VideoPlayer.Position = target;
        }
        _pendingSeek = null;
    }

    private void GraphCanvas_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        _magnifierDragStart = e.GetPosition(GraphCanvas);
        _magnifierSelectionOutline = new Rectangle
        {
            Stroke = Brushes.Yellow,
            StrokeThickness = 1.5,
            StrokeDashArray = new DoubleCollection { 4, 3 },
            Fill = new SolidColorBrush(Color.FromArgb(40, 255, 255, 0))
        };
        Canvas.SetLeft(_magnifierSelectionOutline, _magnifierDragStart.Value.X);
        Canvas.SetTop(_magnifierSelectionOutline, _magnifierDragStart.Value.Y);
        MagnifierCanvas.Children.Add(_magnifierSelectionOutline);
        GraphCanvas.CaptureMouse();
        e.Handled = true;
    }

    private void GraphCanvas_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_magnifierDragStart is not Point dragStart)
        {
            return;
        }
        _magnifierDragStart = null;

        var selection = new Rect(dragStart, e.GetPosition(GraphCanvas));

        if (_magnifierSelectionOutline is not null)
        {
            MagnifierCanvas.Children.Remove(_magnifierSelectionOutline);
            _magnifierSelectionOutline = null;
        }
        if (GraphCanvas.IsMouseCaptured)
        {
            GraphCanvas.ReleaseMouseCapture();
        }
        e.Handled = true;

        // A plain right-click (no meaningful drag) clears any existing magnifier instead of
        // drawing a degenerate zero-size one.
        const double minDragSize = 8.0;
        if (selection.Width < minDragSize || selection.Height < minDragSize)
        {
            ClearMagnifier();
            return;
        }

        ShowMagnifier(selection);
    }

    private void ShowMagnifier(Rect selection)
    {
        var canvasWidth = GraphCanvas.ActualWidth;
        var canvasHeight = GraphCanvas.ActualHeight;
        if (canvasWidth <= 0 || canvasHeight <= 0)
        {
            return;
        }

        const double scale = 2.0;
        var insetWidth = Math.Min(canvasWidth, selection.Width * scale);
        var insetHeight = Math.Min(canvasHeight, selection.Height * scale);
        var left = Math.Clamp(selection.X - (insetWidth - selection.Width) / 2, 0, Math.Max(0, canvasWidth - insetWidth));
        var top = Math.Clamp(selection.Y - (insetHeight - selection.Height) / 2, 0, Math.Max(0, canvasHeight - insetHeight));

        ClearMagnifier();

        _magnifierInset = new Rectangle
        {
            Width = insetWidth,
            Height = insetHeight,
            Stroke = Brushes.Yellow,
            StrokeThickness = 2,
            // Absolute units take the Viewbox directly in VideoPlayer's own local coordinate
            // space (the same space GraphCanvas's selection was measured in, since the two
            // controls are coincident siblings) - RelativeToBoundingBox was tried here first,
            // but MediaElement's "bounding box" for Stretch="Uniform" content didn't match
            // either the full control or the letterboxed sub-rect cleanly enough to be worth
            // the inference; Absolute sidesteps that ambiguity entirely.
            Fill = new VisualBrush(VideoPlayer)
            {
                Viewbox = selection,
                ViewboxUnits = BrushMappingMode.Absolute,
                Stretch = Stretch.Fill
            }
        };
        Canvas.SetLeft(_magnifierInset, left);
        Canvas.SetTop(_magnifierInset, top);
        _magnifierInset.Cursor = Cursors.SizeAll;
        _magnifierInset.MouseLeftButtonDown += MagnifierInset_MouseLeftButtonDown;
        _magnifierInset.MouseMove += MagnifierInset_MouseMove;
        _magnifierInset.MouseLeftButtonUp += MagnifierInset_MouseLeftButtonUp;
        _magnifierInset.MouseRightButtonDown += MagnifierInset_MouseRightButtonDown;
        MagnifierCanvas.Children.Add(_magnifierInset);
    }

    private void ClearMagnifier()
    {
        if (_magnifierInset is not null)
        {
            MagnifierCanvas.Children.Remove(_magnifierInset);
            _magnifierInset = null;
        }
        _magnifierMoveOffset = null;
    }

    private void MagnifierInset_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var inset = (Rectangle)sender;
        var mousePos = e.GetPosition(MagnifierCanvas);
        _magnifierMoveOffset = new Point(mousePos.X - Canvas.GetLeft(inset), mousePos.Y - Canvas.GetTop(inset));
        inset.CaptureMouse();
        e.Handled = true;
    }

    private void MagnifierInset_MouseMove(object sender, MouseEventArgs e)
    {
        if (sender is not Rectangle inset || !inset.IsMouseCaptured || _magnifierMoveOffset is not Point offset)
        {
            return;
        }

        var mousePos = e.GetPosition(MagnifierCanvas);
        var canvasWidth = GraphCanvas.ActualWidth;
        var canvasHeight = GraphCanvas.ActualHeight;

        var left = Math.Clamp(mousePos.X - offset.X, 0, Math.Max(0, canvasWidth - inset.Width));
        var top = Math.Clamp(mousePos.Y - offset.Y, 0, Math.Max(0, canvasHeight - inset.Height));
        Canvas.SetLeft(inset, left);
        Canvas.SetTop(inset, top);
    }

    private void MagnifierInset_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        var inset = (Rectangle)sender;
        if (inset.IsMouseCaptured)
        {
            inset.ReleaseMouseCapture();
        }
        _magnifierMoveOffset = null;
        e.Handled = true;
    }

    private void MagnifierInset_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        ClearMagnifier();
        e.Handled = true;
    }

    private void LegendPanel_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        // Positions the panel near its old fixed top-right spot exactly once, the first time
        // both it and GraphCanvas have a real size - afterward, dragging (or collapsing, which
        // also raises SizeChanged) must never snap it back here.
        if (_legendPositioned || LegendPanel.ActualWidth <= 0 || GraphCanvas.ActualWidth <= 0)
        {
            return;
        }
        _legendPositioned = true;
        LegendPanel.Margin = new Thickness(Math.Max(0, GraphCanvas.ActualWidth - LegendPanel.ActualWidth - 10), 10, 0, 0);
    }

    private void LegendCaption_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var captionBar = (UIElement)sender;
        var mousePos = e.GetPosition(GraphCanvas);
        _legendDragOffset = new Point(mousePos.X - LegendPanel.Margin.Left, mousePos.Y - LegendPanel.Margin.Top);
        captionBar.CaptureMouse();
        e.Handled = true;
    }

    private void LegendCaption_MouseMove(object sender, MouseEventArgs e)
    {
        if (sender is not UIElement captionBar || !captionBar.IsMouseCaptured || _legendDragOffset is not Point offset)
        {
            return;
        }

        var mousePos = e.GetPosition(GraphCanvas);
        var canvasWidth = GraphCanvas.ActualWidth;
        var canvasHeight = GraphCanvas.ActualHeight;

        var left = Math.Clamp(mousePos.X - offset.X, 0, Math.Max(0, canvasWidth - LegendPanel.ActualWidth));
        var top = Math.Clamp(mousePos.Y - offset.Y, 0, Math.Max(0, canvasHeight - LegendPanel.ActualHeight));
        LegendPanel.Margin = new Thickness(left, top, 0, 0);
    }

    private void LegendCaption_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        var captionBar = (UIElement)sender;
        if (captionBar.IsMouseCaptured)
        {
            captionBar.ReleaseMouseCapture();
        }
        _legendDragOffset = null;
        e.Handled = true;
    }

    private void LegendCollapseToggle_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var collapsing = LegendBody.Visibility == Visibility.Visible;
        LegendBody.Visibility = collapsing ? Visibility.Collapsed : Visibility.Visible;
        LegendCollapseGlyph.Text = collapsing ? "▶" : "▼";
        e.Handled = true;
    }

    private void UpdateGraphSelection(double x)
    {
        const double margin = 36.0;
        var plotWidth = GraphCanvas.ActualWidth - margin * 2;
        if (plotWidth <= 0 || x < margin || x > GraphCanvas.ActualWidth - margin)
        {
            _viewModel.ClearSelection();
            return;
        }

        var xAxisMax = Math.Max(1, _viewModel.Points.Max(point => point.TimeSeconds));
        var selectedTime = Math.Clamp((x - margin) / plotWidth * xAxisMax, 0, xAxisMax);
        _viewModel.SelectNearestPointByTime(selectedTime);

        if (VideoPlayer.Source is not null)
        {
            _pendingSeek = TimeSpan.FromSeconds(selectedTime);
            if (!_isScrubbing)
            {
                ApplyPendingSeek();
            }
        }
    }

    private void DrawGraph()
    {
        var width = GraphCanvas.ActualWidth;
        var height = GraphCanvas.ActualHeight;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        var points = _viewModel.Points;

        GraphCanvas.Children.Clear();

        var margin = 36.0;
        var plotWidth = width - margin * 2;
        var plotHeight = height - margin * 2;
        var halfHeight = plotHeight / 2;
        var zeroY = margin + halfHeight;
        var xAxisMax = Math.Max(1, points.Count > 0 ? points.Max(point => point.TimeSeconds) : 1);

        const int horizontalGridlineDivisions = 4;
        for (var i = 1; i <= horizontalGridlineDivisions; i++)
        {
            var offset = (i / (double)horizontalGridlineDivisions) * halfHeight;
            AddLine(margin, zeroY - offset, width - margin, zeroY - offset, Brushes.White, 0.10);
            AddLine(margin, zeroY + offset, width - margin, zeroY + offset, Brushes.White, 0.10);
        }

        AddLine(margin, zeroY, width - margin, zeroY, Brushes.Aqua, 0.35);

        for (var tick = 0; tick <= 5; tick++)
        {
            var x = margin + (tick / 5.0) * plotWidth;
            AddLine(x, margin, x, height - margin, Brushes.White, 0.22);
            AddText(x - 10, height - margin + 16, $"{xAxisMax * tick / 5.0:F1}s", 12);
        }

        AddText(width / 2 - 20, height - 8, "Time", 12);

        if (_viewModel.TouchdownVisible)
        {
            foreach (var point in points)
            {
                if (point.TouchdownSim)
                {
                    var markerX = margin + (point.TimeSeconds / xAxisMax) * plotWidth;
                    AddDashedLine(markerX, margin, markerX, height - margin, Brushes.Yellow);
                }
            }
        }

        // Each line maps its own [Min, Max] display range onto the full plot height; values are clamped
        // so an out-of-range spike doesn't draw off-canvas. Points where a variable is missing or the
        // formula result isn't finite are skipped. Lines whose variables aren't in the data aren't drawn at all.
        double ToY(PlotLineViewModel line, double value)
        {
            var span = line.Max - line.Min;
            return height - margin - ((Math.Clamp(value, line.Min, line.Max) - line.Min) / span) * plotHeight;
        }

        var drawnLines = _viewModel.PlotLines.Where(line => line.ShouldDraw).ToList();
        var lineHandles = new List<(PlotLineViewModel Line, Point Start, double StartValue)>();
        _graphPlotHeight = plotHeight;

        if (points.Count > 1)
        {
            foreach (var line in drawnLines)
            {
                var polylinePoints = new PointCollection(points.Count);
                double? startValue = null;
                foreach (var point in points)
                {
                    if (line.Evaluate(point) is double value)
                    {
                        startValue ??= value;
                        polylinePoints.Add(new Point(margin + (point.TimeSeconds / xAxisMax) * plotWidth, ToY(line, value)));
                    }
                }

                if (polylinePoints.Count > 1)
                {
                    polylinePoints.Freeze();
                    GraphCanvas.Children.Add(new Polyline
                    {
                        Stroke = CreateBrush(line.Color),
                        StrokeThickness = 2,
                        Points = polylinePoints,
                    });
                    lineHandles.Add((line, polylinePoints[0], startValue!.Value));
                }
            }
        }

        // Handles are added after all polylines so they stay clickable on top of every line.
        foreach (var (line, start, startValue) in lineHandles)
        {
            AddLineHandle(line, start, startValue);
        }

        if (_viewModel.SelectedPointIndex is int selectedIndex && selectedIndex < points.Count)
        {
            var selectedPoint = points[selectedIndex];
            var selectedX = margin + (selectedPoint.TimeSeconds / xAxisMax) * plotWidth;
            AddLine(selectedX, margin, selectedX, height - margin, Brushes.White, 0.9);
            AddValueLabel(selectedX + 6, height - margin - 20, $"{selectedPoint.TimeSeconds:F2}s", Colors.White);

            foreach (var line in drawnLines)
            {
                if (line.Evaluate(selectedPoint) is double value)
                {
                    AddValueLabel(selectedX + 6, ToY(line, value) - 9, line.FormatValue(value), CreateBrush(line.Color).Color);
                }
            }
        }
    }

    /// <summary>
    /// A dot at the start of a plot line: drag it vertically to pan the line's range, right-click it to reset the
    /// range to the configured default, scroll over it to zoom
    /// around the dot's own value, so the dot stays put under the mouse (the legend's scroll instead follows the
    /// line's scale mode, which would make the dot run away). The graph is rebuilt on every tick, so the dot
    /// is recreated constantly — drag state therefore lives in <see cref="_lineDrag"/> and the mouse is captured
    /// by <see cref="GraphCanvas"/> (which persists), not by the dot itself.
    /// </summary>
    private void AddLineHandle(PlotLineViewModel line, Point start, double startValue)
    {
        const double size = 11;
        var handle = new Ellipse
        {
            Width = size,
            Height = size,
            Fill = CreateBrush(line.Color),
            Stroke = Brushes.White,
            StrokeThickness = _lineDrag?.Line == line ? 2 : 1,
            Cursor = Cursors.SizeNS,
            ToolTip = $"{line.ShortName}: drag to move, scroll to scale, right-click to reset",
        };
        Canvas.SetLeft(handle, start.X - size / 2);
        Canvas.SetTop(handle, start.Y - size / 2);

        handle.MouseLeftButtonDown += (_, e) =>
        {
            _lineDrag = new LineDrag(line, e.GetPosition(GraphCanvas).Y, line.Min, line.Max);
            GraphCanvas.CaptureMouse();
            e.Handled = true; // Don't start scrubbing.
        };
        // Handled so the canvas doesn't also start a magnifier selection (its right-button-up handler is a
        // no-op when no selection was started).
        handle.MouseRightButtonDown += (_, e) =>
        {
            _viewModel.ResetPlotLineRange(line);
            e.Handled = true;
        };
        handle.MouseWheel += (_, e) =>
        {
            // Anchor at the value the dot is drawn at (clamped into range, like its on-screen position).
            _viewModel.ZoomPlotLineAround(line, e.Delta, Math.Clamp(startValue, line.Min, line.Max));
            e.Handled = true;
        };

        GraphCanvas.Children.Add(handle);
    }

    private void UpdateLineDrag(double mouseY)
    {
        if (_lineDrag is not { } drag || _graphPlotHeight <= 0)
        {
            return;
        }

        // Shift the range by the dragged distance in value units, so the line follows the mouse
        // (dragging down raises min/max, which moves the line down). Computed from the range at drag start
        // to avoid accumulating drift.
        var shift = (mouseY - drag.StartY) / _graphPlotHeight * (drag.StartMax - drag.StartMin);
        _viewModel.SetPlotLineRange(drag.Line, drag.StartMin + shift, drag.StartMax + shift);
    }

    private static SolidColorBrush CreateBrush(string hexColor)
    {
        try
        {
            if (ColorConverter.ConvertFromString(hexColor) is Color color)
            {
                return new SolidColorBrush(color);
            }
        }
        catch (FormatException)
        {
            // Settings validation normally prevents this; fall back to white for a hand-edited settings.json.
        }

        return new SolidColorBrush(Colors.White);
    }

    private void AddLine(double x1, double y1, double x2, double y2, Brush brush, double opacity)
    {
        var line = new Line
        {
            X1 = x1,
            Y1 = y1,
            X2 = x2,
            Y2 = y2,
            Stroke = new SolidColorBrush(((SolidColorBrush)brush).Color) { Opacity = opacity },
            StrokeThickness = 1
        };

        GraphCanvas.Children.Add(line);
    }

    private void AddDashedLine(double x1, double y1, double x2, double y2, Brush brush)
    {
        var line = new Line
        {
            X1 = x1,
            Y1 = y1,
            X2 = x2,
            Y2 = y2,
            Stroke = brush,
            StrokeThickness = 1.5,
            StrokeDashArray = new DoubleCollection { 4, 3 }
        };

        GraphCanvas.Children.Add(line);
    }

    private void AddText(double x, double y, string text, double fontSize)
    {
        var label = new TextBlock
        {
            Text = text,
            FontSize = fontSize,
            Foreground = Brushes.White,
            Margin = new Thickness(x, y, 0, 0)
        };

        GraphCanvas.Children.Add(label);
    }

    private void AddValueLabel(double x, double y, string text, Color color)
    {
        var label = new TextBlock
        {
            Text = text,
            FontSize = 12,
            Foreground = new SolidColorBrush(color),
            Background = new SolidColorBrush(Color.FromArgb(220, 11, 18, 32)),
            Margin = new Thickness(x, y, 0, 0)
        };

        GraphCanvas.Children.Add(label);
    }
}
