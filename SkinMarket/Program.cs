using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Localization;
using Microsoft.EntityFrameworkCore;
using SkinMarket.Contracts;
using SkinMarket.Data;
using SkinMarket.Infrastructure;
using SkinMarket.Models;
using SkinMarket.Services;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.StaticFiles;

LocalEnvFileLoader.TryLoad(Directory.GetCurrentDirectory());

var builder = WebApplication.CreateBuilder(args);

var port = builder.Configuration["PORT"];
if (string.IsNullOrWhiteSpace(builder.Configuration["ASPNETCORE_URLS"]) &&
    !string.IsNullOrWhiteSpace(port))
{
    builder.WebHost.UseUrls($"http://0.0.0.0:{port}");
}

var supportedCultures = new[]
{
    new CultureInfo("en"),
    new CultureInfo("uk"),
    new CultureInfo("ru")
};

// Add services to the container.
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/Auth/Login";
        options.LogoutPath = "/Auth/Logout";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
    });
builder.Services.Configure<AppRuntimeOptions>(builder.Configuration.GetSection(AppRuntimeOptions.SectionName));
var runtimeOptions = builder.Configuration.GetSection(AppRuntimeOptions.SectionName).Get<AppRuntimeOptions>() ?? new AppRuntimeOptions();
var connectionString = DatabaseConnectionStringFactory.ResolveOptional(builder.Configuration);
var usedDevelopmentDatabaseFallback = false;
if (!runtimeOptions.DisableDatabase &&
    string.IsNullOrWhiteSpace(connectionString) &&
    builder.Environment.IsDevelopment())
{
    runtimeOptions.DisableDatabase = true;
    usedDevelopmentDatabaseFallback = true;
}

builder.Services.PostConfigure<AppRuntimeOptions>(options => options.DisableDatabase = runtimeOptions.DisableDatabase);

if (!runtimeOptions.DisableDatabase && string.IsNullOrWhiteSpace(connectionString))
{
    throw new InvalidOperationException(
        "Database is enabled but no connection string was configured. Set DATABASE_URL or ConnectionStrings__DefaultConnection, or set App__DisableDatabase=true for degraded mode.");
}

var isDatabaseAvailable = !runtimeOptions.DisableDatabase;
builder.Services.AddSingleton(new AppRuntimeState
{
    IsDatabaseAvailable = isDatabaseAvailable
});
builder.Services.AddDbContext<AppDbContext>(options =>
{
    if (isDatabaseAvailable)
    {
        options.UseNpgsql(
            connectionString!,
            npgsqlOptions => npgsqlOptions.EnableRetryOnFailure(
                maxRetryCount: 5,
                maxRetryDelay: TimeSpan.FromSeconds(10),
                errorCodesToAdd: null));
        return;
    }

    options.UseInMemoryDatabase("SkinMarketDegraded");
});
var steamBotOptions = SteamConfigurationResolver.ResolveSteamBotOptions(builder.Configuration);
var steamApiOptions = SteamConfigurationResolver.ResolveSteamApiOptions(builder.Configuration);
builder.Services.AddSingleton(Options.Create(steamBotOptions));
builder.Services.AddSingleton(Options.Create(steamApiOptions));
builder.Services.AddSingleton<BotServiceAvailabilityTracker>();
builder.Services.Configure<PricingOptions>(builder.Configuration.GetSection(PricingOptions.SectionName));
builder.Services.AddMemoryCache();
builder.Services.AddLocalization(options => options.ResourcesPath = "Resources");
builder.Services.AddScoped<AdminAccessPageFilter>();
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownNetworks.Clear();
    options.KnownProxies.Clear();
});
builder.Services.AddRazorPages(options =>
    {
        options.Conventions.AddFolderApplicationModelConvention(
            "/Admin",
            model => model.Filters.Add(new ServiceFilterAttribute(typeof(AdminAccessPageFilter))));
    })
    .AddViewLocalization()
    .AddDataAnnotationsLocalization();
