using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Lakehold.Api.Auth;
using Lakehold.ControlPlane.Data;
using Lakehold.ControlPlane.Model;
using Lakehold.ControlPlane.Security;
using Lakehold.Engine.Catalog;
using Lakehold.Engine.Configuration;

namespace Lakehold.Api.Endpoints;

/// <summary>
///     Provisioning and credential management: create and delete tenants and catalogs, and mint,
///     list, and revoke API tokens. These are what turn a freshly bootstrapped node into a usable
///     one — before them a production deployment starts empty with no supported way to add anything.
/// </summary>
/// <remarks>
///     Every route here shares the group's <see cref="LakeholdAuthorizationFilter"/>. Provisioning is
///     instance-scoped; token management is tenant-admin (an instance token, or a full token acting on
///     its own tenant). Neither destroys lake data: deleting a catalog or tenant removes the
///     control-plane record and detaches the session, leaving the DuckLake metadata and Parquet in
///     place, exactly as destructive maintenance stays opt-in (invariant 10).
/// </remarks>
public static partial class AdminEndpoints
{
    /// <summary>Maps the provisioning and token endpoints onto the tenant group.</summary>
    public static void MapAdminEndpoints(this RouteGroupBuilder tenants)
    {
        ArgumentNullException.ThrowIfNull(tenants);

        tenants.MapPost("/", CreateTenantAsync)
            .RequireCapability(RouteCapability.Instance)
            .WithSummary("Creates a tenant. Instance scope.");

        tenants.MapDelete("/{tenantSlug}", DeleteTenantAsync)
            .RequireCapability(RouteCapability.Instance)
            .WithSummary("Deletes a tenant's control-plane records, leaving lake data in place. Instance scope.");

        tenants.MapPost("/{tenantSlug}/catalogs", CreateCatalogAsync)
            .RequireCapability(RouteCapability.Instance)
            .WithSummary("Creates a catalog under a tenant. Instance scope.");

        tenants.MapDelete("/{tenantSlug}/catalogs/{catalogName}", DeleteCatalogAsync)
            .RequireCapability(RouteCapability.Instance)
            .WithSummary("Detaches a catalog, leaving its metadata and Parquet in place. Instance scope.");

        tenants.MapPost("/{tenantSlug}/tokens", CreateTokenAsync)
            .RequireCapability(RouteCapability.TenantAdmin)
            .WithSummary("Mints a tenant-scoped API token, returned once.");

        tenants.MapGet("/{tenantSlug}/tokens", ListTokensAsync)
            .RequireCapability(RouteCapability.TenantAdmin)
            .WithSummary("Lists token metadata for a tenant. Never returns the secret.");

        tenants.MapDelete("/{tenantSlug}/tokens/{id:int}", RevokeTokenAsync)
            .RequireCapability(RouteCapability.TenantAdmin)
            .WithSummary("Revokes a token; it is refused thereafter on the HTTP and wire surfaces alike.");
    }

    internal static async Task<Results<Created<TenantDto>, BadRequest<string>, Conflict<string>>> CreateTenantAsync(
        CreateTenantRequest request,
        ControlPlaneContext context,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        if (request is null || !IsValidSlug(request.Slug))
        {
            return TypedResults.BadRequest(
                "A slug of 1-63 characters is required: lower-case letters, digits, and hyphens, "
                + "starting with a letter or digit.");
        }

        if (string.Equals(request.Slug, ApiTokenFactory.InstanceSlug, StringComparison.Ordinal))
        {
            // A tenant named 'admin' would mint tokens indistinguishable from instance-scoped ones.
            return TypedResults.BadRequest($"'{ApiTokenFactory.InstanceSlug}' is a reserved slug.");
        }

        if (string.IsNullOrWhiteSpace(request.DisplayName) || request.DisplayName.Length > 200)
        {
            return TypedResults.BadRequest("A display name of 1-200 characters is required.");
        }

        if (await context.Tenants.AnyAsync(t => t.Slug == request.Slug, cancellationToken).ConfigureAwait(false))
        {
            return TypedResults.Conflict($"Tenant '{request.Slug}' already exists.");
        }

        var tenant = new Tenant
        {
            Slug = request.Slug,
            DisplayName = request.DisplayName,
            CreatedUtc = clock.GetUtcNow(),
        };

        context.Tenants.Add(tenant);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return TypedResults.Created(
            $"/api/tenants/{tenant.Slug}",
            new TenantDto(tenant.Slug, tenant.DisplayName, []));
    }

