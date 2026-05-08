using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using SkinMarket.Contracts;
using SkinMarket.Data;
using SkinMarket.Models;

namespace SkinMarket.Pages.Admin;

public class ItemsModel : PageModel
{
    private const long MaxImageSizeBytes = 5 * 1024 * 1024;
    private static readonly HashSet<string> AllowedContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/jpeg",
        "image/png",
        "image/webp",
        "image/gif"
    };
    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg",
        ".jpeg",
        ".png",
        ".webp",
        ".gif"
    };

    private readonly AppDbContext _dbContext;
    private readonly IAppLogService _appLogService;
    private readonly IServiceItemImageStorage _imageStorage;

    public ItemsModel(
        AppDbContext dbContext,
        IAppLogService appLogService,
        IServiceItemImageStorage imageStorage)
    {
        _dbContext = dbContext;
        _appLogService = appLogService;
        _imageStorage = imageStorage;
    }

    public List<ServiceItem> Items { get; private set; } = new();
    [TempData]
    public string? SuccessMessage { get; set; }
    [TempData]
    public string? ErrorMessage { get; set; }
    public string? PageErrorMessage { get; private set; }
    public string? DisplayErrorMessage => PageErrorMessage ?? ErrorMessage;
    [BindProperty]
    public CreateItemInputModel CreateInput { get; set; } = new();
    [BindProperty]
    public Guid DeleteItemId { get; set; }

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        if (ErrorMessage is "Image upload failed." or "Item data is invalid." or "Item with this name already exists.")
        {
            ErrorMessage = null;
        }

        await LoadItemsAsync(cancellationToken);
    }

    public async Task<IActionResult> OnPostCreateAsync(CancellationToken cancellationToken)
    {
        if (CreateInput.Image is null || CreateInput.Image.Length == 0)
        {
            ModelState.AddModelError("CreateInput.Image", "Image file is required.");
        }
        else
        {
            ValidateImage(CreateInput.Image);
        }

        if (!ModelState.IsValid)
        {
            PageErrorMessage = "Item data is invalid.";
            await LoadItemsAsync(cancellationToken);
            return Page();
        }

        var normalizedName = CreateInput.Name.Trim();
        var itemExists = await _dbContext.ServiceItems
            .AnyAsync(item => item.Name.ToLower() == normalizedName.ToLower(), cancellationToken);
        if (itemExists)
        {
            ModelState.AddModelError("CreateInput.Name", "Item with this name already exists.");
            PageErrorMessage = "Item with this name already exists.";
            await LoadItemsAsync(cancellationToken);
            return Page();
        }

        var itemId = Guid.NewGuid();
        var image = CreateInput.Image!;
        ServiceItemImageUploadResult uploadedImage;
        try
        {
            uploadedImage = await _imageStorage.UploadAsync(itemId, image, cancellationToken);
        }
        catch (Exception exception)
        {
            ModelState.AddModelError("CreateInput.Image", "Image upload failed. Check Cloudinary configuration and try again.");
            PageErrorMessage = "Image upload failed.";
            await _appLogService.WriteAsync(
                "Error",
                $"Admin service item image upload failed. ItemId={itemId}; FileName={image.FileName}; {CloudinaryDiagnostics}",
                nameof(ItemsModel),
                exception,
                cancellationToken);
            await LoadItemsAsync(cancellationToken);
            return Page();
        }

        try
        {
            var now = DateTime.UtcNow;
            var item = new ServiceItem
            {
                Id = itemId,
                Name = normalizedName,
                Description = string.IsNullOrWhiteSpace(CreateInput.Description) ? null : CreateInput.Description.Trim(),
                Price = decimal.Round(CreateInput.Price, 2, MidpointRounding.AwayFromZero),
                ImageUrl = uploadedImage.ImageUrl,
                ImageStoragePath = uploadedImage.StoragePath,
                ImageFileName = uploadedImage.OriginalFileName,
                ImageContentType = uploadedImage.ContentType,
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            };

            _dbContext.ServiceItems.Add(item);
            await _dbContext.SaveChangesAsync(cancellationToken);
            await _appLogService.WriteAsync(
                "Information",
                $"Admin created service item. ItemId={item.Id}; Name={item.Name}; Price={item.Price:0.00}; Image={item.ImageUrl}",
                nameof(ItemsModel),
                cancellationToken: cancellationToken);

            SuccessMessage = $"Item {item.Name} was created.";
            return RedirectToPage();
        }
        catch
        {
            await _imageStorage.DeleteAsync(uploadedImage.StoragePath, cancellationToken);
            throw;
        }
    }

    public async Task<IActionResult> OnPostDeleteAsync(CancellationToken cancellationToken)
    {
        var item = await _dbContext.ServiceItems
            .SingleOrDefaultAsync(candidate => candidate.Id == DeleteItemId, cancellationToken);
        if (item is null)
        {
            ErrorMessage = "Item was not found.";
            return RedirectToPage();
        }

        _dbContext.ServiceItems.Remove(item);
        await _dbContext.SaveChangesAsync(cancellationToken);
        await _imageStorage.DeleteAsync(item.ImageStoragePath, cancellationToken);

        await _appLogService.WriteAsync(
            "Warning",
            $"Admin deleted service item. ItemId={item.Id}; Name={item.Name}; Image={item.ImageUrl}",
            nameof(ItemsModel),
            cancellationToken: cancellationToken);

        SuccessMessage = $"Item {item.Name} was deleted.";
        return RedirectToPage();
    }

    private async Task LoadItemsAsync(CancellationToken cancellationToken)
    {
        Items = await _dbContext.ServiceItems
            .AsNoTracking()
            .OrderByDescending(item => item.CreatedAtUtc)
            .ToListAsync(cancellationToken);
    }

    private void ValidateImage(IFormFile image)
    {
        if (image.Length > MaxImageSizeBytes)
        {
            ModelState.AddModelError("CreateInput.Image", "Image must be 5 MB or smaller.");
        }

        var extension = Path.GetExtension(image.FileName);
        if (!AllowedExtensions.Contains(extension))
        {
            ModelState.AddModelError("CreateInput.Image", "Allowed image formats: JPG, PNG, WEBP, GIF.");
        }

        if (!AllowedContentTypes.Contains(image.ContentType))
        {
            ModelState.AddModelError("CreateInput.Image", "Uploaded file must be an image.");
        }
    }

    private string CloudinaryDiagnostics => _imageStorage is ICloudinaryServiceItemImageStorageDiagnostics diagnostics
        ? diagnostics.GetConfigurationSummary()
        : "ImageStorage=Unknown";

    public sealed class CreateItemInputModel
    {
        [Required]
        [StringLength(160)]
        public string Name { get; set; } = string.Empty;
        [StringLength(1000)]
        public string? Description { get; set; }
        [Range(
            typeof(decimal),
            "0.01",
            "1000000",
            ParseLimitsInInvariantCulture = true,
            ConvertValueInInvariantCulture = true)]
        public decimal Price { get; set; }
        public IFormFile? Image { get; set; }
    }
}
