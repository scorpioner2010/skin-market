namespace SkinMarket.Contracts;

public interface IServiceItemImageStorage
{
    Task<ServiceItemImageUploadResult> UploadAsync(
        Guid itemId,
        IFormFile image,
        CancellationToken cancellationToken = default);

    Task DeleteAsync(string? storagePath, CancellationToken cancellationToken = default);
}

public interface ICloudinaryServiceItemImageStorageDiagnostics
{
    string GetConfigurationSummary();
}

public sealed record ServiceItemImageUploadResult(
    string ImageUrl,
    string StoragePath,
    string OriginalFileName,
    string ContentType);
