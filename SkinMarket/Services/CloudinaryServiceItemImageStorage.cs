using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Net.Http.Headers;
using Microsoft.Extensions.Options;
using SkinMarket.Contracts;
using SkinMarket.Models;

namespace SkinMarket.Services;

public sealed class CloudinaryServiceItemImageStorage :
    IServiceItemImageStorage,
    ICloudinaryServiceItemImageStorageDiagnostics
{
    private const string StoragePathPrefix = "cloudinary:";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _httpClient;
    private readonly IWebHostEnvironment _environment;
    private readonly CloudinaryOptions _options;

    public CloudinaryServiceItemImageStorage(
        HttpClient httpClient,
        IWebHostEnvironment environment,
        IOptions<CloudinaryOptions> options)
    {
        _httpClient = httpClient;
        _environment = environment;
        _options = ResolveOptions(options.Value);
    }

    public async Task<ServiceItemImageUploadResult> UploadAsync(
        Guid itemId,
        IFormFile image,
        CancellationToken cancellationToken = default)
    {
        var publicId = BuildPublicId(itemId);
        using var form = new MultipartFormDataContent();
        AddUploadAuthentication(form, publicId);

        await using var imageStream = image.OpenReadStream();
        using var imageContent = new StreamContent(imageStream);
        if (!string.IsNullOrWhiteSpace(image.ContentType))
        {
            imageContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(image.ContentType);
        }

        form.Add(imageContent, "file", Path.GetFileName(image.FileName));

        using var response = await _httpClient.PostAsync(
            $"https://api.cloudinary.com/v1_1/{Uri.EscapeDataString(_options.CloudName)}/image/upload",
            form,
            cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Cloudinary upload failed with status {(int)response.StatusCode}: {responseBody}");
        }

        var uploadResponse = JsonSerializer.Deserialize<CloudinaryUploadResponse>(responseBody, JsonOptions);
        if (uploadResponse is null ||
            string.IsNullOrWhiteSpace(uploadResponse.SecureUrl) ||
            string.IsNullOrWhiteSpace(uploadResponse.PublicId))
        {
            throw new InvalidOperationException("Cloudinary upload response did not include secure_url or public_id.");
        }

        return new ServiceItemImageUploadResult(
            uploadResponse.SecureUrl,
            $"{StoragePathPrefix}{uploadResponse.PublicId}",
            Path.GetFileName(image.FileName),
            image.ContentType);
    }

    public async Task DeleteAsync(string? storagePath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(storagePath))
        {
            return;
        }

        if (!storagePath.StartsWith(StoragePathPrefix, StringComparison.Ordinal))
        {
            DeleteLocalFileIfExists(storagePath);
            return;
        }

        EnsureSignedUploadConfigured();

        var publicId = storagePath[StoragePathPrefix.Length..];
        if (string.IsNullOrWhiteSpace(publicId))
        {
            return;
        }

        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var destroyParameters = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["invalidate"] = "true",
            ["public_id"] = publicId,
            ["timestamp"] = timestamp
        };

        using var form = new MultipartFormDataContent();
        foreach (var parameter in destroyParameters)
        {
            AddFormField(form, parameter.Key, parameter.Value);
        }

        AddFormField(form, "api_key", _options.ApiKey);
        AddFormField(form, "signature", Sign(destroyParameters));

        using var response = await _httpClient.PostAsync(
            $"https://api.cloudinary.com/v1_1/{Uri.EscapeDataString(_options.CloudName)}/image/destroy",
            form,
            cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException($"Cloudinary delete failed with status {(int)response.StatusCode}: {responseBody}");
        }
    }

    public string GetConfigurationSummary()
    {
        return string.Join("; ", new[]
        {
            $"CloudinaryCloudNameSet={!string.IsNullOrWhiteSpace(_options.CloudName)}",
            $"CloudinaryApiKeySet={!string.IsNullOrWhiteSpace(_options.ApiKey)}",
            $"CloudinaryApiSecretSet={!string.IsNullOrWhiteSpace(_options.ApiSecret)}",
            $"CloudinaryUploadPresetSet={HasUploadPreset()}",
            $"CloudinaryFolder={NormalizeFolder(_options.Folder)}"
        });
    }

    private void EnsureConfigured()
    {
        if (HasUploadPreset())
        {
            if (string.IsNullOrWhiteSpace(_options.CloudName))
            {
                throw new InvalidOperationException("Cloudinary is not configured. Set Cloudinary__CloudName.");
            }

            return;
        }

        EnsureSignedUploadConfigured();
    }

    private void EnsureSignedUploadConfigured()
    {
        if (string.IsNullOrWhiteSpace(_options.CloudName) ||
            string.IsNullOrWhiteSpace(_options.ApiKey) ||
            string.IsNullOrWhiteSpace(_options.ApiSecret))
        {
            throw new InvalidOperationException(
                "Cloudinary is not configured. Set Cloudinary__CloudName, Cloudinary__ApiKey and Cloudinary__ApiSecret.");
        }
    }

    private void AddUploadAuthentication(MultipartFormDataContent form, string publicId)
    {
        EnsureConfigured();

        var folder = NormalizeFolder(_options.Folder);
        AddFormField(form, "folder", folder);
        AddFormField(form, "public_id", publicId);

        if (HasUploadPreset())
        {
            AddFormField(form, "upload_preset", _options.UploadPreset.Trim());
            return;
        }

        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var uploadParameters = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["folder"] = folder,
            ["overwrite"] = "true",
            ["public_id"] = publicId,
            ["timestamp"] = timestamp
        };

        AddFormField(form, "overwrite", "true");
        AddFormField(form, "timestamp", timestamp);
        AddFormField(form, "api_key", _options.ApiKey);
        AddFormField(form, "signature", Sign(uploadParameters));
    }

    private static void AddFormField(MultipartFormDataContent form, string name, string value)
    {
        var content = new ByteArrayContent(Encoding.UTF8.GetBytes(value));
        content.Headers.ContentDisposition = new ContentDispositionHeaderValue("form-data")
        {
            Name = $"\"{name}\""
        };
        form.Add(content);
    }

    private string Sign(SortedDictionary<string, string> parameters)
    {
        var payload = string.Join("&", parameters
            .Where(parameter => !string.IsNullOrWhiteSpace(parameter.Value))
            .Select(parameter => $"{parameter.Key}={parameter.Value}"));
        var bytes = SHA1.HashData(Encoding.UTF8.GetBytes(payload + _options.ApiSecret));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static CloudinaryOptions ResolveOptions(CloudinaryOptions configured)
    {
        var resolved = new CloudinaryOptions
        {
            CloudName = configured.CloudName,
            ApiKey = configured.ApiKey,
            ApiSecret = configured.ApiSecret,
            Folder = configured.Folder,
            UploadPreset = configured.UploadPreset
        };

        if (!string.IsNullOrWhiteSpace(resolved.CloudName) &&
            !string.IsNullOrWhiteSpace(resolved.ApiKey) &&
            !string.IsNullOrWhiteSpace(resolved.ApiSecret))
        {
            return resolved;
        }

        var cloudinaryUrl = Environment.GetEnvironmentVariable("CLOUDINARY_URL");
        if (string.IsNullOrWhiteSpace(cloudinaryUrl) ||
            !Uri.TryCreate(cloudinaryUrl, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, "cloudinary", StringComparison.OrdinalIgnoreCase))
        {
            return resolved;
        }

        resolved.CloudName = uri.Host;
        if (!string.IsNullOrWhiteSpace(uri.UserInfo))
        {
            var credentials = uri.UserInfo.Split(':', 2);
            if (string.IsNullOrWhiteSpace(resolved.ApiKey))
            {
                resolved.ApiKey = Uri.UnescapeDataString(credentials[0]);
            }

            if (credentials.Length > 1 && string.IsNullOrWhiteSpace(resolved.ApiSecret))
            {
                resolved.ApiSecret = Uri.UnescapeDataString(credentials[1]);
            }
        }

        return resolved;
    }

    private static string NormalizeFolder(string? folder)
    {
        return string.IsNullOrWhiteSpace(folder)
            ? "skin-market/items"
            : folder.Trim().Trim('/');
    }

    private static string BuildPublicId(Guid itemId)
    {
        return itemId.ToString("N");
    }

    private bool HasUploadPreset()
    {
        return !string.IsNullOrWhiteSpace(_options.UploadPreset);
    }

    private void DeleteLocalFileIfExists(string relativeStoragePath)
    {
        var webRoot = string.IsNullOrWhiteSpace(_environment.WebRootPath)
            ? Path.Combine(_environment.ContentRootPath, "wwwroot")
            : _environment.WebRootPath;
        var fullPath = Path.GetFullPath(Path.Combine(webRoot, relativeStoragePath));
        var allowedRoot = Path.GetFullPath(Path.Combine(webRoot, "uploads", "items"));
        if (!fullPath.StartsWith(allowedRoot, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (File.Exists(fullPath))
        {
            File.Delete(fullPath);
        }
    }

    private sealed class CloudinaryUploadResponse
    {
        [JsonPropertyName("public_id")]
        public string? PublicId { get; set; }

        [JsonPropertyName("secure_url")]
        public string? SecureUrl { get; set; }
    }
}
