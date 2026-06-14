using System.Globalization;
using System.Windows.Media.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using DrawingRectangle = System.Drawing.Rectangle;
using FormsScreen = System.Windows.Forms.Screen;
using WpfApplication = System.Windows.Application;
using WpfBrushes = System.Windows.Media.Brushes;
using WpfFontFamily = System.Windows.Media.FontFamily;
using WpfImage = System.Windows.Controls.Image;
using WpfColor = System.Windows.Media.Color;
using WpfWindow = System.Windows.Window;

namespace PoeAncientsPriceHelper;

internal enum MemeKind { None, Mirror, Headhunter }
internal enum UnpricedReason { Unknown, Loading, MissingPrice, NeedsGemLevel }

internal sealed record PriceRow(
    int CenterY,
    string OcrText,
    decimal DivineValue,
    decimal ExaltedValue,
    bool HasPrice,
    int Multiplier = 1,
    string Name = "",
    bool ExactMatch = false,
    MemeKind Meme = MemeKind.None,
    UnpricedReason UnpricedReason = UnpricedReason.Unknown);

internal sealed class PriceOverlayWindow : WpfWindow
{
    private const int RowHalfHeight = 28;
    private const int OverlayWidth = 260;
    private const int OverlayMargin = 8;
    private const int IconSize = 32;

    private readonly Canvas _canvas = new();
    private readonly IconCache _icons;
    private readonly BitmapSource? _divineIcon;
    private readonly BitmapSource? _exaltedIcon;
    private readonly BitmapSource? _mirrorIcon;
    private readonly BitmapSource? _headhunterIcon;
    private IReadOnlyList<PriceRow> _rows = [];
    private DrawingRectangle _regionRect;
    private int _xOffset;
    private bool _panelOpen;
    private bool _debug;
    private DateTime _lastTopmostLogAtUtc = DateTime.MinValue;

    public PriceOverlayWindow(DrawingRectangle regionRect, int xOffset, IconCache icons, bool debug)
    {
        _regionRect = regionRect;
        _xOffset = xOffset;
        _icons = icons;
        _debug = debug;
        _divineIcon = ToBitmapSource(_icons.Divine);
        _exaltedIcon = ToBitmapSource(_icons.Exalted);
        _mirrorIcon = ToBitmapSource(_icons.Mirror);
        _headhunterIcon = ToBitmapSource(_icons.Headhunter);

        WindowStyle = System.Windows.WindowStyle.None;
        ResizeMode = System.Windows.ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Background = WpfBrushes.Transparent;
        AllowsTransparency = true;
        Content = _canvas;
        SizeToContent = System.Windows.SizeToContent.Manual;
        Left = regionRect.Right + xOffset;
        Top = regionRect.Top;
        Width = 2;
        Height = 2;

        LogOverlay($"wpf created region={_regionRect} xOffset={_xOffset} debug={_debug}");
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var handle = new WindowInteropHelper(this).Handle;
        var style = GetWindowLongPtr(handle, GWL_EXSTYLE).ToInt64();
        style |= WS_EX_TOOLWINDOW | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_LAYERED;
        SetWindowLongPtr(handle, GWL_EXSTYLE, new IntPtr(style));
        LogOverlay($"wpf extended styles applied handle={handle} exStyle=0x{style:X}");
    }

    public void SetGeometry(DrawingRectangle regionRect, int xOffset)
    {
        _regionRect = regionRect;
        _xOffset = xOffset;
        UpdateOverlayBounds();
        Render();
    }

    public void UpdateState(IReadOnlyList<PriceRow> rows, bool panelOpen, bool reading)
    {
        _rows = rows;
        _panelOpen = panelOpen;
        UpdateOverlayBounds();
        ApplyVisibility(reading);
        Render();
    }

    public void SetDebug(bool debug)
    {
        _debug = debug;
        UpdateOverlayBounds();
        ApplyVisibility(reading: false);
        Render();
    }

    public void HideNow()
    {
        _rows = [];
        _panelOpen = false;
        Hide();
        LogOverlay("wpf hidden now");
    }

