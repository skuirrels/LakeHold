using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Lakehold.Api.Security;
using Lakehold.ControlPlane.Model;
using Lakehold.Engine.Catalog;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;

namespace Lakehold.Api.Connectors;

/// <summary>Checkpointed PostgreSQL table reader with typed, parameterised cursor predicates.</summary>
internal sealed class PostgreSqlDataConnectorSource(
    IOptions<ConnectorOptions> options,
    ConnectorSecretResolver secrets) : IDataConnectorSource
{
    public ConnectorAdapterManifest Manifest { get; } = new(
        "lakehold.postgresql",
        1,
        DataConnectorKind.PostgreSql,
        new HashSet<DataConnectorReadMode> { DataConnectorReadMode.Incremental },
        new HashSet<DataConnectorAuthenticationKind> { DataConnectorAuthenticationKind.PostgreSqlPassword },
        SupportsSourceVersion: true);

    public async Task<ConnectorSourceResult> ReadAsync(
        ConnectorReadContext context,
        IDataConnectorRecordWriter destination,
        CancellationToken cancellationToken)
    {
        var connector = context.Connector;
        var settings = connector.SourceSettings();
        if (!settings.CursorIsCommitMonotonic)
        {
            throw new InvalidOperationException(
                "PostgreSQL ordered polling requires an explicitly declared commit-monotonic cursor.");
        }

        var endpoint = new Uri(connector.EndpointUrl, UriKind.Absolute);
        if (!string.Equals(endpoint.Scheme, "postgresql", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(endpoint.Scheme, "postgres", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("PostgreSQL connectors require a postgresql:// endpoint.");
        }

        if (!string.IsNullOrEmpty(endpoint.UserInfo))
        {
            throw new InvalidOperationException("PostgreSQL endpoint URLs must not contain embedded credentials.");
        }

        var approved = await ResolveDatabaseHostAsync(endpoint, cancellationToken).ConfigureAwait(false);
        var (schema, table) = ParseTable(settings.SourceTable);
        var cursorColumn = SqlIdentifier.Quote(settings.CursorColumn
            ?? throw new InvalidOperationException("PostgreSQL incremental connectors require a cursor column."));
        var pageSize = Math.Clamp(settings.PageSize, 1, 10_000);
        var authentication = connector.Authentication();
        if (authentication.Kind != DataConnectorAuthenticationKind.PostgreSqlPassword)
        {
            throw new InvalidOperationException("PostgreSQL connectors require PostgreSQL password authentication.");
        }

        var username = await secrets.ResolveAsync(
                authentication.UsernameSecretReference
                ?? throw new InvalidOperationException("PostgreSQL authentication requires a username secret reference."),
                context.TenantSlug,
                context.CatalogName,
                endpoint.DnsSafeHost,
                cancellationToken)
            .ConfigureAwait(false);
        var password = await secrets.ResolveAsync(
                authentication.PasswordSecretReference
                ?? throw new InvalidOperationException("PostgreSQL authentication requires a password secret reference."),
                context.TenantSlug,
                context.CatalogName,
                endpoint.DnsSafeHost,
                cancellationToken)
            .ConfigureAwait(false);

        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = approved?.ToString() ?? endpoint.DnsSafeHost,
            Port = endpoint.IsDefaultPort ? 5432 : endpoint.Port,
            Database = endpoint.AbsolutePath.Trim('/'),
            Username = username,
            Password = password,
            Pooling = false,
            Timeout = Math.Max(1, (int)Math.Ceiling(options.Value.RequestTimeout.TotalSeconds)),
            CommandTimeout = Math.Max(1, (int)Math.Ceiling(options.Value.RequestTimeout.TotalSeconds)),
            SslMode = options.Value.AllowUnsafeDestinations ? SslMode.Prefer : SslMode.VerifyFull,
        };
        var dataSourceBuilder = new NpgsqlDataSourceBuilder(builder.ConnectionString);
        if (approved is not null)
        {
            dataSourceBuilder.UseSslClientAuthenticationOptionsCallback(
                ssl => ssl.TargetHost = endpoint.DnsSafeHost);
        }

        await using var dataSource = dataSourceBuilder.Build();
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var source = $"{SqlIdentifier.QuoteName(schema)}.{SqlIdentifier.QuoteName(table)}";
        var cursor = SqlIdentifier.QuoteName(cursorColumn);
        var predicate = context.Checkpoint is null ? string.Empty : $"WHERE {cursor} > @checkpoint";
        await using (var uniqueness = new NpgsqlCommand(
                         $"SELECT {cursor} FROM {source} {predicate} "
                         + $"GROUP BY {cursor} HAVING count(*) > 1 LIMIT 1",
                         connection))
        {
            if (context.Checkpoint is not null)
            {
                uniqueness.Parameters.Add(CheckpointParameter(settings.CursorType, context.Checkpoint));
            }

            if (await uniqueness.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null)
            {
                throw new InvalidDataException(
                    "The PostgreSQL cursor is not unique, so advancing it could skip source rows.");
            }
        }

        await using var command = new NpgsqlCommand(
            $"SELECT * FROM {source} {predicate} ORDER BY {cursor} LIMIT @limit",
            connection);
        command.Parameters.AddWithValue("limit", pageSize);
        if (context.Checkpoint is not null)
        {
            command.Parameters.Add(CheckpointParameter(settings.CursorType, context.Checkpoint));
        }

        object? lastCursor = null;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        var cursorOrdinal = reader.GetOrdinal(cursorColumn);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            lastCursor = reader.IsDBNull(cursorOrdinal) ? null : reader.GetValue(cursorOrdinal);
            if (lastCursor is null)
            {
                throw new InvalidDataException("The PostgreSQL cursor column contains a null value.");
            }

            await destination.WriteAsync(ToJson(reader), cancellationToken).ConfigureAwait(false);
        }

        var proposed = lastCursor is null
            ? context.Checkpoint
            : FormatCheckpoint(lastCursor, settings.CursorType);
        return new ConnectorSourceResult(
            proposed,
            proposed,
            proposed is null ? null : $"{context.Checkpoint ?? "<initial>"}->{proposed}");
    }

    private async Task<IPAddress?> ResolveDatabaseHostAsync(Uri endpoint, CancellationToken cancellationToken)
    {
        var policyUri = new UriBuilder(Uri.UriSchemeHttps, endpoint.DnsSafeHost).Uri;
        var resolution = await OutboundDestinationPolicy.ResolveAsync(
                policyUri,
                options.Value,
                "PostgreSQL connector",
                cancellationToken)
            .ConfigureAwait(false);
        if (resolution.Error is not null)
        {
            throw new InvalidOperationException(resolution.Error);
        }

        return resolution.Address;
    }

    private static (string Schema, string Table) ParseTable(string? sourceTable)
    {
        var parts = (sourceTable ?? string.Empty).Split('.', StringSplitOptions.TrimEntries);
        if (parts.Length != 2)
        {
            throw new InvalidOperationException("PostgreSQL source tables must use schema.table notation.");
        }

        return (SqlIdentifier.Quote(parts[0]), SqlIdentifier.Quote(parts[1]));
    }

    private static NpgsqlParameter CheckpointParameter(string? cursorType, string checkpoint) =>
        cursorType?.Trim().ToLowerInvariant() switch
        {
            "int64" => new NpgsqlParameter("checkpoint", NpgsqlDbType.Bigint)
            {
                Value = long.Parse(checkpoint, CultureInfo.InvariantCulture),
            },
            "timestamptz" => new NpgsqlParameter("checkpoint", NpgsqlDbType.TimestampTz)
            {
                Value = DateTimeOffset.Parse(checkpoint, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            },
            "uuid" => new NpgsqlParameter("checkpoint", NpgsqlDbType.Uuid) { Value = Guid.Parse(checkpoint) },
            "text" => new NpgsqlParameter("checkpoint", NpgsqlDbType.Text) { Value = checkpoint },
            _ => throw new InvalidOperationException("PostgreSQL cursor type must be int64, timestamptz, uuid, or text."),
        };

    private static string FormatCheckpoint(object value, string? cursorType) =>
        cursorType?.Trim().ToLowerInvariant() switch
    {
        "int64" => Convert.ToInt64(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture),
        "timestamptz" => value switch
        {
            DateTime dateTime when dateTime.Kind == DateTimeKind.Utc =>
                dateTime.ToString("O", CultureInfo.InvariantCulture),
            DateTimeOffset dateTimeOffset =>
                dateTimeOffset.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            _ => throw new InvalidDataException(
                "The PostgreSQL timestamptz cursor did not materialize with an explicit UTC offset."),
        },
        "uuid" => value is Guid guid
            ? guid.ToString("D")
            : throw new InvalidDataException("The PostgreSQL UUID cursor returned an incompatible value."),
        "text" => value as string
                  ?? throw new InvalidDataException("The PostgreSQL text cursor returned an incompatible value."),
        _ => throw new InvalidOperationException(
            "PostgreSQL cursor type must be int64, timestamptz, uuid, or text."),
    };

    private static string ToJson(NpgsqlDataReader reader)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            for (var index = 0; index < reader.FieldCount; index++)
            {
                writer.WritePropertyName(reader.GetName(index));
                if (reader.IsDBNull(index))
                {
                    writer.WriteNullValue();
                }
                else
                {
                    JsonSerializer.Serialize(writer, reader.GetValue(index), reader.GetFieldType(index));
                }
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