builder.Services.Configure<RequestLocalizationOptions>(options =>
{
    options.DefaultRequestCulture = new RequestCulture("en");
    options.SupportedCultures = supportedCultures;
    options.SupportedUICultures = supportedCultures;
    options.RequestCultureProviders = new IRequestCultureProvider[]
    {
        new CookieRequestCultureProvider(),
        new AcceptLanguageHeaderRequestCultureProvider()
    };
});
builder.Services.AddSingleton<IGameCatalog, GameCatalog>();
builder.Services.AddScoped<IBalanceService, BalanceService>();
builder.Services.AddSingleton<AppLogService>();
builder.Services.AddSingleton<IAppLogService>(provider => provider.GetRequiredService<AppLogService>());
builder.Services.AddSingleton<IAppLogReader>(provider => provider.GetRequiredService<AppLogService>());
builder.Services.AddHostedService<LocalSteamBotHostService>();
builder.Services.AddScoped<IHistoryService, HistoryService>();
builder.Services.AddScoped<IItemPricingService, ItemPricingService>();
builder.Services.AddScoped<IItemPriceResolver, ItemPriceResolver>();
builder.Services.AddSingleton<InventoryPriceRefreshService>();
builder.Services.AddSingleton<IInventoryPriceRefreshService>(provider => provider.GetRequiredService<InventoryPriceRefreshService>());
builder.Services.AddHostedService(provider => provider.GetRequiredService<InventoryPriceRefreshService>());
builder.Services.AddScoped<IMarketPricingService, MarketPricingService>();
builder.Services.AddScoped<IMarketService, MarketService>();
builder.Services.AddScoped<IMarketPurchaseService, MarketPurchaseService>();
builder.Services.AddScoped<IMarketDeliveryService, MarketDeliveryService>();
builder.Services.AddScoped<IItemChatService, ItemChatService>();
builder.Services.AddScoped<ICreditService, CreditService>();
builder.Services.AddScoped<IMinefieldGameService, MinefieldGameService>();
builder.Services.AddScoped<ITradeOperationService, TradeOperationService>();
builder.Services.AddScoped<ISteamBotIntakeService, SteamBotIntakeService>();
builder.Services.AddHostedService<SteamTradeAutomationService>();
builder.Services.AddHostedService<SteamTradeSyncService>();
builder.Services.AddHttpClient<ICsFloatPriceService, CsFloatPriceService>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(15);
    client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("SkinMarket", "1.0"));
    client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("(pricing-refresh)"));
    client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
});
builder.Services.AddHttpClient<ISteamTradeClient, BotServiceSteamTradeClient>((serviceProvider, client) =>
{
    var options = serviceProvider.GetRequiredService<IOptions<SteamBotOptions>>().Value;
    client.BaseAddress = new Uri(options.ServiceUrl, UriKind.Absolute);
    client.Timeout = TimeSpan.FromSeconds(30);
    client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
});
builder.Services.AddHttpClient<IBotServiceStatusClient, BotServiceStatusClient>((serviceProvider, client) =>
{
    var options = serviceProvider.GetRequiredService<IOptions<SteamBotOptions>>().Value;
    client.BaseAddress = new Uri(options.ServiceUrl, UriKind.Absolute);
    client.Timeout = TimeSpan.FromSeconds(10);
    client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
});
builder.Services.AddHttpClient<ISteamBotInventoryClient, BotServiceSteamInventoryClient>((serviceProvider, client) =>
{
    var options = serviceProvider.GetRequiredService<IOptions<SteamBotOptions>>().Value;
    client.BaseAddress = new Uri(options.ServiceUrl, UriKind.Absolute);
    client.Timeout = TimeSpan.FromSeconds(30);
    client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
});
builder.Services.AddHttpClient<ISteamOpenIdService, SteamOpenIdService>();
builder.Services.AddHttpClient<ISteamInventoryService, SteamInventoryService>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(20);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (compatible; SkinMarket/1.0; +https://skinmarket.local)");
    client.DefaultRequestHeaders.Accept.ParseAdd("application/json,text/javascript,*/*;q=0.9");
})
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
    {
        AutomaticDecompression = DecompressionMethods.Brotli |
                                 DecompressionMethods.GZip |
                                 DecompressionMethods.Deflate
    });
builder.Services.AddHttpClient<ISteamProfileService, SteamProfileService>();
builder.Services.AddHttpClient<ISkinportPricingService, SkinportPricingService>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(25);
    client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("SkinMarket", "1.0"));
    client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("(pricing-refresh)"));
    client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    client.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Encoding", "br");
})
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
    {
        AutomaticDecompression = System.Net.DecompressionMethods.Brotli |
                                 System.Net.DecompressionMethods.GZip |
                                 System.Net.DecompressionMethods.Deflate
    });
