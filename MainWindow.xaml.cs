using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using SpotCont.Infrastructure;
using SpotCont.Models;
using SpotCont.ViewModels;

namespace SpotCont;

public partial class MainWindow : Window, IDisposable
{
    private const int SwShow = 5;
    private const double OuterBottomCornerRadius = 8;
    private const double InnerBottomCornerRadius = 7;

    private readonly LauncherViewModel _viewModel;
    private readonly SemaphoreSlim _toggleGate = new(1, 1);
    private ContextMenu? _activeContextMenu;
    private CancellationTokenSource? _deactivationHideCancellationTokenSource;
    private CancellationTokenSource? _focusStabilizationCancellationTokenSource;
    private DateTimeOffset _suppressDeactivationUntilUtc = DateTimeOffset.MinValue;
    private bool _isTransitioning;
    private bool _disposed;

    public MainWindow(LauncherViewModel viewModel)
    {
        InitializeComponent();

        _viewModel = viewModel;
        DataContext = _viewModel;
        _viewModel.HideRequested += ViewModel_HideRequested;
        Activated += Window_Activated;
        Closed += (_, _) => Dispose();
    }

    public async Task ToggleAsync()
    {
        await _toggleGate.WaitAsync();
        try
        {
            if (IsVisible)
            {
                await HideLauncherAsync();
                return;
            }

            await ShowLauncherAsync();
        }
        finally
        {
            _toggleGate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        CloseActiveContextMenu();
        _viewModel.HideRequested -= ViewModel_HideRequested;
        Activated -= Window_Activated;
        _viewModel.Dispose();
        _toggleGate.Dispose();
        CancelPendingDeactivationHide();
        CancelFocusStabilization();
    }

    private async Task ShowLauncherAsync()
    {
        CancelPendingDeactivationHide();
        _suppressDeactivationUntilUtc = DateTimeOffset.UtcNow.AddMilliseconds(320);
        PositionWindow();

        Task showAnimationTask = Task.CompletedTask;
        if (!IsVisible)
        {
            showAnimationTask = WindowAnimationHelper.ShowAsync(this, RootScaleTransform);
        }

        _viewModel.SetLauncherVisibility(true);
        await EnsureSearchInputFocusAsync(selectAllText: true);
        ScheduleFocusStabilization();

        _ = _viewModel.RefreshAsync();
        await showAnimationTask;
        await EnsureSearchInputFocusAsync(selectAllText: false);
        ScheduleFocusStabilization();
    }

    private async Task HideLauncherAsync()
    {
        if (!IsVisible || _isTransitioning)
        {
            return;
        }

        CancelPendingDeactivationHide();
        _isTransitioning = true;
        try
        {
            CloseActiveContextMenu();
            await WindowAnimationHelper.HideAsync(this, RootScaleTransform);
            _viewModel.SetLauncherVisibility(false);
        }
        finally
        {
            _isTransitioning = false;
        }
    }

    private void PositionWindow()
    {
        var workArea = WindowPlacementHelper.GetActiveMonitorWorkArea();
        Left = workArea.Left + (workArea.Width - Width) / 2;
        Top = workArea.Top;
    }

    private async Task EnsureSearchInputFocusAsync(bool selectAllText)
    {
        await Dispatcher.InvokeAsync(
            () => ApplySearchInputFocus(selectAllText),
            DispatcherPriority.Send);

        await Dispatcher.InvokeAsync(
            () => ApplySearchInputFocus(selectAllText),
            DispatcherPriority.Input);

        await Dispatcher.InvokeAsync(
            () => ApplySearchInputFocus(selectAllText),
            DispatcherPriority.ContextIdle);
    }

    private void ApplySearchInputFocus(bool selectAllText)
    {
        if (!IsVisible)
        {
            return;
        }

        Topmost = true;
        Activate();
        BringToForeground();
        Keyboard.ClearFocus();
        Focus();
        FocusManager.SetFocusedElement(this, SearchTextBox);
        Keyboard.Focus(SearchTextBox);
        SearchTextBox.Focus();

        if (selectAllText)
        {
            SearchTextBox.SelectAll();
        }
        else
        {
            SearchTextBox.CaretIndex = SearchTextBox.Text.Length;
        }
    }

    private void ScheduleFocusStabilization()
    {
        var cancellationTokenSource = new CancellationTokenSource();
        var previousCancellationTokenSource = Interlocked.Exchange(
            ref _focusStabilizationCancellationTokenSource,
            cancellationTokenSource);

        if (previousCancellationTokenSource is not null)
        {
            try
            {
                previousCancellationTokenSource.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }

            previousCancellationTokenSource.Dispose();
        }

        _ = StabilizeSearchInputFocusAsync(cancellationTokenSource);
    }

    private async Task StabilizeSearchInputFocusAsync(CancellationTokenSource cancellationTokenSource)
    {
        try
        {
            await Task.Delay(60, cancellationTokenSource.Token);
            await Dispatcher.InvokeAsync(
                () => ApplySearchInputFocus(selectAllText: false),
                DispatcherPriority.ApplicationIdle,
                cancellationTokenSource.Token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        finally
        {
            if (ReferenceEquals(
                    Interlocked.CompareExchange(
                        ref _focusStabilizationCancellationTokenSource,
                        null,
                        cancellationTokenSource),
                    cancellationTokenSource))
            {
                cancellationTokenSource.Dispose();
            }
        }
    }

    private void CancelFocusStabilization()
    {
        var cancellationTokenSource = Interlocked.Exchange(ref _focusStabilizationCancellationTokenSource, null);
        if (cancellationTokenSource is null)
        {
            return;
        }

        try
        {
            cancellationTokenSource.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
        finally
        {
            cancellationTokenSource.Dispose();
        }
    }

    private void SchedulePendingDeactivationHide(TimeSpan delay)
    {
        var cancellationTokenSource = new CancellationTokenSource();
        var previousCancellationTokenSource = Interlocked.Exchange(
            ref _deactivationHideCancellationTokenSource,
            cancellationTokenSource);
        TryCancelAndDispose(previousCancellationTokenSource);
        _ = HideAfterDeactivationDelayAsync(delay, cancellationTokenSource);
    }

    private async Task HideAfterDeactivationDelayAsync(
        TimeSpan delay,
        CancellationTokenSource cancellationTokenSource)
    {
        try
        {
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, cancellationTokenSource.Token);
            }

            if (cancellationTokenSource.IsCancellationRequested ||
                !IsVisible ||
                IsActive ||
                _isTransitioning)
            {
                return;
            }

            await HideLauncherAsync();
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception exception)
        {
            ReportException("PendingDeactivationHide", exception);
        }
        finally
        {
            if (ReferenceEquals(
                    Interlocked.CompareExchange(
                        ref _deactivationHideCancellationTokenSource,
                        null,
                        cancellationTokenSource),
                    cancellationTokenSource))
            {
                cancellationTokenSource.Dispose();
            }
        }
    }

    private void CancelPendingDeactivationHide()
    {
        var cancellationTokenSource = Interlocked.Exchange(ref _deactivationHideCancellationTokenSource, null);
        TryCancelAndDispose(cancellationTokenSource);
    }

    private static void TryCancelAndDispose(CancellationTokenSource? cancellationTokenSource)
    {
        if (cancellationTokenSource is null)
        {
            return;
        }

        try
        {
            cancellationTokenSource.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
        finally
        {
            cancellationTokenSource.Dispose();
        }
    }

    private void BringToForeground()
    {
        var windowHandle = new WindowInteropHelper(this).Handle;
        if (windowHandle == IntPtr.Zero)
        {
            return;
        }

        var foregroundWindowHandle = GetForegroundWindow();
        var currentThreadId = GetCurrentThreadId();
        var foregroundThreadId = foregroundWindowHandle == IntPtr.Zero
            ? 0
            : GetWindowThreadProcessId(foregroundWindowHandle, IntPtr.Zero);
        var attached = false;

        try
        {
            if (foregroundThreadId != 0 && foregroundThreadId != currentThreadId)
            {
                attached = AttachThreadInput(foregroundThreadId, currentThreadId, true);
            }

            ShowWindow(windowHandle, SwShow);
            BringWindowToTop(windowHandle);
            SetForegroundWindow(windowHandle);
            SetActiveWindow(windowHandle);
        }
        finally
        {
            if (attached)
            {
                AttachThreadInput(foregroundThreadId, currentThreadId, false);
            }
        }
    }

    private async void Window_Deactivated(object sender, EventArgs e)
    {
        try
        {
            if (!IsVisible || _isTransitioning)
            {
                return;
            }

            var delay = _suppressDeactivationUntilUtc - DateTimeOffset.UtcNow;
            if (delay > TimeSpan.Zero)
            {
                SchedulePendingDeactivationHide(delay);
                return;
            }

            await HideLauncherAsync();
        }
        catch (Exception exception)
        {
            ReportException("Deactivated", exception);
        }
    }

    private void Window_Activated(object? sender, EventArgs e)
    {
        CancelPendingDeactivationHide();
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        UpdateSurfaceClipping();
    }

    private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateSurfaceClipping();
    }

    private void UpdateSurfaceClipping()
    {
        ApplyBottomRoundedClip(RootSurface, OuterBottomCornerRadius);
        ApplyBottomRoundedClip(ContentClipHost, InnerBottomCornerRadius);
    }

    private static void ApplyBottomRoundedClip(FrameworkElement element, double bottomRadius)
    {
        var width = element.ActualWidth;
        var height = element.ActualHeight;
        if (width <= 0 || height <= 0)
        {
            element.Clip = null;
            return;
        }

        var radius = Math.Max(0, Math.Min(bottomRadius, Math.Min(width / 2, height / 2)));
        if (radius <= 0)
        {
            element.Clip = new RectangleGeometry(new Rect(0, 0, width, height));
            return;
        }

        var figure = new PathFigure
        {
            StartPoint = new Point(0, 0),
            IsClosed = true,
            IsFilled = true
        };

        figure.Segments.Add(new LineSegment(new Point(width, 0), true));
        figure.Segments.Add(new LineSegment(new Point(width, height - radius), true));
        figure.Segments.Add(new ArcSegment(
            new Point(width - radius, height),
            new Size(radius, radius),
            0,
            false,
            SweepDirection.Clockwise,
            true));
        figure.Segments.Add(new LineSegment(new Point(radius, height), true));
        figure.Segments.Add(new ArcSegment(
            new Point(0, height - radius),
            new Size(radius, radius),
            0,
            false,
            SweepDirection.Clockwise,
            true));
        figure.Segments.Add(new LineSegment(new Point(0, 0), true));

        element.Clip = new PathGeometry(new[] { figure });
    }

    private async void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        try
        {
            var key = e.Key == Key.System ? e.SystemKey : e.Key;

            if ((Keyboard.Modifiers & ModifierKeys.Alt) == ModifierKeys.Alt)
            {
                if (key == Key.Space)
                {
                    e.Handled = true;
                    return;
                }

                var shortcutIndex = key switch
                {
                    Key.D1 or Key.NumPad1 => 0,
                    Key.D2 or Key.NumPad2 => 1,
                    Key.D3 or Key.NumPad3 => 2,
                    Key.D4 or Key.NumPad4 => 3,
                    Key.D5 or Key.NumPad5 => 4,
                    Key.D6 or Key.NumPad6 => 5,
                    Key.D7 or Key.NumPad7 => 6,
                    Key.D8 or Key.NumPad8 => 7,
                    Key.D9 or Key.NumPad9 => 8,
                    _ => -1
                };

                if (shortcutIndex >= 0)
                {
                    e.Handled = true;
                    await _viewModel.ExecuteByShortcutAsync(shortcutIndex);
                    return;
                }
            }

            switch (key)
            {
                case Key.Down:
                    CloseActiveContextMenu();
                    _viewModel.MoveSelection(1);
                    e.Handled = true;
                    break;
                case Key.Up:
                    CloseActiveContextMenu();
                    _viewModel.MoveSelection(-1);
                    e.Handled = true;
                    break;
                case Key.Enter:
                    CloseActiveContextMenu();
                    e.Handled = true;
                    await _viewModel.ExecuteSelectedAsync();
                    break;
                case Key.Tab:
                    CloseActiveContextMenu();
                    e.Handled = true;
                    if (_viewModel.ApplyAutocomplete())
                    {
                        SearchTextBox.Focus();
                        SearchTextBox.CaretIndex = SearchTextBox.Text.Length;
                    }

                    break;
                case Key.Escape:
                    CloseActiveContextMenu();
                    e.Handled = true;
                    await HideLauncherAsync();
                    break;
            }
        }
        catch (Exception exception)
        {
            ReportException("PreviewKeyDown", exception);
        }
    }

    private async void Window_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        try
        {
            if (!IsVisible ||
                _isTransitioning ||
                string.IsNullOrWhiteSpace(e.Text) ||
                SearchTextBox.IsKeyboardFocusWithin)
            {
                return;
            }

            await EnsureSearchInputFocusAsync(selectAllText: false);

            // Ensure the first typed characters are not lost when focus races.
            SearchTextBox.SelectedText = e.Text;
            SearchTextBox.CaretIndex = SearchTextBox.Text.Length;
            e.Handled = true;
        }
        catch (Exception exception)
        {
            ReportException("PreviewTextInput", exception);
        }
    }

