using System;
using System.Globalization;
using System.Runtime.InteropServices;
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
using Serilog;
using Bitmap = Avalonia.Media.Imaging.Bitmap;
using Brushes = Avalonia.Media.Brushes;
using MainWindow = GalaxyBudsClient.Interface.MainWindow;
using Point = Avalonia.Point;

namespace GalaxyBudsClient.Utils.Interface;

public static class WindowIconRenderer
{
    /// <summary>
    /// AppKit shrinks status bar images to floor(menuFontSize * 4/3) points tall, so the layout is
    /// authored directly in those final points and only the rasterization scale changes per screen.
    /// </summary>
    private const double CanvasHeight = 17;
    private const double EarbudHeight = 13;
    /// <summary>Height of the battery percentage glyphs, matching the macOS menu bar text size.</summary>
    private const double TextHeight = 11;
    private const double Gap = 4;
    private const double SidePadding = 1;
    /// <summary>Windows and Linux trays rescale the bitmap themselves, so hand them a large one.</summary>
    private const double GenericRenderScale = 15;
    /// <summary>
    /// Extra raster resolution handed to AppKit on top of the screen scale. Drawing a template
    /// image at exactly 1:1 makes AppKit harden the alpha mask, which turns the earbud outline into
    /// hard stair-steps; letting it resample instead keeps the anti-aliased edge intact.
    /// </summary>
    private const double Supersample = 2;
    private const int AnimFrameCount = 12;
    private static readonly TimeSpan AnimFrameInterval = TimeSpan.FromMilliseconds(20);

    private static readonly TrayArtwork Artwork = TrayArtwork.Load();

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

