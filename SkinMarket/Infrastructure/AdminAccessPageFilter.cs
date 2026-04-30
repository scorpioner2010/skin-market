using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;
using SkinMarket.Data;

namespace SkinMarket.Infrastructure;

public class AdminAccessPageFilter : IAsyncPageFilter
{
    private readonly AppDbContext _dbContext;
    private readonly AppRuntimeState _runtimeState;

    public AdminAccessPageFilter(AppDbContext dbContext, AppRuntimeState runtimeState)
    {
        _dbContext = dbContext;
        _runtimeState = runtimeState;
    }

    public Task OnPageHandlerSelectionAsync(PageHandlerSelectedContext context)
    {
        return Task.CompletedTask;
    }

    public async Task OnPageHandlerExecutionAsync(PageHandlerExecutingContext context, PageHandlerExecutionDelegate next)
    {
        if (_runtimeState.IsDegradedMode)
        {
            context.Result = new RedirectToPageResult("/Index");
            return;
        }

        var steamId = context.HttpContext.User.FindFirst("SteamId")?.Value;
        if (string.IsNullOrWhiteSpace(steamId))
        {
            context.Result = new RedirectToPageResult("/Auth/Login");
            return;
        }

        var isAdmin = await _dbContext.AppUsers
            .AsNoTracking()
            .AnyAsync(user => user.SteamId == steamId && user.IsAdmin, context.HttpContext.RequestAborted);
        if (!isAdmin)
        {
            context.Result = new ForbidResult();
            return;
        }

        await next();
    }
}