    internal static async Task<Results<NoContent, NotFound<string>>> DeleteTenantAsync(
        string tenantSlug,
        ControlPlaneContext context,
        LakehouseService lakehouse,
        CancellationToken cancellationToken)
    {
        var tenant = await context.Tenants
            .Include(t => t.Catalogs)
            .FirstOrDefaultAsync(t => t.Slug == tenantSlug, cancellationToken)
            .ConfigureAwait(false);
        if (tenant is null)
        {
            return TypedResults.NotFound($"Tenant '{tenantSlug}' was not found.");
        }

        var catalogNames = tenant.Catalogs.Select(c => c.Name).ToArray();

        // Cascade removes the tenant's catalogs, saved queries, subscriptions, history, and tokens —
        // control-plane records only. The DuckLake metadata and Parquet are never touched here.
        context.Tenants.Remove(tenant);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        foreach (var name in catalogNames)
        {
            await lakehouse.ForgetCatalogAsync(name).ConfigureAwait(false);
        }

        return TypedResults.NoContent();
    }

    internal static async Task<Results<Created<CatalogDto>, NotFound<string>, BadRequest<string>, Conflict<string>>> CreateCatalogAsync(
        string tenantSlug,
        CreateCatalogRequest request,
        ControlPlaneContext context,
        IOptions<LakehouseOptions> options,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        if (request is null || !SqlIdentifier.IsValid(request.Name) || request.Name.Length > 63)
        {
            return TypedResults.BadRequest("A catalog name that is a bare SQL identifier of at most 63 characters is required.");
        }

        var tenant = await context.Tenants
            .FirstOrDefaultAsync(t => t.Slug == tenantSlug, cancellationToken)
            .ConfigureAwait(false);
        if (tenant is null)
        {
            return TypedResults.NotFound($"Tenant '{tenantSlug}' was not found.");
        }

        if (await context.Catalogs.AnyAsync(c => c.TenantId == tenant.Id && c.Name == request.Name, cancellationToken).ConfigureAwait(false))
        {
            return TypedResults.Conflict($"Catalog '{request.Name}' already exists for tenant '{tenantSlug}'.");
        }

        var lakehouseOptions = options.Value;
        var dataPath = string.IsNullOrWhiteSpace(request.DataPath)
            ? Path.Combine(lakehouseOptions.DataRoot, request.Name)
            : request.DataPath;
        var metadataSource = Path.Combine(lakehouseOptions.MetadataRoot, $"{request.Name}.ducklake");

        // Create local directories so the first attach succeeds. An object-store data path has no
        // directory to create; the bucket and its credentials are the deployment's responsibility.
        Directory.CreateDirectory(lakehouseOptions.MetadataRoot);
        if (!IsUri(dataPath))
        {
            Directory.CreateDirectory(dataPath);
        }

        var catalog = new LakeCatalog
        {
            TenantId = tenant.Id,
            Name = request.Name,
            MetadataKind = CatalogMetadataKind.LocalFile,
            MetadataSource = metadataSource,
            DataPath = dataPath,
            IsReadOnly = request.ReadOnly,
            CreatedUtc = clock.GetUtcNow(),
        };

        context.Catalogs.Add(catalog);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return TypedResults.Created(
            $"/api/tenants/{tenantSlug}/catalogs/{catalog.Name}",
            new CatalogDto(catalog.Name, catalog.DataPath, catalog.IsReadOnly));
    }

    internal static async Task<Results<NoContent, NotFound<string>>> DeleteCatalogAsync(
        string tenantSlug,
        string catalogName,
        ControlPlaneContext context,
        LakehouseService lakehouse,
        CancellationToken cancellationToken)
    {
        var catalog = await context.Catalogs
            .Include(c => c.Tenant)
            .FirstOrDefaultAsync(c => c.Tenant.Slug == tenantSlug && c.Name == catalogName, cancellationToken)
            .ConfigureAwait(false);
        if (catalog is null)
        {
            return TypedResults.NotFound($"Catalog '{catalogName}' was not found for tenant '{tenantSlug}'.");
        }

        context.Catalogs.Remove(catalog);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        // Drop the warm session and cached descriptor. The metadata file and Parquet stay on disk —
        // a detach is a control-plane operation, not a data deletion.
        await lakehouse.ForgetCatalogAsync(catalogName).ConfigureAwait(false);

        return TypedResults.NoContent();
    }

