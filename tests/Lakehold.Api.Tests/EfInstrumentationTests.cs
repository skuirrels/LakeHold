using System.Diagnostics;
using DuckDB.EFCoreProvider.Extensions;
using Lakehold.Engine.Execution;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using OpenTelemetry;
using OpenTelemetry.Trace;
using Xunit;

namespace Lakehold.Api.Tests;

/// <summary>
///     Cover for the one instrumentation that could export a tenant's SQL.
/// </summary>
/// <remarks>
///     The data plane is EF Core: a submitted statement reaches DuckDB through
///     <c>LakeContext.Database.SqlQueryDynamicRawAsync</c>, so anything the EF instrumentation
///     captures as command text is tenant data. The installed version exports none, so this passes
///     today on the package's behaviour alone — which is the point. It asserts the guarantee rather
///     than the mechanism, so a prerelease bump that starts exporting statements fails here instead
///     of shipping tenant SQL to a collector unnoticed. It runs the real pipeline against the real
///     production configuration rather than a copy of it.
/// </remarks>
public sealed class EfInstrumentationTests
{
    private const string Secret = "SUPER_SECRET_TENANT_LITERAL";

    private sealed class CollectingExporter(List<Activity> sink) : BaseExporter<Activity>
    {
        public override ExportResult Export(in Batch<Activity> batch)
        {
            foreach (var activity in batch)
            {
                sink.Add(activity);
            }

            return ExportResult.Success;
        }
    }

    private static async Task<List<Activity>> RunTenantStatementAsync(bool useProductionConfiguration)
    {
        var exported = new List<Activity>();

        var tracerProvider = Sdk.CreateTracerProviderBuilder()
            .AddEntityFrameworkCoreInstrumentation(options =>
            {
                if (useProductionConfiguration)
                {
                    Extensions.ConfigureEntityFrameworkInstrumentation(options);
                }
            })
            .AddProcessor(new SimpleActivityExportProcessor(new CollectingExporter(exported)))
            .Build();

        using (tracerProvider)
        {
            // The data plane exactly as Duckling drives it: a model-less context running submitted
            // SQL through the provider's dynamic path.
            var options = new DbContextOptionsBuilder<LakeContext>()
                .UseDuckDB("Data Source=:memory:")
                .Options;

            await using var context = new LakeContext(options);
            await using var reader = await context.Database
                .SqlQueryDynamicRawAsync($"SELECT '{Secret}' AS leaked", CancellationToken.None);

            await foreach (var _ in reader.ReadRowsAsync(CancellationToken.None))
            {
                // Drain so the command completes and the activity stops.
            }
        }

        return exported;
    }

    [Fact]
    public async Task The_instrumentation_is_wired_and_produces_a_span_for_a_statement()
    {
        // Guards the guard: if the instrumentation stopped emitting entirely, the leak assertion
        // below would pass for the wrong reason.
        var exported = await RunTenantStatementAsync(useProductionConfiguration: false);

        Assert.NotEmpty(exported);
    }

    [Fact]
    public async Task A_submitted_statement_never_reaches_an_exported_span()
    {
        var exported = await RunTenantStatementAsync(useProductionConfiguration: true);

        Assert.NotEmpty(exported);

        foreach (var activity in exported)
        {
            foreach (var tag in activity.TagObjects)
            {
                var value = Convert.ToString(tag.Value, System.Globalization.CultureInfo.InvariantCulture);
                Assert.False(
                    value?.Contains(Secret, StringComparison.Ordinal) == true,
                    $"tag '{tag.Key}' exported the submitted statement");
            }

            Assert.DoesNotContain(Secret, activity.DisplayName, StringComparison.Ordinal);
        }
    }
}
