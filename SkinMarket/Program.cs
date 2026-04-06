using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Localization;
using Microsoft.EntityFrameworkCore;
using SkinMarket.Contracts;
using SkinMarket.Data;
using SkinMarket.Infrastructure;
using SkinMarket.Models;
using SkinMarket.Services;
using System.Globalization;

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
var connectionString = DatabaseConnectionStringFactory.Resolve(builder.Configuration);
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(connectionString));
builder.Services.Configure<SteamBotOptions>(builder.Configuration.GetSection(SteamBotOptions.SectionName));
builder.Services.Configure<SteamApiOptions>(builder.Configuration.GetSection(SteamApiOptions.SectionName));
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
builder.Services.AddSingleton<ISteamTradeClient, StubSteamTradeClient>();
builder.Services.AddHttpClient<ICsFloatPriceService, CsFloatPriceService>();
builder.Services.AddHttpClient<ISteamOpenIdService, SteamOpenIdService>();
builder.Services.AddHttpClient<ISteamInventoryService, SteamInventoryService>();
builder.Services.AddHttpClient<ISteamProfileService, SteamProfileService>();
builder.Services.AddHttpClient<ISkinportPricingService, SkinportPricingService>(client =>
{
    client.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Encoding", "br");
})
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
    {
        AutomaticDecompression = System.Net.DecompressionMethods.Brotli |
                                 System.Net.DecompressionMethods.GZip |
                                 System.Net.DecompressionMethods.Deflate
    });
builder.Services.AddHttpClient<ISteamMarketPriceService, SteamMarketPriceService>();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");
    try
    {
        logger.LogInformation("Applying database migrations.");
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        dbContext.Database.Migrate();
        logger.LogInformation("Database migrations applied successfully.");
    }
    catch (Exception exception)
    {
        logger.LogCritical(exception, "Application startup failed during database migration.");
        throw;
    }
}

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
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

    await refreshService.QueueRefreshAsync(marketHashNames, request.GameType, cancellationToken);
    return Results.Accepted();
});

app.MapPost("/api/inventory/prices/status", async (
    HttpContext httpContext,
    InventoryPriceRefreshRequest request,
    IInventoryPriceRefreshService refreshService,
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
