using System.Drawing.Imaging;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Authentication;
using System.Text;
using System.Text.Json;
using System.Windows.Media.Imaging;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace KeyboardPadBridge.Services;

public sealed class ScreenshotShareService : IDisposable
{
    private const int Port = 8765;
    private const int HttpsPort = 8766;
    private readonly string screenshotDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "KeyBridge",
        "Screenshots");
    private readonly SemaphoreSlim fileLock = new(1, 1);
    private readonly SemaphoreSlim textLock = new(1, 1);
    private readonly SemaphoreSlim clipboardLock = new(1, 1);
    private CancellationTokenSource? serverCancellation;
    private TcpListener? listener;
    private TcpListener? httpsListener;
    private Task? serverTask;
    private Task? httpsServerTask;
    private X509Certificate2? httpsCertificate;
    private string latestClipboardText = string.Empty;
    private DateTimeOffset? latestClipboardTextUpdatedAt;
    private ClipboardShareKind latestClipboardKind = ClipboardShareKind.None;
    private DateTimeOffset? latestClipboardUpdatedAt;
    private List<SharedClipboardFile> latestClipboardFiles = new();
    private string sharePin = GenerateSharePin();
    private string accessToken = GenerateAccessToken();

    public string LatestScreenshotPath => Path.Combine(screenshotDirectory, "latest.png");

    public string LocalUrl => $"http://{GetLocalAddress()}:{Port}/";

    public string HttpsLocalUrl => $"https://{GetLocalAddress()}:{HttpsPort}/";

    public string ClipboardTextUrl => $"{HttpsLocalUrl}text?t={accessToken}";

    public string ClipboardUrl => $"{HttpsLocalUrl}clipboard?t={accessToken}";

    public string HttpClipboardUrl => $"{LocalUrl}clipboard?t={accessToken}";

    public string SharePin => sharePin;

    public void SetSharePin(string pin)
    {
        sharePin = string.IsNullOrWhiteSpace(pin) ? GenerateSharePin() : pin.Trim();
    }

    public void RegenerateAccessToken()
    {
        accessToken = GenerateAccessToken();
        latestClipboardUpdatedAt = DateTimeOffset.Now;
    }

    public async Task StartAsync()
    {
        if (listener is not null)
        {
            return;
        }

        Directory.CreateDirectory(screenshotDirectory);
        serverCancellation = new CancellationTokenSource();
        listener = new TcpListener(IPAddress.Any, Port);
        httpsListener = new TcpListener(IPAddress.Any, HttpsPort);
        httpsCertificate = CreateHttpsCertificate();
        listener.Start();
        httpsListener.Start();
        serverTask = Task.Run(() => RunServerAsync(serverCancellation.Token));
        httpsServerTask = Task.Run(() => RunHttpsServerAsync(serverCancellation.Token));
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
        await clipboardLock.WaitAsync();
        try
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));
            await using var stream = File.Create(LatestScreenshotPath);
            encoder.Save(stream);
            latestClipboardKind = ClipboardShareKind.Image;
            latestClipboardUpdatedAt = DateTimeOffset.Now;
        }
        finally
        {
            clipboardLock.Release();
            fileLock.Release();
        }

        return new ScreenshotShareResult(LatestScreenshotPath, $"{LocalUrl}latest.png");
    }

    public async Task<ClipboardTextShareResult> PublishClipboardTextAsync(string text)
    {
        await clipboardLock.WaitAsync();
        await textLock.WaitAsync();
        try
        {
            latestClipboardText = text;
            latestClipboardTextUpdatedAt = DateTimeOffset.Now;
            latestClipboardKind = ClipboardShareKind.Text;
            latestClipboardUpdatedAt = latestClipboardTextUpdatedAt;
        }
        finally
        {
            clipboardLock.Release();
            textLock.Release();
        }

        return new ClipboardTextShareResult(ClipboardTextUrl, text.Length);
    }

    public async Task<ClipboardFileShareResult> PublishClipboardFilesAsync(IEnumerable<string> filePaths)
    {
        var files = filePaths
            .Where(File.Exists)
            .Select(path => new FileInfo(path))
            .Select((file, index) => new SharedClipboardFile(index, file.FullName, file.Name, file.Length))
            .ToList();

        await clipboardLock.WaitAsync();
        try
        {
            latestClipboardFiles = files;
            latestClipboardKind = files.Count > 0 ? ClipboardShareKind.Files : ClipboardShareKind.None;
            latestClipboardUpdatedAt = DateTimeOffset.Now;
        }
        finally
        {
            clipboardLock.Release();
        }

        return new ClipboardFileShareResult(ClipboardUrl, files.Count);
    }

    public void Dispose()
    {
        serverCancellation?.Cancel();
        listener?.Stop();
        httpsListener?.Stop();
        try { serverTask?.Wait(TimeSpan.FromMilliseconds(500)); } catch { /* App shutdown cleanup only. */ }
        try { httpsServerTask?.Wait(TimeSpan.FromMilliseconds(500)); } catch { /* App shutdown cleanup only. */ }
        serverCancellation?.Dispose();
        httpsCertificate?.Dispose();
        fileLock.Dispose();
        textLock.Dispose();
        clipboardLock.Dispose();
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

    private async Task RunHttpsServerAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await httpsListener!.AcceptTcpClientAsync(cancellationToken);
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

            _ = Task.Run(() => HandleHttpsClientAsync(client, cancellationToken), cancellationToken);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using var _ = client;
        await using var stream = client.GetStream();
        await HandleRequestAsync(stream, cancellationToken);
    }

    private async Task HandleHttpsClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using var _ = client;
        await using var networkStream = client.GetStream();
        await using var stream = new SslStream(networkStream, leaveInnerStreamOpen: false);
        try
        {
            await stream.AuthenticateAsServerAsync(
                httpsCertificate!,
                clientCertificateRequired: false,
                enabledSslProtocols: SslProtocols.Tls12,
                checkCertificateRevocation: false);
            await HandleRequestAsync(stream, cancellationToken);
        }
        catch
        {
            // Browser rejected the local certificate or closed the TLS handshake.
        }
    }

    private async Task HandleRequestAsync(Stream stream, CancellationToken cancellationToken)
    {
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

        var rawPath = requestLine.Split(' ', StringSplitOptions.RemoveEmptyEntries).ElementAtOrDefault(1) ?? "/";
        var query = string.Empty;
        var queryStart = rawPath.IndexOf('?', StringComparison.Ordinal);
        if (queryStart >= 0)
        {
            query = rawPath[(queryStart + 1)..];
        }
        var path = queryStart >= 0 ? rawPath[..queryStart] : rawPath;
        path = Uri.UnescapeDataString(path);
        if (!path.StartsWith("/", StringComparison.Ordinal))
        {
            path = "/" + path;
        }

        var isProtectedPath = path.Equals("/clipboard", StringComparison.OrdinalIgnoreCase)
            || path.Equals("/clipboard.state", StringComparison.OrdinalIgnoreCase)
            || path.Equals("/text", StringComparison.OrdinalIgnoreCase)
            || path.Equals("/text.raw", StringComparison.OrdinalIgnoreCase)
            || path.Equals("/latest.png", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/file/", StringComparison.OrdinalIgnoreCase);

        if (isProtectedPath && !IsValidAccessToken(query))
        {
            await SendTextAsync(stream, HttpStatusCode.Forbidden, "Invalid KeyBridge clipboard token.", cancellationToken);
            return;
        }

        if (path.Equals("/latest.png", StringComparison.OrdinalIgnoreCase))
        {
            await SendTextAsync(stream, HttpStatusCode.Forbidden, "Images are encrypted on /clipboard. Open the clipboard page and enter the PIN.", cancellationToken);
            return;
        }

        if (path.StartsWith("/file/", StringComparison.OrdinalIgnoreCase))
        {
            await SendTextAsync(stream, HttpStatusCode.Forbidden, "File downloads are disabled in secure clipboard mode.", cancellationToken);
            return;
        }

        if (path.Equals("/clipboard.state", StringComparison.OrdinalIgnoreCase))
        {
            await SendClipboardStateAsync(stream, cancellationToken);
            return;
        }

        if (path.Equals("/clipboard", StringComparison.OrdinalIgnoreCase))
        {
            await SendClipboardPageAsync(stream, cancellationToken);
            return;
        }

        if (path.Equals("/text.raw", StringComparison.OrdinalIgnoreCase))
        {
            await SendTextAsync(stream, HttpStatusCode.Forbidden, "Text is encrypted on /clipboard. Open the clipboard page and enter the PIN.", cancellationToken);
            return;
        }

        if (path.Equals("/text", StringComparison.OrdinalIgnoreCase))
        {
            await SendTextAsync(stream, HttpStatusCode.Forbidden, "Open /clipboard with the KeyBridge token and PIN.", cancellationToken);
            return;
        }

        await SendIndexAsync(stream, cancellationToken);
    }

    private async Task SendLatestImageAsync(Stream stream, CancellationToken cancellationToken)
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

    private async Task SendIndexAsync(Stream stream, CancellationToken cancellationToken)
    {
        var hasImage = File.Exists(LatestScreenshotPath);
        var body = hasImage
            ? $"<!doctype html><meta name=\"viewport\" content=\"width=device-width\"><title>KeyBridge</title><p><a href=\"/clipboard?t={WebUtility.HtmlEncode(accessToken)}\">Open clipboard share</a></p><p>Image content is encrypted on the clipboard page.</p>"
            : $"<!doctype html><meta name=\"viewport\" content=\"width=device-width\"><title>KeyBridge</title><p><a href=\"/clipboard?t={WebUtility.HtmlEncode(accessToken)}\">Open clipboard share</a></p><p>No screenshot captured yet.</p>";

        await SendTextAsync(stream, HttpStatusCode.OK, body, cancellationToken, "text/html; charset=utf-8");
    }

    private async Task SendClipboardStateAsync(Stream stream, CancellationToken cancellationToken)
    {
        ClipboardShareKind kind;
        DateTimeOffset? updatedAt;
        int textLength;
        int fileCount;
        await clipboardLock.WaitAsync(cancellationToken);
        await textLock.WaitAsync(cancellationToken);
        try
        {
            kind = latestClipboardKind;
            updatedAt = latestClipboardUpdatedAt;
            textLength = latestClipboardText.Length;
            fileCount = latestClipboardFiles.Count;
        }
        finally
        {
            textLock.Release();
            clipboardLock.Release();
        }

        var state = CreateClipboardState(kind, updatedAt, textLength, fileCount);
        await SendTextAsync(stream, HttpStatusCode.OK, state, cancellationToken);
    }

    private async Task SendClipboardPageAsync(Stream stream, CancellationToken cancellationToken)
    {
        ClipboardShareKind kind;
        DateTimeOffset? updatedAt;
        string text;
        List<SharedClipboardFile> files;
        string? encryptedPayload = null;

        await clipboardLock.WaitAsync(cancellationToken);
        await textLock.WaitAsync(cancellationToken);
        try
        {
            kind = latestClipboardKind;
            updatedAt = latestClipboardUpdatedAt;
            text = latestClipboardText;
            files = latestClipboardFiles.ToList();
        }
        finally
        {
            textLock.Release();
            clipboardLock.Release();
        }

        var content = kind switch
        {
            ClipboardShareKind.Text => CreateSecureClipboardSection(),
            ClipboardShareKind.Image => CreateSecureClipboardSection(),
            ClipboardShareKind.Files => "<p class=\"empty\">보안 모드에서는 파일 공유를 지원하지 않습니다.</p>",
            _ => "<p class=\"empty\">아직 공유된 클립보드가 없습니다.</p>"
        };
        if (kind is ClipboardShareKind.Text or ClipboardShareKind.Image)
        {
            encryptedPayload = await CreateEncryptedClipboardPayloadAsync(kind, text, cancellationToken);
        }
        var updatedText = updatedAt is null
            ? "대기 중"
            : $"업데이트 {WebUtility.HtmlEncode(updatedAt.Value.ToString("HH:mm:ss"))}";

        var body = $$"""
            <!doctype html>
            <html lang="ko">
            <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <title>KeyBridge Clipboard</title>
            <style>
            :root { color-scheme: light; font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif; }
            body { margin: 0; padding: 14px; background: #f5f6f8; color: #14171a; }
            main { max-width: 820px; margin: 0 auto; }
            .topbar { display: flex; align-items: baseline; justify-content: space-between; gap: 12px; margin: 0 0 10px; }
            h1 { margin: 0; font-size: 22px; line-height: 1.15; }
            p { margin: 0 0 10px; color: #5b6470; }
            textarea { box-sizing: border-box; width: 100%; min-height: 50vh; padding: 12px; border: 1px solid #cfd5dc; border-radius: 10px; background: #fff; color: #111; font: 16px/1.45 ui-monospace, SFMono-Regular, Menlo, monospace; }
            button, .button { display: block; box-sizing: border-box; width: 100%; margin-top: 10px; padding: 12px 14px; border: 0; border-radius: 10px; background: #111827; color: #fff; font-size: 17px; font-weight: 700; text-align: center; text-decoration: none; }
            .content-action { margin: 0 0 10px; }
            img { display: block; max-width: 100%; max-height: 64vh; height: auto; border-radius: 10px; background: #fff; object-fit: contain; }
            ul { padding: 0; margin: 0; list-style: none; }
            li { margin: 0 0 8px; }
            label { display: block; margin: 0 0 6px; font-weight: 800; }
            input { box-sizing: border-box; width: 100%; height: 44px; padding: 0 12px; border: 1px solid #cfd5dc; border-radius: 10px; font-size: 18px; }
            .unlock { padding: 12px; border: 1px solid #d9dee5; border-radius: 14px; background: #fff; }
            .pin-help { margin: 0 0 10px; font-size: 13px; line-height: 1.35; }
            .file { display: block; padding: 12px 14px; border: 1px solid #cfd5dc; border-radius: 10px; background: #fff; color: #111827; text-decoration: none; }
            .meta, .empty, #status { color: #5b6470; }
            .meta { flex: 0 0 auto; margin: 0; font-size: 13px; white-space: nowrap; }
            #status { margin-top: 8px; font-size: 14px; font-weight: 600; }
            @media (max-width: 520px) {
              body { padding: 10px; }
              h1 { font-size: 20px; }
              textarea { min-height: 46vh; font-size: 15px; }
            }
            </style>
            </head>
            <body>
            <main data-state="{{WebUtility.HtmlEncode(CreateClipboardState(kind, updatedAt, text.Length, files.Count))}}">
            <div class="topbar">
              <h1>KeyBridge</h1>
              <p class="meta" id="meta">{{updatedText}}</p>
            </div>
            {{content}}
            <div id="status"></div>
            </main>
            <script>
            const encryptedPayload = {{(encryptedPayload is null ? "null" : encryptedPayload)}};
            const root = document.querySelector('main');
            let textarea = document.getElementById('text');
            const status = document.getElementById('status');
            const copyButton = document.getElementById('copy');
            let copyImageButton = document.getElementById('copy-image');
            const unlockButton = document.getElementById('unlock');
            const pinInput = document.getElementById('pin');
            let latestImageBlob = null;
            let latestImageUrl = null;
            const decoder = new TextDecoder();
            function fromBase64(value) {
              const binary = atob(value);
              const bytes = new Uint8Array(binary.length);
              for (let index = 0; index < binary.length; index++) bytes[index] = binary.charCodeAt(index);
              return bytes;
            }
            async function deriveKey(pin, salt) {
              const baseKey = await crypto.subtle.importKey('raw', new TextEncoder().encode(pin), 'PBKDF2', false, ['deriveKey']);
              return crypto.subtle.deriveKey(
                { name: 'PBKDF2', salt, iterations: encryptedPayload.iterations, hash: 'SHA-256' },
                baseKey,
                { name: 'AES-GCM', length: 256 },
                false,
                ['decrypt']
              );
            }
            async function unlockClipboard() {
              if (!encryptedPayload) return;
              if (!window.isSecureContext || !crypto?.subtle) {
                status.textContent = '이 주소에서는 브라우저가 보안 해제를 막습니다. 앱의 HTTPS 주소나 터널 주소로 다시 열어주세요.';
                return;
              }
              try {
                const key = await deriveKey(pinInput.value, fromBase64(encryptedPayload.salt));
                const bytes = await crypto.subtle.decrypt(
                  { name: 'AES-GCM', iv: fromBase64(encryptedPayload.iv) },
                  key,
                  fromBase64(encryptedPayload.ciphertext)
                );
                const payload = JSON.parse(decoder.decode(bytes));
                renderPayload(payload);
                sessionStorage.setItem('keybridge-pin', pinInput.value);
                document.querySelector('.unlock')?.setAttribute('hidden', '');
                status.textContent = '내용을 열었습니다.';
              } catch {
                status.textContent = 'PIN이 다르거나 전송 내용이 손상되었습니다.';
              }
            }
            function renderPayload(payload) {
              const holder = document.getElementById('secure-content');
              if (!holder) return;
              if (payload.kind === 'Text') {
                holder.innerHTML = '<button id="copy" class="content-action" type="button">복사</button><textarea id="text" spellcheck="false"></textarea>';
                textarea = document.getElementById('text');
                textarea.value = payload.text || '';
                document.getElementById('copy').addEventListener('click', copySharedText);
                return;
              }
              if (payload.kind === 'Image') {
                const bytes = fromBase64(payload.data || '');
                latestImageBlob = new Blob([bytes], { type: payload.mimeType || 'image/png' });
                if (latestImageUrl) URL.revokeObjectURL(latestImageUrl);
                latestImageUrl = URL.createObjectURL(latestImageBlob);
                holder.innerHTML = '<p>PIN으로 보호된 이미지입니다. 복사 버튼이 막히면 이미지를 길게 누르세요.</p><img id="shared-image" alt="Shared clipboard image"><button id="copy-image" type="button">이미지 복사</button>';
                document.getElementById('shared-image').src = latestImageUrl;
                copyImageButton = document.getElementById('copy-image');
                copyImageButton.addEventListener('click', copySharedImage);
              }
            }
            async function copySharedText() {
              try {
                if (!textarea) throw new Error('No text');
                await navigator.clipboard.writeText(textarea.value);
                status.textContent = 'iPad 클립보드에 복사했습니다.';
              } catch {
                textarea.focus();
                textarea.select();
                document.execCommand('copy');
                status.textContent = '선택한 텍스트를 복사했습니다.';
              }
            }
            async function copySharedImage() {
              try {
                if (!latestImageBlob) throw new Error('No image');
                await navigator.clipboard.write([
                  new ClipboardItem({ [latestImageBlob.type || 'image/png']: latestImageBlob })
                ]);
                status.textContent = 'iPad 클립보드에 이미지를 복사했습니다.';
              } catch {
                status.textContent = 'Safari가 이미지 복사를 막았습니다. 이미지를 길게 누르거나 이미지 열기를 사용하세요.';
              }
            }
            if (copyButton && textarea) {
              copyButton.addEventListener('click', copySharedText);
            }
            if (copyImageButton) {
              copyImageButton.addEventListener('click', copySharedImage);
            }
            document.addEventListener('keydown', async (event) => {
              if (event.key?.toLowerCase() !== 'v' || (!event.metaKey && !event.ctrlKey)) return;
              event.preventDefault();
              if (textarea) {
                await copySharedText();
              } else if (copyImageButton) {
                await copySharedImage();
              } else {
                status.textContent = '복사할 공유 내용이 없습니다.';
              }
            });
            if (unlockButton && pinInput) {
              unlockButton.addEventListener('click', unlockClipboard);
              pinInput.addEventListener('keydown', (event) => {
                if (event.key === 'Enter') unlockClipboard();
              });
              const savedPin = sessionStorage.getItem('keybridge-pin');
              if (savedPin) {
                pinInput.value = savedPin;
                unlockClipboard();
              }
            }
            setInterval(async () => {
              try {
                const response = await fetch('/clipboard.state?t={{accessToken}}', { cache: 'no-store' });
                if (!response.ok) return;
                const state = await response.text();
                if (state !== root.dataset.state && document.activeElement !== textarea) {
                  location.reload();
                }
              } catch {}
            }, 1500);
            </script>
            </body>
            </html>
            """;

        await SendTextAsync(stream, HttpStatusCode.OK, body, cancellationToken, "text/html; charset=utf-8");
    }

    private async Task SendClipboardFileAsync(Stream stream, string path, CancellationToken cancellationToken)
    {
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2 || !int.TryParse(segments[1], out var index))
        {
            await SendTextAsync(stream, HttpStatusCode.BadRequest, "Invalid file URL.", cancellationToken);
            return;
        }

        SharedClipboardFile? sharedFile;
        await clipboardLock.WaitAsync(cancellationToken);
        try
        {
            sharedFile = latestClipboardFiles.FirstOrDefault(file => file.Index == index);
        }
        finally
        {
            clipboardLock.Release();
        }

        if (sharedFile is null || !File.Exists(sharedFile.Path))
        {
            await SendTextAsync(stream, HttpStatusCode.NotFound, "File is no longer available.", cancellationToken);
            return;
        }

        await using var fileStream = File.OpenRead(sharedFile.Path);
        var contentType = GetContentType(sharedFile.Name);
        var header = Encoding.UTF8.GetBytes(
            "HTTP/1.1 200 OK\r\n" +
            $"Content-Type: {contentType}\r\n" +
            $"Content-Length: {fileStream.Length}\r\n" +
            $"Content-Disposition: inline; filename=\"{EscapeHeaderValue(sharedFile.Name)}\"\r\n" +
            "Cache-Control: no-store\r\n" +
            "Connection: close\r\n\r\n");
        await stream.WriteAsync(header, cancellationToken);
        await fileStream.CopyToAsync(stream, cancellationToken);
    }

    private async Task SendLatestClipboardTextRawAsync(Stream stream, CancellationToken cancellationToken)
    {
        string text;
        await textLock.WaitAsync(cancellationToken);
        try
        {
            text = latestClipboardText;
        }
        finally
        {
            textLock.Release();
        }

        await SendTextAsync(stream, HttpStatusCode.OK, text, cancellationToken);
    }

    private async Task SendClipboardTextPageAsync(Stream stream, CancellationToken cancellationToken)
    {
        string text;
        DateTimeOffset? updatedAt;
        await textLock.WaitAsync(cancellationToken);
        try
        {
            text = latestClipboardText;
            updatedAt = latestClipboardTextUpdatedAt;
        }
        finally
        {
            textLock.Release();
        }

        var updatedText = updatedAt is null
            ? "No text sent yet."
            : $"Updated {WebUtility.HtmlEncode(updatedAt.Value.ToString("HH:mm:ss"))}";
        var body = $$"""
            <!doctype html>
            <html lang="ko">
            <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <title>KeyBridge Text</title>
            <style>
            :root { color-scheme: light; font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif; }
            body { margin: 0; padding: 24px; background: #f5f6f8; color: #14171a; }
            main { max-width: 760px; margin: 0 auto; }
            h1 { margin: 0 0 8px; font-size: 28px; }
            p { margin: 0 0 18px; color: #5b6470; }
            textarea { box-sizing: border-box; width: 100%; min-height: 58vh; padding: 16px; border: 1px solid #cfd5dc; border-radius: 12px; background: #fff; color: #111; font: 17px/1.55 ui-monospace, SFMono-Regular, Menlo, monospace; }
            button { width: 100%; margin: 0 0 14px; padding: 15px 18px; border: 0; border-radius: 12px; background: #111827; color: #fff; font-size: 18px; font-weight: 700; }
            #status { margin-top: 12px; color: #2f6f4e; font-weight: 600; }
            </style>
            </head>
            <body>
            <main>
            <h1>KeyBridge Text</h1>
            <p id="meta">{{updatedText}}</p>
            <button id="copy" class="content-action" type="button">복사</button>
            <textarea id="text" spellcheck="false">{{WebUtility.HtmlEncode(text)}}</textarea>
            <div id="status"></div>
            </main>
            <script>
            const textarea = document.getElementById('text');
            const status = document.getElementById('status');
            document.getElementById('copy').addEventListener('click', async () => {
              try {
                await navigator.clipboard.writeText(textarea.value);
                status.textContent = 'iPad 클립보드에 복사했습니다.';
              } catch {
                textarea.focus();
                textarea.select();
                document.execCommand('copy');
                status.textContent = '선택한 텍스트를 복사했습니다.';
              }
            });
            async function refreshText() {
              try {
                const response = await fetch('/text.raw', { cache: 'no-store' });
                if (!response.ok) return;
                const nextText = await response.text();
                if (document.activeElement !== textarea && textarea.value !== nextText) {
                  textarea.value = nextText;
                  document.getElementById('meta').textContent = 'Updated just now';
                  status.textContent = '새 텍스트를 받았습니다.';
                }
              } catch {}
            }
            setInterval(refreshText, 1500);
            </script>
            </body>
            </html>
            """;

        await SendTextAsync(stream, HttpStatusCode.OK, body, cancellationToken, "text/html; charset=utf-8");
    }

    private static string CreateClipboardTextSection(string text)
    {
        return $$"""
            <button id="copy" class="content-action" type="button">복사</button>
            <textarea id="text" spellcheck="false">{{WebUtility.HtmlEncode(text)}}</textarea>
            """;
    }

    private static string CreateClipboardImageSection()
    {
        return """
            <p>이미지는 길게 눌러 복사하거나 공유 메뉴로 저장할 수 있습니다.</p>
            <img src="/latest.png" alt="Shared clipboard image">
            <button id="copy-image" type="button">이미지 복사</button>
            <a class="button" href="/latest.png">이미지 열기</a>
            """;
    }

    private static string CreateClipboardFilesSection(IReadOnlyList<SharedClipboardFile> files)
    {
        if (files.Count == 0)
        {
            return "<p class=\"empty\">공유할 수 있는 파일이 없습니다.</p>";
        }

        var builder = new StringBuilder("<ul>");
        foreach (var file in files)
        {
            var href = $"/file/{file.Index}/{Uri.EscapeDataString(file.Name)}";
            var label = $"{WebUtility.HtmlEncode(file.Name)} · {FormatFileSize(file.Length)}";
            builder.Append("<li><a class=\"file\" href=\"")
                .Append(href)
                .Append("\">")
                .Append(label)
                .Append("</a></li>");
        }

        builder.Append("</ul>");
        return builder.ToString();
    }

    private static string CreateClipboardState(ClipboardShareKind kind, DateTimeOffset? updatedAt, int textLength, int fileCount)
    {
        return $"{kind}|{updatedAt?.ToUnixTimeMilliseconds() ?? 0}|{textLength}|{fileCount}";
    }

    private static string CreateSecureClipboardSection()
    {
        return """
            <div class="unlock">
              <label for="pin">앱에 표시된 PIN</label>
              <p class="pin-help">PC에서 보낸 내용을 여는 임시 비밀번호입니다.</p>
              <input id="pin" inputmode="numeric" autocomplete="off" placeholder="앱 화면의 PIN 입력">
              <button id="unlock" type="button">내용 열기</button>
            </div>
            <div id="secure-content"></div>
            """;
    }

    private async Task<string> CreateEncryptedClipboardPayloadAsync(ClipboardShareKind kind, string text, CancellationToken cancellationToken)
    {
        object payload;
        if (kind == ClipboardShareKind.Image)
        {
            byte[] imageBytes;
            await fileLock.WaitAsync(cancellationToken);
            try
            {
                imageBytes = File.Exists(LatestScreenshotPath)
                    ? await File.ReadAllBytesAsync(LatestScreenshotPath, cancellationToken)
                    : [];
            }
            finally
            {
                fileLock.Release();
            }

            payload = new
            {
                Kind = "Image",
                MimeType = "image/png",
                Data = Convert.ToBase64String(imageBytes)
            };
        }
        else
        {
            payload = new
            {
                Kind = "Text",
                Text = text
            };
        }

        return EncryptPayload(payload, sharePin);
    }

    private static string EncryptPayload(object payload, string pin)
    {
        const int iterations = 150000;
        var salt = RandomNumberGenerator.GetBytes(16);
        var iv = RandomNumberGenerator.GetBytes(12);
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(payload, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });
        var key = Rfc2898DeriveBytes.Pbkdf2(pin, salt, iterations, HashAlgorithmName.SHA256, 32);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[16];

        using (var aes = new AesGcm(key, tag.Length))
        {
            aes.Encrypt(iv, plaintext, ciphertext, tag);
        }

        var combined = new byte[ciphertext.Length + tag.Length];
        Buffer.BlockCopy(ciphertext, 0, combined, 0, ciphertext.Length);
        Buffer.BlockCopy(tag, 0, combined, ciphertext.Length, tag.Length);

        return JsonSerializer.Serialize(new
        {
            version = 1,
            algorithm = "AES-GCM",
            iterations,
            salt = Convert.ToBase64String(salt),
            iv = Convert.ToBase64String(iv),
            ciphertext = Convert.ToBase64String(combined)
        });
    }

    private bool IsValidAccessToken(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return false;
        }

        foreach (var part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pieces = part.Split('=', 2);
            var name = WebUtility.UrlDecode(pieces[0]);
            if (!string.Equals(name, "t", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(name, "token", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var value = pieces.Length > 1 ? WebUtility.UrlDecode(pieces[1]) : string.Empty;
            return CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(value),
                Encoding.UTF8.GetBytes(accessToken));
        }

        return false;
    }

    private static async Task SendTextAsync(Stream stream, HttpStatusCode statusCode, string body, CancellationToken cancellationToken, string contentType = "text/plain; charset=utf-8")
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

    private static string EscapeHeaderValue(string value)
    {
        return value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
    }

    private static string FormatFileSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        double size = bytes;
        var unitIndex = 0;
        while (size >= 1024 && unitIndex < units.Length - 1)
        {
            size /= 1024;
            unitIndex++;
        }

        return unitIndex == 0 ? $"{bytes} {units[unitIndex]}" : $"{size:0.##} {units[unitIndex]}";
    }

    private static string GetContentType(string fileName)
    {
        return Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".pdf" => "application/pdf",
            ".txt" => "text/plain; charset=utf-8",
            ".html" or ".htm" => "text/html; charset=utf-8",
            ".json" => "application/json; charset=utf-8",
            ".csv" => "text/csv; charset=utf-8",
            _ => "application/octet-stream"
        };
    }

    private static string GenerateSharePin()
    {
        return RandomNumberGenerator.GetInt32(100000, 1000000).ToString("D6");
    }

    private static string GenerateAccessToken()
    {
        return Convert.ToBase64String(RandomNumberGenerator.GetBytes(24))
            .Replace("+", "-", StringComparison.Ordinal)
            .Replace("/", "_", StringComparison.Ordinal)
            .TrimEnd('=');
    }

    private static X509Certificate2 CreateHttpsCertificate()
    {
        var key = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=KeyBridge Local Clipboard",
            key,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        var addresses = new[] { "127.0.0.1", GetLocalAddress() }
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        foreach (var addressText in addresses)
        {
            if (IPAddress.TryParse(addressText, out var address))
            {
                san.AddIpAddress(address);
            }
        }

        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, critical: true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            [new Oid("1.3.6.1.5.5.7.3.1")],
            critical: false));
        using var certificate = request.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddDays(30));
        var pfxBytes = certificate.Export(X509ContentType.Pkcs12);

        // Schannel is picky about in-memory self-signed certs; loading the PFX
        // back with a persisted key makes it usable as an HTTPS server cert.
        return X509CertificateLoader.LoadPkcs12(
            pfxBytes,
            password: null,
            X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.Exportable | X509KeyStorageFlags.PersistKeySet);
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
public sealed record ClipboardTextShareResult(string Url, int CharacterCount);
public sealed record ClipboardFileShareResult(string Url, int FileCount);

internal enum ClipboardShareKind
{
    None,
    Text,
    Image,
    Files
}

internal sealed record SharedClipboardFile(int Index, string Path, string Name, long Length);