builder.Services.AddHttpClient<ISteamMarketPriceService, SteamMarketPriceService>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(15);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (compatible; SkinMarket/1.0; +https://skinmarket.local)");
    client.DefaultRequestHeaders.Accept.ParseAdd("application/json,text/javascript,*/*;q=0.9");
});

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");
    var runtimeState = scope.ServiceProvider.GetRequiredService<AppRuntimeState>();
    if (!runtimeState.IsDatabaseAvailable)
    {
        if (usedDevelopmentDatabaseFallback)
        {
            logger.LogWarning(
                "No PostgreSQL connection string was configured in Development. Starting application in degraded mode. Set DATABASE_URL or ConnectionStrings__DefaultConnection to enable PostgreSQL locally.");
        }

        logger.LogWarning("Database mode: DISABLED. Starting application in degraded mode.");
    }
    else
    {
        try
        {
            logger.LogInformation("Database mode: ENABLED. Using PostgreSQL and applying migrations.");
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            dbContext.Database.Migrate();
            var canConnect = dbContext.Database.CanConnect();
            if (!canConnect)
            {
                throw new InvalidOperationException("Database migration completed, but connectivity check failed.");
            }

            logger.LogInformation("Database connection succeeded.");
            logger.LogInformation("Database migrations applied successfully.");
        }
        catch (Exception exception)
        {
            logger.LogCritical(exception, "Application startup failed while initializing PostgreSQL.");
            var appLogService = scope.ServiceProvider.GetService<IAppLogService>();
            if (appLogService is not null)
            {
                await appLogService.WriteAsync("Error", exception.Message, "Startup", exception);
            }
            throw;
        }
    }
}

// Configure the HTTP request pipeline.
app.Use(async (context, next) =>
{
    try
    {
        await next();
    }
    catch (Exception exception)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var appLogService = scope.ServiceProvider.GetRequiredService<IAppLogService>();
        await appLogService.WriteAsync("Error", exception.Message, context.Request.Path, exception, context.RequestAborted);
        throw;
    }
});

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler(errorApp =>
    {
        errorApp.Run(async context =>
        {
            var exceptionFeature = context.Features.Get<IExceptionHandlerPathFeature>();
            if (exceptionFeature?.Error is not null)
            {
                await using var scope = app.Services.CreateAsyncScope();
                var appLogService = scope.ServiceProvider.GetRequiredService<IAppLogService>();
                await appLogService.WriteAsync(
                    "Error",
                    exceptionFeature.Error.Message,
                    exceptionFeature.Path,
                    exceptionFeature.Error,
                    context.RequestAborted);
            }

            if (context.Request.Path.StartsWithSegments("/api"))
            {
                context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                await context.Response.WriteAsJsonAsync(new { error = "Server error" });
                return;
            }

            context.Response.Redirect("/Error");
        });
    });
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseForwardedHeaders();
app.UseHttpsRedirection();
var staticFileContentTypes = new FileExtensionContentTypeProvider();
staticFileContentTypes.Mappings[".data"] = "application/octet-stream";
staticFileContentTypes.Mappings[".wasm"] = "application/wasm";
staticFileContentTypes.Mappings[".symbols.json"] = "application/json";
app.UseStaticFiles(new StaticFileOptions
{
    ContentTypeProvider = staticFileContentTypes
});

app.UseRouting();
app.UseRequestLocalization();

app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/set-language", (HttpContext httpContext, string culture, string? returnUrl) =>
{
    var normalizedCulture = supportedCultures.Any(item => item.Name == culture) ? culture : "en";
    var cookieValue = CookieRequestCultureProvider.MakeCookieValue(new RequestCulture(normalizedCulture));

    httpContext.Response.Cookies.Append(
        CookieRequestCultureProvider.DefaultCookieName,
        cookieValue,
        new CookieOptions
        {
            Expires = DateTimeOffset.UtcNow.AddYears(1),
            IsEssential = true,
            Path = "/"
        });

    var targetUrl = string.IsNullOrWhiteSpace(returnUrl) ? "/" : returnUrl;
    return Results.LocalRedirect(targetUrl);
});

