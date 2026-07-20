# Contributing to Lakehold

Thanks for your interest in Lakehold. Contributions of code, documentation, tests, and
well-scoped bug reports are all welcome.

## Contributor License Agreement

Before your first contribution can be merged, you must agree to the
[Contributor License Agreement (CLA)](CLA.md).

The CLA confirms that you have the right to contribute your work and that you grant the
project owner a broad license to use, distribute, and — where necessary — relicense it. This
keeps the project's licensing options open (for example, offering a commercially licensed
edition alongside the open-source one) without having to track down every past contributor
for permission later. You retain copyright to your contributions.

**How to agree:** on your first pull request, the CLA check will ask you to confirm agreement
by leaving a short comment on the PR:

```
I have read the CLA and I hereby agree to the terms of the Lakehold Contributor License Agreement.
```

Your GitHub identity and that confirmation are recorded as your signature. Corporate
contributors (contributing as part of employment) should contact the maintainer to arrange a
Corporate CLA before submitting.

> Enabling the automated check: install the free
> [CLA Assistant](https://cla-assistant.io/) GitHub App (or the
> [`cla-assistant-lite` GitHub Action](https://github.com/marketplace/actions/cla-assistant-lite))
> on this repository and point it at [`CLA.md`](CLA.md). Until that is enabled, the maintainer
> records agreement manually from the PR comment above.

## Before you open a pull request

1. **Open an issue first for anything non-trivial.** A one-line bug fix can go straight to a
   PR; a new feature, a dependency change, or anything touching the architectural invariants
   in [`AGENT.md`](AGENT.md) should start as an issue so we can agree on the approach.
2. **Keep changes scoped.** One logical change per pull request.
3. **Preserve the architectural invariants** documented in [`AGENT.md`](AGENT.md) — the
   control-/data-plane split, tenant isolation by catalog attachment, streaming result caps,
   dry-run-by-default destructive maintenance, and the open-format exit path. If a change must
   alter one of these, say so explicitly and update the relevant documentation.

## Building and testing

Requirements: the .NET 10 SDK and Node.js 20 or newer.

```bash
# Backend build
dotnet build Lakehold.slnx

# Full backend test suite (integration tests skip unless their service is configured)
dotnet test Lakehold.slnx

# Frontend
npm ci --prefix web/lakehold-ui
npm run build --prefix web/lakehold-ui
```

Run at least the affected build and the relevant tests before opening a pull request, and the
full suite before it is ready to merge. For changes to tenant isolation, query streaming,
maintenance, storage, seeding, or the exit path, add focused tests rather than relying on
compilation alone. See [`AGENT.md`](AGENT.md) for the full build, run, and verification guide.

## Coding conventions

Nullable reference types, warnings-as-errors, and code-style enforcement are on centrally, so
the build will reject style violations. Match the surrounding code, keep async operations
cancellable end to end, keep API DTOs in the API layer, and avoid logging submitted data,
credentials, or secret-bearing connection details. The conventions section of
[`AGENT.md`](AGENT.md) has the details.

## Licensing of contributions

Lakehold is distributed under the [Apache License 2.0](LICENSE). Unless the CLA states
otherwise, your contributions are accepted under that license, and — per the CLA — the
project owner may also distribute them under other terms.