    public void ForceTopmost()
    {
        if (!IsVisible) return;

        Topmost = false;
        Topmost = true;

        var handle = new WindowInteropHelper(this).Handle;
        var shouldLog = DateTime.UtcNow - _lastTopmostLogAtUtc >= TimeSpan.FromSeconds(10);
        if (handle != IntPtr.Zero && !SetWindowPos(handle, new IntPtr(-1), 0, 0, 0, 0, 0x0002 | 0x0001 | 0x0010))
            LogOverlay($"wpf topmost failed win32={Marshal.GetLastWin32Error()} bounds={BoundsText()} region={_regionRect}");
        else if (shouldLog)
        {
            _lastTopmostLogAtUtc = DateTime.UtcNow;
            LogOverlay($"wpf topmost ok handle={handle} bounds={BoundsText()} region={_regionRect}");
        }
    }

    private void ApplyVisibility(bool reading)
    {
        var priced = _rows.Count(r => r.HasPrice);
        var shouldShow = (_panelOpen && _rows.Count > 0) || _debug;

        if (shouldShow && !IsVisible)
        {
            Show();
            ForceTopmost();
            LogOverlay($"wpf show panelOpen={_panelOpen} reading={reading} debug={_debug} rows={_rows.Count} priced={priced} bounds={BoundsText()} region={_regionRect}");
        }
        else if (!shouldShow && IsVisible)
        {
            Hide();
            LogOverlay($"wpf hide panelOpen={_panelOpen} reading={reading} debug={_debug} rows={_rows.Count} priced={priced} bounds={BoundsText()} region={_regionRect}");
        }
    }

    private void UpdateOverlayBounds()
    {
        var screenBounds = FormsScreen.FromRectangle(_regionRect).Bounds;
        var desired = CalculateOverlayBounds(screenBounds, _regionRect, _xOffset, _rows, _debug);
        Left = desired.X;
        Top = desired.Y;
        Width = desired.Width;
        Height = desired.Height;
    }

    private void Render()
    {
        _canvas.Children.Clear();
        _canvas.Width = Width;
        _canvas.Height = Height;
        _canvas.Background = WpfBrushes.Transparent;

        if (_debug)
        {
            var outline = new Border
            {
                Width = Math.Max(1, Width),
                Height = Math.Max(1, Height),
                BorderBrush = new SolidColorBrush(WpfColor.FromRgb(255, 196, 90)),
                BorderThickness = new System.Windows.Thickness(1),
                Background = WpfBrushes.Transparent,
            };
            _canvas.Children.Add(outline);
        }

        if (!_panelOpen) return;

        foreach (var row in _rows)
        {
            var screenY = _regionRect.Top + row.CenterY;
            var localY = screenY - Top;
            if (row.HasPrice)
            {
                var label = BuildLabel(row);
                AddPriceRow(row, label, localY);
            }
            else
            {
                AddWarningRow(row, localY);
            }
        }
    }

    private void AddPriceRow(PriceRow row, string label, double centerY)
    {
        var icon = CurrencyIcon(row);
        var top = Math.Max(0, centerY - IconSize / 2.0);
        var textX = row.Meme == MemeKind.Headhunter ? IconSize * 2 + 6 : IconSize + 6;
        var stripWidth = Math.Min(OverlayWidth - 2, Math.Max(104, textX + EstimateTextWidth(label) + 10));
        var strip = new Border
        {
            Width = stripWidth,
            Height = 40,
            CornerRadius = new System.Windows.CornerRadius(5),
            Background = new SolidColorBrush(WpfColor.FromArgb(132, 18, 18, 22)),
            BorderBrush = new SolidColorBrush(WpfColor.FromArgb(110, 0, 0, 0)),
            BorderThickness = new System.Windows.Thickness(1),
        };
        Canvas.SetLeft(strip, -3);
        Canvas.SetTop(strip, Math.Max(0, centerY - strip.Height / 2.0));
        _canvas.Children.Add(strip);

        if (icon is not null)
        {
            var image = new WpfImage
            {
                Source = icon,
                Width = row.Meme == MemeKind.Headhunter ? IconSize * 2 : IconSize,
                Height = IconSize,
                Stretch = Stretch.Uniform,
                Effect = new DropShadowEffect
                {
                    Color = Colors.Black,
                    BlurRadius = 4,
                    ShadowDepth = 1,
                    Opacity = 0.85,
                },
            };
            Canvas.SetLeft(image, 0);
            Canvas.SetTop(image, top);
            _canvas.Children.Add(image);
        }
        else
        {
            AddOutlinedText(CurrencyFallback(row), 0, centerY - 16, WpfColor.FromRgb(245, 245, 245), 0f, 20);
        }

        AddOutlinedText(label, textX, centerY - 15, TextColor(row), DivineGlow(row), 24);
    }

