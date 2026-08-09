# NFour AutoUpdater documentation

This directory has two documentation layers:

- The maintained guides describe how to build, run, test, and operate the current code.
- The [Initial specification](Initial/README.md) records the complete design, protocol contracts,
  security rationale, review disposition, and roadmap.

## Start here

| Guide | Use it for |
|---|---|
| [Getting started](getting-started.md) | Build the solution and exercise the first safe CLI workflows |
| [CLI reference](cli.md) | Current commands, address syntax, trust behavior, and exit codes |
| [Development and testing](development.md) | Local builds, integration services, repository checks, and CI |

## Design and protocol references

| Topic | Document |
|---|---|
| Scope and requirements | [01-scope-and-requirements.md](Initial/01-scope-and-requirements.md) |
| Architecture and project boundaries | [03-architecture.md](Initial/03-architecture.md) |
| Variant model and composition | [04-variant-model.md](Initial/04-variant-model.md) |
| Repository and object formats | [05-repository-format.md](Initial/05-repository-format.md) |
| Storage backends | [06-storage-backends.md](Initial/06-storage-backends.md) |
| Client engine | [07-client-engine.md](Initial/07-client-engine.md) |
| Publishing and validation | [08-publishing-and-validation.md](Initial/08-publishing-and-validation.md) |
| Management API | [09-control-plane-server.md](Initial/09-control-plane-server.md) |
| Security and trust | [10-security.md](Initial/10-security.md) |
| Signed documents | [17-signed-documents.md](Initial/17-signed-documents.md) |
| Normative identifiers and digests | [18-normative-contract.md](Initial/18-normative-contract.md) |
| Build-tree layout | [20-build-tree-layout.md](Initial/20-build-tree-layout.md) |

## Documentation policy

Examples use placeholders such as `<repository-address>`, `<build-root>`, and `<install-root>`.
They intentionally avoid machine-specific paths and credentials. Values inside angle brackets
must be replaced for a real invocation.

When implementation and an Initial design example differ, the current executable behavior and
tests are authoritative for usage. Update the maintained guide and the relevant specification
together when changing a protocol or persisted format.
