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
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Localization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.StaticFiles;
using SkinMarket;
using SkinMarket.Localization;

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
builder.Services.Configure<SteamMarketPriceOptions>(builder.Configuration.GetSection(SteamMarketPriceOptions.SectionName));
builder.Services.Configure<SteamInventoryRefreshOptions>(builder.Configuration.GetSection(SteamInventoryRefreshOptions.SectionName));
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
builder.Services.AddSingleton<IPriceDiagnosticLogService, PriceDiagnosticLogService>();
builder.Services.AddSingleton<IFxRateService, UsdOnlyFxRateService>();
builder.Services.AddSingleton<InventoryPriceRefreshService>();
builder.Services.AddSingleton<IInventoryPriceRefreshService>(provider => provider.GetRequiredService<InventoryPriceRefreshService>());
builder.Services.AddHostedService(provider => provider.GetRequiredService<InventoryPriceRefreshService>());
builder.Services.AddSingleton<SteamInventoryRefreshWorker>();
builder.Services.AddSingleton<ISteamInventoryRefreshService>(provider => provider.GetRequiredService<SteamInventoryRefreshWorker>());
builder.Services.AddHostedService(provider => provider.GetRequiredService<SteamInventoryRefreshWorker>());
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
builder.Services.AddHttpClient<IDMarketPricingService, DMarketPricingService>(client =>
{
    client.BaseAddress = new Uri("https://api.dmarket.com", UriKind.Absolute);
    client.Timeout = TimeSpan.FromSeconds(20);
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
builder.Services.AddHttpClient("SteamInventoryRefresh", (serviceProvider, client) =>
{
    var options = serviceProvider.GetRequiredService<IOptions<SteamInventoryRefreshOptions>>().Value;
    client.Timeout = TimeSpan.FromSeconds(Math.Max(5, options.RequestTimeoutSeconds));
    client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (compatible; SkinMarket/1.0; +https://skinmarket.local)");
    client.DefaultRequestHeaders.Accept.ParseAdd("application/json,text/javascript,*/*;q=0.9");
})
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
    {
        AutomaticDecompression = DecompressionMethods.Brotli |
                                 DecompressionMethods.GZip |
                                 DecompressionMethods.Deflate
    });
builder.Services.AddHttpClient("SteamInventoryDiagnostic", client =>
{
    client.Timeout = TimeSpan.FromSeconds(25);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (compatible; SkinMarket/1.0; +https://skinmarket.local)");
    client.DefaultRequestHeaders.Accept.ParseAdd("application/json,text/javascript,*/*;q=0.9");
})
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
    {
        AllowAutoRedirect = false,
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

app.MapPost("/api/inventory/refresh", async (
    HttpContext httpContext,
    SteamInventoryRefreshRequest request,
    ISteamInventoryRefreshService refreshService,
    AppDbContext dbContext,
    CancellationToken cancellationToken) =>
{
    if (!(httpContext.User.Identity?.IsAuthenticated ?? false))
    {
        return Results.Unauthorized();
    }

    var appUser = await ResolveCurrentAppUserAsync(httpContext, dbContext, cancellationToken);
    if (appUser is null)
    {
        return Results.Unauthorized();
    }

    var status = await refreshService.EnqueueRefreshAsync(
        appUser.SteamId,
        request.GameType,
        SteamInventoryRefreshPriority.High,
        cancellationToken,
        request.ForceFreshness,
        string.IsNullOrWhiteSpace(request.Reason) ? SteamInventoryRefreshReasons.Manual : request.Reason);

    return Results.Ok(status);
});

app.MapPost("/api/inventory/status", async (
    HttpContext httpContext,
    SteamInventoryRefreshRequest request,
    ISteamInventoryRefreshService refreshService,
    AppDbContext dbContext,
    CancellationToken cancellationToken) =>
{
    if (!(httpContext.User.Identity?.IsAuthenticated ?? false))
    {
        return Results.Unauthorized();
    }

    var appUser = await ResolveCurrentAppUserAsync(httpContext, dbContext, cancellationToken);
    if (appUser is null)
    {
        return Results.Unauthorized();
    }

    var status = await refreshService.GetStatusAsync(appUser.SteamId, request.GameType, cancellationToken);
    return Results.Ok(status);
});

app.MapPost("/api/admin/steam-inventory/test", async (
    HttpContext httpContext,
    SteamInventoryServerTestRequest request,
    AppDbContext dbContext,
    IHttpClientFactory httpClientFactory,
    IGameCatalog gameCatalog,
    IAppLogService appLogService,
    CancellationToken cancellationToken) =>
{
    var appUser = await ResolveCurrentAppUserAsync(httpContext, dbContext, cancellationToken);
    if (appUser is null)
    {
        return Results.Unauthorized();
    }

    if (!appUser.IsAdmin)
    {
        return Results.Forbid();
    }

    var steamId = string.IsNullOrWhiteSpace(request.SteamId)
        ? appUser.SteamId
        : request.SteamId.Trim();
    var game = gameCatalog.Get(request.GameType);
    var timestampUtc = DateTime.UtcNow;
    var outboundIp = await ReadOutboundIpAsync(httpClientFactory, cancellationToken);
    var inventoryResult = await TestSteamInventoryRequestAsync(
        httpClientFactory,
        steamId,
        game,
        cancellationToken);

    var level = inventoryResult.HttpStatusCode is 429 or 403 ||
                !string.IsNullOrWhiteSpace(inventoryResult.ExceptionMessage)
        ? "Warning"
        : "Info";
    await appLogService.WriteAsync(
        level,
        string.Join("; ", new[]
        {
            "Event=SteamInventoryServerTest",
            $"TimestampUtc={timestampUtc:O}",
            $"SteamId={steamId}",
            $"GameType={(int)game.Type}",
            $"HttpStatusCode={inventoryResult.HttpStatusCode?.ToString() ?? "<null>"}",
            $"RetryAfter={inventoryResult.RetryAfter ?? "<none>"}",
            $"BodyLength={inventoryResult.BodyLength?.ToString() ?? "<null>"}",
            $"BodySnippet={TruncateForDiagnosticLog(inventoryResult.BodySnippet, 300)}",
            $"DurationMs={inventoryResult.DurationMs}",
            $"OutboundIp={outboundIp.OutboundIp ?? "<null>"}",
            $"IpLookupError={outboundIp.ExceptionMessage ?? "<null>"}",
            $"Headers={TruncateForDiagnosticLog(inventoryResult.Headers, 1200)}",
            $"Exception={inventoryResult.ExceptionMessage ?? "<null>"}"
        }),
        "AdminSteamInventoryDiagnostic",
        cancellationToken: CancellationToken.None);

    return Results.Ok(new
    {
        timestampUtc,
        steamId,
        gameType = game.Type,
        appId = game.SteamAppId,
        contextId = game.SteamContextId,
        outboundIp = outboundIp.OutboundIp,
        outboundIpTimestampUtc = outboundIp.TimestampUtc,
        outboundIpException = outboundIp.ExceptionMessage,
        inventory = inventoryResult
    });
});

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
            PriceType = PriceTypeNames.Unavailable,
            Status = "Refreshing",
            FailureReason = "Price refresh pending."
        };

        return new InventoryPriceStatusItem
        {
            AssetId = item.AssetId,
            MarketHashName = normalizedMarketHashName,
            HasPrice = status.HasPrice,
            Price = status.Price,
            PriceUsd = status.PriceUsd,
            DisplayPrice = status.HasPrice && status.DisplayPriceUsd.HasValue
                ? $"{(status.IsEstimated ? "~" : string.Empty)}${status.DisplayPriceUsd.Value:0.00} {(status.IsStale ? "Stale" : status.IsCached ? "Cached" : status.IsEstimated ? "Estimated" : status.Source)}"
                : "No reliable price",
            Currency = status.Currency,
            Source = status.Source,
            PriceType = status.PriceType,
            Status = status.Status,
            IsCached = status.IsCached,
            IsEstimated = status.IsEstimated,
            IsStale = status.IsStale,
            ConfidenceScore = status.ConfidenceScore,
            ConfidenceLabel = status.ConfidenceLabel,
            LastUpdatedUtc = status.LastUpdatedUtc,
            ObservedAtUtc = status.ObservedAtUtc,
            ExpiresAtUtc = status.ExpiresAtUtc,
            OriginalPrice = status.OriginalPrice,
            OriginalCurrency = status.OriginalCurrency,
            FxRate = status.FxRate,
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
    ISteamInventoryRefreshService inventoryRefreshService,
    IGameCatalog gameCatalog,
    IAppLogService appLogService,
    IStringLocalizer<SharedResource> localizer,
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
            message = UiTextLocalizer.LocalizeMessage(localizer, "Local user profile was not found.")
        });
    }

    var syncOperations = await dbContext.TradeOperations
        .Where(operation =>
            operation.AppUserId == appUser.Id &&
            operation.TradeOfferId != null &&
            TradeFlowStatusPolicy.ActiveIntakeStatuses.Contains(operation.Status))
        .ToListAsync(cancellationToken);

    var syncDeliveries = await dbContext.MarketPurchaseRecords
        .Where(item =>
            item.BuyerAppUserId == appUser.Id &&
            item.DeliveryTradeOfferId != null &&
            item.DeliveryStatus != null &&
            TradeFlowStatusPolicy.ActiveDeliveryStatuses.Contains(item.DeliveryStatus))
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
        var forceRefreshRequests = new List<(GameType GameType, string Reason)>();
        var changed = false;
        foreach (var operation in syncOperations)
        {
            if (!statusMap.TryGetValue($"intake:{operation.TradeOfferId}", out var status))
            {
                continue;
            }

            var previousStatus = operation.Status;
            if (SteamTradeSyncService.ApplyTradeOperationStatus(operation, status, transitionLogs))
            {
                changed = true;
                if (!string.Equals(previousStatus, "ReceivedByBot", StringComparison.Ordinal) &&
                    string.Equals(operation.Status, "ReceivedByBot", StringComparison.Ordinal))
                {
                    forceRefreshRequests.Add((
                        ResolveGameType(gameCatalog, operation.AppId, operation.ContextId),
                        SteamInventoryRefreshReasons.TradeAccepted));
                }
            }
        }

        foreach (var item in syncDeliveries)
        {
            if (!statusMap.TryGetValue($"delivery:{item.DeliveryTradeOfferId}", out var status))
            {
                continue;
            }

            var previousDeliveryStatus = item.DeliveryStatus;
            if (SteamTradeSyncService.ApplyDeliveryStatus(item, status, transitionLogs))
            {
                changed = true;
                if (!string.Equals(previousDeliveryStatus, "Delivered", StringComparison.Ordinal) &&
                    string.Equals(item.DeliveryStatus, "Delivered", StringComparison.Ordinal))
                {
                    forceRefreshRequests.Add((
                        ResolveGameType(gameCatalog, item.AppId, item.ContextId),
                        SteamInventoryRefreshReasons.ItemDelivered));
                }
            }
        }

        if (changed)
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        foreach (var entry in transitionLogs)
        {
            await appLogService.WriteAsync(entry.Level, entry.Message, entry.Source, cancellationToken: cancellationToken);
        }

        foreach (var request in forceRefreshRequests)
        {
            await TryEnqueueInventoryRefreshAsync(
                inventoryRefreshService,
                appLogService,
                appUser.SteamId,
                request.GameType,
                request.Reason,
                cancellationToken,
                "live sale status poll");
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
            TradeFlowStatusPolicy.ActiveIntakeStatuses.Contains(operation.Status))
        .OrderByDescending(operation => operation.UpdatedAtUtc)
        .Select(operation => new
        {
            id = operation.Id,
            flow = "intake",
            assetId = operation.AssetId,
            itemName = operation.ItemName,
            status = operation.Status,
            statusText = UiTextLocalizer.LocalizeStatus(localizer, operation.Status),
            detailText = SaleStatusApiText.DescribeStatus("intake", operation.Status, operation.TradeOfferId),
            tradeOfferId = operation.TradeOfferId,
            steamOfferUrl = SaleStatusApiText.BuildSteamOfferUrl(operation.TradeOfferId),
            accountTradeOffersUrl = SaleStatusApiText.AccountTradeOffersUrl,
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
            TradeFlowStatusPolicy.ActiveDeliveryStatuses.Contains(item.DeliveryStatus) &&
            (item.DeliveryStatus != "AwaitingBotConfirmation" || item.DeliveryTradeOfferId != null))
        .OrderByDescending(item => item.UpdatedAtUtc)
        .Select(item => new
        {
            id = item.Id,
            flow = "delivery",
            assetId = item.AssetId,
            itemName = item.ItemName,
            status = item.DeliveryStatus!,
            statusText = UiTextLocalizer.LocalizeStatus(localizer, item.DeliveryStatus),
            detailText = SaleStatusApiText.DescribeStatus("delivery", item.DeliveryStatus, item.DeliveryTradeOfferId),
            tradeOfferId = item.DeliveryTradeOfferId,
            steamOfferUrl = SaleStatusApiText.BuildSteamOfferUrl(item.DeliveryTradeOfferId),
            accountTradeOffersUrl = SaleStatusApiText.AccountTradeOffersUrl,
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
            statusText = UiTextLocalizer.LocalizeStatus(localizer, operation.Status),
            detailText = SaleStatusApiText.DescribeStatus("intake", operation.Status, operation.TradeOfferId),
            tradeOfferId = operation.TradeOfferId,
            steamOfferUrl = SaleStatusApiText.BuildSteamOfferUrl(operation.TradeOfferId),
            accountTradeOffersUrl = SaleStatusApiText.AccountTradeOffersUrl,
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
            statusText = UiTextLocalizer.LocalizeStatus(localizer, item.DeliveryStatus ?? item.Status),
            detailText = SaleStatusApiText.DescribeStatus("delivery", item.DeliveryStatus ?? item.Status, item.DeliveryTradeOfferId),
            tradeOfferId = item.DeliveryTradeOfferId,
            steamOfferUrl = SaleStatusApiText.BuildSteamOfferUrl(item.DeliveryTradeOfferId),
            accountTradeOffersUrl = SaleStatusApiText.AccountTradeOffersUrl,
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

    await appLogService.WriteAsync(
        "Debug",
        $"Sales status requested. AppUserId={appUser.Id}; ActiveOperations={activeOperations.Count}; Statuses={string.Join("|", activeOperations.Select(item => $"{item.flow}:{item.status}"))}; OperationIds={string.Join("|", activeOperations.Select(item => item.id))}; TradeOfferIds={string.Join("|", activeOperations.Select(item => item.tradeOfferId ?? "<null>"))}",
        "SalesStatusApi",
        cancellationToken: cancellationToken);

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
    IStringLocalizer<SharedResource> localizer,
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
            message = UiTextLocalizer.LocalizeMessage(localizer, "Cancel request is invalid.")
        });
    }

    var form = await httpContext.Request.ReadFormAsync(cancellationToken);
    if (!Guid.TryParse(form["tradeOperationId"], out var tradeOperationId))
    {
        return Results.BadRequest(new
        {
            success = false,
            message = UiTextLocalizer.LocalizeMessage(localizer, "Sale request is invalid.")
        });
    }

    var appUser = await dbContext.AppUsers
        .SingleOrDefaultAsync(user => user.SteamId == steamId, cancellationToken);
    if (appUser is null)
    {
        return Results.NotFound(new
        {
            success = false,
            message = UiTextLocalizer.LocalizeMessage(localizer, "Local user profile was not found.")
        });
    }

    var operation = await dbContext.TradeOperations
        .SingleOrDefaultAsync(item => item.Id == tradeOperationId && item.AppUserId == appUser.Id, cancellationToken);
    if (operation is null)
    {
        return Results.NotFound(new
        {
            success = false,
            message = UiTextLocalizer.LocalizeMessage(localizer, "Sale request was not found.")
        });
    }

    if (string.IsNullOrWhiteSpace(operation.TradeOfferId) || !SaleStatusApiText.CanCancelIntakeStatus(operation.Status))
    {
        return Results.BadRequest(new
        {
            success = false,
            message = UiTextLocalizer.LocalizeMessage(localizer, $"Trade offer cannot be canceled from status {operation.Status}.")
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
            message = UiTextLocalizer.LocalizeMessage(localizer, result.Message)
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
        message = UiTextLocalizer.LocalizeMessage(localizer, "Trade offer was canceled.")
    });
});