    private static double EstimateTextWidth(string text) => Math.Max(0, text.Length * 11.5);

    private void AddWarningRow(PriceRow row, double centerY)
    {
        var marker = row.UnpricedReason switch
        {
            UnpricedReason.Loading => "...",
            UnpricedReason.NeedsGemLevel => "Lv?",
            _ => "n/a"
        };
        var loading = row.UnpricedReason == UnpricedReason.Loading;
        var strip = new Border
        {
            Width = 58,
            Height = 30,
            CornerRadius = new System.Windows.CornerRadius(5),
            Background = new SolidColorBrush(WpfColor.FromArgb(96, 18, 18, 22)),
            BorderBrush = new SolidColorBrush(loading
                ? WpfColor.FromArgb(80, 255, 176, 64)
                : WpfColor.FromArgb(96, 170, 145, 110)),
            BorderThickness = new System.Windows.Thickness(1),
            Child = new TextBlock
            {
                Text = marker,
                Foreground = new SolidColorBrush(loading
                    ? WpfColor.FromRgb(255, 176, 64)
                    : WpfColor.FromRgb(190, 176, 150)),
                FontFamily = new WpfFontFamily("Segoe UI"),
                FontSize = loading ? 20 : 16,
                FontWeight = System.Windows.FontWeights.Bold,
                TextAlignment = System.Windows.TextAlignment.Center,
                VerticalAlignment = System.Windows.VerticalAlignment.Center,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            },
        };
        Canvas.SetLeft(strip, 0);
        Canvas.SetTop(strip, Math.Max(0, centerY - strip.Height / 2.0));
        _canvas.Children.Add(strip);
    }

    private void AddOutlinedText(string text, double x, double y, WpfColor color, float glowStrength, double fontSize)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        if (glowStrength > 0f)
        {
            AddText(text, x, y, fontSize, WpfColor.FromArgb((byte)(115 + glowStrength * 80), 255, 210, 80), 0, 0, 8 + glowStrength * 5);
        }

        foreach (var (dx, dy) in new[] { (-2, 0), (2, 0), (0, -2), (0, 2), (-1, -1), (1, -1), (-1, 1), (1, 1) })
            AddText(text, x + dx, y + dy, fontSize, WpfColor.FromRgb(0, 0, 0), 0, 0, 0);

