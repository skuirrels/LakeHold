// UseAutoIncrement is native-DuckDB-only surface — DuckLake has no sequences and no RETURNING, and
// rejects it at model validation. It therefore lives in the provider's own namespace rather than
// Microsoft.EntityFrameworkCore, and this using is what marks the dependency on native storage.
using DuckDB.EFCoreProvider.Extensions;
using Microsoft.EntityFrameworkCore;
using Lakehold.ControlPlane.Model;

namespace Lakehold.ControlPlane.Data;

/// <summary>
///     Control-plane persistence: tenants, catalogs, saved queries, and query history.
/// </summary>
/// <remarks>
///     <para>
///         Backed by a native DuckDB file rather than a DuckLake catalog. That is deliberate. The
///         provider's DuckLake profile rejects sequences, store-generated values, and EF migrations,
///         because DuckLake has no <c>RETURNING</c> and no enforced uniqueness — all of which this
///         model depends on. Native DuckDB supports every one of them.
///     </para>
///     <para>
///         The trade-off is that DuckDB is single-writer, so this context does not scale to a
///         multi-node control plane. For an HA deployment, point this at PostgreSQL instead; the
///         model is provider-agnostic and only the registration in <c>Program.cs</c> changes.
///     </para>
/// </remarks>
public sealed class ControlPlaneContext(DbContextOptions<ControlPlaneContext> options) : DbContext(options)
{
    public DbSet<Tenant> Tenants => Set<Tenant>();

    public DbSet<LakeCatalog> Catalogs => Set<LakeCatalog>();

    public DbSet<SavedQuery> SavedQueries => Set<SavedQuery>();

    public DbSet<QueryRun> QueryRuns => Set<QueryRun>();

    public DbSet<ChangeSubscription> ChangeSubscriptions => Set<ChangeSubscription>();

    public DbSet<ApiToken> ApiTokens => Set<ApiToken>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.Entity<Tenant>(entity =>
        {
            entity.HasKey(t => t.Id);
            entity.Property(t => t.Id).UseAutoIncrement();
            entity.Property(t => t.Slug).HasMaxLength(64);
            entity.Property(t => t.DisplayName).HasMaxLength(200);
            entity.HasIndex(t => t.Slug).IsUnique();
        });

        modelBuilder.Entity<LakeCatalog>(entity =>
        {
            entity.HasKey(c => c.Id);
            entity.Property(c => c.Id).UseAutoIncrement();
            entity.Property(c => c.Name).HasMaxLength(63);
            entity.Property(c => c.MetadataSource).HasMaxLength(1024);
            entity.Property(c => c.DataPath).HasMaxLength(1024);
            entity.Property(c => c.StorageSecretName).HasMaxLength(63);

            // Catalog names are attached identifiers, so they must be unique per tenant to keep
            // the tenant -> attached-catalog mapping unambiguous.
            entity.HasIndex(c => new { c.TenantId, c.Name }).IsUnique();

            entity.HasOne(c => c.Tenant)
                .WithMany(t => t.Catalogs)
                .HasForeignKey(c => c.TenantId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<SavedQuery>(entity =>
        {
            entity.HasKey(q => q.Id);
            entity.Property(q => q.Id).UseAutoIncrement();
            entity.Property(q => q.Name).HasMaxLength(200);
            entity.Property(q => q.Description).HasMaxLength(1000);
            entity.HasIndex(q => new { q.TenantId, q.Name }).IsUnique();

            entity.HasOne(q => q.Tenant)
                .WithMany()
                .HasForeignKey(q => q.TenantId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ChangeSubscription>(entity =>
        {
            entity.HasKey(s => s.Id);
            entity.Property(s => s.Id).UseAutoIncrement();
            entity.Property(s => s.CatalogName).HasMaxLength(63);
            entity.Property(s => s.SchemaName).HasMaxLength(63);
            entity.Property(s => s.TableName).HasMaxLength(63);
            entity.Property(s => s.EndpointUrl).HasMaxLength(2048);

            // The secret column is sized, not encrypted: the control-plane file carries catalog
            // records of equal sensitivity, and at-rest protection is the deployment's disk story.
            // What the code guarantees is narrower and absolute — it never leaves via API or log.
            entity.Property(s => s.Secret).HasMaxLength(256);
            entity.Property(s => s.LastError).HasMaxLength(4000);

            // The dispatcher sweeps active subscriptions per catalog on every poll.
            entity.HasIndex(s => new { s.TenantId, s.CatalogName });

            entity.HasOne(s => s.Tenant)
                .WithMany()
                .HasForeignKey(s => s.TenantId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<QueryRun>(entity =>
        {
            entity.HasKey(r => r.Id);
            entity.Property(r => r.Id).UseAutoIncrement();
            entity.Property(r => r.CatalogName).HasMaxLength(63);
            entity.Property(r => r.Error).HasMaxLength(4000);

            // The history panel always reads newest-first within a tenant.
            entity.HasIndex(r => new { r.TenantId, r.StartedUtc });

            entity.HasOne(r => r.Tenant)
                .WithMany()
                .HasForeignKey(r => r.TenantId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ApiToken>(entity =>
        {
            entity.HasKey(t => t.Id);
            entity.Property(t => t.Id).UseAutoIncrement();
            entity.Property(t => t.Name).HasMaxLength(200);
            entity.Property(t => t.Prefix).HasMaxLength(80);
            entity.Property(t => t.SecretHash).HasMaxLength(64);
            entity.Property(t => t.CatalogName).HasMaxLength(63);

            // Stored as its underlying int, so the column default of 0 for a row that predates the
            // column reads back as Owner — the capability every token had before roles existed.
            entity.Property(t => t.Role).HasConversion<int>();

            // The prefix narrows a lookup to one tenant's candidate tokens before the secret is
            // verified; the hash is the per-token key, unique because a repeated secret would be a
            // generator failure worth rejecting rather than storing.
            entity.HasIndex(t => t.Prefix);
            entity.HasIndex(t => t.SecretHash).IsUnique();

            // A tenant token is removed with its tenant; an instance token has a null tenant and is
            // untouched by any tenant deletion.
            entity.HasOne(t => t.Tenant)
                .WithMany()
                .HasForeignKey(t => t.TenantId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
