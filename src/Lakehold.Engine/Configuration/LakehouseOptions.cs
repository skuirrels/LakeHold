namespace Lakehold.Engine.Configuration;

/// <summary>
///     Deployment-wide configuration for the DuckLake catalogs this node can attach.
/// </summary>
public sealed class LakehouseOptions
{
    public const string SectionName = "Lakehouse";

    /// <summary>
    ///     Root directory for catalog metadata files when a tenant uses local (single-node) metadata.
    /// </summary>
    public string MetadataRoot { get; set; } = "./.lakehold/catalogs";

    /// <summary>
    ///     Root URI for table data. Local path for single-node, or an object-store URI
    ///     (<c>s3://</c>, <c>gs://</c>, <c>az://</c>) for a bring-your-own-bucket deployment.
    /// </summary>
    public string DataRoot { get; set; } = "./.lakehold/data";

    /// <summary>
    ///     Root under which catalog metadata backups are written.
    /// </summary>
    /// <remarks>
    ///     Deliberately a <em>sibling</em> of <see cref="DataRoot"/> rather than a directory inside
    ///     it. DuckLake's orphan cleanup sweeps everything under the data path that the catalog does
    ///     not reference, and a backup is by definition unreferenced — verified: with the cleanup
    ///     window widened, 30 of 30 deletion candidates were backup files. Nesting backups under the
    ///     data path makes them self-deleting once they age past the retention cutoff.
    /// </remarks>
    public string BackupRoot { get; set; } = "./.lakehold/backups";

    /// <summary>
    ///     Number of backup generations to retain. The oldest are pruned after a successful backup.
    /// </summary>
    public int BackupRetainCount { get; set; } = 7;

    /// <summary>
    ///     Root under which verified eject bundles are written.
    /// </summary>
    /// <remarks>
    ///     A <em>sibling</em> of <see cref="DataRoot"/> for the same reason as <see cref="BackupRoot"/>:
    ///     an eject bundle is a fresh, reader-agnostic copy of the data, and anything DuckLake finds
    ///     under the data path that its catalog does not reference is a candidate for orphan cleanup.
    ///     A bundle nested under the data path would delete itself.
    /// </remarks>
    public string EjectRoot { get; set; } = "./.lakehold/ejects";

    /// <summary>
    ///     Optional signing key for eject manifests, resolved from configuration so it never appears
    ///     in a catalog record, an options dump, or a log.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         When set, an eject manifest carries an HMAC-SHA256 signature over its canonical form.
    ///         That makes the attestation tamper-evident: a party holding the same key can prove the
    ///         per-table row counts and per-file digests were produced by this deployment and have not
    ///         been altered since. Unset leaves the bundle unsigned but still digest-attested.
    ///     </para>
    ///     <para>
    ///         This is a secret. It is read from configuration (environment or a secret store) exactly
    ///         like an object-store credential, and Lakehold never writes it back out — not to the
    ///         manifest, not to a response, not to a log.
    ///     </para>
    /// </remarks>
    public string? EjectSigningKey { get; set; }

    /// <summary>
    ///     DuckDB extensions loaded into every compute session, in order.
    /// </summary>
    public IList<string> Extensions { get; } = ["ducklake", "httpfs", "json", "parquet"];

    /// <summary>
    ///     Memory ceiling applied per compute session. Bounds a single tenant's blast radius
    ///     on a shared node.
    /// </summary>
    public string MemoryLimit { get; set; } = "2GB";

    /// <summary>
    ///     Worker threads per compute session.
    /// </summary>
    public int Threads { get; set; } = 4;

    /// <summary>
    ///     How long an idle session is kept warm before it is evicted. Keeping sessions warm is
    ///     what makes repeat queries fast; evicting them is what keeps a node's memory bounded.
    /// </summary>
    public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>
    ///     Maximum number of simultaneously warm tenant sessions on this node.
    /// </summary>
    public int MaxWarmSessions { get; set; } = 32;

    /// <summary>
    ///     Server-side ceiling on rows materialised into a single query response. Protects the
    ///     API and browser from an unbounded <c>SELECT *</c>.
    /// </summary>
    public int MaxRowsPerResult { get; set; } = 10_000;

    /// <summary>
    ///     Wall-clock ceiling for a single statement.
    /// </summary>
    public TimeSpan StatementTimeout { get; set; } = TimeSpan.FromMinutes(2);
}