    /// <summary>
    /// Pixels per layout point. On macOS this has to track the screen showing the menu bar, since
    /// AppKit maps the bitmap onto a fixed point size and anything coarser just gets blurred away;
    /// retina is assumed when detection fails.
    /// </summary>
    private static double RenderScale
    {
        get
        {
            if (!PlatformUtils.IsOSX)
                return GenericRenderScale;

            try
            {
                if (MainWindow.Instance.Screens.Primary?.Scaling is { } scaling and > 0)
                    return Math.Clamp(Math.Round(scaling), 1, 3) * Supersample;
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "WindowIconRenderer: cannot determine screen scaling");
            }

            return 2 * Supersample;
        }
    }

    /// <param name="progress">0 = earbud centered / % hidden; 1 = final earbud+% layout.</param>
    /// <param name="compact">
    /// When true (idle disconnected only), bitmap width is icon-sized so the pill stays narrow.
    /// During animation always pass false so AppKit does not resize every frame.
    /// </param>
    private static WindowIcon MakeBatteryFrame(int level, double progress, bool compact)
    {
        progress = Math.Clamp(progress, 0, 1);
        var scale = RenderScale;

        var iconHeight = EarbudHeight * scale;
        var iconWidth = iconHeight * Artwork.Aspect;

        IBrush brush = PlatformUtils.IsOSX
            ? Brushes.Black
            : new SolidColorBrush(Settings.Data.AccentColor);
        var text = BuildText($"{level}%", TextHeight * scale, brush);

        var narrowWidth = SnapWidth(SidePadding * 2 * scale + iconWidth, scale);
        var wideWidth = SnapWidth(narrowWidth + Gap * scale + text.Ink.Width, scale);
        var width = compact ? narrowWidth : wideWidth;
        var height = (int)Math.Round(CanvasHeight * scale);

        // macOS menu-bar items grow/shrink leftward (right edge stays put). Keep the earbud locked
        // to that right edge at progress=0 so the narrow↔wide snap does not jump the icon on screen
        // — only the empty left side appears/disappears.
        var centeredIconX = (narrowWidth - iconWidth) / 2;
        var iconX = compact
            ? centeredIconX
            : Lerp(wideWidth - narrowWidth + centeredIconX, SidePadding * scale, progress);

        var render = new RenderTargetBitmap(new PixelSize(width, height), new Vector(96, 96));
        using (var ctx = render.CreateDrawingContext())
        {
            using var _ = ctx.PushRenderOptions(new RenderOptions
            {
                BitmapInterpolationMode = BitmapInterpolationMode.HighQuality,
                TextRenderingMode = TextRenderingMode.Antialias,
                EdgeMode = EdgeMode.Antialias,
                RequiresFullOpacityHandling = true
            });

            ctx.DrawImage(Artwork.Bitmap, Artwork.Ink,
                new Rect(iconX, (height - iconHeight) / 2, iconWidth, iconHeight));

            if (!compact && progress > 0.01)
            {
                // Both ink boxes share the canvas center line so digits and earbud read as one unit
                var origin = new Point(
                    iconX + iconWidth + Gap * scale - text.Ink.X,
                    (height - text.Ink.Height) / 2 - text.Ink.Y);
                using (ctx.PushOpacity(progress))
                    ctx.DrawText(text.Formatted, origin);
            }
        }

        return new WindowIcon(render);
    }

    private static double Lerp(double from, double to, double t) => from + (to - from) * t;

    /// <summary>
    /// Rounds up to a whole number of layout points. AppKit derives the status item width from the
    /// bitmap aspect ratio, so fractional pixel widths would make the content shift around.
    /// </summary>
    private static int SnapWidth(double pixels, double scale) =>
        (int)(Math.Max(1, Math.Ceiling(pixels / scale)) * scale);

    private static TrayText BuildText(string text, double inkHeight, IBrush brush)
    {
        // Font metrics give the em box, not the glyph ink box, so probe once and scale from there
        var probe = Measure(text, 100, brush);
        var fontSize = probe.Ink.Height > 0 ? 100 * inkHeight / probe.Ink.Height : inkHeight;
        return Measure(text, fontSize, brush);
    }

    private static TrayText Measure(string text, double fontSize, IBrush brush)
    {
        var formatted = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.Medium), fontSize, brush);
        return new TrayText(formatted, formatted.BuildGeometry(new Point(0, 0))?.Bounds ?? default);
    }

    /// <summary>Laid out percentage text plus the bounds of the glyph ink relative to its origin.</summary>
    private readonly record struct TrayText(FormattedText Formatted, Rect Ink);

    /// <summary>Tray bitmap together with the bounds of its non-transparent artwork.</summary>
    private sealed record TrayArtwork(Bitmap Bitmap, Rect Ink)
    {
        public double Aspect { get; } = Ink.Width / Ink.Height;

        public static TrayArtwork Load()
        {
            // OSX uses templated icons
            var type = PlatformUtils.IsOSX ? "black" :
                PlatformUtils.IsWindows ? "white_outlined_single" : "white_outlined_multi";
            var uri = $"{Program.AvaresUrl}/Resources/icon_{type}_tray.ico";
            var bitmap = new Bitmap(AssetLoader.Open(new Uri(uri)));
            return new TrayArtwork(bitmap, MeasureInk(bitmap));
        }

        /// <summary>
        /// The tray assets pad their artwork to a square, which would leave the earbuds far smaller
        /// than the surrounding menu bar glyphs once the whole bitmap is fitted to the bar height.
        /// </summary>
        private static Rect MeasureInk(Bitmap bitmap)
        {
            var size = bitmap.PixelSize;
            var stride = size.Width * 4;
            var buffer = Marshal.AllocHGlobal(stride * size.Height);
            try
            {
                bitmap.CopyPixels(new PixelRect(size), buffer, stride * size.Height, stride);

                int left = size.Width, top = size.Height, right = -1, bottom = -1;
                for (var y = 0; y < size.Height; y++)
                {
                    for (var x = 0; x < size.Width; x++)
                    {
                        if (Marshal.ReadByte(buffer, y * stride + x * 4 + 3) <= 8)
                            continue;
                        left = Math.Min(left, x);
                        top = Math.Min(top, y);
                        right = Math.Max(right, x);
                        bottom = Math.Max(bottom, y);
                    }
                }

                if (right >= left && bottom >= top)
                    return new Rect(left, top, right - left + 1, bottom - top + 1);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "WindowIconRenderer: cannot measure tray artwork bounds");
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }

            return new Rect(size.ToSize(1));
        }
    }
}
