using Lakehold.ControlPlane.Model;
using Lakehold.Engine.Catalog;
using Lakehold.Engine.Execution;
using Lakehold.Querying;
using Microsoft.EntityFrameworkCore;

namespace Lakehold.ControlPlane.Data;

/// <summary>Raised when a saved-query definition is invalid.</summary>
public sealed class SavedQueryValidationException(string message) : Exception(message);

/// <summary>Raised when a saved query is absent from the tenant/catalog boundary.</summary>
public sealed class SavedQueryNotFoundException(string message) : Exception(message);

/// <summary>Raised when a saved-query mutation conflicts with current persisted state.</summary>
public sealed class SavedQueryConflictException : Exception
{
    public SavedQueryConflictException(string message)
        : base(message)
    {
    }

    public SavedQueryConflictException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
///     Catalog-scoped saved-query use cases: authoring, read-only execution, and explicit
///     publication as a DuckLake view.
/// </summary>
/// <remarks>
///     <para>
///         Definitions live in the control plane; published views live in the catalog. That keeps
///         drafts, descriptions, and revisions out of the analytical schema while making a
///         deliberate publication visible to every SQL client.
///     </para>
///     <para>
///         Execution always requests a read-only Duckling, regardless of the caller's role. The
///         single-statement and leading-query checks improve authoring feedback, but they are not
///         the security boundary: a saved definition cannot acquire a writable attachment.
///     </para>
/// </remarks>
public sealed class SavedQueryService(
    ControlPlaneContext context,
    LakehouseService lakehouse,
    TimeProvider clock,
    QuerySourcePlanningService planning)
{
    private const int MaxSourceLength = 100_000;

    public Task<SavedQuery> CreateAsync(
        string tenantSlug,
        string catalogName,
        string name,
        string? description,
        string sql,
        int? tokenId,
        CancellationToken cancellationToken)
        => CreateAsync(
            tenantSlug,
            catalogName,
            name,
            description,
            sql,
            "sql",
            tokenId,
            cancellationToken);

    /// <summary>Lists saved queries bound to one reachable catalog.</summary>
    public async Task<IReadOnlyList<SavedQuery>> ListAsync(
        string tenantSlug,
        string catalogName,
        CancellationToken cancellationToken)
    {
        var catalog = await ResolveCatalogAsync(tenantSlug, catalogName, cancellationToken).ConfigureAwait(false);

        return await context.SavedQueries
            .AsNoTracking()
            .Where(q => q.CatalogId == catalog.Id)
            .OrderBy(q => q.Name)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Reads one saved query without allowing an id to cross a catalog boundary.</summary>
    public async Task<SavedQuery> GetAsync(
        string tenantSlug,
        string catalogName,
        int id,
        CancellationToken cancellationToken)
    {
        var query = await FindAsync(tenantSlug, catalogName, id, tracking: false, cancellationToken)
            .ConfigureAwait(false);
        return query ?? throw NotFound(tenantSlug, catalogName, id);
    }

    /// <summary>Creates a query definition at revision one.</summary>
    public async Task<SavedQuery> CreateAsync(
        string tenantSlug,
        string catalogName,
        string name,
        string? description,
        string sql,
        string language,
        int? tokenId,
        CancellationToken cancellationToken)
    {
        var catalog = await ResolveCatalogAsync(tenantSlug, catalogName, cancellationToken).ConfigureAwait(false);
        var definition = await ValidateAsync(
                tenantSlug,
                catalogName,
                name,
                description,
                sql,
                language,
                cancellationToken)
            .ConfigureAwait(false);
        var now = clock.GetUtcNow();

        var query = new SavedQuery
        {
            TenantId = catalog.TenantId,
            CatalogId = catalog.Id,
            Name = definition.Name,
            Description = definition.Description,
            Sql = definition.Sql,
            Language = definition.Language,
            Revision = 1,
            ConcurrencyVersion = 1,
            CreatedByTokenId = tokenId,
            UpdatedByTokenId = tokenId,
            CreatedUtc = now,
            UpdatedUtc = now,
        };

        context.SavedQueries.Add(query);
        await SaveAsync(
            $"A saved query named '{definition.Name}' already exists in this catalog.",
            cancellationToken).ConfigureAwait(false);
        return query;
    }

    /// <summary>
    ///     Replaces the authored definition and advances its optimistic revision. A published view
    ///     is deliberately not rewritten here; its lower published revision makes the drift visible
    ///     until an editor explicitly republishes.
    /// </summary>
    public Task<SavedQuery> UpdateAsync(
        string tenantSlug,
        string catalogName,
        int id,
        int expectedRevision,
        string name,
        string? description,
        string sql,
        int? tokenId,
        CancellationToken cancellationToken)
        => UpdateAsync(
            tenantSlug,
            catalogName,
            id,
            expectedRevision,
            name,
            description,
            sql,
            "sql",
            tokenId,
            cancellationToken);

    public async Task<SavedQuery> UpdateAsync(
        string tenantSlug,
        string catalogName,
        int id,
        int expectedRevision,
        string name,
        string? description,
        string sql,
        string language,
        int? tokenId,
        CancellationToken cancellationToken)
    {
        var query = await FindAsync(tenantSlug, catalogName, id, tracking: true, cancellationToken)
            .ConfigureAwait(false)
            ?? throw NotFound(tenantSlug, catalogName, id);
        EnsureRevision(query, expectedRevision);

        var definition = await ValidateAsync(
                tenantSlug,
                catalogName,
                name,
                description,
                sql,
                language,
                cancellationToken)
            .ConfigureAwait(false);

        query.Name = definition.Name;
        query.Description = definition.Description;
        query.Sql = definition.Sql;
        query.Language = definition.Language;
        query.Revision++;
        query.ConcurrencyVersion++;
        query.UpdatedByTokenId = tokenId;
        query.UpdatedUtc = clock.GetUtcNow();

        await SaveAsync(
            $"A saved query named '{definition.Name}' already exists in this catalog.",
            cancellationToken).ConfigureAwait(false);
        return query;
    }

    /// <summary>Deletes an unpublished saved query.</summary>
    public async Task DeleteAsync(
        string tenantSlug,
        string catalogName,
        int id,
        int expectedRevision,
        CancellationToken cancellationToken)
    {
        var query = await FindAsync(tenantSlug, catalogName, id, tracking: true, cancellationToken)
            .ConfigureAwait(false)
            ?? throw NotFound(tenantSlug, catalogName, id);
        EnsureRevision(query, expectedRevision);

        if (query.PublishedViewName is not null)
        {
            throw new SavedQueryConflictException(
                $"Saved query '{query.Name}' is published as " +
                $"'{query.PublishedSchema}.{query.PublishedViewName}'. Unpublish it before deleting it.");
        }

        context.SavedQueries.Remove(query);
        await SaveAsync("The saved query changed before it could be deleted.", cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Executes the persisted definition through a structurally read-only attachment.</summary>
    public async Task<QueryResult> ExecuteAsync(
        string tenantSlug,
        string catalogName,
        int id,
        int? tokenId,
        bool recordHistory,
        CancellationToken cancellationToken)
    {
        var execution = await ExecutePlannedAsync(
            tenantSlug,
            catalogName,
            id,
            tokenId,
            recordHistory,
            cancellationToken).ConfigureAwait(false);
        return execution.Result;
    }

    /// <summary>Plans and executes the persisted definition, retaining generated SQL for the UI.</summary>
    public async Task<SavedQueryExecutionResult> ExecutePlannedAsync(
        string tenantSlug,
        string catalogName,
        int id,
        int? tokenId,
        bool recordHistory,
        CancellationToken cancellationToken)
    {
        var query = await GetAsync(tenantSlug, catalogName, id, cancellationToken).ConfigureAwait(false);
        var plan = await PlanAsync(tenantSlug, catalogName, query.Language, query.Sql, cancellationToken)
            .ConfigureAwait(false);
        var result = await lakehouse.ExecuteAsync(
            tenantSlug,
            catalogName,
            plan.Sql,
            cancellationToken,
            readOnly: true,
            tokenId,
            recordHistory,
            QueryPlanParameterMapper.Decode(plan),
            query.Language,
            query.Sql).ConfigureAwait(false);
        return new SavedQueryExecutionResult(result, plan, query.Language);
    }

    /// <summary>
    ///     Publishes the current revision as a catalog view. The first publication refuses an
    ///     existing object; later publications replace only the target already owned by this query.
    /// </summary>
    public async Task<SavedQuery> PublishAsync(
        string tenantSlug,
        string catalogName,
        int id,
        int expectedRevision,
        string schema,
        string viewName,
        int? tokenId,
        CancellationToken cancellationToken)
    {
        if (!SqlIdentifier.IsValid(schema) || !SqlIdentifier.IsValid(viewName))
        {
            throw new SavedQueryValidationException(
                "Published schema and view names must be bare SQL identifiers of at most 63 characters.");
        }

        var query = await FindAsync(tenantSlug, catalogName, id, tracking: true, cancellationToken)
            .ConfigureAwait(false)
            ?? throw NotFound(tenantSlug, catalogName, id);
        EnsureRevision(query, expectedRevision);
        var plan = await PlanAsync(tenantSlug, catalogName, query.Language, query.Sql, cancellationToken)
            .ConfigureAwait(false);
        if (plan.Parameters.Count > 0)
        {
            throw new SavedQueryValidationException(
                "A parameterized query cannot be published as a view. Use literals in the saved definition.");
        }

        var alreadyPublished = query.PublishedViewName is not null;
        if (alreadyPublished
            && (!string.Equals(query.PublishedSchema, schema, StringComparison.Ordinal)
                || !string.Equals(query.PublishedViewName, viewName, StringComparison.Ordinal)))
        {
            throw new SavedQueryConflictException(
                $"Saved query '{query.Name}' is already published as " +
                $"'{query.PublishedSchema}.{query.PublishedViewName}'. Unpublish it before choosing a new target.");
        }

        var verb = alreadyPublished ? "CREATE OR REPLACE VIEW" : "CREATE VIEW";
        // Validate at the trust boundary, then quote the already-approved identifier so a valid
        // name that happens to be a SQL keyword is still addressable.
        var target =
            $"{SqlIdentifier.QuoteName(SqlIdentifier.Quote(schema))}." +
            $"{SqlIdentifier.QuoteName(SqlIdentifier.Quote(viewName))}";
        var statement = $"{verb} {target} AS\n{plan.Sql}";
        var viewChanged = false;

        await using var transaction = await context.Database
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        try
        {
            // Claim the record before touching the data plane. The concurrency update takes the
            // control-plane write lock inside this transaction, so another update, publish, or
            // unpublish must win or lose before it can execute its own DDL.
            query.ConcurrencyVersion++;
            await SaveAsync(
                    "The saved query changed before publication could begin. Reload it before retrying.",
                    cancellationToken)
                .ConfigureAwait(false);

            // A writable attachment is necessary for DDL. Authorization is performed by the
            // transport before this use case is reached. Identifier and one-statement validation
            // keep the composite DDL well-formed; they do not decide write authority.
            await lakehouse
                .ExecuteAsync(tenantSlug, catalogName, statement, cancellationToken, readOnly: false, tokenId)
                .ConfigureAwait(false);
            viewChanged = true;

            query.PublishedSchema = schema;
            query.PublishedViewName = viewName;
            query.PublishedSchemaFingerprint = plan.SchemaFingerprint;
            query.PublishedRevision = query.Revision;
            query.PublishedUtc = clock.GetUtcNow();
            query.UpdatedByTokenId = tokenId;

            // Once DDL commits, finish the matching metadata transaction even if the HTTP request
            // disconnects. A failed commit is reconciled below before the conflict is returned.
            await SaveAsync(
                    "The view changed but its publication metadata could not be finalised.",
                    CancellationToken.None)
                .ConfigureAwait(false);
            await transaction.CommitAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception workflowFailure)
        {
            try
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception rollbackFailure)
            {
                throw new SavedQueryConflictException(
                    "Publication failed and its control-plane transaction could not be rolled back. " +
                    $"Inspect '{schema}.{viewName}' before retrying.",
                    new AggregateException(workflowFailure, rollbackFailure));
            }

            if (viewChanged)
            {
                await ReconcileFailedPublishAsync(
                        tenantSlug,
                        catalogName,
                        query.Id,
                        schema,
                        viewName,
                        target,
                        tokenId,
                        workflowFailure)
                    .ConfigureAwait(false);
            }

            throw;
        }

        return query;
    }

    /// <summary>Drops the published view and returns the definition to draft-only state.</summary>
    public async Task<SavedQuery> UnpublishAsync(
        string tenantSlug,
        string catalogName,
        int id,
        int expectedRevision,
        int? tokenId,
        CancellationToken cancellationToken)
    {
        var query = await FindAsync(tenantSlug, catalogName, id, tracking: true, cancellationToken)
            .ConfigureAwait(false)
            ?? throw NotFound(tenantSlug, catalogName, id);
        EnsureRevision(query, expectedRevision);

        if (query.PublishedSchema is null || query.PublishedViewName is null)
        {
            throw new SavedQueryConflictException($"Saved query '{query.Name}' is not published.");
        }

        var target =
            $"{SqlIdentifier.QuoteName(query.PublishedSchema)}.{SqlIdentifier.QuoteName(query.PublishedViewName)}";

        await using var transaction = await context.Database
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        try
        {
            query.ConcurrencyVersion++;
            await SaveAsync(
                    "The saved query changed before unpublish could begin. Reload it before retrying.",
                    cancellationToken)
                .ConfigureAwait(false);

            await lakehouse
                .ExecuteAsync(
                    tenantSlug,
                    catalogName,
                    $"DROP VIEW IF EXISTS {target}",
                    cancellationToken,
                    readOnly: false,
                    tokenId)
                .ConfigureAwait(false);

            query.PublishedSchema = null;
            query.PublishedViewName = null;
            query.PublishedSchemaFingerprint = null;
            query.PublishedRevision = null;
            query.PublishedUtc = null;
            query.UpdatedByTokenId = tokenId;

            await SaveAsync(
                    "The view was removed but its publication metadata could not be finalised.",
                    CancellationToken.None)
                .ConfigureAwait(false);
            await transaction.CommitAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception workflowFailure)
        {
            try
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception rollbackFailure)
            {
                throw new SavedQueryConflictException(
                    "Unpublish failed and its control-plane transaction could not be rolled back. " +
                    $"Inspect '{query.PublishedSchema}.{query.PublishedViewName}' before retrying.",
                    new AggregateException(workflowFailure, rollbackFailure));
            }

            context.ChangeTracker.Clear();
            throw;
        }

        return query;
    }

    /// <summary>
    ///     Reconciles a view whose publication DDL succeeded but whose metadata write lost a race.
    /// </summary>
    /// <remarks>
    ///     If the winning row still records this target, it owns the view and a later republish can
    ///     finalise the newest definition. Otherwise this request created an untracked target, so it
    ///     is removed before the conflict reaches the caller.
    /// </remarks>
    private async Task ReconcileFailedPublishAsync(
        string tenantSlug,
        string catalogName,
        int id,
        string schema,
        string viewName,
        string target,
        int? tokenId,
        Exception persistenceFailure)
    {
        try
        {
            context.ChangeTracker.Clear();
            var persisted = await context.SavedQueries
                .AsNoTracking()
                .FirstOrDefaultAsync(q => q.Id == id, CancellationToken.None)
                .ConfigureAwait(false);

            if (persisted is not null
                && string.Equals(persisted.PublishedSchema, schema, StringComparison.Ordinal)
                && string.Equals(persisted.PublishedViewName, viewName, StringComparison.Ordinal))
            {
                return;
            }

            await lakehouse
                .ExecuteAsync(
                    tenantSlug,
                    catalogName,
                    $"DROP VIEW IF EXISTS {target}",
                    CancellationToken.None,
                    readOnly: false,
                    tokenId)
                .ConfigureAwait(false);
        }
        catch (Exception reconciliationFailure)
        {
            throw new SavedQueryConflictException(
                $"Publication metadata could not be saved and '{schema}.{viewName}' could not be reconciled. " +
                "Inspect the live view before retrying.",
                new AggregateException(persistenceFailure, reconciliationFailure));
        }
    }

    private async Task<LakeCatalog> ResolveCatalogAsync(
        string tenantSlug,
        string catalogName,
        CancellationToken cancellationToken)
        => await context.Catalogs
            .AsNoTracking()
            .FirstOrDefaultAsync(
                c => c.Tenant.Slug == tenantSlug && c.Name == catalogName,
                cancellationToken)
            .ConfigureAwait(false)
            ?? throw new CatalogNotFoundException(
                $"Catalog '{catalogName}' was not found for tenant '{tenantSlug}'.");

    private Task<SavedQuery?> FindAsync(
        string tenantSlug,
        string catalogName,
        int id,
        bool tracking,
        CancellationToken cancellationToken)
    {
        var source = tracking ? context.SavedQueries : context.SavedQueries.AsNoTracking();
        return source.FirstOrDefaultAsync(
            q => q.Id == id
                 && q.CatalogId != null
                 && q.Catalog!.Name == catalogName
                 && q.Tenant.Slug == tenantSlug,
            cancellationToken);
    }

    private async Task SaveAsync(string conflictMessage, CancellationToken cancellationToken)
    {
        try
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new SavedQueryConflictException(conflictMessage);
        }
        catch (DbUpdateException)
        {
            throw new SavedQueryConflictException(conflictMessage);
        }
    }

    private static void EnsureRevision(SavedQuery query, int expectedRevision)
    {
        if (expectedRevision < 1 || query.Revision != expectedRevision)
        {
            throw new SavedQueryConflictException(
                $"Saved query '{query.Name}' is at revision {query.Revision}, not {expectedRevision}. Reload it before retrying.");
        }
    }

    private async Task<SavedQueryDefinition> ValidateAsync(
        string tenantSlug,
        string catalogName,
        string name,
        string? description,
        string sql,
        string language,
        CancellationToken cancellationToken)
    {
        name = name?.Trim() ?? string.Empty;
        description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();

        if (name.Length is < 1 or > 200)
        {
            throw new SavedQueryValidationException("A saved-query name of 1-200 characters is required.");
        }

        if (description is { Length: > 1000 })
        {
            throw new SavedQueryValidationException("A saved-query description may contain at most 1000 characters.");
        }

        language = string.IsNullOrWhiteSpace(language) ? "sql" : language.Trim();
        if (language.Length > 32)
        {
            throw new SavedQueryValidationException("A query language id may contain at most 32 characters.");
        }

        if (string.IsNullOrWhiteSpace(sql) || sql.Length > MaxSourceLength)
        {
            throw new SavedQueryValidationException(
                $"Query source of 1-{MaxSourceLength:N0} characters is required.");
        }

        var plan = await PlanAsync(tenantSlug, catalogName, language, sql, cancellationToken)
            .ConfigureAwait(false);

        var statements = SqlStatementSplitter.Split(plan.Sql);
        if (statements.Count != 1)
        {
            throw new SavedQueryValidationException("A saved query must contain exactly one SQL statement.");
        }

        var normalized = statements[0];
        if (StatementVerb.Of(normalized) is not ("SELECT" or "WITH" or "VALUES"))
        {
            throw new SavedQueryValidationException(
                "A saved query must be a SELECT, WITH, or VALUES query. Data and schema changes cannot be saved.");
        }

        if (!await lakehouse
                .IsReadQueryAsync(tenantSlug, catalogName, normalized, cancellationToken)
                .ConfigureAwait(false))
        {
            throw new SavedQueryValidationException(
                "A saved query must produce rows. WITH-prefixed data changes cannot be saved.");
        }

        return new SavedQueryDefinition(
            name,
            description,
            string.Equals(language, "sql", StringComparison.Ordinal) ? normalized : sql.Trim(),
            language);
    }

    private async Task<QueryPlan> PlanAsync(
        string tenantSlug,
        string catalogName,
        string language,
        string source,
        CancellationToken cancellationToken)
    {
        try
        {
            return await planning.PlanAsync(tenantSlug, catalogName, language, source, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (ArgumentException ex)
        {
            throw new SavedQueryValidationException(ex.Message);
        }
        catch (QueryLanguageUnavailableException ex)
        {
            throw new SavedQueryValidationException(ex.Message);
        }
        catch (QuerySourceInvalidException ex)
        {
            var message = ex.Diagnostics.Count == 0
                ? ex.Message
                : string.Join(Environment.NewLine, ex.Diagnostics.Select(diagnostic => diagnostic.Message));
            throw new SavedQueryValidationException(message);
        }
        catch (QueryPlanRejectedException ex)
        {
            throw new SavedQueryValidationException(ex.Message);
        }
    }

    private static SavedQueryNotFoundException NotFound(string tenant, string catalog, int id)
        => new($"Saved query {id} was not found for '{tenant}/{catalog}'.");

    private sealed record SavedQueryDefinition(
        string Name,
        string? Description,
        string Sql,
        string Language);
}

public sealed record SavedQueryExecutionResult(QueryResult Result, QueryPlan Plan, string Language);
