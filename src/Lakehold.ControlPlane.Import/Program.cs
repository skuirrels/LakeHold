using DuckDB.EFCoreProvider.Extensions;
using DuckDB.NET.Data;
using Lakehold.ControlPlane.Data;
using Lakehold.Engine.Catalog;
using Microsoft.EntityFrameworkCore;
using Npgsql;

var arguments = ImportArguments.Parse(args);
if (arguments is null)
{
    return 2;
}

var sourcePath = Path.GetFullPath(arguments.SourcePath);
if (!File.Exists(sourcePath))
{
    Console.Error.WriteLine($"Source file does not exist: {sourcePath}");
    return 2;
}

var temporaryRoot = Path.Combine(Path.GetTempPath(), "lakehold-control-plane-import");
Directory.CreateDirectory(temporaryRoot);
var temporarySource = Path.Combine(temporaryRoot, Guid.NewGuid().ToString("N") + ".duckdb");

// Hold DuckDB's own read-only database lock for the whole copy. It refuses an active writer, and
// keeping the attachment open prevents a writer from starting between the check and File.Copy.
await using var sourceLock = new DuckDBConnection("Data Source=:memory:");
await sourceLock.OpenAsync();
try
{
    await using var attach = sourceLock.CreateCommand();
    attach.CommandText =
        $"ATTACH {SqlIdentifier.Literal(sourcePath)} AS legacy_source_lock (READ_ONLY)";
    await attach.ExecuteNonQueryAsync();
}
catch (DuckDBException ex)
{
    Console.Error.WriteLine(
        "Refusing to snapshot the legacy control plane because DuckDB could not acquire a "
        + $"read-only lock. Stop every legacy writer and retry. Detail: {ex.Message}");
    return 4;
}

var sourceWal = sourcePath + ".wal";
if (File.Exists(sourceWal) && new FileInfo(sourceWal).Length > 0)
{
    Console.Error.WriteLine(
        $"Refusing to snapshot '{sourcePath}' while the outstanding WAL '{sourceWal}' exists. "
        + "Stop every writer and open/checkpoint the source with DuckDB so the WAL is incorporated, "
        + "then retry. The importer has not copied or modified either file.");
    return 4;
}

File.Copy(sourcePath, temporarySource);

try
{
    var sourceOptions = new DbContextOptionsBuilder<ControlPlaneContext>()
        .UseDuckDB($"Data Source={temporarySource}")
        .Options;
    await using var source = new ControlPlaneContext(sourceOptions);

    // Adapt a disposable copy to the current legacy model. The original file is never opened for
    // writing, including in --apply mode.
    await AdditiveSchema.EnsureModelTablesAsync(source, CancellationToken.None);
    await AdditiveSchema.EnsureModelColumnsAsync(source, CancellationToken.None);

    var tenants = await source.Tenants.AsNoTracking().ToListAsync();
    var catalogs = await source.Catalogs.AsNoTracking().ToListAsync();
    var savedQueries = await source.SavedQueries.AsNoTracking().ToListAsync();
    var queryRuns = await source.QueryRuns.AsNoTracking().ToListAsync();
    var subscriptions = await source.ChangeSubscriptions.AsNoTracking().ToListAsync();
    var tokens = await source.ApiTokens.AsNoTracking().ToListAsync();

    Console.WriteLine("Legacy control-plane inventory:");
    Console.WriteLine($"  tenants:             {tenants.Count}");
    Console.WriteLine($"  catalogs:            {catalogs.Count}");
    Console.WriteLine($"  saved queries:       {savedQueries.Count}");
    Console.WriteLine($"  query runs:          {queryRuns.Count}");
    Console.WriteLine($"  change subscriptions:{subscriptions.Count}");
    Console.WriteLine($"  API tokens:          {tokens.Count}");

    if (!arguments.Apply)
    {
        Console.WriteLine("Dry run only. Re-run with --apply to migrate this inventory.");
        return 0;
    }

    var targetOptions = new DbContextOptionsBuilder<ControlPlaneContext>()
        .UseNpgsql(arguments.TargetConnection)
        .Options;
    if (await HasApplicationRowsAsync(arguments.TargetConnection))
    {
        Console.Error.WriteLine(
            "Refusing to import into a non-empty PostgreSQL control plane. No schema or rows were written.");
        return 3;
    }

    await using var target = new ControlPlaneContext(targetOptions);
    await target.Database.MigrateAsync();

    var targetRows = await target.Tenants.CountAsync()
        + await target.Catalogs.CountAsync()
        + await target.SavedQueries.CountAsync()
        + await target.QueryRuns.CountAsync()
        + await target.ChangeSubscriptions.CountAsync()
        + await target.ApiTokens.CountAsync();
    if (targetRows != 0)
    {
        Console.Error.WriteLine(
            "Refusing to import into a non-empty PostgreSQL control plane. No rows were written.");
        return 3;
    }

    foreach (var catalog in catalogs)
    {
        catalog.ConfigurationVersion = Math.Max(1, catalog.ConfigurationVersion);
    }

    await using var transaction = await target.Database.BeginTransactionAsync();
    target.Tenants.AddRange(tenants);
    target.Catalogs.AddRange(catalogs);
    target.SavedQueries.AddRange(savedQueries);
    target.QueryRuns.AddRange(queryRuns);
    target.ChangeSubscriptions.AddRange(subscriptions);
    target.ApiTokens.AddRange(tokens);
    await target.SaveChangesAsync();

    string[] tables = ["Tenants", "Catalogs", "SavedQueries", "QueryRuns", "ChangeSubscriptions", "ApiTokens"];
    foreach (var table in tables)
    {
        var resetSequenceSql =
            $"""
              SELECT setval(
                  pg_get_serial_sequence('"{table}"', 'Id'),
                  GREATEST(COALESCE((SELECT MAX("Id") FROM "{table}"), 0), 1),
                  EXISTS (SELECT 1 FROM "{table}"))
              """;
        await target.Database.ExecuteSqlRawAsync(resetSequenceSql);
    }

    await transaction.CommitAsync();
    Console.WriteLine("Import committed. The source DuckDB file was not modified.");
    return 0;
}
finally
{
    try
    {
        File.Delete(temporarySource);
        File.Delete(temporarySource + ".wal");
    }
    catch (IOException)
    {
        // A disposable copy left in the OS temp directory does not invalidate the import.
    }
}

