using System.Drawing.Imaging;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Windows.Media.Imaging;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace KeyboardPadBridge.Services;

public sealed class ScreenshotShareService : IDisposable
{
    private const int Port = 8765;
    private readonly string screenshotDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "KeyBridge",
        "Screenshots");
    private readonly SemaphoreSlim fileLock = new(1, 1);
    private CancellationTokenSource? serverCancellation;
    private TcpListener? listener;
    private Task? serverTask;

    public string LatestScreenshotPath => Path.Combine(screenshotDirectory, "latest.png");

    public string LocalUrl => $"http://{GetLocalAddress()}:{Port}/";

    public async Task StartAsync()
    {
        if (listener is not null)
        {
            return;
        }

        Directory.CreateDirectory(screenshotDirectory);
        serverCancellation = new CancellationTokenSource();
        listener = new TcpListener(IPAddress.Any, Port);
        listener.Start();
        serverTask = Task.Run(() => RunServerAsync(serverCancellation.Token));
        await Task.CompletedTask;
    }

    public async Task<ScreenshotShareResult> CaptureLatestAsync()
    {
        Directory.CreateDirectory(screenshotDirectory);

        await fileLock.WaitAsync();
        try
        {
            var bounds = GetVirtualScreenBounds();
            using var bitmap = new Drawing.Bitmap(bounds.Width, bounds.Height);
            using (var graphics = Drawing.Graphics.FromImage(bitmap))
            {
                graphics.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bounds.Size);
            }

            bitmap.Save(LatestScreenshotPath, ImageFormat.Png);
        }
        finally
        {
            fileLock.Release();
        }

        return new ScreenshotShareResult(LatestScreenshotPath, $"{LocalUrl}latest.png");
    }

    public async Task<ScreenshotShareResult> SaveClipboardImageAsync(BitmapSource image)
    {
        Directory.CreateDirectory(screenshotDirectory);

        await fileLock.WaitAsync();
        try
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));
            await using var stream = File.Create(LatestScreenshotPath);
            encoder.Save(stream);
        }
        finally
        {
            fileLock.Release();
        }

        return new ScreenshotShareResult(LatestScreenshotPath, $"{LocalUrl}latest.png");
    }

    public void Dispose()
    {
        serverCancellation?.Cancel();
        listener?.Stop();
        try { serverTask?.Wait(TimeSpan.FromMilliseconds(500)); } catch { /* App shutdown cleanup only. */ }
        serverCancellation?.Dispose();
        fileLock.Dispose();
    }

    private async Task RunServerAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener!.AcceptTcpClientAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                continue;
            }

            _ = Task.Run(() => HandleClientAsync(client, cancellationToken), cancellationToken);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using var _ = client;
        await using var stream = client.GetStream();
        using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);

        var requestLine = await reader.ReadLineAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(requestLine))
        {
            return;
        }

        while (!string.IsNullOrEmpty(await reader.ReadLineAsync(cancellationToken)))
        {
            // Drain headers.
        }

        var path = requestLine.Split(' ', StringSplitOptions.RemoveEmptyEntries).ElementAtOrDefault(1) ?? "/";
        if (path.Equals("/latest.png", StringComparison.OrdinalIgnoreCase))
        {
            await SendLatestImageAsync(stream, cancellationToken);
            return;
        }

        await SendIndexAsync(stream, cancellationToken);
    }

    private async Task SendLatestImageAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        if (!File.Exists(LatestScreenshotPath))
        {
            await SendTextAsync(stream, HttpStatusCode.NotFound, "Capture a screenshot from KeyBridge first.", cancellationToken);
            return;
        }

        byte[] bytes;
        await fileLock.WaitAsync(cancellationToken);
        try
        {
            bytes = await File.ReadAllBytesAsync(LatestScreenshotPath, cancellationToken);
        }
        finally
        {
            fileLock.Release();
        }

        var header = Encoding.ASCII.GetBytes(
            "HTTP/1.1 200 OK\r\n" +
            "Content-Type: image/png\r\n" +
            $"Content-Length: {bytes.Length}\r\n" +
            "Cache-Control: no-store\r\n" +
            "Connection: close\r\n\r\n");
        await stream.WriteAsync(header, cancellationToken);
        await stream.WriteAsync(bytes, cancellationToken);
    }

    private async Task SendIndexAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var hasImage = File.Exists(LatestScreenshotPath);
        var body = hasImage
            ? "<!doctype html><meta name=\"viewport\" content=\"width=device-width\"><title>KeyBridge Screenshot</title><img src=\"/latest.png\" style=\"max-width:100%;height:auto\">"
            : "<!doctype html><meta name=\"viewport\" content=\"width=device-width\"><title>KeyBridge Screenshot</title><p>No screenshot captured yet.</p>";

        await SendTextAsync(stream, HttpStatusCode.OK, body, cancellationToken, "text/html; charset=utf-8");
    }

    private static async Task SendTextAsync(NetworkStream stream, HttpStatusCode statusCode, string body, CancellationToken cancellationToken, string contentType = "text/plain; charset=utf-8")
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        var header = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {(int)statusCode} {statusCode}\r\n" +
            $"Content-Type: {contentType}\r\n" +
            $"Content-Length: {bytes.Length}\r\n" +
            "Cache-Control: no-store\r\n" +
            "Connection: close\r\n\r\n");
        await stream.WriteAsync(header, cancellationToken);
        await stream.WriteAsync(bytes, cancellationToken);
    }

    private static Drawing.Rectangle GetVirtualScreenBounds()
    {
        var left = Forms.Screen.AllScreens.Min(screen => screen.Bounds.Left);
        var top = Forms.Screen.AllScreens.Min(screen => screen.Bounds.Top);
        var right = Forms.Screen.AllScreens.Max(screen => screen.Bounds.Right);
        var bottom = Forms.Screen.AllScreens.Max(screen => screen.Bounds.Bottom);
        return Drawing.Rectangle.FromLTRB(left, top, right, bottom);
    }

    private static string GetLocalAddress()
    {
        try
        {
            var gatewayAddress = NetworkInterface.GetAllNetworkInterfaces()
                .Where(networkInterface => networkInterface.OperationalStatus == OperationalStatus.Up)
                .Where(networkInterface => networkInterface.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(networkInterface => networkInterface.GetIPProperties().UnicastAddresses
                    .Where(address => address.Address.AddressFamily == AddressFamily.InterNetwork)
                    .Where(_ => networkInterface.GetIPProperties().GatewayAddresses.Any(gateway => gateway.Address.AddressFamily == AddressFamily.InterNetwork))
                    .Select(address => address.Address.ToString()))
                .FirstOrDefault();

            if (!string.IsNullOrWhiteSpace(gatewayAddress))
            {
                return gatewayAddress;
            }

            return Dns.GetHostEntry(Dns.GetHostName())
                .AddressList
                .Where(address => address.AddressFamily == AddressFamily.InterNetwork)
                .Select(address => address.ToString())
                .FirstOrDefault(address => !address.StartsWith("127.", StringComparison.Ordinal))
                ?? "127.0.0.1";
        }
        catch
        {
            return "127.0.0.1";
        }
    }
}

public sealed record ScreenshotShareResult(string FilePath, string Url);
