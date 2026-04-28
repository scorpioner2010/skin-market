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
    private readonly IWebHostEnvironment _environment;
    private readonly IAppLogService _appLogService;

    public ItemsModel(AppDbContext dbContext, IWebHostEnvironment environment, IAppLogService appLogService)
    {
        _dbContext = dbContext;
        _environment = environment;
        _appLogService = appLogService;
    }

    public List<ServiceItem> Items { get; private set; } = new();
    [TempData]
    public string? SuccessMessage { get; set; }
    [TempData]
    public string? ErrorMessage { get; set; }
    [BindProperty]
    public CreateItemInputModel CreateInput { get; set; } = new();
    [BindProperty]
    public Guid DeleteItemId { get; set; }

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
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
            ErrorMessage = "Item data is invalid.";
            await LoadItemsAsync(cancellationToken);
            return Page();
        }

        var normalizedName = CreateInput.Name.Trim();
        var itemExists = await _dbContext.ServiceItems
            .AnyAsync(item => item.Name.ToLower() == normalizedName.ToLower(), cancellationToken);
        if (itemExists)
        {
            ModelState.AddModelError("CreateInput.Name", "Item with this name already exists.");
            ErrorMessage = "Item with this name already exists.";
            await LoadItemsAsync(cancellationToken);
            return Page();
        }

        var itemId = Guid.NewGuid();
        var image = CreateInput.Image!;
        var extension = Path.GetExtension(image.FileName).ToLowerInvariant();
        var storedFileName = $"{itemId:N}{extension}";
        var uploadRoot = GetUploadRoot();
        Directory.CreateDirectory(uploadRoot);
        var physicalPath = Path.Combine(uploadRoot, storedFileName);
        var relativeStoragePath = Path.Combine("uploads", "items", storedFileName);
        var imageUrl = $"/uploads/items/{storedFileName}";

        try
        {
            await using (var fileStream = System.IO.File.Create(physicalPath))
            {
                await image.CopyToAsync(fileStream, cancellationToken);
            }

            var now = DateTime.UtcNow;
            var item = new ServiceItem
            {
                Id = itemId,
                Name = normalizedName,
                Description = string.IsNullOrWhiteSpace(CreateInput.Description) ? null : CreateInput.Description.Trim(),
                Price = decimal.Round(CreateInput.Price, 2, MidpointRounding.AwayFromZero),
                ImageUrl = imageUrl,
                ImageStoragePath = relativeStoragePath,
                ImageFileName = Path.GetFileName(image.FileName),
                ImageContentType = image.ContentType,
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
            DeletePhysicalFileIfExists(relativeStoragePath);
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
        DeletePhysicalFileIfExists(item.ImageStoragePath);

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

    private string GetUploadRoot()
    {
        var webRoot = string.IsNullOrWhiteSpace(_environment.WebRootPath)
            ? Path.Combine(_environment.ContentRootPath, "wwwroot")
            : _environment.WebRootPath;

        return Path.Combine(webRoot, "uploads", "items");
    }

    private void DeletePhysicalFileIfExists(string? relativeStoragePath)
    {
        if (string.IsNullOrWhiteSpace(relativeStoragePath))
        {
            return;
        }

        var webRoot = string.IsNullOrWhiteSpace(_environment.WebRootPath)
            ? Path.Combine(_environment.ContentRootPath, "wwwroot")
            : _environment.WebRootPath;
        var fullPath = Path.GetFullPath(Path.Combine(webRoot, relativeStoragePath));
        var allowedRoot = Path.GetFullPath(Path.Combine(webRoot, "uploads", "items"));
        if (!fullPath.StartsWith(allowedRoot, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (System.IO.File.Exists(fullPath))
        {
            System.IO.File.Delete(fullPath);
        }
    }

    public sealed class CreateItemInputModel
    {
        [Required]
        [StringLength(160)]
        public string Name { get; set; } = string.Empty;
        [StringLength(1000)]
        public string? Description { get; set; }
        [Range(typeof(decimal), "0.01", "1000000")]
        public decimal Price { get; set; }
        public IFormFile? Image { get; set; }
    }
}
