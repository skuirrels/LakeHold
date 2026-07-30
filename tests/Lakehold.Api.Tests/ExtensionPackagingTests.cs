using System.Text.RegularExpressions;
using DuckDB.NET.Data;
using Lakehold.Engine.Configuration;
using Xunit;

namespace Lakehold.Api.Tests;

/// <summary>Prevents development and production extension caches from drifting from the engine.</summary>
public sealed partial class ExtensionPackagingTests
{
    [Fact]
    public async Task Docker_extension_manifest_matches_native_engine_and_session_defaults()
    {
        var dockerfile = await File.ReadAllTextAsync(
            Path.Combine(AppContext.BaseDirectory, "Lakehold.Api.Dockerfile"));

        var versionMatch = VersionArgument().Match(dockerfile);
        Assert.True(versionMatch.Success, "The API Dockerfile must declare DUCKDB_EXTENSION_VERSION.");

        var extensionsMatch = ExtensionsArgument().Match(dockerfile);
        Assert.True(
            extensionsMatch.Success,
            "The API Dockerfile must declare DUCKDB_PRELOADED_EXTENSIONS.");
        Assert.Contains(
            "FROM mcr.microsoft.com/dotnet/sdk:10.0 AS development",
            dockerfile,
            StringComparison.Ordinal);
        Assert.Contains(
            "/duckdb-extensions/ /root/.duckdb/extensions/",
            dockerfile,
            StringComparison.Ordinal);
        Assert.Contains(
            "mkdir -p /var/lib/lakehold /var/lib/lakehold-imports",
            dockerfile,
            StringComparison.Ordinal);
        Assert.Contains(
            "chown -R app:app /var/lib/lakehold /var/lib/lakehold-imports",
            dockerfile,
            StringComparison.Ordinal);

        await using var connection = new DuckDBConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT version()";
        var nativeVersion = Assert.IsType<string>(await command.ExecuteScalarAsync());

        Assert.Equal(nativeVersion.TrimStart('v'), versionMatch.Groups["version"].Value);

        var packaged = extensionsMatch.Groups["extensions"].Value
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.Ordinal);
        var required = new LakehouseOptions().Extensions
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal(required.Order(), packaged.Order());
    }

    [GeneratedRegex(
        """ARG\s+DUCKDB_EXTENSION_VERSION=(?<version>[0-9]+\.[0-9]+\.[0-9]+)""",
        RegexOptions.CultureInvariant)]
    private static partial Regex VersionArgument();

    [GeneratedRegex(
        "ARG\\s+DUCKDB_PRELOADED_EXTENSIONS=\"(?<extensions>[a-z0-9_ ]+)\"",
        RegexOptions.CultureInvariant)]
    private static partial Regex ExtensionsArgument();
}
