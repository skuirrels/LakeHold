# LakeHold SDKs

The SDKs in this directory are generated from the reviewed
[`openapi/lakehold-v1.json`](../openapi/lakehold-v1.json) contract with OpenAPI Generator 7.14.0.
They expose every documented `/api/v1` operation and share the same authentication, pagination,
idempotency, error, and durable-operation contract.

| Language | Directory | Package identity | Build command |
|---|---|---|---|
| Java | `java` | `io.lakehold:lakehold-sdk` | `mvn test` |
| Go | `go` | `lakehold` | `go test ./...` |
| .NET | `dotnet` | `Lakehold.Sdk` | `dotnet test` |
| Python | `python` | `lakehold-sdk` | `python -m pytest` |

Regenerate all libraries with `./scripts/generate-sdks.sh`. Generated source is reviewed and built
in CI; do not edit it directly. Add handwritten conveniences only outside generator-owned files.

Each client provides typed models and low-level operations for every operation in the frozen v1
contract. A small handwritten runtime layer adds the shared supported behavior that generators do
not provide consistently:

- SDK user-agent and explicit request timeout configuration (`LakeholdApiClient` supplies Python's
  default while still allowing a per-call override);
- typed RFC 9457 failures with LakeHold code, request id, detail, and `Retry-After`;
- bounded retries for calls the application has explicitly identified as retry-safe;
- caller-generated and validated idempotency keys;
- lazy cursor traversal and durable-operation polling;
- incremental NDJSON query and finite CDC streams with terminal-completion validation;
- transport-appropriate cancellation, request timeouts, and access to `X-Request-Id`; and
- tolerance for additive response fields.

Use `io.lakehold.sdk.runtime.LakeholdRuntime`, `runtime.go`,
`Lakehold.Sdk.Runtime.LakeholdRuntime`, or `lakehold_sdk.runtime` respectively. The runtime accepts
callable page/operation loaders, keeping it independent of generated method names while the frozen
OpenAPI document remains the only wire-model source. Retry helpers never retry unless the caller
passes `retrySafe=true` (or its language equivalent); use an idempotency key for a retryable
mutation.

Token issuance deliberately has no `Idempotency-Key` parameter: a replayable response would require
LakeHold to retain the one-time plaintext credential. Streaming imports are also excluded from
response idempotency because their bodies exceed the bounded replay contract.

Go and .NET propagate request cancellation. Java generated async calls are cancellable and runtime
waits are interruptible. Python uses a synchronous urllib3 transport: cancellation is cooperative
between retries and operation polls, while the configured request timeout bounds an in-flight call.

The shared language-neutral fixtures under [`conformance`](conformance/)
drives every language suite. It currently proves authentication, token redaction, typed problems,
bounded retry and `Retry-After`, cursor traversal, idempotency validation, operation polling,
transport-appropriate cancellation, correlation identifiers, user agents, timeouts, and
additive-field compatibility, plus incremental query/CDC stream framing.

The dedicated `sdk-conformance.yml` workflow pulls an immutable released LakeHold API image and
provisions a tenant/catalog-scoped reader independently for each language. It verifies authenticated
query streaming, credential-bound tenant/catalog routing, and transport-appropriate cancellation. It
does not exercise the Phase 3 arbitrary-SQL process/filesystem/network containment boundary. Exhaustive
coverage of every stable public error code remains open.
See [`REFERENCE.md`](REFERENCE.md), [`COMPATIBILITY.md`](COMPATIBILITY.md), and
[`examples`](examples/) for the supported runtime surface.

These packages are source-complete but are **not published** to Maven Central, a Go module proxy,
NuGet, or PyPI by this change. `sdk-release.yml` implements fail-closed signing, provenance,
publication, registry-indexing, and clean-install gates; it still requires repository/namespace
ownership, protected credentials, an approved version, and an explicit publication run. The 0.1.0
non-publishing dry run has verified builds, package assembly, build provenance, and uploaded evidence;
it did not sign or publish artifacts.