app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));

app.MapPost("/api/inventory/prices/refresh", async (
    HttpContext httpContext,
    InventoryPriceRefreshRequest request,
    IInventoryPriceRefreshService refreshService,
    IAppLogService appLogService,
    CancellationToken cancellationToken) =>
{
    if (!(httpContext.User.Identity?.IsAuthenticated ?? false))
    {
        return Results.Unauthorized();
    }

    var marketHashNames = request.Items
        .Select(item => item.MarketHashName)
        .Where(item => !string.IsNullOrWhiteSpace(item))
        .ToList();

    await appLogService.WriteAsync(
        "Info",
        $"Refresh requested. GameType={(int)request.GameType}; Count={marketHashNames.Count}; Items={string.Join(" | ", marketHashNames.Take(20))}",
        "InventoryPricesRefresh",
        cancellationToken: cancellationToken);
    await refreshService.QueueRefreshAsync(marketHashNames, request.GameType, cancellationToken);
    return Results.Accepted();
});

app.MapPost("/api/inventory/prices/status", async (
    HttpContext httpContext,
    InventoryPriceRefreshRequest request,
    IInventoryPriceRefreshService refreshService,
    IAppLogService appLogService,
    CancellationToken cancellationToken) =>
{
    if (!(httpContext.User.Identity?.IsAuthenticated ?? false))
    {
        return Results.Unauthorized();
    }

    var marketHashNames = request.Items
        .Select(item => item.MarketHashName)
        .Where(item => !string.IsNullOrWhiteSpace(item))
        .Distinct(StringComparer.Ordinal)
        .ToList();

    var statuses = await refreshService.GetStatusAsync(marketHashNames, request.GameType, cancellationToken);
    await appLogService.WriteAsync(
        "Info",
        $"Status requested. GameType={(int)request.GameType}; Count={marketHashNames.Count}; Summary={string.Join(" | ", statuses.Take(20).Select(item => $"{item.Key}={item.Value.Status}/{item.Value.Source}/{item.Value.FailureReason}"))}",
        "InventoryPricesStatus",
        cancellationToken: cancellationToken);
    var responseItems = request.Items.Select(item =>
    {
        var normalizedMarketHashName = MarketHashNameUtility.Normalize(item.MarketHashName) ?? item.MarketHashName;
        statuses.TryGetValue(normalizedMarketHashName, out var status);
        status ??= new ItemPriceResolutionResult
        {
            Currency = "USD",
            Source = "Unavailable",
            Status = "Refreshing",
            FailureReason = "Price refresh pending."
        };

        return new InventoryPriceStatusItem
        {
            AssetId = item.AssetId,
            MarketHashName = normalizedMarketHashName,
            HasPrice = status.HasPrice,
            Price = status.Price,
            Currency = status.Currency,
            Source = status.Source,
            Status = status.Status,
            IsCached = status.IsCached,
            IsEstimated = status.IsEstimated,
            FailureReason = status.FailureReason
        };
    }).ToList();

    return Results.Ok(new { Items = responseItems });
});

