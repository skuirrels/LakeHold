using Microsoft.EntityFrameworkCore;

namespace Lakehold.Engine.Execution;

/// <summary>
///     A model-less <see cref="DbContext"/> that hosts one tenant's DuckLake session.
/// </summary>
/// <remarks>
///     <para>
///         It deliberately declares no <see cref="DbSet{TEntity}"/>. The data plane serves SQL whose
///         shape is unknown until it runs, so there is nothing to model. The context exists to own
///         the provider-managed connection lifecycle — extension load, secret creation, <c>ATTACH</c>,
///         and <c>USE</c>, in that order — and to expose the provider's dynamic-query and DuckLake
///         maintenance surfaces.
///     </para>
///     <para>
///         Before provider 1.13.0 this was a hand-rolled <c>DuckDBConnection</c> wrapper, because EF
///         Core required a CLR type per result shape and had none to offer for arbitrary SQL. The
///         provider's <c>SqlQueryDynamicRawAsync</c> removed that constraint, so the data plane and
///         the control plane now share one dependency and one type-mapping implementation.
///     </para>
/// </remarks>
public sealed class LakeContext(DbContextOptions<LakeContext> options) : DbContext(options);
