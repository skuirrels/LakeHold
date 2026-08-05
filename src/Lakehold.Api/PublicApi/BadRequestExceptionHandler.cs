using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http.Features;

namespace Lakehold.Api.PublicApi;

/// <summary>
///     Answers a malformed request with the status it earned rather than <c>500</c>.
/// </summary>
/// <remarks>
///     ASP.NET Core raises <see cref="BadHttpRequestException"/> when it cannot bind a request —
///     a missing required query parameter, an unparseable value, a body over the size limit — and
///     carries the intended status on the exception. <c>UseExceptionHandler</c> does not read it, so
///     every one of those became a <c>500</c>: the API told a caller who had simply omitted a
///     parameter that the server had failed, and the log recorded an unhandled exception for a
///     request that was never going to succeed.
///
///     That is also a contract break. <c>PUBLIC-API.md</c> promises RFC 9457 <c>problem+json</c>
///     with a stable code, and <c>5xx</c> is the one class of response an SDK is entitled to retry.
///     Retrying a request that is missing a parameter cannot ever succeed.
///
///     The exception's own message is used as the detail because for binding failures it names the
///     parameter and where it was expected, which is exactly what the caller needs. It is bounded
///     like every other detail, and it describes the request rather than anything about the
///     deployment.
/// </remarks>
internal sealed class BadRequestExceptionHandler : IExceptionHandler
{
    /// <inheritdoc/>
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        if (exception is not BadHttpRequestException badRequest)
        {
            return false;
        }

        // A response already on the wire cannot be replaced; letting the pipeline continue is the
        // only honest option, and it is what the framework's own handler does in this case.
        if (httpContext.Response.HasStarted)
        {
            return false;
        }

        // The framework defaults this to 400 and raises it to 413 or 415 where those apply, so the
        // exception is the authority on the status rather than a guess made here.
        var status = badRequest.StatusCode;
        httpContext.Response.StatusCode = status;

        // Clear any buffered partial write so the problem document is the whole body.
        httpContext.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

        await PublicApiProblems.Create(httpContext, status, badRequest.Message)
            .ExecuteAsync(httpContext)
            .ConfigureAwait(false);

        return true;
    }
}
