using System.Net;
using System.Text;
using Lakehold.Api.Querying;
using Lakehold.Querying;
using Microsoft.Extensions.Options;
using Xunit;

namespace Lakehold.Api.Tests;

public sealed class QueryPlannerRegistryTests
{
    [Fact]
    public async Task Planner_auth_header_is_sent_and_oversized_response_is_refused()
    {
        var handler = new RecordingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(new string('x', 2_048), Encoding.UTF8, "application/json"),
        });
        var registry = new QueryPlannerRegistry(
            new StubHttpClientFactory(handler),
            Options.Create(new QueryPlannerOptions
            {
                MaxResponseBytes = 1_024,
                Planners =
                [
                    new ExternalQueryPlannerOptions
                    {
                        Id = "csharp-linq",
                        Endpoint = new Uri("http://planner/"),
                        SharedSecret = "shared-test-secret",
                    },
                ],
            }));

        await Assert.ThrowsAsync<HttpRequestException>(() => registry.PlanAsync(
            "csharp-linq",
            new QueryPlanningRequest("Main.Events", "schema", []),
            default));
        Assert.Equal("shared-test-secret", handler.PlannerKey);
    }

    private sealed class RecordingHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        public string? PlannerKey { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            PlannerKey = request.Headers.GetValues("X-Lakehold-Planner-Key").Single();
            return Task.FromResult(response);
        }
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}