app.MapRazorPages();

app.Run();

static async Task<SteamInventoryServerTestResult> TestSteamInventoryRequestAsync(
    IHttpClientFactory httpClientFactory,
    string steamId,
    GameDefinition game,
    CancellationToken cancellationToken)
{
    var client = httpClientFactory.CreateClient("SteamInventoryDiagnostic");
    var url = $"https://steamcommunity.com/inventory/{steamId}/{game.SteamAppId}/{game.SteamContextId}?l=english&count=2000";
    var stopwatch = Stopwatch.StartNew();

    try
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Referrer = new Uri($"https://steamcommunity.com/profiles/{steamId}/inventory");
        request.Headers.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        stopwatch.Stop();

        return new SteamInventoryServerTestResult
        {
            RequestUrl = url,
            HttpStatusCode = (int)response.StatusCode,
            RetryAfter = GetRetryAfterValue(response),
            Headers = BuildHeaderSummary(response),
            BodyLength = body.Length,
            BodySnippet = BuildDiagnosticBodySnippet(body),
            DurationMs = stopwatch.ElapsedMilliseconds
        };
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
        throw;
    }
    catch (Exception exception)
    {
        stopwatch.Stop();
        return new SteamInventoryServerTestResult
        {
            RequestUrl = url,
            DurationMs = stopwatch.ElapsedMilliseconds,
            ExceptionMessage = exception.Message
        };
    }
}

