using System.Net;
using System.Net.Http.Json;
using System.Text;
using Lakehold.Api.Querying;
using Lakehold.Querying;
using Microsoft.Extensions.Logging.Abstractions;
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
        var registry = Create(handler, maxResponseBytes: 1_024);

        await Assert.ThrowsAsync<HttpRequestException>(() => registry.PlanAsync(
            "csharp-linq",
            new QueryPlanningRequest("Main.Events", "schema", []),
            default));
        Assert.Equal("shared-test-secret", handler.PlannerKey);
    }

    [Fact]
    public async Task Rejected_planner_key_is_reported_as_an_unavailable_language_not_a_missing_one()
    {
        var registry = Create(new RecordingHandler(new HttpResponseMessage(HttpStatusCode.Unauthorized)));

        var languages = await registry.GetLanguagesAsync(default);

        var linq = Assert.Single(languages, language => language.Id == "csharp-linq");
        Assert.False(linq.Available);
        Assert.Contains("same shared secret", linq.UnavailableReason, StringComparison.Ordinal);
        // SQL runs in this process and cannot be taken down by a plugin's health.
        Assert.True(Assert.Single(languages, language => language.Id == "sql").Available);
    }

    [Fact]
    public async Task A_planner_that_misses_the_discovery_deadline_reports_the_deadline()
    {
        var registry = Create(new StallingHandler());

        var languages = await registry.GetLanguagesAsync(default);

        var linq = Assert.Single(languages, language => language.Id == "csharp-linq");
        Assert.False(linq.Available);
        Assert.Contains("did not answer within", linq.UnavailableReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_endpoint_serving_a_different_language_is_reported_rather_than_silently_dropped()
    {
        var registry = Create(new RecordingHandler(Descriptor(new QueryLanguageDescriptor(
            "sql-macro",
            "SQL macros",
            "sql",
            "SELECT 1",
            ReadOnly: true,
            SupportsSavedQueries: true))));

        var languages = await registry.GetLanguagesAsync(default);

        var linq = Assert.Single(languages, language => language.Id == "csharp-linq");
        Assert.False(linq.Available);
        Assert.Contains("different language id", linq.UnavailableReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_planner_cannot_declare_its_own_health()
    {
        // A plugin that sent Available: false would otherwise remove itself from every selector, and
        // one that sent Available: true while failing would claim health the host never observed.
        var registry = Create(new RecordingHandler(Descriptor(new QueryLanguageDescriptor(
            "csharp-linq",
            "C# LINQ",
            "csharp",
            "from row in Main.Events select row",
            ReadOnly: true,
            SupportsSavedQueries: true,
            Available: false,
            UnavailableReason: "ignore me"))));

        var languages = await registry.GetLanguagesAsync(default);

        var linq = Assert.Single(languages, language => language.Id == "csharp-linq");
        Assert.True(linq.Available);
        Assert.Null(linq.UnavailableReason);
    }

    [Fact]
    public async Task An_unhealthy_planner_keeps_the_name_it_had_when_it_was_last_up()
    {
        var cache = new QueryPlannerDescriptorCache();
        var healthy = new SequencedHandler(
            Descriptor(new QueryLanguageDescriptor(
                "csharp-linq",
                "C# LINQ",
                "csharp",
                "from row in Main.Events select row",
                ReadOnly: true,
                SupportsSavedQueries: true)),
            new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

        var first = await Create(healthy, cache: cache).GetLanguagesAsync(default);
        var second = await Create(healthy, cache: cache).GetLanguagesAsync(default);

        Assert.Equal("C# LINQ", Assert.Single(first, language => language.Id == "csharp-linq").DisplayName);
        var down = Assert.Single(second, language => language.Id == "csharp-linq");
        Assert.Equal("C# LINQ", down.DisplayName);
        Assert.Equal("csharp", down.EditorLanguage);
        Assert.False(down.Available);
        // Saving or publishing needs the planner that is down, whatever it advertised while it was up.
        Assert.False(down.SupportsSavedQueries);
        Assert.True(down.ReadOnly);
    }

    [Fact]
    public async Task A_descriptor_that_cannot_be_parsed_says_so_rather_than_blaming_the_network()
    {
        var registry = Create(new RecordingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{ not json", Encoding.UTF8, "application/json"),
        }));

        var languages = await registry.GetLanguagesAsync(default);

        var linq = Assert.Single(languages, language => language.Id == "csharp-linq");
        Assert.False(linq.Available);
        Assert.Contains("cannot read", linq.UnavailableReason, StringComparison.Ordinal);
    }

    [Theory]
    // A body over the cap is refused twice over: once up front when the planner declares its length,
    // and again mid-read when it does not. Both are the same problem to the operator reading the
    // reason, so both have to arrive there as the same reason — the declared case reported the
    // planner unreachable, which sends them to check a network that is working.
    [InlineData(true)]
    [InlineData(false)]
    public async Task An_oversized_descriptor_says_so_however_the_size_is_discovered(bool declareLength)
    {
        var content = new StringContent(new string('x', 8_192), Encoding.UTF8, "application/json");
        if (!declareLength)
        {
            content.Headers.ContentLength = null;
        }

        var registry = Create(
            new RecordingHandler(new HttpResponseMessage(HttpStatusCode.OK) { Content = content }),
            maxResponseBytes: 1_024);

        var languages = await registry.GetLanguagesAsync(default);

        var linq = Assert.Single(languages, language => language.Id == "csharp-linq");
        Assert.False(linq.Available);
        Assert.Contains("cannot read", linq.UnavailableReason, StringComparison.Ordinal);
        // The byte cap is the host's business, and the planner's own size claim is untrusted input.
        Assert.DoesNotContain("8,192", linq.UnavailableReason, StringComparison.Ordinal);
    }

    private static QueryPlannerRegistry Create(
        HttpMessageHandler handler,
        int maxResponseBytes = 4 * 1024 * 1024,
        QueryPlannerDescriptorCache? cache = null)
        => new(
            new StubHttpClientFactory(handler),
            Options.Create(new QueryPlannerOptions
            {
                MaxResponseBytes = maxResponseBytes,
                Planners =
                [
                    new ExternalQueryPlannerOptions
                    {
                        Id = "csharp-linq",
                        Endpoint = new Uri("http://planner/"),
                        SharedSecret = "shared-test-secret",
                    },
                ],
            }),
            cache ?? new QueryPlannerDescriptorCache(),
            NullLogger<QueryPlannerRegistry>.Instance);

    private static HttpResponseMessage Descriptor(QueryLanguageDescriptor descriptor)
        => new(HttpStatusCode.OK) { Content = JsonContent.Create(descriptor) };

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

    /// <summary>Answers each call from the queue, so one handler can be healthy and then not.</summary>
    private sealed class SequencedHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        private int _call;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(responses[Math.Min(_call++, responses.Length - 1)]);
    }

    /// <summary>Never answers, so the registry's own discovery deadline is what ends the call.</summary>
    private sealed class StallingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new System.Diagnostics.UnreachableException();
        }
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}
