using Google.Apis.Auth.OAuth2;
using Google.Apis.Docs.v1;
using Google.Apis.Docs.v1.Data;
using Google.Apis.Download;
using Google.Apis.Drive.v3;
using Google.Apis.Services;
using Google.Apis.Upload;
using Google.Apis.Util.Store;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace KeyboardPadBridge.Services;

public sealed class GoogleDocsClipboardService : IDisposable
{
    private const string ApplicationName = "KeyBridge";
    private const string TemporaryImageFolderName = "Key Bridge 전송용 임시 이미지";
    private const string LatestImageFileName = "latest-image.png";
    private static readonly string[] Scopes = [DocsService.Scope.Documents, DriveService.Scope.DriveFile];
    private static readonly Regex DocumentUrlRegex = new(@"/document/d/([^/?#]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly string configDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "KeyBridge");
    private readonly SemaphoreSlim documentUpdateLock = new(1, 1);
    private DocsService? docsService;
    private DriveService? driveService;

    public GoogleDocsClipboardSettings Settings { get; private set; } = new();

    private string SettingsPath => Path.Combine(configDirectory, "google-docs-settings.json");

    private string TokenDirectory => Path.Combine(configDirectory, "GoogleDocsToken");

    public void Load()
    {
        try
        {
            if (!File.Exists(SettingsPath))
            {
                return;
            }

            var json = File.ReadAllText(SettingsPath);
            Settings = JsonSerializer.Deserialize<GoogleDocsClipboardSettings>(json) ?? new GoogleDocsClipboardSettings();
        }
        catch
        {
            Settings = new GoogleDocsClipboardSettings();
        }
    }

    public void Save(GoogleDocsClipboardSettings settings)
    {
        Directory.CreateDirectory(configDirectory);
        Settings = settings with
        {
            DocumentId = ExtractDocumentId(settings.DocumentId)
        };
        docsService = null;
        driveService = null;

        var json = JsonSerializer.Serialize(Settings, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(SettingsPath, json);
    }

    public async Task<string> TestConnectionAsync(CancellationToken cancellationToken = default)
    {
        var service = await GetDocsServiceAsync(cancellationToken);
        var document = await service.Documents.Get(Settings.DocumentId).ExecuteAsync(cancellationToken);
        return string.IsNullOrWhiteSpace(document.Title) ? Settings.DocumentId : document.Title;
    }

    public async Task ReplaceLatestTextAsync(string text, CancellationToken cancellationToken = default)
    {
        if (!Settings.Enabled || string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        await documentUpdateLock.WaitAsync(cancellationToken);
        try
        {
            ValidateSettings();
            var service = await GetDocsServiceAsync(cancellationToken);
            var document = await service.Documents.Get(Settings.DocumentId).ExecuteAsync(cancellationToken);
            var endIndex = document.Body?.Content?.LastOrDefault()?.EndIndex ?? 1;
            var normalizedText = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
            var requests = new List<Request>();

            if (endIndex > 2)
            {
                requests.Add(new Request
                {
                    DeleteContentRange = new DeleteContentRangeRequest
                    {
                        Range = new Google.Apis.Docs.v1.Data.Range
                        {
                            StartIndex = 1,
                            EndIndex = endIndex - 1
                        }
                    }
                });
            }

            requests.Add(new Request
            {
                InsertText = new InsertTextRequest
                {
                    Location = new Location { Index = 1 },
                    Text = normalizedText
                }
            });

            var update = new BatchUpdateDocumentRequest { Requests = requests };
            await service.Documents.BatchUpdate(update, Settings.DocumentId).ExecuteAsync(cancellationToken);
        }
        finally
        {
            documentUpdateLock.Release();
        }
    }

    public async Task<GoogleDriveImageUploadResult> UploadLatestImageAsync(string imagePath, CancellationToken cancellationToken = default)
    {
        if (!Settings.Enabled || string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
        {
            return new GoogleDriveImageUploadResult(string.Empty, string.Empty);
        }

        ValidateSettings();
        var service = await GetDriveServiceAsync(cancellationToken);
        var folderId = await GetOrCreateTemporaryImageFolderAsync(service, cancellationToken);
        var fileId = await GetLatestImageFileIdAsync(service, folderId, cancellationToken);

        await using var stream = File.OpenRead(imagePath);
        if (string.IsNullOrWhiteSpace(fileId))
        {
            var metadata = new Google.Apis.Drive.v3.Data.File
            {
                Name = LatestImageFileName,
                Parents = [folderId],
                MimeType = "image/png"
            };
            var create = service.Files.Create(metadata, stream, "image/png");
            create.Fields = "id, webViewLink";
            await create.UploadAsync(cancellationToken);
            if (create.GetProgress().Status == UploadStatus.Failed)
            {
                throw create.GetProgress().Exception ?? new InvalidOperationException("Google Drive image upload failed.");
            }

            fileId = create.ResponseBody.Id;
        }
        else
        {
            var metadata = new Google.Apis.Drive.v3.Data.File
            {
                Name = LatestImageFileName,
                MimeType = "image/png"
            };
            var update = service.Files.Update(metadata, fileId, stream, "image/png");
            update.Fields = "id, webViewLink";
            await update.UploadAsync(cancellationToken);
            if (update.GetProgress().Status == UploadStatus.Failed)
            {
                throw update.GetProgress().Exception ?? new InvalidOperationException("Google Drive image update failed.");
            }
        }

        return new GoogleDriveImageUploadResult(
            fileId,
            $"https://drive.google.com/file/d/{fileId}/view");
    }

    public static string ExtractDocumentId(string value)
    {
        var trimmed = value.Trim();
        var match = DocumentUrlRegex.Match(trimmed);
        return match.Success ? match.Groups[1].Value : trimmed;
    }

    public void Dispose()
    {
        docsService?.Dispose();
        driveService?.Dispose();
        documentUpdateLock.Dispose();
    }

    private void ValidateSettings()
    {
        if (string.IsNullOrWhiteSpace(Settings.ClientSecretsPath) || !File.Exists(Settings.ClientSecretsPath))
        {
            throw new InvalidOperationException("Google OAuth client JSON path is missing.");
        }

        if (string.IsNullOrWhiteSpace(Settings.DocumentId))
        {
            throw new InvalidOperationException("Google Docs document URL or ID is missing.");
        }
    }

    private async Task<DocsService> GetDocsServiceAsync(CancellationToken cancellationToken)
    {
        if (docsService is not null)
        {
            return docsService;
        }

        ValidateSettings();

        await using var stream = File.OpenRead(Settings.ClientSecretsPath);
        var secrets = GoogleClientSecrets.FromStream(stream).Secrets;
        var credential = await GoogleWebAuthorizationBroker.AuthorizeAsync(
            secrets,
            Scopes,
            "user",
            cancellationToken,
            new FileDataStore(TokenDirectory, fullPath: true));

        docsService = new DocsService(new BaseClientService.Initializer
        {
            HttpClientInitializer = credential,
            ApplicationName = ApplicationName
        });

        return docsService;
    }

    private async Task<DriveService> GetDriveServiceAsync(CancellationToken cancellationToken)
    {
        if (driveService is not null)
        {
            return driveService;
        }

        ValidateSettings();

        await using var stream = File.OpenRead(Settings.ClientSecretsPath);
        var secrets = GoogleClientSecrets.FromStream(stream).Secrets;
        var credential = await GoogleWebAuthorizationBroker.AuthorizeAsync(
            secrets,
            Scopes,
            "user",
            cancellationToken,
            new FileDataStore(TokenDirectory, fullPath: true));

        driveService = new DriveService(new BaseClientService.Initializer
        {
            HttpClientInitializer = credential,
            ApplicationName = ApplicationName
        });

        return driveService;
    }

    private static async Task<string> GetOrCreateTemporaryImageFolderAsync(DriveService service, CancellationToken cancellationToken)
    {
        var list = service.Files.List();
        list.Q = $"mimeType = 'application/vnd.google-apps.folder' and name = '{EscapeDriveQueryValue(TemporaryImageFolderName)}' and trashed = false";
        list.Fields = "files(id, name)";
        list.PageSize = 1;
        var existing = await list.ExecuteAsync(cancellationToken);
        var folder = existing.Files?.FirstOrDefault();
        if (folder is not null)
        {
            return folder.Id;
        }

        var metadata = new Google.Apis.Drive.v3.Data.File
        {
            Name = TemporaryImageFolderName,
            MimeType = "application/vnd.google-apps.folder"
        };
        var create = service.Files.Create(metadata);
        create.Fields = "id";
        var created = await create.ExecuteAsync(cancellationToken);
        return created.Id;
    }

    private static async Task<string?> GetLatestImageFileIdAsync(DriveService service, string folderId, CancellationToken cancellationToken)
    {
        var list = service.Files.List();
        list.Q = $"'{EscapeDriveQueryValue(folderId)}' in parents and name = '{EscapeDriveQueryValue(LatestImageFileName)}' and trashed = false";
        list.Fields = "files(id, name)";
        list.PageSize = 1;
        var existing = await list.ExecuteAsync(cancellationToken);
        return existing.Files?.FirstOrDefault()?.Id;
    }

    private static string EscapeDriveQueryValue(string value)
    {
        return value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("'", "\\'", StringComparison.Ordinal);
    }
}

public sealed record GoogleDocsClipboardSettings
{
    public bool Enabled { get; init; }

    public string ClientSecretsPath { get; init; } = string.Empty;

    public string DocumentId { get; init; } = string.Empty;

    public bool IncludeCodeBlockLanguage { get; init; }
}

public sealed record GoogleDriveImageUploadResult(string FileId, string Url);