        AddText(text, x, y, fontSize, color, 0, 0, 0);
    }

    private void AddText(string text, double x, double y, double fontSize, WpfColor color, double shadowDepth, double shadowOpacity, double blurRadius)
    {
        var block = new TextBlock
        {
            Text = text,
            Foreground = new SolidColorBrush(color),
            FontFamily = new WpfFontFamily("Segoe UI"),
            FontSize = fontSize,
            FontWeight = System.Windows.FontWeights.Bold,
        };
        if (blurRadius > 0)
        {
            block.Effect = new DropShadowEffect
            {
                Color = color,
                BlurRadius = blurRadius,
                ShadowDepth = shadowDepth,
                Opacity = Math.Max(0.25, shadowOpacity),
            };
        }
        Canvas.SetLeft(block, x);
        Canvas.SetTop(block, y);
        _canvas.Children.Add(block);
    }

    private BitmapSource? CurrencyIcon(PriceRow row)
    {
        return row.Meme switch
        {
            MemeKind.Mirror => _mirrorIcon,
            MemeKind.Headhunter => _headhunterIcon,
            _ => row.DivineValue >= 1.0m ? _divineIcon : _exaltedIcon,
        };
    }

    private static string CurrencyFallback(PriceRow row)
    {
        return row.Meme switch
        {
            MemeKind.Mirror => "M",
            MemeKind.Headhunter => "HH",
            _ => row.DivineValue >= 1.0m ? "d" : "ex",
        };
    }

    private static WpfColor TextColor(PriceRow row)
    {
        if (row.Meme == MemeKind.Mirror) return WpfColor.FromRgb(190, 230, 255);
        if (row.Meme == MemeKind.Headhunter) return WpfColor.FromRgb(223, 142, 60);
        return WpfColor.FromRgb(255, 176, 64);
    }

    internal static string BuildLabel(PriceRow row)
    {
        if (row.Meme == MemeKind.Mirror)
        {
            var count = Math.Max(1, row.Multiplier);
            return count == 1 ? "1 Mirror" : $"{count} Mirrors";
        }
        if (row.Meme == MemeKind.Headhunter) return "HH/Mageblood";

        var mult = Math.Max(1, row.Multiplier);
        var useDivine = row.DivineValue >= 1.0m;
        var unit = useDivine ? row.DivineValue : row.ExaltedValue;
        var total = unit * mult;
        var suffix = useDivine ? "d" : "ex";
        var totalText = $"{FormatAmount(total)}{suffix}";
        if (mult <= 1)
            return totalText;

        return $"{totalText} ({FormatAmount(unit)}{suffix} ea)";
    }

    internal static string FormatAmount(decimal value)
    {
        if (value <= 0m) return "0";

        var format = value >= 100m ? "0"
            : value >= 10m ? "0.#"
            : value >= 1m ? "0.##"
            : "0.###";
        var text = value.ToString(format, CultureInfo.InvariantCulture);
        return text == "0" ? "<0.001" : text;
    }

    private static float DivineGlow(PriceRow row)
    {
        if (row.DivineValue < 1.0m) return 0f;
        var clamped = decimal.Min(100m, decimal.Max(1m, row.DivineValue));
        return 0.62f + (float)((clamped - 1m) / 99m) * 0.35f;
    }

    private static DrawingRectangle CalculateOverlayBounds(
        DrawingRectangle screenBounds,
        DrawingRectangle regionRect,
        int xOffset,
        IReadOnlyList<PriceRow> rows,
        bool debug)
    {
        var visible = rows.Where(r => r.HasPrice || !string.IsNullOrWhiteSpace(r.OcrText)).ToList();
        var x = regionRect.Right + xOffset;
        if (x + OverlayWidth > screenBounds.Right - OverlayMargin)
            x = Math.Max(screenBounds.Left + OverlayMargin, screenBounds.Right - OverlayWidth - OverlayMargin);

        if (visible.Count == 0)
        {
            var y = debug ? regionRect.Top : Math.Min(regionRect.Top, screenBounds.Bottom - 1);
            return new DrawingRectangle(x, y, debug ? 180 : 2, debug ? 40 : 2);
        }

        var minY = visible.Min(r => regionRect.Top + r.CenterY) - RowHalfHeight - OverlayMargin;
        var maxY = visible.Max(r => regionRect.Top + r.CenterY) + RowHalfHeight + OverlayMargin;
        var top = Math.Max(screenBounds.Top + OverlayMargin, minY);
        var bottom = Math.Min(screenBounds.Bottom - OverlayMargin, maxY);
        var height = Math.Max(IconSize + OverlayMargin * 2, bottom - top);
        return new DrawingRectangle(x, top, OverlayWidth, height);
    }

    private static BitmapSource? ToBitmapSource(System.Drawing.Bitmap? bitmap)
    {
        if (bitmap is null) return null;

        var handle = bitmap.GetHbitmap();
        try
        {
            var source = Imaging.CreateBitmapSourceFromHBitmap(
                handle,
                IntPtr.Zero,
                System.Windows.Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        finally
        {
            DeleteObject(handle);
        }
    }

    private string BoundsText() => $"{{X={(int)Left},Y={(int)Top},Width={(int)Width},Height={(int)Height}}}";

    private static void LogOverlay(string message)
    {
        try
        {
            File.AppendAllText(
                Path.Combine(AppContext.BaseDirectory, "overlay_log.txt"),
                $"[{DateTime.Now:HH:mm:ss.fff}] {message}\n");
        }
        catch { }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int X, int Y, int cx, int cy, uint uFlags);

    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TRANSPARENT = 0x00000020;
    private const long WS_EX_TOOLWINDOW = 0x00000080;
    private const long WS_EX_LAYERED = 0x00080000;
    private const long WS_EX_NOACTIVATE = 0x08000000;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr hObject);
}