app.MapGet("/api/sales/status", async (
    HttpContext httpContext,
    AppDbContext dbContext,
    ISteamTradeClient steamTradeClient,
    ICreditService creditService,
    IAppLogService appLogService,
    CancellationToken cancellationToken) =>
{
    if (!(httpContext.User.Identity?.IsAuthenticated ?? false))
    {
        return Results.Unauthorized();
    }

    var steamId = httpContext.User.FindFirst("SteamId")?.Value;
    if (string.IsNullOrWhiteSpace(steamId))
    {
        return Results.Unauthorized();
    }

    var appUser = await dbContext.AppUsers
        .AsNoTracking()
        .SingleOrDefaultAsync(user => user.SteamId == steamId, cancellationToken);
    if (appUser is null)
    {
        return Results.NotFound(new
        {
            success = false,
            message = "Local user profile was not found."
        });
    }

    var statusesToPoll = new[]
    {
        "Pending",
        "BotPending",
        "AwaitingBotConfirmation",
        "TradeCreated",
        "AwaitingUserAction",
        "TradeAcceptedPendingReceipt",
        "ReceivedByBot",
        "InEscrow"
    };

    var syncOperations = await dbContext.TradeOperations
        .Where(operation =>
            operation.AppUserId == appUser.Id &&
            operation.TradeOfferId != null &&
            statusesToPoll.Contains(operation.Status))
        .ToListAsync(cancellationToken);

    var deliveryStatusesToPoll = new[]
    {
        "PendingDelivery",
        "DeliveryBotPending",
        "AwaitingBotConfirmation",
        "DeliveryTradeCreated",
        "AwaitingBuyerAction",
        "DeliveryInEscrow"
    };

    var syncDeliveries = await dbContext.MarketPurchaseRecords
        .Where(item =>
            item.BuyerAppUserId == appUser.Id &&
            item.DeliveryTradeOfferId != null &&
            item.DeliveryStatus != null &&
            deliveryStatusesToPoll.Contains(item.DeliveryStatus))
        .ToListAsync(cancellationToken);

    var statusRequests = syncOperations
        .Select(operation => new SteamTradeOfferStatusRequest
        {
            OfferId = operation.TradeOfferId!,
            Flow = "intake"
        })
        .Concat(syncDeliveries.Select(item => new SteamTradeOfferStatusRequest
        {
            OfferId = item.DeliveryTradeOfferId!,
            Flow = "delivery"
        }))
        .ToList();
    if (statusRequests.Count > 0)
    {
        var statusResults = await steamTradeClient.GetOfferStatusesAsync(statusRequests, cancellationToken);
        var statusMap = statusResults.ToDictionary(
            item => $"{item.Flow}:{item.OfferId}",
            item => item,
            StringComparer.Ordinal);
        var transitionLogs = new List<(string Level, string Message, string Source)>();
        var changed = false;
        foreach (var operation in syncOperations)
        {
            if (!statusMap.TryGetValue($"intake:{operation.TradeOfferId}", out var status))
            {
                continue;
            }

            changed |= SteamTradeSyncService.ApplyTradeOperationStatus(operation, status, transitionLogs);
        }

        foreach (var item in syncDeliveries)
        {
            if (!statusMap.TryGetValue($"delivery:{item.DeliveryTradeOfferId}", out var status))
            {
                continue;
            }

            changed |= SteamTradeSyncService.ApplyDeliveryStatus(item, status, transitionLogs);
        }

        if (changed)
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        foreach (var entry in transitionLogs)
        {
            await appLogService.WriteAsync(entry.Level, entry.Message, entry.Source, cancellationToken: cancellationToken);
        }

        var creditTargets = syncOperations
            .Where(operation => operation.Status == "ReceivedByBot" && !operation.CreditedAtUtc.HasValue)
            .Select(operation => new { operation.Id, operation.AppUserId })
            .ToList();
        foreach (var target in creditTargets)
        {
            var result = await creditService.ConfirmReceivedAndCreditAsync(target.Id, target.AppUserId, cancellationToken);
            await appLogService.WriteAsync(
                result.Success ? "Info" : "Warning",
                $"Live status poll credit result. TradeOperationId={target.Id}; Success={result.Success}; Status={result.NewStatus}; OfferId={result.TradeOfferId ?? "<null>"}; Message={result.Message}",
                "SalesStatusApi",
                cancellationToken: cancellationToken);
        }
    }

    var operations = await dbContext.TradeOperations
        .AsNoTracking()
        .Where(operation =>
            operation.AppUserId == appUser.Id &&
            statusesToPoll.Contains(operation.Status))
        .OrderByDescending(operation => operation.UpdatedAtUtc)
        .Select(operation => new
        {
            id = operation.Id,
            flow = "intake",
            assetId = operation.AssetId,
            itemName = operation.ItemName,
            status = operation.Status,
            statusText = SaleStatusApiText.FormatStatus(operation.Status),
            tradeOfferId = operation.TradeOfferId,
            steamOfferUrl = SaleStatusApiText.BuildSteamOfferUrl(operation.TradeOfferId),
            accountTradeOffersUrl = "https://steamcommunity.com/id/angielanz75/tradeoffers",
            canCancel = SaleStatusApiText.CanCancelIntakeStatus(operation.Status) && operation.TradeOfferId != null,
            creditAmount = operation.CreditAmount,
            updatedAtUtc = operation.UpdatedAtUtc
        })
        .ToListAsync(cancellationToken);

    var deliveries = await dbContext.MarketPurchaseRecords
        .AsNoTracking()
        .Where(item =>
            item.BuyerAppUserId == appUser.Id &&
            item.DeliveryStatus != null &&
            deliveryStatusesToPoll.Contains(item.DeliveryStatus))
        .OrderByDescending(item => item.UpdatedAtUtc)
        .Select(item => new
        {
            id = item.Id,
            flow = "delivery",
            assetId = item.AssetId,
            itemName = item.ItemName,
            status = item.DeliveryStatus!,
            statusText = SaleStatusApiText.FormatStatus(item.DeliveryStatus),
            tradeOfferId = item.DeliveryTradeOfferId,
            steamOfferUrl = SaleStatusApiText.BuildSteamOfferUrl(item.DeliveryTradeOfferId),
            accountTradeOffersUrl = "https://steamcommunity.com/id/angielanz75/tradeoffers",
            canCancel = false,
            creditAmount = 0m,
            updatedAtUtc = item.UpdatedAtUtc
        })
        .ToListAsync(cancellationToken);

    var recentIntakes = await dbContext.TradeOperations
        .AsNoTracking()
        .Where(operation => operation.AppUserId == appUser.Id)
        .OrderByDescending(operation => operation.UpdatedAtUtc)
        .Take(20)
        .Select(operation => new
        {
            id = operation.Id,
            flow = "intake",
            assetId = operation.AssetId,
            itemName = operation.ItemName,
            status = operation.Status,
            statusText = SaleStatusApiText.FormatStatus(operation.Status),
            tradeOfferId = operation.TradeOfferId,
            steamOfferUrl = SaleStatusApiText.BuildSteamOfferUrl(operation.TradeOfferId),
            accountTradeOffersUrl = "https://steamcommunity.com/id/angielanz75/tradeoffers",
            canCancel = SaleStatusApiText.CanCancelIntakeStatus(operation.Status) && operation.TradeOfferId != null,
            creditAmount = operation.CreditAmount,
            updatedAtUtc = operation.UpdatedAtUtc
        })
        .ToListAsync(cancellationToken);

    var recentDeliveries = await dbContext.MarketPurchaseRecords
        .AsNoTracking()
        .Where(item => item.BuyerAppUserId == appUser.Id)
        .OrderByDescending(item => item.UpdatedAtUtc)
        .Take(20)
        .Select(item => new
        {
            id = item.Id,
            flow = "delivery",
            assetId = item.AssetId,
            itemName = item.ItemName,
            status = item.DeliveryStatus ?? item.Status,
            statusText = SaleStatusApiText.FormatStatus(item.DeliveryStatus ?? item.Status),
            tradeOfferId = item.DeliveryTradeOfferId,
            steamOfferUrl = SaleStatusApiText.BuildSteamOfferUrl(item.DeliveryTradeOfferId),
            accountTradeOffersUrl = "https://steamcommunity.com/id/angielanz75/tradeoffers",
            canCancel = false,
            creditAmount = 0m,
            updatedAtUtc = item.UpdatedAtUtc
        })
        .ToListAsync(cancellationToken);

    var activeOperations = operations.Concat(deliveries)
        .OrderByDescending(item => item.updatedAtUtc)
        .ToList();
    var recentOperations = recentIntakes.Concat(recentDeliveries)
        .OrderByDescending(item => item.updatedAtUtc)
        .ToList();

    return Results.Ok(new
    {
        success = true,
        operations = activeOperations,
        recentOperations
    });
});

