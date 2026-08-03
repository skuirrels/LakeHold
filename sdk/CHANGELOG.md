# SDK changelog

## Unreleased

- Add incremental NDJSON query and CDC helpers for Java, Go, .NET, and Python.
- Add source-native CDC cursor traversal and stable snapshot keyset models generated from `/api/v1`.
- Add shared streaming fixtures and an authenticated released-server conformance workflow.
- Make released-server conformance self-contained over an immutable published API image, with
  tenant-isolation and streaming-cancellation checks in every language.
- Add semantic OpenAPI compatibility enforcement, runtime support matrices, examples, and gated
  signed-package release/provenance automation.

No public package publication is asserted by this entry. A version moves out of `Unreleased` only
after registry indexing and clean-install evidence exists for all four ecosystems.
