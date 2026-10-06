using System;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using GalaxyBudsClient.Interface.StyledWindow;
using GalaxyBudsClient.Model.Config;
using GalaxyBudsClient.Model.Constants;
using GalaxyBudsClient.Platform;
using GalaxyBudsClient.Utils.Extensions;
using Timer = System.Timers.Timer;

namespace GalaxyBudsClient.Interface.Dialogs;

public enum BudsPopupMode
{
    Battery,
    Connecting
}

public partial class BudsPopup : Window
{
    /// <summary>Minimum time the connecting message stays visible so users can read it.</summary>
    private const double MinConnectingVisibleMs = 3000;
    private const int FadeMs = 350;

    public EventHandler? ClickedEventHandler { get; set; }

    private readonly Timer _timer = new(3000) { AutoReset = false };
    private BudsPopupMode _mode = BudsPopupMode.Battery;
    private DateTime _modeEnteredAt = DateTime.UtcNow;
    private CancellationTokenSource? _deferredModeCts;
    private int _showGeneration;
     
    public BudsPopupMode Mode => _mode;
    public bool IsPresented => IsVisible && OuterBorder.Opacity > 0.05;

    public BudsPopup() 
    {
        InitializeComponent();

        BatteryStatus.IsVisible = false;
        ConnectingStatus.IsVisible = false;

        Settings.MainSettingsPropertyChanged += OnMainSettingsPropertyChanged;
        _timer.Elapsed += (_, _) => Dispatcher.UIThread.Post(() => Hide(force: false), DispatcherPriority.Background);
    }
    
