using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Minio;
using Minio.ApiEndpoints;
using Minio.DataModel.Args;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Headers;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace bsckend.Controllers;

[ApiController]
[Route("api/[controller]")]
public class StorageController : ControllerBase
{
    private readonly IMinioClient _minioClient;
    private readonly ILogger<StorageController> _logger;
    private readonly IConfiguration _configuration;
    private readonly string _bucketName;

    public StorageController(IMinioClient minioClient, ILogger<StorageController> logger, IConfiguration configuration)
    {
        _minioClient = minioClient;
        _logger = logger;
        _configuration = configuration;
        _bucketName = _configuration["MinIO:Bucket"]
                      ?? throw new InvalidOperationException("MinIO:Bucket is not configured");
    }

    [HttpPost("upload")]
    public async Task<IActionResult> UploadFile(IFormFile file, [FromQuery] string userId)
    {
        if (file == null || file.Length == 0)
        {
            _logger.LogWarning("Upload attempt with empty file");
            return BadRequest("Файл не выбран.");
        }

        if (string.IsNullOrEmpty(userId))
        {
            _logger.LogWarning("Upload attempt without userId");
            return BadRequest("Не указан ID пользователя.");
        }

        try
        {
            string objectName = $"{userId}/{file.FileName}";
            _logger.LogInformation("Uploading file {File} for user {User}", file.FileName, userId);

            using var stream = file.OpenReadStream();

            var putObjectArgs = new PutObjectArgs()
                .WithBucket(_bucketName)
                .WithObject(objectName)
                .WithStreamData(stream)
                .WithObjectSize(file.Length)
                .WithContentType(file.ContentType);

            await _minioClient.PutObjectAsync(putObjectArgs);

            _logger.LogInformation("File {File} uploaded successfully for user {User}", file.FileName, userId);

            return Ok($"Файл '{file.FileName}' успешно загружен.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error uploading file {File} for user {User}", file.FileName, userId);
            return BadRequest($"Ошибка при загрузке: {ex.Message}");
        }
    }

    [HttpGet("download/{fileName}")]
    public async Task<IActionResult> DownloadFile(string fileName, [FromQuery] string userId)
    {
        if (string.IsNullOrEmpty(userId))
        {
            _logger.LogWarning("Download attempt without userId");
            return BadRequest("Не указан ID пользователя.");
        }

        try
        {
            string objectName = $"{userId}/{fileName}";
            _logger.LogInformation("Downloading file {File} for user {User}", fileName, userId);

            var memoryStream = new MemoryStream();

            var getObjectArgs = new GetObjectArgs()
                .WithBucket(_bucketName)
                .WithObject(objectName)
                .WithCallbackStream(stream => stream.CopyTo(memoryStream));

            await _minioClient.GetObjectAsync(getObjectArgs);

            memoryStream.Position = 0;

            _logger.LogInformation("File {File} downloaded successfully for user {User}", fileName, userId);

            return File(memoryStream, GetContentType(fileName), fileName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error downloading file {File} for user {User}", fileName, userId);
            return BadRequest($"Ошибка при скачивании: {ex.Message}");
        }
    }

    [HttpPost("callback")]
    public async Task<IActionResult> Callback(
        [FromBody] JsonElement data,
        [FromQuery] string fileName,
        [FromQuery] string userId)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(fileName) || string.IsNullOrWhiteSpace(userId))
            {
                _logger.LogWarning("OnlyOffice callback rejected: missing fileName/userId");
                return Ok(new { error = 1 });
            }

            var callbackData = data;
            if (!callbackData.TryGetProperty("status", out var statusElement))
            {
                if (!TryExtractCallbackDataFromToken(data, out callbackData) ||
                    !callbackData.TryGetProperty("status", out statusElement))
                {
                    _logger.LogWarning("OnlyOffice callback rejected: missing status for file {File}", fileName);
                    return Ok(new { error = 1 });
                }
            }

            int status = statusElement.GetInt32();

            _logger.LogInformation("OnlyOffice callback received. Status: {Status}, File: {File}", status, fileName);

            // 2 - MustSave, 6 - MustForceSave
            if (status == 2 || status == 6)
            {
                var downloadUrl = callbackData.TryGetProperty("url", out var urlElement)
                    ? urlElement.GetString()
                    : null;

                if (string.IsNullOrWhiteSpace(downloadUrl))
                {
                    _logger.LogWarning("OnlyOffice callback has empty download url for file {File}", fileName);
                    return Ok(new { error = 1 });
                }

                string objectName = $"{userId}/{fileName}";

                using var httpClient = new HttpClient();

                // В некоторых конфигурациях OnlyOffice ожидает тот же JWT для выдачи файла по url.
                if (data.TryGetProperty("token", out var tokenElement))
                {
                    var callbackToken = tokenElement.GetString();
                    if (!string.IsNullOrWhiteSpace(callbackToken))
                    {
                        httpClient.DefaultRequestHeaders.Authorization =
                            new AuthenticationHeaderValue("Bearer", callbackToken);
                    }
                }

                var response = await httpClient.GetAsync(downloadUrl);
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogError(
                        "OnlyOffice file download failed for {File}. Status: {StatusCode}",
                        fileName,
                        (int)response.StatusCode);
                    return Ok(new { error = 1 });
                }

