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
using System.Net.Http.Headers;
using Microsoft.Extensions.Options;

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
builder.Services.Configure<PricingOptions>(builder.Configuration.GetSection(PricingOptions.SectionName));
builder.Services.AddMemoryCache();
builder.Services.AddLocalization(options => options.ResourcesPath = "Resources");
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownNetworks.Clear();
    options.KnownProxies.Clear();
});
builder.Services.AddRazorPages()
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
builder.Services.AddScoped<IAppLogService, AppLogService>();
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
builder.Services.AddScoped<ICreditService, CreditService>();
builder.Services.AddScoped<ITradeOperationService, TradeOperationService>();
builder.Services.AddScoped<ISteamBotIntakeService, SteamBotIntakeService>();
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
builder.Services.AddHttpClient<ISteamOpenIdService, SteamOpenIdService>();
builder.Services.AddHttpClient<ISteamInventoryService, SteamInventoryService>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(20);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (compatible; SkinMarket/1.0; +https://skinmarket.local)");
    client.DefaultRequestHeaders.Accept.ParseAdd("application/json,text/javascript,*/*;q=0.9");
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
app.UseStaticFiles();

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

app.MapRazorPages();

app.Run();
