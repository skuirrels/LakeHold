# LakeHold public API contract

`lakehold-v1.json` is the reviewed OpenAPI 3.1 contract emitted by the production API. It is the
source contract for the Java, Go, .NET, and Python SDKs; it is not maintained by hand.

To refresh it, run LakeHold locally and execute:

```bash
./scripts/export-openapi.sh http://127.0.0.1:5200/api/v1/openapi.json
```

The export fails if the document contains a non-v1 route, lacks unique operation identifiers,
advertises a deployment-specific server, weakens Bearer requirements, exposes idempotency on
one-time token issuance, publishes inconsistent idempotency-key syntax or cursor bounds, or loses
the typed problem-error contract. Review the resulting diff as a public compatibility change before
regenerating SDKs.

`scripts/generate-sdks.sh` uses the reviewed document and a digest-pinned OpenAPI Generator 7.14.0
image. CI regenerates the clients and rejects any unexplained drift between the contract and the
committed Java, Go, .NET, or Python source.
