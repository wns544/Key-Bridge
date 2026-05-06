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
        var panel = new StackPanel
        {
            MinWidth = 162,
            Margin = new Thickness(0),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center
        };

        panel.Children.Add(CreateStatusMark(symbol, isConnected));

        panel.Children.Add(new TextBlock
        {
            Text = $"{label} {(isConnected ? "연결" : "해제")}",
            FontFamily = new MediaFontFamily("Segoe UI Variable Text, Segoe UI"),
            FontSize = 14,
            FontWeight = FontWeights.Normal,
            Foreground = new SolidColorBrush(MediaColor.FromRgb(25, 42, 58)),
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            Margin = new Thickness(18, 0, 18, 20)
        });

        return new Border
        {
            Background = new SolidColorBrush(MediaColor.FromRgb(250, 250, 250)),
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 18,
                ShadowDepth = 2,
                Opacity = 0.18
            },
            Child = panel
        };
    }

    private static UIElement CreateStatusMark(string symbol, bool isConnected)
    {
        var mark = new Grid
        {
            Width = 96,
            Height = 45,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            Margin = new Thickness(0, 18, 0, 8)
        };

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
                FontSize = 30,
                FontWeight = FontWeights.SemiBold,
                Foreground = MediaBrushes.Black,
                TextAlignment = TextAlignment.Center,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            });
        }

        if (!isConnected)
        {
            mark.Children.Add(new System.Windows.Shapes.Line
            {
                X1 = 18,
                Y1 = 38,
                X2 = 78,
                Y2 = 5,
                Stroke = MediaBrushes.Black,
                StrokeThickness = 2.2,
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
            Width = 38,
            Height = 42,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        icon.Children.Add(new Border
        {
            Width = 31,
            Height = 40,
            CornerRadius = new CornerRadius(15, 15, 13, 13),
            BorderBrush = MediaBrushes.Black,
            BorderThickness = new Thickness(2.3),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        });

        icon.Children.Add(new Line
        {
            X1 = 19,
            Y1 = 3,
            X2 = 19,
            Y2 = 16,
            Stroke = MediaBrushes.Black,
            StrokeThickness = 1.8,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round
        });

        icon.Children.Add(new Ellipse
        {
            Width = 4,
            Height = 8,
            Fill = MediaBrushes.Black,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 7, 0, 0)
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
