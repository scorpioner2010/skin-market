using Npgsql;

namespace SkinMarket.Infrastructure;

public static class DatabaseConnectionStringFactory
{
    public static string? ResolveOptional(IConfiguration configuration)
    {
        var databaseUrl = configuration["DATABASE_URL"];
        if (!string.IsNullOrWhiteSpace(databaseUrl))
        {
            return ConvertToConnectionString(databaseUrl);
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

    private static string ConvertToConnectionString(string rawValue)
    {
        if (LooksLikeKeywordValueConnectionString(rawValue))
        {
            return rawValue;
        }

        var normalizedValue = NormalizeDatabaseUrl(rawValue);
        if (!Uri.TryCreate(normalizedValue, UriKind.Absolute, out var uri))
        {
            throw new InvalidOperationException("DATABASE_URL is not a valid PostgreSQL URI or Npgsql connection string.");
        }

        if (!string.Equals(uri.Scheme, "postgres", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(uri.Scheme, "postgresql", StringComparison.OrdinalIgnoreCase))
        {
            return rawValue;
        }

        var userInfo = uri.UserInfo.Split(':', 2, StringSplitOptions.None);
        var username = userInfo.ElementAtOrDefault(0);
        var password = userInfo.ElementAtOrDefault(1);
        var database = uri.AbsolutePath.Trim('/');

        if (string.IsNullOrWhiteSpace(uri.Host) ||
            string.IsNullOrWhiteSpace(database) ||
            string.IsNullOrWhiteSpace(username))
        {
            throw new InvalidOperationException("DATABASE_URL is missing host, database name, or username.");
        }

        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = uri.Host,
            Port = uri.Port > 0 ? uri.Port : 5432,
            Username = Uri.UnescapeDataString(username ?? string.Empty),
            Password = Uri.UnescapeDataString(password ?? string.Empty),
            Database = Uri.UnescapeDataString(database),
            SslMode = SslMode.Require
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
                    case "trustservercertificate":
                    case "trust_server_certificate":
                        if (bool.TryParse(pair.Value.ToString(), out var trustServerCertificate) && trustServerCertificate)
                        {
                            // Npgsql 8 treats this parameter as obsolete/no-op, so ignore it.
                        }
                        break;
                }
            }
        }

        return builder.ConnectionString;
    }

    private static bool LooksLikeKeywordValueConnectionString(string value)
    {
        return value.Contains("Host=", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("Server=", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("Username=", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeDatabaseUrl(string value)
    {
        return value
            .Trim()
            .Replace("[", string.Empty, StringComparison.Ordinal)
            .Replace("]", string.Empty, StringComparison.Ordinal)
            .Replace("(mailto:", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace(")", string.Empty, StringComparison.Ordinal);
    }
}