    private void ResultsListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        CloseActiveContextMenu();

        if (ResultsListBox.SelectedItem is not null)
        {
            ResultsListBox.ScrollIntoView(ResultsListBox.SelectedItem);
        }

        AnimatePreviewPanel();
    }

    private void ResultItem_MouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is ListBoxItem listBoxItem && listBoxItem.DataContext is SearchResultItem item)
        {
            _viewModel.SelectFromMouse(item);
        }
    }

    private async void ResultItem_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        try
        {
            if (sender is ListBoxItem listBoxItem && listBoxItem.DataContext is SearchResultItem item)
            {
                CloseActiveContextMenu();
                _viewModel.SelectFromMouse(item);
                e.Handled = true;
                await _viewModel.ExecuteSelectedAsync();
            }
        }
        catch (Exception exception)
        {
            ReportException("MouseExecute", exception);
        }
    }

    private void ResultItem_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBoxItem listBoxItem && listBoxItem.DataContext is SearchResultItem item)
        {
            _viewModel.SelectFromMouse(item);
            listBoxItem.IsSelected = true;
            if (TryPrepareContextMenu(listBoxItem, item))
            {
                if (_activeContextMenu is not null)
                {
                    _activeContextMenu.IsOpen = true;
                    e.Handled = true;
                }
            }
        }
    }

    private void ResultItem_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is not ListBoxItem listBoxItem || listBoxItem.DataContext is not SearchResultItem item)
        {
            e.Handled = true;
            return;
        }

        _viewModel.SelectFromMouse(item);
        if (!TryPrepareContextMenu(listBoxItem, item))
        {
            e.Handled = true;
            return;
        }
    }

    private async void ContextMenuItem_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (sender is MenuItem { Tag: ContextActionTag actionTag })
            {
                CloseActiveContextMenu();
                await _viewModel.ExecuteContextActionAsync(actionTag.Item, actionTag.ActionKind);
            }
        }
        catch (Exception exception)
        {
            ReportException("ContextAction", exception);
        }
    }

    private async void ViewModel_HideRequested(object? sender, EventArgs e)
    {
        try
        {
            await HideLauncherAsync();
        }
        catch (Exception exception)
        {
            ReportException("HideRequested", exception);
        }
    }

    private void SearchTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        CloseActiveContextMenu();
    }

    private void ResultsListBox_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        CloseActiveContextMenu();
    }

    private void PreviewPanelScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        CloseActiveContextMenu();

        if (sender is not ScrollViewer scrollViewer)
        {
            e.Handled = true;
            return;
        }

        var scrollLines = SystemParameters.WheelScrollLines;
        var stepCount = scrollLines <= 0 ? 3 : scrollLines;

        for (var lineIndex = 0; lineIndex < stepCount; lineIndex++)
        {
            if (e.Delta > 0)
            {
                scrollViewer.LineUp();
            }
            else if (e.Delta < 0)
            {
                scrollViewer.LineDown();
            }
        }

        e.Handled = true;
    }

    private void PreviewPath_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        try
        {
            var previewPath = _viewModel.FilePreviewPath?.Trim();
            if (string.IsNullOrWhiteSpace(previewPath))
            {
                return;
            }

            if (TryOpenPathLocation(previewPath))
            {
                e.Handled = true;
            }
        }
        catch (Exception exception)
        {
            ReportException("PreviewPathClick", exception);
        }
    }

    private bool TryPrepareContextMenu(ListBoxItem listBoxItem, SearchResultItem item)
    {
        var actions = _viewModel.GetContextActions(item);
        if (actions.Count == 0)
        {
            return false;
        }

        CloseActiveContextMenu();

        var contextMenu = new ContextMenu
        {
            PlacementTarget = listBoxItem,
            StaysOpen = false
        };
        if (TryFindResource(typeof(ContextMenu)) is Style contextMenuStyle)
        {
            contextMenu.Style = contextMenuStyle;
        }

        foreach (var action in actions)
        {
            var menuItem = new MenuItem
            {
                Header = action.Title,
                Tag = new ContextActionTag(item, action.Kind)
            };
            if (TryFindResource(typeof(MenuItem)) is Style menuItemStyle)
            {
                menuItem.Style = menuItemStyle;
            }

            menuItem.Click += ContextMenuItem_Click;
            contextMenu.Items.Add(menuItem);
        }

        contextMenu.Closed += ContextMenu_Closed;
        listBoxItem.ContextMenu = contextMenu;
        _activeContextMenu = contextMenu;
        return true;
    }

    private void ContextMenu_Closed(object sender, RoutedEventArgs e)
    {
        if (ReferenceEquals(_activeContextMenu, sender))
        {
            _activeContextMenu = null;
        }
    }

    private void CloseActiveContextMenu()
    {
        var contextMenu = _activeContextMenu;
        if (contextMenu is null)
        {
            return;
        }

        _activeContextMenu = null;

        try
        {
            contextMenu.IsOpen = false;
        }
        catch
        {
        }
    }

    private static bool TryOpenPathLocation(string previewPath)
    {
        if (Directory.Exists(previewPath))
        {
            return TryStartProcess("explorer.exe", $"\"{previewPath}\"");
        }

        if (File.Exists(previewPath))
        {
            return TryStartProcess("explorer.exe", $"/select,\"{previewPath}\"");
        }

        var containingDirectoryPath = Path.GetDirectoryName(previewPath);
        return !string.IsNullOrWhiteSpace(containingDirectoryPath) &&
               Directory.Exists(containingDirectoryPath) &&
               TryStartProcess("explorer.exe", $"\"{containingDirectoryPath}\"");
    }

    private static bool TryStartProcess(string fileName, string arguments)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = true
            });
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void AnimatePreviewPanel()
    {
        if (PreviewPanelHost.Visibility != Visibility.Visible)
        {
            return;
        }

        PreviewPanelHost.BeginAnimation(UIElement.OpacityProperty, null);
        PreviewPanelScaleTransform.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        PreviewPanelScaleTransform.BeginAnimation(ScaleTransform.ScaleYProperty, null);

        PreviewPanelHost.Opacity = 0;
        PreviewPanelScaleTransform.ScaleX = 0.985;
        PreviewPanelScaleTransform.ScaleY = 0.985;

        var easing = new QuadraticEase
        {
            EasingMode = EasingMode.EaseOut
        };

        var opacityAnimation = new DoubleAnimation(1, TimeSpan.FromMilliseconds(95))
        {
            EasingFunction = easing
        };
        var scaleAnimation = new DoubleAnimation(1, TimeSpan.FromMilliseconds(115))
        {
            EasingFunction = easing
        };

        PreviewPanelHost.BeginAnimation(UIElement.OpacityProperty, opacityAnimation);
        PreviewPanelScaleTransform.BeginAnimation(ScaleTransform.ScaleXProperty, scaleAnimation);
        PreviewPanelScaleTransform.BeginAnimation(ScaleTransform.ScaleYProperty, scaleAnimation.Clone());
    }

    private static void ReportException(string source, Exception exception)
    {
        Debug.WriteLine($"[SpotCont:MainWindow:{source}] {exception}");
    }

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr SetActiveWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr lpdwProcessId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    private sealed record ContextActionTag(SearchResultItem Item, SearchResultContextActionKind ActionKind);
}
