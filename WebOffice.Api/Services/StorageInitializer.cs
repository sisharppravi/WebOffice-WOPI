using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Minio;
using Minio.DataModel.Args;

namespace bsckend.Services;

public static class StorageInitializer
{
    public static async Task InitializeAsync(
        IMinioClient minioClient,
        IConfiguration configuration,
        ILogger logger)
    {
        var bucketName = configuration["MinIO:Bucket"];
        if (string.IsNullOrWhiteSpace(bucketName))
        {
            throw new InvalidOperationException("MinIO:Bucket is not configured");
        }

        try
        {
            logger.LogInformation("Checking MinIO bucket {Bucket}", bucketName);

            var exists = await minioClient.BucketExistsAsync(
                new BucketExistsArgs().WithBucket(bucketName));

            if (exists)
            {
                logger.LogInformation("MinIO bucket {Bucket} already exists", bucketName);
                return;
            }

            await minioClient.MakeBucketAsync(new MakeBucketArgs().WithBucket(bucketName));
            logger.LogInformation("MinIO bucket {Bucket} created", bucketName);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to initialize MinIO bucket");
            throw;
        }
    }
}
