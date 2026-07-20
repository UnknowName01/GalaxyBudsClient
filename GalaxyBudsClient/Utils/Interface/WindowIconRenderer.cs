using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using GalaxyBudsClient.Message.Decoder;
using GalaxyBudsClient.Model.Config;
using GalaxyBudsClient.Model.Constants;
using GalaxyBudsClient.Platform;
using Bitmap = Avalonia.Media.Imaging.Bitmap;
using Brushes = Avalonia.Media.Brushes;
using Point = Avalonia.Point;

namespace GalaxyBudsClient.Utils.Interface;

public static class WindowIconRenderer
{
    private const double CanvasWidth = 560;
    private const double CanvasHeight = 256;
    private const double Padding = 6;
    private const double Gap = 20;
    private const int AnimFrameCount = 12;
    private static readonly TimeSpan AnimFrameInterval = TimeSpan.FromMilliseconds(20);

    private static readonly Bitmap DefaultTrayBitmap = MakeDefaultBitmap();
    // Same canvas as battery frames so the macOS status-item pill width never jumps mid-animation.
    private static readonly WindowIcon DefaultIcon = MakeBatteryFrame(0, progress: 0);

    private static DispatcherTimer? _animTimer;
    private static int _animFrame;
    private static int _animDirection;
    private static int _targetLevel;
    private static bool _showingBattery;

    public static void UpdateDynamicIcon(IBasicStatusUpdate status)
    {
        var trayIcons = TrayIcon.GetIcons(Application.Current!);
        if (trayIcons == null)
            return;

        var batteryLeft = status.BatteryL;
        var batteryRight = status.BatteryR;

        // Ignore battery level of disconnected earbuds
        if (batteryLeft <= 0)
            batteryLeft = batteryRight;
        if (batteryRight <= 0)
            batteryRight = batteryLeft;

        int? level = Settings.Data.DynamicTrayIconMode switch
        {
            DynamicTrayIconModes.BatteryMin => Math.Min(batteryLeft, batteryRight),
            DynamicTrayIconModes.BatteryAvg => (batteryLeft + batteryRight) / 2,
            _ => null
        };

        if (level == null)
            return;

        var clamped = Math.Clamp(level.Value, 0, 100);
        Dispatcher.UIThread.Post(() =>
        {
            _targetLevel = clamped;
            if (_showingBattery || _animDirection > 0)
            {
                // Already expanded (or expanding): refresh final layout without replaying.
                if (_animTimer == null)
                    SetTrayIcon(MakeBatteryFrame(clamped, 1));
                return;
            }

            StartAnimation(expand: true);
        });
    }

    public static void ResetIconToDefault()
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (!_showingBattery && _animDirection <= 0)
            {
                StopAnimation();
                SetTrayIcon(DefaultIcon);
                return;
            }

            StartAnimation(expand: false);
        });
    }

    private static void StartAnimation(bool expand)
    {
        StopAnimation();
        // Lock variable-width before the first wide frame so AppKit doesn't resize mid-tween.
        EnsureVariableTrayWidth();
        _animFrame = expand ? 0 : AnimFrameCount;
        _animDirection = expand ? 1 : -1;
        _animTimer = new DispatcherTimer { Interval = AnimFrameInterval };
        _animTimer.Tick += OnAnimTick;
        _animTimer.Start();
        ApplyAnimFrame();
    }

    private static void StopAnimation()
    {
        if (_animTimer == null)
            return;
        _animTimer.Stop();
        _animTimer.Tick -= OnAnimTick;
        _animTimer = null;
        _animDirection = 0;
    }

    private static void OnAnimTick(object? sender, EventArgs e)
    {
        _animFrame += _animDirection;
        ApplyAnimFrame();

        if (_animDirection > 0 && _animFrame >= AnimFrameCount)
        {
            StopAnimation();
            _showingBattery = true;
            SetTrayIcon(MakeBatteryFrame(_targetLevel, 1));
        }
        else if (_animDirection < 0 && _animFrame <= 0)
        {
            StopAnimation();
            _showingBattery = false;
            SetTrayIcon(DefaultIcon);
        }
    }

    private static void ApplyAnimFrame()
    {
        var t = Math.Clamp(_animFrame / (double)AnimFrameCount, 0, 1);
        // Smoothstep for a less linear slide/fade.
        var eased = t * t * (3 - 2 * t);
        SetTrayIcon(MakeBatteryFrame(_targetLevel, eased));
    }

    private static void SetTrayIcon(WindowIcon icon)
    {
        var trayIcons = TrayIcon.GetIcons(Application.Current!);
        if (trayIcons == null || trayIcons.Count == 0)
            return;

        trayIcons[0].Icon = icon;
        EnsureVariableTrayWidth();
    }