app.MapGet("/api/chats/unread-counts", async (
    HttpContext httpContext,
    AppDbContext dbContext,
    IItemChatService itemChatService,
    CancellationToken cancellationToken) =>
{
    if (!(httpContext.User.Identity?.IsAuthenticated ?? false))
    {
        return Results.Unauthorized();
    }

    var appUserIdClaim = httpContext.User.FindFirst("AppUserId")?.Value;
    var steamId = httpContext.User.FindFirst("SteamId")?.Value;
    AppUser? appUser = null;
    if (Guid.TryParse(appUserIdClaim, out var appUserId))
    {
        appUser = await dbContext.AppUsers
            .AsNoTracking()
            .SingleOrDefaultAsync(user => user.Id == appUserId, cancellationToken);
    }

    if (appUser is null && !string.IsNullOrWhiteSpace(steamId))
    {
        appUser = await dbContext.AppUsers
            .AsNoTracking()
            .SingleOrDefaultAsync(user => user.SteamId == steamId, cancellationToken);
    }

    if (appUser is null)
    {
        return Results.NotFound(new
        {
            success = false,
            message = "Local user profile was not found."
        });
    }

    var userUnreadChats = await itemChatService.CountUserUnreadThreadsAsync(appUser.Id, cancellationToken);
    var adminUnreadChats = appUser.IsAdmin
        ? await itemChatService.CountAdminUnreadThreadsAsync(cancellationToken)
        : 0;

    return Results.Ok(new
    {
        success = true,
        userUnreadChats,
        adminUnreadChats
    });
});