    private void OnMainSettingsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if(e.PropertyName is nameof(Settings.Data.Theme) or nameof(Settings.Data.BlurStrength))
        {
            RequestedThemeVariant = IStyledWindow.GetThemeVariant();
        }
    }
    
    public void RearmTimer()
    {
        if (_mode == BudsPopupMode.Connecting)
            return;

        _timer.Stop();
        _timer.Interval = 3000;
        _timer.Start();
    }

    public void SetMode(BudsPopupMode mode)
    {
        if (mode == _mode && IsPresented)
            return;

        if (mode == BudsPopupMode.Battery &&
            _mode == BudsPopupMode.Connecting &&
            IsPresented)
        {
            var remainingMs = MinConnectingVisibleMs - (DateTime.UtcNow - _modeEnteredAt).TotalMilliseconds;
            if (remainingMs > 50)
            {
                ScheduleDeferred(() => ApplyMode(BudsPopupMode.Battery), remainingMs);
                return;
            }
        }

        CancelDeferredMode();
        ApplyMode(mode);
    }

    private void ApplyMode(BudsPopupMode mode)
    {
        var modeChanged = _mode != mode;
        _mode = mode;
        if (modeChanged || !IsPresented)
            _modeEnteredAt = DateTime.UtcNow;

        BatteryStatus.IsVisible = mode == BudsPopupMode.Battery;
        ConnectingStatus.IsVisible = mode == BudsPopupMode.Connecting;

        _timer.Stop();
        if (mode == BudsPopupMode.Connecting)
        {
            _timer.Interval = 60000;
            _timer.Start();
        }
        else
        {
            _timer.Interval = 3000;
            _timer.Start();
        }

        UpdateSettings(reposition: !IsVisible);
    }

    public void Hide(bool force = false)
    {
        if (!force &&
            _mode == BudsPopupMode.Connecting &&
            IsPresented)
        {
            var remainingMs = MinConnectingVisibleMs - (DateTime.UtcNow - _modeEnteredAt).TotalMilliseconds;
            if (remainingMs > 50)
            {
                ScheduleDeferred(() => Hide(force: true), remainingMs);
                return;
            }
        }

        CancelDeferredMode();
        _ = HideAnimatedAsync();
    }
 
    public override void Hide()
    {
        Hide(force: false);
    }

    public override void Show()
    {
        _ = ShowAnimatedAsync();
    }

    /// <summary>
    /// Soft-show on the UI thread: layout first (opacity 0), then fade in via transition.
    /// Reuses the same window — no Close/recreate (that was the main macOS jitter source).
    /// </summary>
    public async Task ShowAnimatedAsync()
    {
        var generation = Interlocked.Increment(ref _showGeneration);

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (generation != _showGeneration)
                return;

            UpdateSettings(reposition: !IsVisible);
            OuterBorder.Opacity = 0;

            if (!IsVisible)
                base.Show();
        }, DispatcherPriority.Normal);

        // One frame at opacity 0, then fade in — keep this short so Settings-connect feels instant.
        await Task.Delay(16);
        if (generation != _showGeneration)
            return;

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (generation != _showGeneration)
                return;
            OuterBorder.Opacity = 1;
            if (!_timer.Enabled)
                _timer.Start();
        }, DispatcherPriority.Render);
    }

    private async Task HideAnimatedAsync()
    {
        var generation = Interlocked.Increment(ref _showGeneration);
        _timer.Stop();

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            OuterBorder.Opacity = 0;
        }, DispatcherPriority.Render);

        await Task.Delay(FadeMs);
        if (generation != _showGeneration)
            return;

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (generation != _showGeneration)
                return;
            // Soft hide — keep the instance warm for the next show.
            if (IsVisible)
                base.Hide();
        }, DispatcherPriority.Background);
    }

    protected override void OnOpened(EventArgs e)
    {
        UpdateSettings(reposition: false);
        base.OnOpened(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        // The popup is recreated each cycle (Hide -> Close), so detach from the static settings
        // event or every closed instance leaks and keeps receiving settings changes.
        Settings.MainSettingsPropertyChanged -= OnMainSettingsPropertyChanged;
        base.OnClosed(e);
    }

    private void Window_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        ClickedEventHandler?.Invoke(this, EventArgs.Empty);
        Hide(force: true);
    }

    private void ScheduleDeferred(Action action, double delayMs)
    {
        CancelDeferredMode();
        _deferredModeCts = new CancellationTokenSource();
        var token = _deferredModeCts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(delayMs), token).ConfigureAwait(false);
                if (token.IsCancellationRequested)
                    return;
                Dispatcher.UIThread.Post(action, DispatcherPriority.Background);
            }
            catch (OperationCanceledException)
            {
            }
        }, token);
    }

    private void CancelDeferredMode()
    {
        _deferredModeCts?.Cancel();
        _deferredModeCts?.Dispose();
        _deferredModeCts = null;
    }

    public void UpdateSettings(bool reposition = true)
    {
        RequestedThemeVariant = IStyledWindow.GetThemeVariant();
        Header.Content = BluetoothImpl.Instance.DeviceName;

        if (Settings.Data.PopupCompact && _mode != BudsPopupMode.Connecting)
        {
            MaxHeight = Height = 205 - 35;
            Grid.RowDefinitions[0].Height = new GridLength(0);
            Header.IsVisible = false;
        }
        else
        {
            MaxHeight = Height = 205;
            Grid.RowDefinitions[0].Height = new GridLength(35);
            Header.IsVisible = true;
        }

        if (!reposition)
            return;
            
        var workArea = (Screens.Primary ?? Screens.All[0]).WorkingArea;
        var scaling = PlatformImpl?.GetPropertyValue<double>("DesktopScaling") ?? 1.0;
        var padding = (int)(20 * scaling);

        Position = Settings.Data.PopupPlacement switch
        {
            PopupPlacement.TopLeft => new PixelPoint(workArea.X + padding, workArea.Y + padding),
            PopupPlacement.TopCenter => new PixelPoint(
                (int)(workArea.Width / 2f - Width * scaling / 2 + workArea.X), workArea.Y + padding),
            PopupPlacement.TopRight => new PixelPoint(
                (int)(workArea.Width - Width * scaling + workArea.X - padding), workArea.Y + padding),
            PopupPlacement.BottomLeft => new PixelPoint(workArea.X + padding,
                (int)(workArea.Height - Height * scaling + workArea.Y - padding)),
            PopupPlacement.BottomCenter => new PixelPoint(
                (int)(workArea.Width / 2f - Width * scaling / 2 + workArea.X),
                (int)(workArea.Height - Height * scaling + workArea.Y - padding)),
            PopupPlacement.BottomRight => new PixelPoint(
                (int)(workArea.Width - Width * scaling + workArea.X - padding),
                (int)(workArea.Height - Height * scaling + workArea.Y - padding)),
            _ => Position
        };
    }
}