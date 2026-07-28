using System.Text;
using System.Data.Common;
using DuckDB.EFCoreProvider.Infrastructure;
using Lakehold.Engine.Catalog;
using Lakehold.Engine.Configuration;
using Lakehold.Engine.Execution;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Lakehold.Api.Storage;

/// <summary>Creates per-session PostgreSQL and object-storage secrets from deployment settings.</summary>
internal sealed class DucklingSessionConfigurator(
    IConfiguration configuration,
    IOptions<LakehouseOptions> options) : IDucklingSessionConfigurator
{
    private readonly string? _metadataConnection = configuration.GetConnectionString("DuckLakeMetadata");
    private readonly LakehouseOptions _options = options.Value;

    public void Configure(CatalogDescriptor catalog, DuckDBDbContextOptionsBuilder options)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(options);

        var statements = new StringBuilder();
        if (catalog.MetadataKind == CatalogMetadataKind.Postgres)
        {
            AppendPostgresSecrets(statements, catalog);
        }

        if (catalog.StorageKind != ParquetStorageKind.Local)
        {
            AppendStorageSecret(statements, catalog, options);
        }

        if (statements.Length == 0)
        {
            return;
        }

        options.ConfigureConnection(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = statements.ToString();
            command.ExecuteNonQuery();
        });
    }

    public Task SecureAfterAttachAsync(
        CatalogDescriptor catalog,
        DbConnection connection,
        CancellationToken cancellationToken)
    {
        if (catalog.MetadataKind != CatalogMetadataKind.Postgres)
        {
            return Task.CompletedTask;
        }

        var sql = new StringBuilder();
        AppendDropSecret(sql, catalog.MetadataSource);
        AppendDropSecret(
            sql,
            catalog.MetadataSecretName
                ?? throw new InvalidOperationException("The catalog has no PostgreSQL metadata credential secret name."));
        return ExecuteAsync(connection, sql.ToString(), cancellationToken);
    }

    public Task EnablePrivilegedMetadataAccessAsync(
        CatalogDescriptor catalog,
        DbConnection connection,
        CancellationToken cancellationToken)
    {
        if (catalog.MetadataKind != CatalogMetadataKind.Postgres)
        {
            return Task.CompletedTask;
        }

        var sql = new StringBuilder();
        AppendPostgresCredential(sql, catalog);
        return ExecuteAsync(connection, sql.ToString(), cancellationToken);
    }

    public Task DisablePrivilegedMetadataAccessAsync(
        CatalogDescriptor catalog,
        DbConnection connection,
        CancellationToken cancellationToken)
    {
        if (catalog.MetadataKind != CatalogMetadataKind.Postgres)
        {
            return Task.CompletedTask;
        }

        var secret = catalog.MetadataSecretName
            ?? throw new InvalidOperationException("The catalog has no PostgreSQL metadata credential secret name.");
        var sql = new StringBuilder();
        AppendDropSecret(sql, secret);
        return ExecuteAsync(connection, sql.ToString(), cancellationToken);
    }

    private void AppendPostgresSecrets(StringBuilder sql, CatalogDescriptor catalog)
    {
        if (string.IsNullOrWhiteSpace(_metadataConnection))
        {
            throw new InvalidOperationException(
                "ConnectionStrings:DuckLakeMetadata is required for PostgreSQL-backed DuckLake catalogs.");
        }

        var credentialsName = SqlIdentifier.Quote(
            catalog.MetadataSecretName
                ?? throw new InvalidOperationException("The catalog has no PostgreSQL metadata credential secret name."));
        var profileName = SqlIdentifier.Quote(catalog.MetadataSource);
        var schema = SqlIdentifier.Quote(
            catalog.MetadataSchema
                ?? throw new InvalidOperationException("The catalog has no PostgreSQL metadata schema."));

        AppendPostgresCredential(sql, catalog);
        sql.AppendLine($$"""
            ATTACH '' AS lakehold_metadata_setup (TYPE postgres, SECRET {{credentialsName}});
            CREATE SCHEMA IF NOT EXISTS lakehold_metadata_setup.{{schema}};
            DETACH lakehold_metadata_setup;
            CREATE OR REPLACE SECRET {{profileName}} (
                TYPE ducklake,
                METADATA_PATH '',
                METADATA_SCHEMA {{SqlIdentifier.Literal(schema)}},
                DATA_PATH {{SqlIdentifier.Literal(EnsureTrailingSlash(catalog.DataPath))}},
                METADATA_PARAMETERS MAP{'TYPE': 'postgres', 'SECRET': {{SqlIdentifier.Literal(credentialsName)}}});
            """);
    }

    private void AppendPostgresCredential(StringBuilder sql, CatalogDescriptor catalog)
    {
        if (string.IsNullOrWhiteSpace(_metadataConnection))
        {
            throw new InvalidOperationException(
                "ConnectionStrings:DuckLakeMetadata is required for PostgreSQL-backed DuckLake catalogs.");
        }

        var credentialsName = SqlIdentifier.Quote(
            catalog.MetadataSecretName
                ?? throw new InvalidOperationException("The catalog has no PostgreSQL metadata credential secret name."));
        var connection = new NpgsqlConnectionStringBuilder(_metadataConnection);
        if (string.IsNullOrWhiteSpace(connection.Host)
            || string.IsNullOrWhiteSpace(connection.Database)
            || string.IsNullOrWhiteSpace(connection.Username))
        {
            throw new InvalidOperationException(
                "ConnectionStrings:DuckLakeMetadata must contain Host, Database, and Username.");
        }

        sql.AppendLine($$"""
            CREATE OR REPLACE SECRET {{credentialsName}} (
                TYPE postgres,
                HOST {{SqlIdentifier.Literal(connection.Host)}},
                PORT {{connection.Port}},
                DATABASE {{SqlIdentifier.Literal(connection.Database)}},
                USER {{SqlIdentifier.Literal(connection.Username)}},
                PASSWORD {{SqlIdentifier.Literal(connection.Password ?? string.Empty)}},
                SSLMODE {{SqlIdentifier.Literal(ToLibpqSslMode(connection.SslMode))}},
                CONNECT_TIMEOUT {{connection.Timeout}});
            """);
    }

    private void AppendStorageSecret(
        StringBuilder sql,
        CatalogDescriptor catalog,
        DuckDBDbContextOptionsBuilder duckDb)
    {
        var profileName = catalog.StorageProfile
            ?? throw new InvalidOperationException(
                $"Catalog '{catalog.CatalogName}' uses {catalog.StorageKind} storage but has no storage profile.");
        if (!_options.StorageProfiles.TryGetValue(profileName, out var profile))
        {
            throw new InvalidOperationException(
                $"Storage profile '{profileName}' is not configured on this node.");
        }

        if (profile.Kind != catalog.StorageKind)
        {
            throw new InvalidOperationException(
                $"Storage profile '{profileName}' is {profile.Kind}, but catalog "
                + $"'{catalog.CatalogName}' requires {catalog.StorageKind}.");
        }

        var secretName = SqlIdentifier.Quote(
            catalog.SecretName
                ?? throw new InvalidOperationException("The catalog has no storage secret name."));

        var scopes = new List<(string Secret, string Scope)> { (secretName, catalog.DataPath) };
        AddOperationalScope(scopes, secretName, "backup", _options.BackupRoot, catalog);
        AddOperationalScope(scopes, secretName, "eject", _options.EjectRoot, catalog);

        if (catalog.StorageKind == ParquetStorageKind.Azure)
        {
            duckDb.LoadExtension("azure");
        }

        foreach (var (name, scope) in scopes.DistinctBy(item => item.Scope, StringComparer.OrdinalIgnoreCase))
        {
            if (catalog.StorageKind == ParquetStorageKind.Azure)
            {
                AppendAzureSecret(sql, name, profile, scope);
            }
            else
            {
                AppendS3CompatibleSecret(sql, name, profileName, profile, catalog.StorageKind, scope);
            }
        }
    }

    private static void AddOperationalScope(
        List<(string Secret, string Scope)> scopes,
        string baseSecret,
        string purpose,
        string root,
        CatalogDescriptor catalog)
    {
        if (!StorageLocation.IsRemote(root) || StorageLocation.KindOf(root) != catalog.StorageKind)
        {
            return;
        }

        scopes.Add((
            SqlIdentifier.Quote($"{baseSecret}_{purpose}"),
            CatalogStorageNamespace.Under(root, catalog)));
    }

    private static void AppendS3CompatibleSecret(
        StringBuilder sql,
        string secretName,
        string profileName,
        ParquetStorageProfileOptions profile,
        ParquetStorageKind storageKind,
        string scope)
    {
        Require(profile.KeyId, profileName, nameof(profile.KeyId));
        Require(profile.Secret, profileName, nameof(profile.Secret));
        var type = storageKind == ParquetStorageKind.Gcs ? "gcs" : "s3";
        sql.AppendLine($"CREATE OR REPLACE SECRET {secretName} (");
        sql.AppendLine($"  TYPE {type},");
        sql.AppendLine($"  SCOPE {SqlIdentifier.Literal(EnsureTrailingSlash(scope))},");
        sql.AppendLine($"  KEY_ID {SqlIdentifier.Literal(profile.KeyId!)},");
        sql.Append($"  SECRET {SqlIdentifier.Literal(profile.Secret!)}");
        AppendOptional(sql, "SESSION_TOKEN", profile.SessionToken);
        AppendOptional(sql, "REGION", profile.Region);
        AppendOptional(sql, "ENDPOINT", profile.Endpoint);
        sql.AppendLine($",\n  USE_SSL {profile.UseSsl.ToString().ToLowerInvariant()},");
        sql.AppendLine($"  URL_STYLE {SqlIdentifier.Literal(profile.UrlStyle)});");
    }

    private static void AppendAzureSecret(
        StringBuilder sql,
        string secretName,
        ParquetStorageProfileOptions profile,
        string scope)
    {
        if (!string.IsNullOrWhiteSpace(profile.AzureConnectionString))
        {
            sql.AppendLine(
                $"CREATE OR REPLACE SECRET {secretName} (TYPE azure, "
                + $"SCOPE {SqlIdentifier.Literal(EnsureTrailingSlash(scope))}, CONNECTION_STRING "
                + $"{SqlIdentifier.Literal(profile.AzureConnectionString)});");
            return;
        }

        Require(profile.AzureAccountName, secretName, nameof(profile.AzureAccountName));
        sql.AppendLine($"CREATE OR REPLACE SECRET {secretName} (");
        sql.AppendLine("  TYPE azure,");
        sql.AppendLine($"  SCOPE {SqlIdentifier.Literal(EnsureTrailingSlash(scope))},");
        sql.AppendLine("  PROVIDER credential_chain,");
        sql.Append($"  ACCOUNT_NAME {SqlIdentifier.Literal(profile.AzureAccountName!)}");
        AppendOptional(sql, "CHAIN", profile.AzureCredentialChain);
        sql.AppendLine(");");
    }

    private static void AppendOptional(StringBuilder sql, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            sql.Append($",\n  {key} {SqlIdentifier.Literal(value)}");
        }
    }

    private static void Require(string? value, string profile, string setting)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException(
                $"Storage profile '{profile}' must configure {setting}.");
        }
    }

    private static string EnsureTrailingSlash(string path) =>
        path.EndsWith('/') ? path : path + '/';

    private static void AppendDropSecret(StringBuilder sql, string secretName)
        => sql.AppendLine($"DROP SECRET IF EXISTS {SqlIdentifier.Quote(secretName)};");

    private static async Task ExecuteAsync(
        DbConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string ToLibpqSslMode(SslMode mode) => mode switch
    {
        SslMode.VerifyCA => "verify-ca",
        SslMode.VerifyFull => "verify-full",
        _ => mode.ToString().ToLowerInvariant(),
    };
}