app.MapGet("/api/games/minefield/state", async (
    HttpContext httpContext,
    AppDbContext dbContext,
    IMinefieldGameService minefieldGameService,
    CancellationToken cancellationToken) =>
{
    var appUser = await ResolveCurrentAppUserAsync(httpContext, dbContext, cancellationToken);
    if (appUser is null)
    {
        return Results.Unauthorized();
    }

    var result = await minefieldGameService.GetStateAsync(appUser.Id, cancellationToken);
    return result.Success ? Results.Ok(result) : Results.BadRequest(result);
});

app.MapPost("/api/games/minefield/start", async (
    HttpContext httpContext,
    AppDbContext dbContext,
    IMinefieldGameService minefieldGameService,
    MinefieldStartRequest request,
    CancellationToken cancellationToken) =>
{
    var appUser = await ResolveCurrentAppUserAsync(httpContext, dbContext, cancellationToken);
    if (appUser is null)
    {
        return Results.Unauthorized();
    }

    var result = await minefieldGameService.StartAsync(appUser.Id, request.Bet, cancellationToken);
    return result.Success ? Results.Ok(result) : Results.BadRequest(result);
});

app.MapPost("/api/games/minefield/step", async (
    HttpContext httpContext,
    AppDbContext dbContext,
    IMinefieldGameService minefieldGameService,
    MinefieldStepRequest request,
    CancellationToken cancellationToken) =>
{
    var appUser = await ResolveCurrentAppUserAsync(httpContext, dbContext, cancellationToken);
    if (appUser is null)
    {
        return Results.Unauthorized();
    }

    var result = await minefieldGameService.StepAsync(appUser.Id, request, cancellationToken);
    return result.Success ? Results.Ok(result) : Results.BadRequest(result);
});

app.MapPost("/api/games/minefield/claim", async (
    HttpContext httpContext,
    AppDbContext dbContext,
    IMinefieldGameService minefieldGameService,
    MinefieldClaimRequest request,
    CancellationToken cancellationToken) =>
{
    var appUser = await ResolveCurrentAppUserAsync(httpContext, dbContext, cancellationToken);
    if (appUser is null)
    {
        return Results.Unauthorized();
    }

    var result = await minefieldGameService.ClaimAsync(appUser.Id, request, cancellationToken);
    return result.Success ? Results.Ok(result) : Results.BadRequest(result);
});

