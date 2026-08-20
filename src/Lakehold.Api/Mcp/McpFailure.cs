using Lakehold.Api.Connectors;
using Lakehold.ControlPlane.Data;
using Lakehold.Querying;
using ModelContextProtocol;

namespace Lakehold.Api.Mcp;

/// <summary>
///     Converts the domain failures a tool can provoke into <see cref="McpException"/>, so the agent
///     reads what went wrong instead of a bare "an error occurred".
/// </summary>
/// <remarks>
///     <para>
///         The SDK reports an uncaught exception to the client as
///         <c>An error occurred invoking '&lt;tool&gt;'</c> with the message withheld. That is the right
///         default — an unexpected exception's message is an implementation detail and may name types,
///         paths, or connection state — but it is the wrong answer for a failure the caller *caused*
///         and could correct. An unknown column, an expired snapshot, or a stale revision are all
///         things an agent fixes on its next call if it is told, and cannot fix if it is not.
///     </para>
///     <para>
///         So the expected set is enumerated here once rather than per tool. Before this existed each
///         tool carried its own <c>catch</c> list and they disagreed: <c>query_history</c> had none at
///         all, and the inspection tools caught two of the four kinds their services throw. A single
///         predicate cannot drift from itself.
///     </para>
///     <para>
///         <see cref="InvalidOperationException"/> is deliberately <em>not</em> in the shared set.
///         EF Core and the DI container both raise it for programming errors whose messages name
///         internal types, and a tool that forwarded those would turn the opaque default into a
///         disclosure. The two call sites where it is a genuine domain signal — a maintenance
///         retention blocker and a connector state transition — opt in explicitly.
///     </para>
/// </remarks>
internal static class McpFailure
{
    /// <summary>Whether an exception describes something the caller can correct.</summary>
    public static bool IsExpected(Exception exception) => exception
        is CatalogNotFoundException
            or ArgumentException
            or DuckDB.NET.Data.DuckDBException
            or SavedQueryNotFoundException
            or SavedQueryValidationException
            or SavedQueryConflictException
            or DataConnectorConflictException

            // Planning failures. A language whose compiler is unreachable, and a source the compiler
            // rejected, are both things the caller acts on: pick another language, or fix the source.
            // Their absence here was found by a test asserting the opaque message never appears.
            or QueryLanguageUnavailableException
            or QuerySourceInvalidException;

    /// <summary>Runs an operation, reporting an expected failure as a readable tool error.</summary>
    public static async Task<T> GuardAsync<T>(Func<Task<T>> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        try
        {
            return await operation().ConfigureAwait(false);
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            throw new McpException(exception.Message);
        }
    }

    /// <summary>Runs an operation with no result, reporting an expected failure as a tool error.</summary>
    public static async Task GuardAsync(Func<Task> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        try
        {
            await operation().ConfigureAwait(false);
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            throw new McpException(exception.Message);
        }
    }

    /// <summary>
    ///     As <see cref="GuardAsync{T}(Func{Task{T}})"/>, additionally treating
    ///     <see cref="InvalidOperationException"/> as a domain signal.
    /// </summary>
    /// <remarks>
    ///     Only for call sites whose service raises it to mean something the caller can act on — a CDC
    ///     retention watermark blocking expiry, a connector that cannot move to the requested state.
    ///     Never as a default, for the reason given on the class.
    /// </remarks>
    public static async Task<T> GuardStatefulAsync<T>(Func<Task<T>> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        try
        {
            return await operation().ConfigureAwait(false);
        }
        catch (Exception exception) when (IsExpected(exception) || exception is InvalidOperationException)
        {
            throw new McpException(exception.Message);
        }
    }
}
