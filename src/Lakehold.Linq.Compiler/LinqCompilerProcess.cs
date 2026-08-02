using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Lakehold.Querying;

namespace Lakehold.Linq.Compiler;

/// <summary>Runs authored-code compilation in a disposable child process with a hard timeout.</summary>
public sealed class LinqCompilerProcess(IOptions<LinqCompilerOptions> options)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly LinqCompilerOptions _options = options.Value;

    public async Task<QueryPlan> CompileAsync(QueryPlanningRequest request, CancellationToken cancellationToken)
    {
        var assembly = typeof(LinqCompilerProcess).Assembly.Location;
        var start = new ProcessStartInfo
        {
            FileName = Environment.ProcessPath ?? "dotnet",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        if (string.Equals(Path.GetFileNameWithoutExtension(start.FileName), "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            start.ArgumentList.Add(assembly);
        }

        start.ArgumentList.Add("--worker");
        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("The LINQ compiler worker process could not be started.");

        var envelope = new LinqWorkerRequest(request, new LinqWorkerLimits(
            _options.MaxSourceLength,
            _options.MaxTables,
            _options.MaxColumns,
            _options.MaxArrayElements));
        await JsonSerializer.SerializeAsync(process.StandardInput.BaseStream, envelope, Json, cancellationToken)
            .ConfigureAwait(false);
        await process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
        process.StandardInput.Close();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.Timeout);
        try
        {
            var responseTask = JsonSerializer.DeserializeAsync<LinqWorkerResponse>(
                process.StandardOutput.BaseStream,
                Json,
                timeout.Token).AsTask();
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            var response = await responseTask.ConfigureAwait(false)
                ?? throw new InvalidOperationException("The LINQ compiler worker returned an empty response.");
            if (process.ExitCode != 0)
            {
                var error = await process.StandardError.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
                throw new InvalidOperationException($"The LINQ compiler worker failed: {error.Trim()}");
            }

            if (response.Diagnostics is { Count: > 0 })
            {
                throw new LinqPlanningException(response.Diagnostics);
            }

            return response.Plan
                ?? throw new InvalidOperationException("The LINQ compiler worker returned no plan.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            Kill(process);
            throw new TimeoutException("LINQ compilation exceeded the configured hard timeout.");
        }
        catch
        {
            Kill(process);
            throw;
        }
    }

    private static void Kill(Process process)
    {
        if (!process.HasExited)
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit();
        }
    }
}

internal sealed record LinqWorkerLimits(
    int MaxSourceLength,
    int MaxTables,
    int MaxColumns,
    int MaxArrayElements);

internal sealed record LinqWorkerRequest(QueryPlanningRequest Request, LinqWorkerLimits Limits);

internal sealed record LinqWorkerResponse(QueryPlan? Plan, IReadOnlyList<QueryDiagnostic>? Diagnostics);

internal static class LinqCompilerWorker
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static async Task RunAsync(Stream input, Stream output, CancellationToken cancellationToken)
    {
        var envelope = await JsonSerializer.DeserializeAsync<LinqWorkerRequest>(input, Json, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("The worker received no compilation request.");
        var compiler = new LinqQueryCompiler(Options.Create(new LinqCompilerOptions
        {
            MaxSourceLength = envelope.Limits.MaxSourceLength,
            MaxTables = envelope.Limits.MaxTables,
            MaxColumns = envelope.Limits.MaxColumns,
            MaxArrayElements = envelope.Limits.MaxArrayElements,
        }));

        LinqWorkerResponse response;
        try
        {
            response = new LinqWorkerResponse(
                await compiler.CompileAsync(envelope.Request, cancellationToken).ConfigureAwait(false),
                null);
        }
        catch (LinqPlanningException exception)
        {
            response = new LinqWorkerResponse(null, exception.Diagnostics);
        }
        catch (ArgumentException exception)
        {
            response = new LinqWorkerResponse(null, [
                new QueryDiagnostic("error", "LINQ000", exception.Message, 1, 1, 1, 1),
            ]);
        }

        await JsonSerializer.SerializeAsync(output, response, Json, cancellationToken).ConfigureAwait(false);
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}
