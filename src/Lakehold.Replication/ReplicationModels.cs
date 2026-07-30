using System.Text.Json;

namespace Lakehold.Replication;

public enum ReplicaTableMode
{
    Keyed,
    AppendOnly,
}

public sealed record ReplicaColumn(string Name, string DataType, bool IsNullable);

public sealed record ReplicaTableDefinition(
    string Schema,
    string Table,
    IReadOnlyList<ReplicaColumn> Columns,
    ReplicaTableMode Mode,
    IReadOnlyList<string> KeyColumns);

public sealed record ReplicaChange(
    long RowId,
    string ChangeType,
    IReadOnlyDictionary<string, JsonElement> Row);

public sealed record ReplicaTableChanges(
    ReplicaTableDefinition Table,
    IReadOnlyList<ReplicaChange> Changes);

public sealed record ReplicaCheckpoint(
    string SourceId,
    string Tenant,
    string Catalog,
    long BootstrapSnapshot,
    long LastAppliedSnapshot,
    string SchemaFingerprint,
    DateTimeOffset UpdatedUtc);