static async Task<OutboundIpDiagnosticResult> ReadOutboundIpAsync(
    IHttpClientFactory httpClientFactory,
    CancellationToken cancellationToken)
{
    var timestampUtc = DateTime.UtcNow;
    try
    {
        var client = httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(10);
        var raw = await client.GetStringAsync("https://api.ipify.org?format=json", cancellationToken);
        string? ip = null;
        using var document = JsonDocument.Parse(raw);
        if (document.RootElement.TryGetProperty("ip", out var ipElement))
        {
            ip = ipElement.GetString();
        }

        return new OutboundIpDiagnosticResult
        {
            TimestampUtc = timestampUtc,
            OutboundIp = ip,
            RawBody = BuildDiagnosticBodySnippet(raw)
        };
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
        throw;
    }
    catch (Exception exception)
    {
        return new OutboundIpDiagnosticResult
        {
            TimestampUtc = timestampUtc,
            ExceptionMessage = exception.Message
        };
    }
}

static string GetRetryAfterValue(HttpResponseMessage response)
{
    var retryAfter = response.Headers.RetryAfter;
    if (retryAfter?.Delta is TimeSpan delta)
    {
        return $"{Math.Max(0, (int)Math.Ceiling(delta.TotalSeconds))}s";
    }

    if (retryAfter?.Date is DateTimeOffset date)
    {
        return date.UtcDateTime.ToString("O");
    }

    return "<none>";
}