app.MapPost("/api/sales/cancel", async (
    HttpContext httpContext,
    AppDbContext dbContext,
    ISteamTradeClient steamTradeClient,
    IAppLogService appLogService,
    CancellationToken cancellationToken) =>
{
    if (!(httpContext.User.Identity?.IsAuthenticated ?? false))
    {
        return Results.Unauthorized();
    }

    var steamId = httpContext.User.FindFirst("SteamId")?.Value;
    if (string.IsNullOrWhiteSpace(steamId))
    {
        return Results.Unauthorized();
    }

    if (!httpContext.Request.HasFormContentType)
    {
        return Results.BadRequest(new
        {
            success = false,
            message = "Cancel request is invalid."
        });
    }

    var form = await httpContext.Request.ReadFormAsync(cancellationToken);
    if (!Guid.TryParse(form["tradeOperationId"], out var tradeOperationId))
    {
        return Results.BadRequest(new
        {
            success = false,
            message = "Sale request is invalid."
        });
    }

    var appUser = await dbContext.AppUsers
        .SingleOrDefaultAsync(user => user.SteamId == steamId, cancellationToken);
    if (appUser is null)
    {
        return Results.NotFound(new
        {
            success = false,
            message = "Local user profile was not found."
        });
    }

    var operation = await dbContext.TradeOperations
        .SingleOrDefaultAsync(item => item.Id == tradeOperationId && item.AppUserId == appUser.Id, cancellationToken);
    if (operation is null)
    {
        return Results.NotFound(new
        {
            success = false,
            message = "Sale request was not found."
        });
    }

    if (string.IsNullOrWhiteSpace(operation.TradeOfferId) || !SaleStatusApiText.CanCancelIntakeStatus(operation.Status))
    {
        return Results.BadRequest(new
        {
            success = false,
            message = $"Trade offer cannot be canceled from status {operation.Status}."
        });
    }

    var previousStatus = operation.Status;
    var result = await steamTradeClient.CancelOfferAsync(
        operation.TradeOfferId,
        "intake",
        $"Seller canceled intake offer from global action panel. TradeOperationId={operation.Id}",
        cancellationToken);

    if (!result.Success)
    {
        return Results.BadRequest(new
        {
            success = false,
            message = result.Message
        });
    }

    operation.Status = "Failed";
    operation.ErrorMessage = $"Trade offer was canceled by seller. {result.Message}";
    operation.UpdatedAtUtc = DateTime.UtcNow;
    await dbContext.SaveChangesAsync(cancellationToken);

    await appLogService.WriteAsync(
        "Warning",
        $"Intake trade canceled by seller from global action panel. TradeOperationId={operation.Id}; AppUserId={appUser.Id}; OfferId={operation.TradeOfferId}; PreviousStatus={previousStatus}; CancelState={result.State ?? "<null>"}; Message={result.Message}",
        "SalesStatusApi",
        cancellationToken: cancellationToken);

    return Results.Ok(new
    {
        success = true,
        message = "Trade offer was canceled."
    });
});

app.MapRazorPages();

app.Run();

static async Task<AppUser?> ResolveCurrentAppUserAsync(
    HttpContext httpContext,
    AppDbContext dbContext,
    CancellationToken cancellationToken)
{
    if (!(httpContext.User.Identity?.IsAuthenticated ?? false))
    {
        return null;
    }

    var appUserIdClaim = httpContext.User.FindFirst("AppUserId")?.Value;
    if (Guid.TryParse(appUserIdClaim, out var appUserId))
    {
        var user = await dbContext.AppUsers
            .SingleOrDefaultAsync(item => item.Id == appUserId, cancellationToken);
        if (user is not null)
        {
            return user;
        }
    }

    var steamId = httpContext.User.FindFirst("SteamId")?.Value;
    return string.IsNullOrWhiteSpace(steamId)
        ? null
        : await dbContext.AppUsers.SingleOrDefaultAsync(item => item.SteamId == steamId, cancellationToken);
}

internal static class SaleStatusApiText
{
    public static string FormatStatus(string? status)
    {
        return status switch
        {
            "Pending" => "Pending",
            "BotPending" => "Bot pending",
            "AwaitingBotConfirmation" => "Awaiting bot confirmation",
            "TradeCreated" => "Trade created",
            "AwaitingUserAction" => "Awaiting user action",
            "AwaitingBuyerAction" => "Awaiting buyer action",
            "TradeAcceptedPendingReceipt" => "Trade accepted",
            "ReceivedByBot" => "Received by bot",
            "PendingDelivery" => "Pending delivery",
            "DeliveryBotPending" => "Delivery bot pending",
            "DeliveryTradeCreated" => "Delivery trade created",
            "DeliveryInEscrow" => "Delivery in escrow",
            "InEscrow" => "In escrow",
            _ => status ?? string.Empty
        };
    }

    public static string BuildSteamOfferUrl(string? offerId)
    {
        return string.IsNullOrWhiteSpace(offerId)
            ? "https://steamcommunity.com/my/tradeoffers/"
            : $"https://steamcommunity.com/tradeoffer/{offerId}/";
    }

    public static bool CanCancelIntakeStatus(string? status)
    {
        return status is "AwaitingBotConfirmation" or "TradeCreated" or "AwaitingUserAction";
    }
}
