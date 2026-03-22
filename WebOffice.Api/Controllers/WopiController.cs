using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Minio;
using Minio.DataModel.Args;
using bsckend.Services;

namespace bsckend.Controllers;

[ApiController]
[Route("wopi")]
public class WopiController : ControllerBase
{
    private readonly IMinioClient _minioClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<WopiController> _logger;
    private readonly IWopiTokenService _tokenService;
    private readonly IWopiLockService _lockService;
    private readonly string _bucketName;

    public WopiController(
        IMinioClient minioClient,
        IConfiguration configuration,
        ILogger<WopiController> logger,
        IWopiTokenService tokenService,
        IWopiLockService lockService)
    {
        _minioClient = minioClient;
        _configuration = configuration;
        _logger = logger;
        _tokenService = tokenService;
        _lockService = lockService;
        _bucketName = _configuration["MinIO:Bucket"]
            ?? throw new InvalidOperationException("MinIO:Bucket is not configured");
    }

    [HttpGet("editor-launch")]
    public IActionResult GetEditorLaunch([FromQuery] string userId, [FromQuery] string fileName)
    {
        _logger.LogInformation(
            "WOPI launch requested. TraceId={TraceId}, UserId={UserId}, FileName={FileName}, Host={Host}",
            HttpContext.TraceIdentifier,
            userId,
            fileName,
            Request.Host.Value);

        if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(fileName))
        {
            return BadRequest("userId and fileName are required.");
        }

        var fileId = EncodeFileId(userId, fileName);
        var ttlMinutes = Math.Max(5, _configuration.GetValue<int?>("Wopi:TokenLifetimeMinutes") ?? 120);
        var lifetime = TimeSpan.FromMinutes(ttlMinutes);
        var token = _tokenService.GenerateToken(userId, fileId, lifetime);

        var internalBase = (_configuration["Wopi:HostBaseUrl"] ?? "http://nginx-proxy").TrimEnd('/');
        var wopiSrc = $"{internalBase}/wopi/files/{Uri.EscapeDataString(fileId)}";

        var documentServerUrl = ResolveDocumentServerUrl();
        var app = ResolveOnlyOfficeApp(fileName);
        var ttlMs = (long)lifetime.TotalMilliseconds;

        var launchUrl =
            $"{documentServerUrl}/hosting/wopi/{app}/edit?wopisrc={Uri.EscapeDataString(wopiSrc)}";

        _logger.LogInformation(
            "WOPI launch generated. TraceId={TraceId}, FileId={FileId}, App={App}, WopiSrc={WopiSrc}, LaunchUrl={LaunchUrl}, TokenTtlMs={TokenTtlMs}",
            HttpContext.TraceIdentifier,
            fileId,
            app,
            wopiSrc,
            launchUrl,
            ttlMs);

