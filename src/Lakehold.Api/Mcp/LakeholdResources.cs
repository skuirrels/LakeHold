using System.Text.Json;
using Lakehold.ControlPlane.Data;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Lakehold.Api.Mcp;

/// <summary>A catalog's schema as an attachable resource rather than a tool call.</summary>
/// <remarks>
///     The same information <c>describe_schema</c> returns, reachable by URI so a client can pin it
///     as standing context instead of spending a tool call — and a round trip of reasoning — every
///     time it needs a column name.
///     <para>
///         It is a <em>template</em>, not a concrete resource: the URI names a tenant and a catalog,
///         which the caller learns from <c>list_tenants</c>. Enumerating every reachable catalog as a
///         concrete resource would mean resolving the credential during resource *listing*, and
///         listing is the one place a mistake would disclose catalog names to a caller that cannot
///         reach them. A template discloses nothing and costs the agent one extra call it was making
///         anyway.
///     </para>
///     <para>
///         Authorization is <see cref="McpCaller"/>, exactly as for a tool. A resource that authorised
///         differently from a tool would be a hole shaped precisely like the one invariant 21 closes.
///     </para>
/// </remarks>
[McpServerResourceType]
public sealed class LakeholdResources(LakehouseService lakehouse, IHttpContextAccessor httpContextAccessor)
{
    [McpServerResource(
        UriTemplate = "lakehold://{tenant}/{catalog}/schema",
        Name = "catalog_schema",
        Title = "Catalog schema",
        MimeType = "application/json")]
    [System.ComponentModel.Description(
        "The schemas, tables, and columns of one Lakehold catalog, as JSON. Tenant and catalog names "
        + "come from the list_tenants tool.")]
    public async Task<string> CatalogSchemaAsync(string tenant, string catalog, CancellationToken cancellationToken)
    {
        McpCaller.Authorize(httpContextAccessor, tenant, catalog);

        try
        {
            var schemas = await lakehouse.GetSchemasAsync(tenant, catalog, cancellationToken).ConfigureAwait(false);

            return JsonSerializer.Serialize(
                schemas.Select(s => new McpSchema(
                    s.Name,
                    [
                        .. s.Tables.Select(t => new McpTable(
                            t.Name,
                            t.Kind,
                            [.. t.Columns.Select(c => new McpSchemaColumn(c.Name, c.DataType, c.IsNullable))])),
                    ])),
                McpJson.Options);
        }
        catch (CatalogNotFoundException ex)
        {
            throw new McpException(ex.Message);
        }
    }
}

/// <summary>Serialisation for resource bodies, which are strings rather than typed results.</summary>
/// <remarks>
///     Tool results are serialised by the SDK; a resource returns text, so its shape is ours to set.
///     camelCase matches what the SDK emits for tools, so an agent sees one convention across both.
/// </remarks>
internal static class McpJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web);
}