static string BuildHeaderSummary(HttpResponseMessage response)
{
    var headers = response.Headers
        .Select(header => $"{header.Key}={string.Join(",", header.Value)}")
        .Concat(response.Content.Headers.Select(header => $"{header.Key}={string.Join(",", header.Value)}"));
    return string.Join(" | ", headers);
}

static string BuildDiagnosticBodySnippet(string? body)
{
    if (string.IsNullOrWhiteSpace(body))
    {
        return "<empty>";
    }

    var compact = body
        .Replace("\r", " ", StringComparison.Ordinal)
        .Replace("\n", " ", StringComparison.Ordinal)
        .Replace("\t", " ", StringComparison.Ordinal);
    while (compact.Contains("  ", StringComparison.Ordinal))
    {
        compact = compact.Replace("  ", " ", StringComparison.Ordinal);
    }

    return TruncateForDiagnosticLog(compact.Trim(), 300);
}

static string TruncateForDiagnosticLog(string? value, int maxLength)
{
    if (string.IsNullOrWhiteSpace(value))
    {
        return "<empty>";
    }

    return value.Length <= maxLength ? value : value[..maxLength] + "...";
}

static GameType ResolveGameType(IGameCatalog gameCatalog, int appId, string contextId)
{
    return gameCatalog.SupportedGames
        .FirstOrDefault(game => game.SteamAppId == appId &&
                                string.Equals(game.SteamContextId.ToString(), contextId, StringComparison.Ordinal))
        ?.Type ?? gameCatalog.DefaultGameType;
}

