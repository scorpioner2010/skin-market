using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using SkinMarket.Contracts;
using SkinMarket.Data;
using SkinMarket.Models;

namespace SkinMarket.Pages.Admin;

public class MenusModel : PageModel
{
    private readonly AppDbContext _dbContext;
    private readonly IAppLogService _appLogService;

    public MenusModel(AppDbContext dbContext, IAppLogService appLogService)
    {
        _dbContext = dbContext;
        _appLogService = appLogService;
    }

    [BindProperty]
    public List<MenuInputModel> Menus { get; set; } = new();

    [TempData]
    public string? SuccessMessage { get; set; }

    [TempData]
    public string? ErrorMessage { get; set; }

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        Menus = await LoadMenuInputsAsync(cancellationToken);
    }

    public async Task<IActionResult> OnPostSaveAsync(CancellationToken cancellationToken)
    {
        var settings = await EnsureSettingsAsync(cancellationToken);
        var editableKeys = NavigationMenuKeys.EditableMenus
            .Select(menu => menu.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var input in Menus.Where(input => editableKeys.Contains(input.Key)))
        {
            if (settings.TryGetValue(input.Key, out var setting))
            {
                setting.IsEnabled = input.IsEnabled;
                setting.UpdatedAtUtc = DateTime.UtcNow;
            }
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
        await _appLogService.WriteAsync(
            "Warning",
            $"Admin updated menu visibility. Enabled={string.Join(",", settings.Values.Where(item => item.IsEnabled).Select(item => item.Key))}",
            nameof(MenusModel),
            cancellationToken: cancellationToken);

        SuccessMessage = "Menu visibility settings saved.";
        return RedirectToPage();
    }

    private async Task<List<MenuInputModel>> LoadMenuInputsAsync(CancellationToken cancellationToken)
    {
        var settings = await EnsureSettingsAsync(cancellationToken);
        return NavigationMenuKeys.EditableMenus
            .OrderBy(menu => menu.SortOrder)
            .Select(menu =>
            {
                var setting = settings[menu.Key];
                return new MenuInputModel
                {
                    Key = menu.Key,
                    DisplayName = setting.DisplayName,
                    IsEnabled = setting.IsEnabled
                };
            })
            .ToList();
    }

    private async Task<Dictionary<string, NavigationMenuSetting>> EnsureSettingsAsync(CancellationToken cancellationToken)
    {
        var settings = await _dbContext.NavigationMenuSettings
            .ToDictionaryAsync(item => item.Key, StringComparer.OrdinalIgnoreCase, cancellationToken);
        var now = DateTime.UtcNow;

        foreach (var menu in NavigationMenuKeys.EditableMenus)
        {
            if (settings.TryGetValue(menu.Key, out var setting))
            {
                setting.DisplayName = menu.DisplayName;
                setting.SortOrder = menu.SortOrder;
                continue;
            }

            setting = new NavigationMenuSetting
            {
                Id = Guid.NewGuid(),
                Key = menu.Key,
                DisplayName = menu.DisplayName,
                IsEnabled = true,
                SortOrder = menu.SortOrder,
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            };
            _dbContext.NavigationMenuSettings.Add(setting);
            settings.Add(setting.Key, setting);
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
        return settings;
    }

    public sealed class MenuInputModel
    {
        public string Key { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public bool IsEnabled { get; set; }
    }
}