                await using var remoteStream = await response.Content.ReadAsStreamAsync();
                await using var memoryStream = new MemoryStream();
                await remoteStream.CopyToAsync(memoryStream);
                memoryStream.Position = 0;

                var putObjectArgs = new PutObjectArgs()
                    .WithBucket(_bucketName)
                    .WithObject(objectName)
                    .WithStreamData(memoryStream)
                    .WithObjectSize(memoryStream.Length)
                    .WithContentType("application/octet-stream");

                await _minioClient.PutObjectAsync(putObjectArgs);

                _logger.LogInformation("File {File} updated in MinIO after OnlyOffice edit", fileName);
            }

            return Ok(new { error = 0 });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing OnlyOffice callback for file {File}", fileName);
            // Для OnlyOffice важно вернуть JSON-контракт, иначе клиент показывает "document could not be saved".
            return Ok(new { error = 1 });
        }
    }

    private static bool TryExtractCallbackDataFromToken(JsonElement data, out JsonElement callbackData)
    {
        callbackData = default;

        if (!data.TryGetProperty("token", out var tokenElement))
        {
            return false;
        }

        var token = tokenElement.GetString();
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        var parts = token.Split('.');
        if (parts.Length < 2)
        {
            return false;
        }

        var payload = parts[1]
            .Replace('-', '+')
            .Replace('_', '/');

        switch (payload.Length % 4)
        {
            case 2:
                payload += "==";
                break;
            case 3:
                payload += "=";
                break;
        }

        try
        {
            var bytes = Convert.FromBase64String(payload);
            using var json = JsonDocument.Parse(bytes);
            var root = json.RootElement;

            if (root.TryGetProperty("payload", out var nestedPayload) && nestedPayload.ValueKind == JsonValueKind.Object)
            {
                callbackData = nestedPayload.Clone();
                return true;
            }

            callbackData = root.Clone();
            return true;
        }
        catch
        {
            return false;
        }
    }

    [HttpGet("editor-config")]
    public IActionResult GetEditorConfig(string fileName, string userId)
    {
        try
        {
            var extension = Path.GetExtension(fileName).TrimStart('.').ToLowerInvariant();
            var fileType = string.IsNullOrWhiteSpace(extension) ? "docx" : extension;

            var document = new Dictionary<string, object>
            {
                ["fileType"] = fileType,
                ["key"] = $"{userId}-{fileName}-{DateTime.UtcNow.Ticks}",
                ["title"] = fileName,
                // OnlyOffice скачивает файл сервер-сервер; URL должен быть доступен из контейнера.
                ["url"] = BuildDocumentDownloadUrl(fileName, userId),
                ["permissions"] = new Dictionary<string, object>
                {
                    ["edit"] = true,
                    ["review"] = true,
                    ["comment"] = true,
                    ["download"] = true,
                    ["print"] = true,
                    ["copy"] = true
                }
            };

            var editorConfig = new Dictionary<string, object>
            {
                ["callbackUrl"] = BuildCallbackUrl(fileName, userId),
                ["mode"] = "edit",
                ["lang"] = "ru",
                ["customization"] = new Dictionary<string, object>
                {
                    ["autosave"] = true,
                    ["forcesave"] = true
                },
                ["user"] = new Dictionary<string, object>
                {
                    ["id"] = userId,
                    ["name"] = userId
                }
            };

            var payload = new Dictionary<string, object>
            {
                ["document"] = document,
                ["editorConfig"] = editorConfig,
                ["documentType"] = "word"
            };

            var token = GenerateOnlyOfficeToken(payload);

            var config = new
            {
                document,
                editorConfig,
                documentType = "word",
                token
            };

            return Ok(config);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating editor config");
            return BadRequest(ex.Message);
        }
    }

    private string GenerateOnlyOfficeToken(IDictionary<string, object> payload)
    {
        var secret = _configuration["OnlyOffice:JwtSecret"];
        if (string.IsNullOrWhiteSpace(secret))
        {
            throw new InvalidOperationException("OnlyOffice:JwtSecret is not configured");
        }

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        var descriptor = new SecurityTokenDescriptor
        {
            Claims = new Dictionary<string, object>(payload),
            SigningCredentials = creds
        };

        var handler = new JwtSecurityTokenHandler();
        return handler.CreateEncodedJwt(descriptor);
    }

    private string BuildCallbackUrl(string fileName, string userId)
    {
        // Можно переопределить через appsettings: OnlyOffice:CallbackBaseUrl
        var callbackBaseUrl = _configuration["OnlyOffice:CallbackBaseUrl"]?.TrimEnd('/');
        if (string.IsNullOrWhiteSpace(callbackBaseUrl))
        {
            callbackBaseUrl = "http://nginx-proxy";
        }

        return
            $"{callbackBaseUrl}/api/storage/callback?fileName={Uri.EscapeDataString(fileName)}&userId={Uri.EscapeDataString(userId)}";
    }

    private string BuildDocumentDownloadUrl(string fileName, string userId)
    {
        // База для загрузки документа самим OnlyOffice (из Docker-контейнера).
        var documentBaseUrl = _configuration["OnlyOffice:DocumentAccessBaseUrl"]?.TrimEnd('/');
        if (string.IsNullOrWhiteSpace(documentBaseUrl))
        {
            documentBaseUrl = "http://nginx-proxy";
        }

        return
            $"{documentBaseUrl}/api/storage/download/{Uri.EscapeDataString(fileName)}?userId={Uri.EscapeDataString(userId)}";
    }

    [HttpGet("list")]
    public async Task<IActionResult> List(string userId)
    {
        try
        {
            _logger.LogInformation("Listing files for user {User}", userId);

            var objects = new List<string>();

            var args = new ListObjectsArgs()
                .WithBucket(_bucketName)
                .WithPrefix($"{userId}/")
                .WithRecursive(true);

            var observable = _minioClient.ListObjectsAsync(args);

            var completion = new TaskCompletionSource<bool>();

            observable.Subscribe(
                item => objects.Add(item.Key.Replace($"{userId}/", "")),
                ex => completion.SetException(ex),
                () => completion.SetResult(true)
            );

            await completion.Task;

            return Ok(objects);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error listing files for user {User}", userId);
            return BadRequest();
        }
    }

    [HttpPost("create")]
    public async Task<IActionResult> Create(string userId, string fileName)
    {
        try
        {
            _logger.LogInformation("Creating document {File} for user {User}", fileName, userId);

            if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(fileName))
            {
                return BadRequest("Не указаны userId или fileName.");
            }

            string objectName = $"{userId}/{fileName}";

            var fileExtension = Path.GetExtension(fileName).ToLowerInvariant();
            var contentType = "application/octet-stream";
            byte[] fileBytes;

            if (fileExtension == ".docx")
            {
                fileBytes = CreateMinimalDocx(fileName);
                contentType = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";
            }
            else
            {
                // Для прочих расширений создаем непустой текстовый файл вместо нулевого потока.
                var text = "New document";
                fileBytes = Encoding.UTF8.GetBytes(text);
                if (fileExtension == ".txt")
                {
                    contentType = "text/plain; charset=utf-8";
                }
            }

            using var stream = new MemoryStream(fileBytes);

            var args = new PutObjectArgs()
                .WithBucket(_bucketName)
                .WithObject(objectName)
                .WithStreamData(stream)
                .WithObjectSize(stream.Length)
                .WithContentType(contentType);

            await _minioClient.PutObjectAsync(args);

            return Ok();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating document {File} for user {User}", fileName, userId);
            return BadRequest();
        }
    }

    private static byte[] CreateMinimalDocx(string fileName)
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, true))
        {
            WriteZipEntry(archive, "[Content_Types].xml",
                "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
                "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
                "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
                "<Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
                "<Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/>" +
                "</Types>");

            WriteZipEntry(archive, "_rels/.rels",
                "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
                "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"word/document.xml\"/>" +
                "</Relationships>");

            var safeTitle = System.Security.SecurityElement.Escape(Path.GetFileNameWithoutExtension(fileName)) ?? "Document";
            WriteZipEntry(archive, "word/document.xml",
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                "<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\">" +
                "<w:body><w:p><w:r><w:t>" + safeTitle + "</w:t></w:r></w:p><w:sectPr/></w:body>" +
                "</w:document>");
        }

        return output.ToArray();
    }

    private static void WriteZipEntry(ZipArchive archive, string entryName, string content)
    {
        var entry = archive.CreateEntry(entryName, CompressionLevel.Fastest);
        using var entryStream = entry.Open();
        using var writer = new StreamWriter(entryStream, new UTF8Encoding(false));
        writer.Write(content);
    }

    private static string GetContentType(string fileName)
    {
        return Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            ".doc" => "application/msword",
            ".txt" => "text/plain; charset=utf-8",
            _ => "application/octet-stream"
        };
    }

    [HttpDelete("delete")]
    public async Task<IActionResult> Delete(string fileName, [FromQuery] string userId)
    {
        if (string.IsNullOrEmpty(userId)) return BadRequest("Не указан ID пользователя.");

        try
        {
            string objectName = $"{userId}/{fileName}";

            var removeArgs = new RemoveObjectArgs()
                .WithBucket(_bucketName)
                .WithObject(objectName);

            await _minioClient.RemoveObjectAsync(removeArgs);
            return Ok();
        }
        catch (Exception ex)
        {
            return BadRequest($"Ошибка при удалении: {ex.Message}");
        }
    }
}
