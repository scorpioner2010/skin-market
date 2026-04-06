using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Localization;
using Microsoft.EntityFrameworkCore;
using SkinMarket.Contracts;
using SkinMarket.Data;
using SkinMarket.Infrastructure;
using SkinMarket.Services;
using System.Globalization;

var builder = WebApplication.CreateBuilder(args);

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
    });
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("DefaultConnection")));
builder.Services.Configure<SteamBotOptions>(builder.Configuration.GetSection(SteamBotOptions.SectionName));
builder.Services.Configure<SteamApiOptions>(builder.Configuration.GetSection(SteamApiOptions.SectionName));
builder.Services.AddMemoryCache();
builder.Services.AddLocalization(options => options.ResourcesPath = "Resources");
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
builder.Services.AddScoped<IMarketPricingService, MarketPricingService>();
builder.Services.AddScoped<IMarketService, MarketService>();
builder.Services.AddScoped<IMarketPurchaseService, MarketPurchaseService>();
builder.Services.AddScoped<IMarketDeliveryService, MarketDeliveryService>();
builder.Services.AddScoped<ICreditService, CreditService>();
builder.Services.AddScoped<ITradeOperationService, TradeOperationService>();
builder.Services.AddScoped<ISteamBotIntakeService, SteamBotIntakeService>();
builder.Services.AddSingleton<ISteamTradeClient, StubSteamTradeClient>();
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

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

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

app.MapRazorPages();

app.Run();
