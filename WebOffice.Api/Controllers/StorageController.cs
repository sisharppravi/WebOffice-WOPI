using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Minio;
using Minio.DataModel.Args;
using System.IO.Compression;
using System.Text;

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

            await foreach (var item in _minioClient.ListObjectsEnumAsync(args))
            {
                objects.Add(item.Key.Replace($"{userId}/", ""));
            }

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
            else if (fileExtension == ".xlsx")
            {
                fileBytes = CreateMinimalXlsx(fileName);
                contentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
            }
            else if (fileExtension == ".txt")
            {
                var text = "New document";
                fileBytes = Encoding.UTF8.GetBytes(text);
                contentType = "text/plain; charset=utf-8";
            }
            else
            {
                return BadRequest("Поддерживаются только .docx, .xlsx и .txt файлы.");
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

    private static byte[] CreateMinimalXlsx(string fileName)
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, true))
        {
            WriteZipEntry(archive, "[Content_Types].xml",
                "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
                "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
                "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
                "<Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
                "<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>" +
                "<Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>" +
                "</Types>");

            WriteZipEntry(archive, "_rels/.rels",
                "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
                "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/>" +
                "</Relationships>");

            WriteZipEntry(archive, "xl/workbook.xml",
                "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
                "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">" +
                "<sheets><sheet name=\"Sheet1\" sheetId=\"1\" r:id=\"rId1\"/></sheets>" +
                "</workbook>");

            WriteZipEntry(archive, "xl/_rels/workbook.xml.rels",
                "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
                "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/>" +
                "</Relationships>");

            var safeTitle = System.Security.SecurityElement.Escape(Path.GetFileNameWithoutExtension(fileName)) ?? "Sheet";
            WriteZipEntry(archive, "xl/worksheets/sheet1.xml",
                "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
                "<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">" +
                "<sheetData><row r=\"1\"><c r=\"A1\" t=\"inlineStr\"><is><t>" + safeTitle + "</t></is></c></row></sheetData>" +
                "</worksheet>");
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