#if OSX
    private static bool _variableWidthApplied;

    private static void EnsureVariableTrayWidth()
    {
        if (_variableWidthApplied)
            return;
        try
        {
            GalaxyBudsClient.Platform.OSX.AppUtils.setTrayIconsUseVariableWidth();
            _variableWidthApplied = true;
        }
        catch (EntryPointNotFoundException)
        {
            // Stale NativeInterop.dylib without this export — ignore rather than crash.
        }
    }
#else
    private static void EnsureVariableTrayWidth() { }
#endif

    /// <param name="progress">0 = earbud centered, battery hidden; 1 = final layout.</param>
    private static WindowIcon MakeBatteryFrame(int level, double progress)
    {
        var contentHeight = CanvasHeight - Padding * 2;

        var src = DefaultTrayBitmap;
        // Match default tray icon size (fill menu-bar height, not shrink into a side column).
        var iconScale = contentHeight / src.Size.Height;
        var iconWidth = src.Size.Width * iconScale;
        var iconHeight = src.Size.Height * iconScale;

        var startIconX = (CanvasWidth - iconWidth) / 2;
        var finalIconX = Padding;
        var iconX = startIconX + (finalIconX - startIconX) * progress;
        var iconY = Padding + (contentHeight - iconHeight) / 2;

        var textAreaWidth = Math.Max(0, CanvasWidth - Padding * 2 - iconWidth - Gap);
        var formattedText = new FormattedText(
            $"{level}%",
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            Typeface.Default,
            220,
            Brushes.Black);

        var textGeometry = formattedText.BuildGeometry(new Point(0, 0))!;
        var textBounds = textGeometry.Bounds;
        var textScale = textAreaWidth > 0
            ? Math.Min(textAreaWidth / textBounds.Width, contentHeight / textBounds.Height)
            : 0;
        var scaledTextWidth = textBounds.Width * textScale;
        var scaledTextHeight = textBounds.Height * textScale;
        var textOriginX = Padding + iconWidth + Gap + (textAreaWidth - scaledTextWidth) / 2;
        var textOriginY = Padding + (contentHeight - scaledTextHeight) / 2;
        var textMatrix = Matrix.CreateScale(textScale, textScale) *
                         Matrix.CreateTranslation(
                             textOriginX - textBounds.X * textScale,
                             textOriginY - textBounds.Y * textScale);

        var render = new RenderTargetBitmap(
            new PixelSize((int)CanvasWidth, (int)CanvasHeight), new Vector(96, 96));
        using (var ctx = render.CreateDrawingContext())
        {
            ctx.PushRenderOptions(new RenderOptions
            {
                BitmapInterpolationMode = BitmapInterpolationMode.HighQuality,
                TextRenderingMode = TextRenderingMode.Antialias,
                EdgeMode = EdgeMode.Antialias,
                RequiresFullOpacityHandling = true
            });

            ctx.DrawImage(src, new Rect(iconX, iconY, iconWidth, iconHeight));

            if (progress > 0.01)
            {
                using (ctx.PushOpacity(progress))
                using (ctx.PushTransform(textMatrix))
                {
                    IBrush brush = PlatformUtils.IsOSX
                        ? Brushes.Black
                        : new SolidColorBrush(Settings.Data.AccentColor);
                    ctx.DrawGeometry(brush, new Pen(Brushes.Transparent, 0), textGeometry);
                }
            }
        }

        return new WindowIcon(render);
    }

    private static Bitmap MakeDefaultBitmap()
    {
        // OSX uses templated icons
        var type = PlatformUtils.IsOSX ? "black" : PlatformUtils.IsWindows ? "white_outlined_single" : "white_outlined_multi";
        var uri = $"{Program.AvaresUrl}/Resources/icon_{type}_tray.ico";
        return new Bitmap(AssetLoader.Open(new Uri(uri)));
    }
}