    internal static async Task<Results<Created<CreatedTokenDto>, NotFound<string>, BadRequest<string>>> CreateTokenAsync(
        string tenantSlug,
        CreateTokenRequest request,
        ControlPlaneContext context,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 200)
        {
            return TypedResults.BadRequest("A token name of 1-200 characters is required.");
        }

        var tenant = await context.Tenants
            .FirstOrDefaultAsync(t => t.Slug == tenantSlug, cancellationToken)
            .ConfigureAwait(false);
        if (tenant is null)
        {
            return TypedResults.NotFound($"Tenant '{tenantSlug}' was not found.");
        }

        if (request.CatalogName is { } narrowed)
        {
            if (!SqlIdentifier.IsValid(narrowed))
            {
                return TypedResults.BadRequest("A catalog narrowing must be a bare SQL identifier.");
            }

            if (!await context.Catalogs.AnyAsync(c => c.TenantId == tenant.Id && c.Name == narrowed, cancellationToken).ConfigureAwait(false))
            {
                return TypedResults.BadRequest($"Catalog '{narrowed}' was not found for tenant '{tenantSlug}'.");
            }
        }

        // An unrecognised role name falls back to owner rather than silently narrowing: a caller who
        // asked for something we do not understand should not receive a credential quieter than they
        // expect, and the request is otherwise well-formed.
        var issued = ApiTokenFactory.Issue(
            TokenScope.Tenant,
            tenant,
            request.Name,
            clock.GetUtcNow(),
            readOnly: request.ReadOnly,
            catalogName: request.CatalogName,
            expiresUtc: request.ExpiresUtc,
            role: TokenRoleParser.Parse(request.Role, TokenRole.Owner));

        context.ApiTokens.Add(issued.Record);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return TypedResults.Created(
            $"/api/tenants/{tenantSlug}/tokens/{issued.Record.Id}",
            new CreatedTokenDto(issued.Record.Id, issued.Record.Name, issued.Plaintext));
    }

    internal static async Task<Results<Ok<IReadOnlyList<ApiTokenDto>>, NotFound<string>>> ListTokensAsync(
        string tenantSlug,
        ControlPlaneContext context,
        CancellationToken cancellationToken)
    {
        if (!await context.Tenants.AnyAsync(t => t.Slug == tenantSlug, cancellationToken).ConfigureAwait(false))
        {
            return TypedResults.NotFound($"Tenant '{tenantSlug}' was not found.");
        }

        var tokens = await context.ApiTokens
            .AsNoTracking()
            .Where(t => t.Tenant!.Slug == tenantSlug)
            .OrderBy(t => t.Id)
            .Select(t => new ApiTokenDto(
                t.Id,
                t.Name,
                t.Scope.ToString(),
                t.Role.ToString(),
                t.CatalogName,
                t.ReadOnly,
                t.CreatedUtc,
                t.ExpiresUtc,
                t.RevokedUtc,
                t.LastUsedUtc))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return TypedResults.Ok<IReadOnlyList<ApiTokenDto>>(tokens);
    }

    internal static async Task<Results<NoContent, NotFound<string>>> RevokeTokenAsync(
        string tenantSlug,
        int id,
        ControlPlaneContext context,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        // Scoped to the route tenant, so a token id alone cannot revoke another tenant's credential.
        var token = await context.ApiTokens
            .FirstOrDefaultAsync(t => t.Id == id && t.Tenant!.Slug == tenantSlug, cancellationToken)
            .ConfigureAwait(false);
        if (token is null)
        {
            return TypedResults.NotFound($"Token {id} was not found for tenant '{tenantSlug}'.");
        }

        // Idempotent: a second revoke leaves the original timestamp and still succeeds.
        token.RevokedUtc ??= clock.GetUtcNow();
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return TypedResults.NoContent();
    }

    private static bool IsValidSlug(string? slug) => slug is not null && SlugPattern().IsMatch(slug);

    private static bool IsUri(string path) => path.Contains("://", StringComparison.Ordinal);

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,62}$")]
    private static partial Regex SlugPattern();
}
