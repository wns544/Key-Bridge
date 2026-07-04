using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using MediaBrushes = System.Windows.Media.Brushes;
using MediaColor = System.Windows.Media.Color;
using MediaFontFamily = System.Windows.Media.FontFamily;

namespace KeyboardPadBridge;

internal sealed class BridgeStatusToastWindow : Window
{
    private const int GwlExStyle = -20;
    private const int WsExNoActivate = 0x08000000;
    private const int WsExToolWindow = 0x00000080;

    private readonly DispatcherTimer closeTimer;

    public BridgeStatusToastWindow(string label, string symbol, bool isConnected)
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = MediaBrushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        Focusable = false;
        SizeToContent = SizeToContent.WidthAndHeight;

        Content = CreateContent(label, symbol, isConnected);

        Loaded += (_, _) => CenterOnPrimaryScreen();

        closeTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(1300)
        };
        closeTimer.Tick += (_, _) =>
        {
            closeTimer.Stop();
            Close();
        };
    }

    public void ShowBriefly()
    {
        Show();
        closeTimer.Stop();
        closeTimer.Start();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        var handle = new WindowInteropHelper(this).Handle;
        var style = GetWindowLong(handle, GwlExStyle);
        SetWindowLong(handle, GwlExStyle, style | WsExNoActivate | WsExToolWindow);
    }

    private static UIElement CreateContent(string label, string symbol, bool isConnected)
    {
        var panel = new Grid
        {
            MinWidth = 238
        };
        panel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var statusMark = CreateStatusMark(symbol, isConnected);
        Grid.SetColumn(statusMark, 0);
        panel.Children.Add(statusMark);

        var copy = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 0, 0)
        };
        copy.Children.Add(new TextBlock
        {
            Text = label,
            FontFamily = new MediaFontFamily("Segoe UI Variable Text, Segoe UI"),
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(MediaColor.FromRgb(17, 24, 39))
        });
        copy.Children.Add(new TextBlock
        {
            Text = isConnected ? "연결됨" : "해제됨",
            FontFamily = new MediaFontFamily("Segoe UI Variable Text, Segoe UI"),
            FontSize = 12,
            Foreground = new SolidColorBrush(isConnected
                ? MediaColor.FromRgb(22, 163, 74)
                : MediaColor.FromRgb(100, 116, 139)),
            Margin = new Thickness(0, 2, 0, 0)
        });
        Grid.SetColumn(copy, 1);
        panel.Children.Add(copy);

        return new Border
        {
            Background = new SolidColorBrush(MediaColor.FromRgb(255, 255, 255)),
            BorderBrush = new SolidColorBrush(MediaColor.FromRgb(221, 227, 234)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14, 12, 16, 12),
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 14,
                ShadowDepth = 2,
                Opacity = 0.12
            },
            Child = panel
        };
    }

    private static UIElement CreateStatusMark(string symbol, bool isConnected)
    {
        var mark = new Grid
        {
            Width = 42,
            Height = 42,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center
        };

        mark.Children.Add(new Border
        {
            Background = new SolidColorBrush(isConnected
                ? MediaColor.FromRgb(236, 253, 243)
                : MediaColor.FromRgb(248, 250, 252)),
            BorderBrush = new SolidColorBrush(isConnected
                ? MediaColor.FromRgb(187, 247, 208)
                : MediaColor.FromRgb(221, 227, 234)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(7)
        });

        if (symbol.Equals("Mouse", StringComparison.OrdinalIgnoreCase))
        {
            mark.Children.Add(CreateMousePictogram());
        }
        else
        {
            mark.Children.Add(new TextBlock
            {
                Text = symbol,
                FontFamily = new MediaFontFamily("Segoe UI Variable Display, Segoe UI"),
                FontSize = 17,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(MediaColor.FromRgb(37, 99, 235)),
                TextAlignment = TextAlignment.Center,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            });
        }

        if (!isConnected)
        {
            mark.Children.Add(new Line
            {
                X1 = 11,
                Y1 = 31,
                X2 = 31,
                Y2 = 11,
                Stroke = new SolidColorBrush(MediaColor.FromRgb(17, 24, 39)),
                StrokeThickness = 2,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round
            });
        }

        return mark;
    }

    private static UIElement CreateMousePictogram()
    {
        var icon = new Grid
        {
            Width = 23,
            Height = 27,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        icon.Children.Add(new Border
        {
            Width = 20,
            Height = 26,
            CornerRadius = new CornerRadius(10, 10, 8, 8),
            BorderBrush = new SolidColorBrush(MediaColor.FromRgb(37, 99, 235)),
            BorderThickness = new Thickness(1.8),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        });

        icon.Children.Add(new Line
        {
            X1 = 11.5,
            Y1 = 2,
            X2 = 11.5,
            Y2 = 10,
            Stroke = new SolidColorBrush(MediaColor.FromRgb(37, 99, 235)),
            StrokeThickness = 1.4,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round
        });

        icon.Children.Add(new Ellipse
        {
            Width = 3,
            Height = 6,
            Fill = new SolidColorBrush(MediaColor.FromRgb(37, 99, 235)),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 5, 0, 0)
        });

        return icon;
    }

    private void CenterOnPrimaryScreen()
    {
        Left = (SystemParameters.PrimaryScreenWidth - ActualWidth) / 2;
        Top = (SystemParameters.PrimaryScreenHeight - ActualHeight) / 2;
    }

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hwnd, int index, int newStyle);
}