        return Ok(new
        {
            fileId,
            accessToken = token,
            accessTokenTtl = ttlMs,
            wopiSrc,
            launchUrl
        });
    }

    private string ResolveDocumentServerUrl()
    {
        var configured = _configuration["OnlyOffice:DocumentServerUrl"];
        if (string.IsNullOrWhiteSpace(configured))
        {
            return $"{Request.Scheme}://{Request.Host}/onlyoffice";
        }

        configured = configured.Trim();
        if (Uri.TryCreate(configured, UriKind.Absolute, out _))
        {
            return configured.TrimEnd('/');
        }

        if (configured.StartsWith('/'))
        {
            return $"{Request.Scheme}://{Request.Host}{configured.TrimEnd('/')}";
        }

        return $"{Request.Scheme}://{Request.Host}/{configured.Trim('/')}";
    }

    [HttpGet("files/{fileId}")]
    public async Task<IActionResult> CheckFileInfo(string fileId)
    {
        _logger.LogInformation("WOPI CheckFileInfo request. TraceId={TraceId}, FileId={FileId}", HttpContext.TraceIdentifier, fileId);

        if (!TryAuthorize(fileId, out var tokenPayload, out var unauthorizedResult))
        {
            return unauthorizedResult;
        }

        if (!TryDecodeFileId(fileId, out var ownerId, out var fileName, out var objectName))
        {
            return BadRequest("Invalid file id.");
        }

        try
        {
            var stat = await _minioClient.StatObjectAsync(
                new StatObjectArgs().WithBucket(_bucketName).WithObject(objectName));

            var extension = Path.GetExtension(fileName).ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(extension))
            {
                extension = ".docx";
            }
            _logger.LogInformation(
                "WOPI CheckFileInfo success. TraceId={TraceId}, FileId={FileId}, ObjectName={ObjectName}, Size={Size}, ETag={ETag}, UserId={UserId}",
                HttpContext.TraceIdentifier,
                fileId,
                objectName,
                stat.Size,
                stat.ETag,
                tokenPayload.UserId);

            var response = new Dictionary<string, object?>
            {
                ["BaseFileName"] = fileName,
                ["OwnerId"] = ownerId,
                ["UserId"] = tokenPayload.UserId,
                ["Size"] = stat.Size,
                ["Version"] = stat.ETag,
                ["UserCanWrite"] = true,
                ["SupportsLocks"] = true,
                ["SupportsGetLock"] = true,
                ["SupportsUpdate"] = true,
                ["SupportsRename"] = false,
                ["UserFriendlyName"] = tokenPayload.UserId,
                ["FileExtension"] = extension,
                ["LastModifiedTime"] = stat.LastModified.ToUniversalTime().ToString("o")
            };

            var json = JsonSerializer.Serialize(response, new JsonSerializerOptions
            {
                PropertyNamingPolicy = null,
                DictionaryKeyPolicy = null
            });

            return Content(json, "application/json");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CheckFileInfo failed for {FileId}", fileId);
            return NotFound();
        }
    }

    [HttpGet("files/{fileId}/contents")]
    public async Task<IActionResult> GetFile(string fileId)
    {
        _logger.LogInformation("WOPI GetFile request. TraceId={TraceId}, FileId={FileId}", HttpContext.TraceIdentifier, fileId);

        if (!TryAuthorize(fileId, out _, out var unauthorizedResult))
        {
            return unauthorizedResult;
        }

        if (!TryDecodeFileId(fileId, out _, out var fileName, out var objectName))
        {
            return BadRequest("Invalid file id.");
        }

        try
        {
            var memory = new MemoryStream();
            await _minioClient.GetObjectAsync(
                new GetObjectArgs()
                    .WithBucket(_bucketName)
                    .WithObject(objectName)
                    .WithCallbackStream(stream => stream.CopyTo(memory)));

            memory.Position = 0;
            _logger.LogInformation(
                "WOPI GetFile success. TraceId={TraceId}, FileId={FileId}, ObjectName={ObjectName}, Bytes={Bytes}",
                HttpContext.TraceIdentifier,
                fileId,
                objectName,
                memory.Length);
            return File(memory, GetContentType(fileName), fileName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetFile failed for {FileId}", fileId);
            return NotFound();
        }
    }

    [HttpPost("files/{fileId}/contents")]
    public async Task<IActionResult> PutFile(string fileId)
    {
        _logger.LogInformation("WOPI PutFile request. TraceId={TraceId}, FileId={FileId}", HttpContext.TraceIdentifier, fileId);

        if (!TryAuthorize(fileId, out _, out var unauthorizedResult))
        {
            return unauthorizedResult;
        }

        if (!TryDecodeFileId(fileId, out _, out var fileName, out var objectName))
        {
            return BadRequest("Invalid file id.");
        }

        var overrideValue = Request.Headers["X-WOPI-Override"].ToString();
        if (!string.Equals(overrideValue, "PUT", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning(
                "WOPI PutFile rejected: wrong override. TraceId={TraceId}, FileId={FileId}, Override={Override}",
                HttpContext.TraceIdentifier,
                fileId,
                overrideValue);
            return BadRequest("X-WOPI-Override: PUT is required.");
        }

        var requestedLock = Request.Headers["X-WOPI-Lock"].ToString();
        if (!_lockService.IsLockMatching(fileId, requestedLock, out var existingLock))
        {
            _logger.LogWarning(
                "WOPI PutFile lock mismatch. TraceId={TraceId}, FileId={FileId}, ProvidedLock={ProvidedLock}, ExistingLock={ExistingLock}",
                HttpContext.TraceIdentifier,
                fileId,
                MaskSecret(requestedLock),
                MaskSecret(existingLock));
            return BuildLockConflict(existingLock);
        }

        await using var memory = new MemoryStream();
        await Request.Body.CopyToAsync(memory);
        memory.Position = 0;

        _logger.LogInformation(
            "WOPI PutFile content received. TraceId={TraceId}, FileId={FileId}, Bytes={Bytes}",
            HttpContext.TraceIdentifier,
            fileId,
            memory.Length);

        try
        {
            await _minioClient.PutObjectAsync(
                new PutObjectArgs()
                    .WithBucket(_bucketName)
                    .WithObject(objectName)
                    .WithStreamData(memory)
                    .WithObjectSize(memory.Length)
                    .WithContentType(GetContentType(fileName)));

            var stat = await _minioClient.StatObjectAsync(
                new StatObjectArgs().WithBucket(_bucketName).WithObject(objectName));
            Response.Headers["X-WOPI-ItemVersion"] = stat.ETag ?? string.Empty;

            _logger.LogInformation(
                "WOPI PutFile success. TraceId={TraceId}, FileId={FileId}, ObjectName={ObjectName}, NewETag={ETag}",
                HttpContext.TraceIdentifier,
                fileId,
                objectName,
                stat.ETag);

            return Ok();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PutFile failed for {FileId}", fileId);
            return BadRequest("Unable to save file.");
        }
    }

    [HttpPost("files/{fileId}")]
    public IActionResult HandleFileOperation(string fileId)
    {
        _logger.LogInformation("WOPI file operation request. TraceId={TraceId}, FileId={FileId}", HttpContext.TraceIdentifier, fileId);

        if (!TryAuthorize(fileId, out _, out var unauthorizedResult))
        {
            return unauthorizedResult;
        }

        var overrideValue = Request.Headers["X-WOPI-Override"].ToString();
        var lockValue = Request.Headers["X-WOPI-Lock"].ToString();
        var oldLockValue = Request.Headers["X-WOPI-OldLock"].ToString();

        _logger.LogInformation(
            "WOPI file operation resolved. TraceId={TraceId}, FileId={FileId}, Override={Override}, Lock={Lock}, OldLock={OldLock}",
            HttpContext.TraceIdentifier,
            fileId,
            overrideValue,
            MaskSecret(lockValue),
            MaskSecret(oldLockValue));

        return overrideValue.ToUpperInvariant() switch
        {
            "LOCK" => HandleLock(fileId, lockValue, oldLockValue),
            "UNLOCK" => HandleUnlock(fileId, lockValue),
            "REFRESH_LOCK" => HandleRefreshLock(fileId, lockValue),
            "GET_LOCK" => HandleGetLock(fileId),
            _ => BadRequest("Unsupported WOPI override operation.")
        };
    }

    private IActionResult HandleLock(string fileId, string lockValue, string oldLockValue)
    {
        if (string.IsNullOrWhiteSpace(lockValue))
        {
            return BadRequest("X-WOPI-Lock is required.");
        }

        // WOPI всегда отправляет X-WOPI-OldLock, даже если его значение пустое. Поэтому проверяем отдельно на null/whitespace.
        if (!string.IsNullOrWhiteSpace(oldLockValue))
        {
            if (_lockService.TryUnlock(fileId, oldLockValue, out var unlockExisting))
            {
                _logger.LogInformation(
                    "WOPI old lock removed before LOCK. TraceId={TraceId}, FileId={FileId}, OldLock={OldLock}",
                    HttpContext.TraceIdentifier,
                    fileId,
                    MaskSecret(oldLockValue));
            }
            else
            {
                _logger.LogWarning(
                    "WOPI old lock mismatch during LOCK. TraceId={TraceId}, FileId={FileId}, OldLock={OldLock}, Existing={Existing}",
                    HttpContext.TraceIdentifier,
                    fileId,
                    MaskSecret(oldLockValue),
                    MaskSecret(unlockExisting));
                return BuildLockConflict(unlockExisting);
            }
        }

        if (_lockService.TryLock(fileId, lockValue, out var existingLock))
        {
            return Ok();
        }

        return BuildLockConflict(existingLock);
    }

    private IActionResult HandleUnlock(string fileId, string lockValue)
    {
        if (string.IsNullOrWhiteSpace(lockValue))
        {
            return BadRequest("X-WOPI-Lock is required.");
        }

        if (_lockService.TryUnlock(fileId, lockValue, out var existingLock))
        {
            return Ok();
        }

        return BuildLockConflict(existingLock);
    }

    private IActionResult HandleRefreshLock(string fileId, string lockValue)
    {
        if (string.IsNullOrWhiteSpace(lockValue))
        {
            return BadRequest("X-WOPI-Lock is required.");
        }

        if (_lockService.TryRefreshLock(fileId, lockValue, out var existingLock))
        {
            return Ok();
        }

        return BuildLockConflict(existingLock);
    }

    private IActionResult HandleGetLock(string fileId)
    {
        if (_lockService.TryGetLock(fileId, out var lockValue) && !string.IsNullOrWhiteSpace(lockValue))
        {
            Response.Headers["X-WOPI-Lock"] = lockValue;
            return Ok();
        }

        return NotFound();
    }

    private ObjectResult BuildLockConflict(string? existingLock)
    {
        _logger.LogWarning(
            "WOPI lock conflict. TraceId={TraceId}, ExistingLock={ExistingLock}",
            HttpContext.TraceIdentifier,
            MaskSecret(existingLock));

        Response.Headers["X-WOPI-Lock"] = existingLock ?? string.Empty;
        Response.Headers["X-WOPI-LockFailureReason"] = "Lock mismatch.";
        return StatusCode(StatusCodes.Status409Conflict, new { message = "Lock mismatch" });
    }

    private bool TryAuthorize(string fileId, out WopiTokenPayload payload, out IActionResult unauthorizedResult)
    {
        unauthorizedResult = Unauthorized();
        payload = default!;

        var token = ExtractAccessToken(out var tokenSource);
        _logger.LogInformation(
            "WOPI authorize start. TraceId={TraceId}, FileId={FileId}, TokenSource={TokenSource}, Token={Token}",
            HttpContext.TraceIdentifier,
            fileId,
            tokenSource,
            MaskSecret(token));

        if (!_tokenService.TryValidateToken(token, out payload))
        {
            _logger.LogWarning(
                "WOPI authorize failed: token invalid. TraceId={TraceId}, FileId={FileId}, TokenSource={TokenSource}",
                HttpContext.TraceIdentifier,
                fileId,
                tokenSource);
            return false;
        }

        if (!string.Equals(payload.FileId, fileId, StringComparison.Ordinal))
        {
            _logger.LogWarning(
                "WOPI authorize failed: file mismatch. TraceId={TraceId}, RequestedFileId={RequestedFileId}, TokenFileId={TokenFileId}, UserId={UserId}",
                HttpContext.TraceIdentifier,
                fileId,
                payload.FileId,
                payload.UserId);
            return false;
        }

        _logger.LogInformation(
            "WOPI authorize success. TraceId={TraceId}, FileId={FileId}, UserId={UserId}",
            HttpContext.TraceIdentifier,
            fileId,
            payload.UserId);

        return true;
    }

    private string ExtractAccessToken(out string source)
    {
        var queryToken = Request.Query["access_token"].ToString();
        if (!string.IsNullOrWhiteSpace(queryToken))
        {
            source = "query";
            return queryToken;
        }

        var authHeader = Request.Headers.Authorization.ToString();
        const string bearerPrefix = "Bearer ";
        if (!string.IsNullOrWhiteSpace(authHeader) &&
            authHeader.StartsWith(bearerPrefix, StringComparison.OrdinalIgnoreCase))
        {
            source = "authorization";
            return authHeader[bearerPrefix.Length..].Trim();
        }

        if (Request.HasFormContentType)
        {
            var formToken = Request.Form["access_token"].ToString();
            if (!string.IsNullOrWhiteSpace(formToken))
            {
                source = "form";
                return formToken;
            }
        }

        source = "none";
        return string.Empty;
    }

    private static string MaskSecret(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "<empty>";
        }

        if (value.Length <= 10)
        {
            return $"{value[0]}***{value[^1]}(len={value.Length})";
        }

        return $"{value[..6]}...{value[^4..]}(len={value.Length})";
    }

    private static string ResolveOnlyOfficeApp(string fileName)
    {
        var extension = Path.GetExtension(fileName).ToLowerInvariant();

        return extension switch
        {
            ".xls" or ".xlsx" or ".csv" or ".ods" => "cell",
            ".ppt" or ".pptx" or ".odp" => "slide",
            _ => "word"
        };
    }

    private static bool TryDecodeFileId(string fileId, out string userId, out string fileName, out string objectName)
    {
        userId = string.Empty;
        fileName = string.Empty;
        objectName = string.Empty;

        try
        {
            var normalized = fileId.Replace('-', '+').Replace('_', '/');
            switch (normalized.Length % 4)
            {
                case 2:
                    normalized += "==";
                    break;
                case 3:
                    normalized += "=";
                    break;
            }

            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(normalized));
            var slashIndex = decoded.IndexOf('/');
            if (slashIndex <= 0 || slashIndex >= decoded.Length - 1)
            {
                return false;
            }

            userId = decoded[..slashIndex];
            fileName = decoded[(slashIndex + 1)..];
            objectName = decoded;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string EncodeFileId(string userId, string fileName)
    {
        var value = Encoding.UTF8.GetBytes($"{userId}/{fileName}");
        return Convert.ToBase64String(value)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static string GetContentType(string fileName)
    {
        return Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            ".doc" => "application/msword",
            ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            ".xls" => "application/vnd.ms-excel",
            ".pptx" => "application/vnd.openxmlformats-officedocument.presentationml.presentation",
            ".ppt" => "application/vnd.ms-powerpoint",
            ".txt" => "text/plain; charset=utf-8",
            _ => "application/octet-stream"
        };
    }
}