static async Task<bool> HasApplicationRowsAsync(string connectionString)
{
    string[] tables = ["Tenants", "Catalogs", "SavedQueries", "QueryRuns", "ChangeSubscriptions", "ApiTokens"];
    await using var connection = new NpgsqlConnection(connectionString);
    await connection.OpenAsync();

    await using var list = connection.CreateCommand();
    list.CommandText =
        """
        SELECT table_name
        FROM information_schema.tables
        WHERE table_schema = current_schema()
          AND table_name = ANY (@tables)
        """;
    list.Parameters.AddWithValue("tables", tables);

    var existing = new List<string>();
    await using (var reader = await list.ExecuteReaderAsync())
    {
        while (await reader.ReadAsync())
        {
            existing.Add(reader.GetString(0));
        }
    }

    foreach (var table in existing)
    {
        await using var any = connection.CreateCommand();
        // table comes exclusively from the fixed allow-list passed to the information-schema query.
        any.CommandText = $"SELECT EXISTS (SELECT 1 FROM \"{table}\" LIMIT 1)";
        if (await any.ExecuteScalarAsync() is true)
        {
            return true;
        }
    }

    return false;
}

internal sealed record ImportArguments(string SourcePath, string TargetConnection, bool Apply)
{
    public static ImportArguments? Parse(string[] args)
    {
        string? source = null;
        string? target = null;
        var apply = false;

        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--source" when index + 1 < args.Length:
                    source = args[++index];
                    break;
                case "--target" when index + 1 < args.Length:
                    target = args[++index];
                    break;
                case "--apply":
                    apply = true;
                    break;
                default:
                    return Usage($"Unknown or incomplete argument: {args[index]}");
            }
        }

        target ??= Environment.GetEnvironmentVariable("ConnectionStrings__ControlPlane");
        if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(target))
        {
            return Usage("--source and --target (or ConnectionStrings__ControlPlane) are required.");
        }

        return new ImportArguments(source, target, apply);
    }

    private static ImportArguments? Usage(string message)
    {
        Console.Error.WriteLine(message);
        Console.Error.WriteLine(
            "Usage: dotnet run --project src/Lakehold.ControlPlane.Import -- "
            + "--source <controlplane.duckdb> [--target <postgres-connection>] [--apply]");
        return null;
    }
}
