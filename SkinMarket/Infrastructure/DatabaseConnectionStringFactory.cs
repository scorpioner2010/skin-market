using Npgsql;

namespace SkinMarket.Infrastructure;

public static class DatabaseConnectionStringFactory
{
    public static string? ResolveOptional(IConfiguration configuration)
    {
        var databaseUrl = configuration["DATABASE_URL"];
        if (!string.IsNullOrWhiteSpace(databaseUrl))
        {
            return TryConvertDatabaseUrl(databaseUrl) ?? databaseUrl;
        }

        var configuredConnectionString = configuration.GetConnectionString("DefaultConnection");
        return string.IsNullOrWhiteSpace(configuredConnectionString) ? null : configuredConnectionString;
    }

    public static string Resolve(IConfiguration configuration)
    {
        var connectionString = ResolveOptional(configuration);
        if (!string.IsNullOrWhiteSpace(connectionString))
        {
            return connectionString;
        }

        throw new InvalidOperationException("Database connection string is not configured. Set DATABASE_URL or ConnectionStrings__DefaultConnection.");
    }

    private static string? TryConvertDatabaseUrl(string databaseUrl)
    {
        if (!Uri.TryCreate(databaseUrl, UriKind.Absolute, out var uri))
        {
            return null;
        }

        if (!string.Equals(uri.Scheme, "postgres", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(uri.Scheme, "postgresql", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var userInfo = uri.UserInfo.Split(':', 2);
        var username = userInfo.ElementAtOrDefault(0);
        var password = userInfo.ElementAtOrDefault(1);
        var database = uri.AbsolutePath.Trim('/');

        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = uri.Host,
            Port = uri.Port > 0 ? uri.Port : 5432,
            Username = Uri.UnescapeDataString(username ?? string.Empty),
            Password = Uri.UnescapeDataString(password ?? string.Empty),
            Database = Uri.UnescapeDataString(database),
            SslMode = SslMode.Prefer
        };

        if (!string.IsNullOrWhiteSpace(uri.Query))
        {
            var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(uri.Query);
            foreach (var pair in query)
            {
                switch (pair.Key.ToLowerInvariant())
                {
                    case "sslmode":
                        if (Enum.TryParse<SslMode>(pair.Value.ToString(), true, out var sslMode))
                        {
                            builder.SslMode = sslMode;
                        }
                        break;
                }
            }
        }

        return builder.ConnectionString;
    }
}
