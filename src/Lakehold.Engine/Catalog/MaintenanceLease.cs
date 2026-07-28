using System.Globalization;
using Lakehold.Engine.Execution;

namespace Lakehold.Engine.Catalog;

/// <summary>
///     A cluster-wide claim on running one maintenance job against one catalog.
/// </summary>
/// <remarks>
///     <para>
///         Quartz's in-memory job store schedules per node, so every node in a cluster fires the
///         same sweep at the same moment. For flush and compact that is wasteful; for backup it also
///         produces duplicate generations that race each other through retention.
///     </para>
///     <para>
///         The lease is only meaningful where a catalog can actually be shared, which is precisely
///         where its metadata is in PostgreSQL. A local-file catalog cannot be opened by two nodes
///         at once — DuckDB refuses the second — so there is nothing to coordinate and
///         <see cref="TryAcquireAsync"/> succeeds without touching anything.
///     </para>
///     <para>
///         This is deliberately not Quartz's <c>AdoJobStore</c>. That would put the *schedule* in a
///         database, but the schedule is already derived from configuration and identical on every
///         node; what actually needs coordinating is the work. Leasing the work also keeps the
///         control plane free of a second database engine.
///     </para>
/// </remarks>
public static class MaintenanceLease
{
    /// <summary>
    ///     Schema holding the lease table.
    /// </summary>
    /// <remarks>
    ///     Separate from <c>public</c>, where DuckLake keeps its own tables, for two reasons: it
    ///     cannot collide with a future DuckLake migration, and catalog backup reads only the
    ///     DuckLake schema so the lease never ends up inside a backup it has no business being in.
    /// </remarks>
    public const string SchemaName = "lakehold";

    private const string TableName = "maintenance_lease";
    private const string Alias = "lakehold_lease";