internal static class PriceOverlayManager
{
    private static PriceOverlayWindow? _window;
    private static bool _debugEnabled;

    public static bool DebugEnabled => _debugEnabled;
    public static bool HasOverlay => _window is not null;

    public static void EnsureVisible(DrawingRectangle regionRect, int xOffset, IconCache icons)
    {
        OnUi(() =>
        {
            if (_window is null)
            {
                var screen = FormsScreen.FromRectangle(regionRect);
                LogOverlayManager($"wpf create screen={screen.DeviceName} bounds={screen.Bounds} region={regionRect} priceX={regionRect.Right + xOffset}");
                _window = new PriceOverlayWindow(regionRect, xOffset, icons, _debugEnabled);
                _window.Closed += (_, _) => _window = null;
            }
            else
            {
                var screen = FormsScreen.FromRectangle(regionRect);
                LogOverlayManager($"wpf reuse screen={screen.DeviceName} bounds={screen.Bounds} region={regionRect} priceX={regionRect.Right + xOffset}");
            }

            _window.SetGeometry(regionRect, xOffset);
        });
    }

    public static void Hide()
    {
        OnUi(() =>
        {
            if (_window is null) return;
            LogOverlayManager("wpf close overlay requested");
            _window.Close();
            _window = null;
        });
    }

    public static void UpdateState(IReadOnlyList<PriceRow> rows, bool panelOpen, bool reading)
    {
        OnUi(() =>
        {
            if (_window is null)
            {
                LogOverlayManager($"wpf update ignored no overlay rows={rows.Count} priced={rows.Count(r => r.HasPrice)} panelOpen={panelOpen} reading={reading}");
                return;
            }

            LogOverlayManager($"wpf update rows={rows.Count} priced={rows.Count(r => r.HasPrice)} panelOpen={panelOpen} reading={reading}");
            _window.UpdateState(rows, panelOpen, reading);
        });
    }

    public static void ForceTopmost()
    {
        OnUi(() => _window?.ForceTopmost());
    }

    public static void ToggleDebug()
    {
        _debugEnabled = !_debugEnabled;
        OnUi(() =>
        {
            LogOverlayManager($"wpf debug toggled enabled={_debugEnabled} hasOverlay={_window is not null}");
            _window?.SetDebug(_debugEnabled);
        });
    }

    public static void HideNow()
    {
        OnUi(() =>
        {
            LogOverlayManager("wpf hide now requested");
            _window?.HideNow();
        });
    }

    private static void OnUi(Action action)
    {
        var dispatcher = WpfApplication.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
            return;

        if (dispatcher.CheckAccess())
            action();
        else
            dispatcher.Invoke(action);
    }

    private static void LogOverlayManager(string message)
    {
        try
        {
            File.AppendAllText(
                Path.Combine(AppContext.BaseDirectory, "overlay_log.txt"),
                $"[{DateTime.Now:HH:mm:ss.fff}] manager {message}\n");
        }
        catch { }
    }
}