static async Task TryEnqueueInventoryRefreshAsync(
    ISteamInventoryRefreshService inventoryRefreshService,
    IAppLogService appLogService,
    string steamId,
    GameType gameType,
    string reason,
    CancellationToken cancellationToken,
    string source)
{
    try
    {
        await inventoryRefreshService.EnqueueRefreshAsync(
            steamId,
            gameType,
            SteamInventoryRefreshPriority.High,
            cancellationToken,
            forceFreshness: true,
            reason: reason);
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
        throw;
    }
    catch (Exception exception)
    {
        await appLogService.WriteAsync(
            "Warning",
            $"Inventory refresh enqueue failed after {source}. SteamId={steamId}; GameType={(int)gameType}; Reason={reason}; Message={exception.Message}",
            "InventoryRefreshEnqueue",
            exception,
            CancellationToken.None);
    }
}

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
    public const string AccountTradeOffersUrl = "https://steamcommunity.com/my/tradeoffers/";

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
            ? AccountTradeOffersUrl
            : $"https://steamcommunity.com/tradeoffer/{offerId}/";
    }

    public static string DescribeStatus(string flow, string? status, string? offerId)
    {
        var isDelivery = string.Equals(flow, "delivery", StringComparison.Ordinal);
        return status switch
        {
            "Pending" => "Waiting for bot to create Steam offer",
            "BotPending" => "Bot is creating Steam offer",
            "AwaitingBotConfirmation" => "Waiting for bot mobile confirmation",
            "TradeCreated" or "AwaitingUserAction" => "Open Steam and accept the trade offer",
            "TradeAcceptedPendingReceipt" or "ReceivedByBot" => "Waiting for bot receipt and credit",
            "InEscrow" => "Steam trade is in escrow",
            "PendingDelivery" => "Waiting for bot to create delivery offer",
            "DeliveryBotPending" => "Bot is creating delivery offer",
            "DeliveryTradeCreated" or "AwaitingBuyerAction" => "Open Steam and accept the delivery offer",
            "DeliveryInEscrow" => "Steam delivery is in escrow",
            _ when string.IsNullOrWhiteSpace(offerId) => isDelivery
                ? "Waiting for bot to create delivery offer"
                : "Waiting for bot to create Steam offer",
            _ => "Waiting for next Steam trade step"
        };
    }

    public static bool CanCancelIntakeStatus(string? status)
    {
        return status is "AwaitingBotConfirmation" or "TradeCreated" or "AwaitingUserAction";
    }
}

internal sealed class SteamInventoryServerTestRequest
{
    public string? SteamId { get; set; }
    public GameType GameType { get; set; } = GameType.CS2;
}

internal sealed class SteamInventoryServerTestResult
{
    public string RequestUrl { get; set; } = string.Empty;
    public int? HttpStatusCode { get; set; }
    public string? RetryAfter { get; set; }
    public string? Headers { get; set; }
    public int? BodyLength { get; set; }
    public string? BodySnippet { get; set; }
    public long DurationMs { get; set; }
    public string? ExceptionMessage { get; set; }
}

internal sealed class OutboundIpDiagnosticResult
{
    public DateTime TimestampUtc { get; set; }
    public string? OutboundIp { get; set; }
    public string? RawBody { get; set; }
    public string? ExceptionMessage { get; set; }
}