    /// <summary>
    ///     Tries to claim <paramref name="job"/> for <paramref name="owner"/> until the lease
    ///     expires.
    /// </summary>
    /// <returns>
    ///     True when this node holds the lease and should do the work; false when another node holds
    ///     it and this node should skip.
    /// </returns>
    public static async Task<bool> TryAcquireAsync(
        Duckling duckling,
        string job,
        string owner,
        TimeSpan duration,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(duckling);

        if (!RequiresLease(duckling.Catalog))
        {
            return true;
        }

        var safeJob = LeaseKey(duckling.Catalog, job);
        var safeOwner = Sanitise(owner);
        var seconds = ((long)duration.TotalSeconds).ToString(CultureInfo.InvariantCulture);

        return await duckling
            .InvokeAsync(
                ct => duckling.InvokeWithPrivilegedMetadataAccessUnguardedAsync(
                    async privilegedToken =>
                    {
                        await using var connection = await LeaseConnection
                            .OpenUnguardedAsync(duckling, privilegedToken)
                            .ConfigureAwait(false);

                        await connection.ExecuteAsync(
                            $"CREATE SCHEMA IF NOT EXISTS {SchemaName}", privilegedToken).ConfigureAwait(false);
                        await connection.ExecuteAsync(
                            $"CREATE TABLE IF NOT EXISTS {SchemaName}.{TableName} (" +
                            "job text PRIMARY KEY, owner text NOT NULL, expires_at timestamptz NOT NULL)",
                            privilegedToken).ConfigureAwait(false);

                        // One statement, so the claim is atomic. The WHERE on the conflict branch is
                        // what makes it a lease rather than a free-for-all.
                        await connection.ExecuteAsync(
                            $"INSERT INTO {SchemaName}.{TableName} (job, owner, expires_at) " +
                            $"VALUES ('{safeJob}', '{safeOwner}', now() + interval '{seconds} seconds') " +
                            "ON CONFLICT (job) DO UPDATE SET owner = EXCLUDED.owner, expires_at = EXCLUDED.expires_at " +
                            $"WHERE {SchemaName}.{TableName}.expires_at < now() " +
                            $"OR {SchemaName}.{TableName}.owner = '{safeOwner}'",
                            privilegedToken).ConfigureAwait(false);

                        var holder = await connection.QueryScalarAsync(
                            $"SELECT owner FROM {SchemaName}.{TableName} WHERE job = '{safeJob}'",
                            privilegedToken).ConfigureAwait(false);

                        return string.Equals(holder, safeOwner, StringComparison.Ordinal);
                    },
                    ct),
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    ///     Releases a lease this node holds, so the next node round does not have to wait it out.
    /// </summary>
    public static async Task ReleaseAsync(
        Duckling duckling,
        string job,
        string owner,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(duckling);

        if (!RequiresLease(duckling.Catalog))
        {
            return;
        }

        try
        {
            var safeJob = LeaseKey(duckling.Catalog, job);
            var safeOwner = Sanitise(owner);
            _ = await duckling
                .InvokeAsync(
                    ct => duckling.InvokeWithPrivilegedMetadataAccessUnguardedAsync(
                        async privilegedToken =>
                        {
                            await using var connection = await LeaseConnection
                                .OpenUnguardedAsync(duckling, privilegedToken)
                                .ConfigureAwait(false);

                            await connection.ExecuteAsync(
                                $"DELETE FROM {SchemaName}.{TableName} " +
                                $"WHERE job = '{safeJob}' AND owner = '{safeOwner}'",
                                privilegedToken).ConfigureAwait(false);
                            return true;
                        },
                        ct),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A lease we fail to release expires on its own. Failing the maintenance run over the
            // release would turn completed work into a reported failure.
        }
    }

    /// <summary>Whether this catalog can be reached by more than one node at a time.</summary>
    private static bool RequiresLease(CatalogDescriptor catalog)
        => catalog.MetadataKind == CatalogMetadataKind.Postgres
           && !string.IsNullOrEmpty(catalog.MetadataSecretName);

    private static string LeaseKey(CatalogDescriptor catalog, string job)
        => Sanitise($"{catalog.TenantKey}.{catalog.CatalogId}.{catalog.CatalogName}.{job}");

    /// <summary>
    ///     Restricts a value to characters that cannot terminate a nested SQL string.
    /// </summary>
    /// <remarks>
    ///     Lease statements are sent through <c>postgres_execute</c>, so they are SQL inside a SQL
    ///     string literal and there is no bind parameter to reach for. Job names are ours and node
    ///     names are operator-configured, but restricting the alphabet is cheaper than reasoning
    ///     about who can influence either.
    /// </remarks>
    private static string Sanitise(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        var filtered = new string([.. value.Where(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.')]);
        return filtered.Length is > 0 and <= 128
            ? filtered
            : throw new ArgumentException(
                $"'{value}' does not yield a usable lease name. Expected 1-128 characters of " +
                "[A-Za-z0-9_.-].",
                nameof(value));
    }

    /// <summary>
    ///     A read-write attachment to the catalog's PostgreSQL metadata database, detached on
    ///     dispose.
    /// </summary>
    private sealed class LeaseConnection(Duckling duckling) : IAsyncDisposable
    {
        public static async Task<LeaseConnection> OpenUnguardedAsync(
            Duckling duckling,
            CancellationToken cancellationToken)
        {
            // Attached by secret name, so no credential is interpolated into a statement DuckDB
            // could echo back in an error.
            await duckling.ExecuteUnguardedAsync(
                $"ATTACH '' AS {Alias} (TYPE postgres, SECRET {SqlIdentifier.Quote(duckling.Catalog.MetadataSecretName)})",
                cancellationToken).ConfigureAwait(false);

            return new LeaseConnection(duckling);
        }

        public async Task ExecuteAsync(string sql, CancellationToken cancellationToken)
            => _ = await duckling.ExecuteUnguardedAsync(
                $"CALL postgres_execute('{Alias}', {SqlIdentifier.Literal(sql)})", cancellationToken)
                .ConfigureAwait(false);

        public async Task<string?> QueryScalarAsync(string sql, CancellationToken cancellationToken)
        {
            var result = await duckling.ExecuteUnguardedAsync(
                $"SELECT * FROM postgres_query('{Alias}', {SqlIdentifier.Literal(sql)})", cancellationToken)
                .ConfigureAwait(false);

            return result.Rows.Count > 0
                ? Convert.ToString(result.Rows[0][0], CultureInfo.InvariantCulture)
                : null;
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await duckling.ExecuteUnguardedAsync($"DETACH {Alias}", CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Leaving the alias attached costs one connection until the session is evicted.
            }
        }
    }
}
