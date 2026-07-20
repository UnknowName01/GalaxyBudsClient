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
    private const double CanvasHeight = 256;
    private const double Padding = 6;
    private const double Gap = 14;
    /// <summary>Extra scale for battery % after fitting into the text area.</summary>
    private const double BatteryTextScaleBoost = 1.28;
    private const int AnimFrameCount = 12;
    private static readonly TimeSpan AnimFrameInterval = TimeSpan.FromMilliseconds(20);

    private static readonly Bitmap DefaultTrayBitmap = MakeDefaultBitmap();

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
                    SetTrayIcon(MakeBatteryFrame(clamped, 1, compact: false));
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
                SetTrayIcon(MakeBatteryFrame(0, 0, compact: true));
                return;
            }

            StartAnimation(expand: false);
        });
    }

    private static void StartAnimation(bool expand)
    {
        StopAnimation();
        EnsureVariableTrayWidth();

        // AppKit jitters if status-item width changes every frame. Keep a stable *wide*
        // canvas for the whole tween; only snap to the compact bitmap when idle.
        if (expand)
            SetTrayIcon(MakeBatteryFrame(_targetLevel, 0, compact: false));

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
        }
        else if (_animDirection < 0 && _animFrame <= 0)
        {
            StopAnimation();
            _showingBattery = false;
            // One compact snap after the content finished centering — saves menu-bar space.
            SetTrayIcon(MakeBatteryFrame(0, 0, compact: true));
        }
    }

    private static void ApplyAnimFrame()
    {
        var t = Math.Clamp(_animFrame / (double)AnimFrameCount, 0, 1);
        var eased = t * t * (3 - 2 * t);
        SetTrayIcon(MakeBatteryFrame(_targetLevel, eased, compact: false));
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

    /// <param name="progress">0 = earbud centered / % hidden; 1 = final earbud+% layout.</param>
    /// <param name="compact">
    /// When true (idle disconnected only), bitmap width is icon-sized so the pill stays narrow.
    /// During animation always pass false so AppKit does not resize every frame.
    /// </param>
    private static WindowIcon MakeBatteryFrame(int level, double progress, bool compact)
    {
        progress = Math.Clamp(progress, 0, 1);
        var contentHeight = CanvasHeight - Padding * 2;

        var src = DefaultTrayBitmap;
        var iconScale = contentHeight / src.Size.Height * 0.9;
        var iconWidth = src.Size.Width * iconScale;
        var iconHeight = src.Size.Height * iconScale;

        var formattedText = new FormattedText(
            $"{level}%",
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            Typeface.Default,
            220,
            Brushes.Black);

        var textGeometry = formattedText.BuildGeometry(new Point(0, 0))!;
        var textBounds = textGeometry.Bounds;

        var maxTextAreaWidth = Math.Max(48, iconWidth * 1.35);
        var textScale = 0.0;
        if (textBounds.Width > 0 && textBounds.Height > 0)
        {
            var fitScale = Math.Min(maxTextAreaWidth / textBounds.Width, contentHeight / textBounds.Height);
            textScale = fitScale * BatteryTextScaleBoost;
            textScale = Math.Min(textScale, maxTextAreaWidth * 1.05 / textBounds.Width);
            textScale = Math.Min(textScale, contentHeight * 1.15 / textBounds.Height);
        }

        var scaledTextWidth = textBounds.Width * textScale;
        var scaledTextHeight = textBounds.Height * textScale;

        var narrowWidth = Padding * 2 + iconWidth;
        var wideWidth = Padding * 2 + iconWidth + Gap + scaledTextWidth;
        // Stable wide canvas while animating; compact only when idle-disconnected.
        var narrowPixelW = Math.Max(2, (int)Math.Round(narrowWidth / 2.0) * 2);
        var widePixelW = Math.Max(2, (int)Math.Round(wideWidth / 2.0) * 2);
        var pixelWidth = compact ? narrowPixelW : widePixelW;
        var pixelHeight = (int)CanvasHeight;

        // macOS menu-bar items grow/shrink leftward (right edge stays put). Keep the
        // earbud locked to that right edge at progress=0 so the narrow↔wide snap
        // does not jump the icon on screen — only the empty left side appears/disappears.
        var compactIconX = (narrowPixelW - iconWidth) / 2;
        var iconXFromRight = narrowPixelW - compactIconX - iconWidth;
        var wideIconXAtProgress0 = widePixelW - iconWidth - iconXFromRight;
        var finalIconX = Padding;

        double iconX;
        if (compact)
            iconX = compactIconX;
        else
            iconX = wideIconXAtProgress0 + (finalIconX - wideIconXAtProgress0) * progress;

        var iconY = Padding + (contentHeight - iconHeight) / 2;

        var textOriginX = iconX + iconWidth + Gap;
        var textOriginY = Padding + (contentHeight - scaledTextHeight) / 2;
        var textMatrix = Matrix.CreateScale(textScale, textScale) *
                         Matrix.CreateTranslation(
                             textOriginX - textBounds.X * textScale,
                             textOriginY - textBounds.Y * textScale);

        var render = new RenderTargetBitmap(
            new PixelSize(pixelWidth, pixelHeight), new Vector(96, 96));
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

            if (!compact && progress > 0.01 && textScale > 0)
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
