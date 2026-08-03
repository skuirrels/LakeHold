# SDK compatibility

This matrix is the supported source and runtime boundary for the LakeHold v1 SDKs. CI tests the
lowest language level declared by each package and the current stable runtime listed here; a runtime
outside the table may work but is not a release claim.

| SDK | Package target | Supported runtimes | CI release runtime |
|---|---|---|---|
| Java | Java 8 bytecode | Temurin/OpenJDK 17 and 21 | 17 and 21 |
| Go | Go module language 1.18 | Go 1.18 and current stable | 1.18 and stable |
| .NET | `net8.0` | .NET 8 | 8 |
| Python | Python `>=3.9` | CPython 3.9 and 3.13 | 3.9 and 3.13 |

All four clients target `/api/v1`. Additive fields and operations are compatible. Removing an
operation, response, model, property, enum member, or making an optional input required is blocked by
the semantic OpenAPI gate and requires a new API major version.

Streaming helpers require an HTTP transport that preserves incremental response delivery. Reverse
proxies must not buffer `application/x-ndjson`; LakeHold sends `X-Accel-Buffering: no` and flushes each
record but cannot override an intermediary that ignores those signals.

Package publication status is tracked in [`../docs/PUBLIC-API.md`](../docs/PUBLIC-API.md). Source
compatibility does not imply that a package is currently present in a public registry.
