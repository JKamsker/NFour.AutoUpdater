# NFour AutoUpdater (`4sup`)

NFour AutoUpdater is a content-addressed publishing and update system for large, variant-aware
installations. The `4sup` CLI builds packages, authors signed releases, manages repositories,
and installs or repairs clients across local, HTTP, S3, and FTP-backed storage topologies.

The implementation targets .NET 10 and currently serves as the implementation baseline for the
[Initial specification](docs/Initial/README.md). Production deployments must replace the
development trust root and configure their own signing and storage identities.

## Core usage

Build the solution and run the default test suite:

```bash
dotnet build AutoUpdater.sln
dotnet test AutoUpdater.sln
```

Run the CLI directly from source:

```bash
dotnet run --project src/NFour.AutoUpdater.Cli -- --help
dotnet run --project src/NFour.AutoUpdater.Cli -- --version
```

Three commands exercise the core parsers without needing a repository:

```bash
dotnet run --project src/NFour.AutoUpdater.Cli -- parse-address "https://updates.example.test/repository/fourstory.client@live"
dotnet run --project src/NFour.AutoUpdater.Cli -- select --select=language=de --select=ui=classic
dotnet run --project src/NFour.AutoUpdater.Cli -- verify-path "bin/game.exe"
```

Validate that a build tree follows the package layout convention:

```bash
dotnet run --project src/NFour.AutoUpdater.Cli -- slice lint <build-root> --warnings-as-errors
```

After publishing `4sup`, the central client workflow is:

```bash
4sup install "<repository-address>" "<install-root>" --dry-run \
  --select arch=x64 --select language=de
4sup install "<repository-address>" "<install-root>" \
  --select arch=x64 --select language=de
4sup status "<install-root>"
4sup plan "<install-root>" --select language=en
4sup update "<install-root>"
4sup verify "<install-root>"
```

First-party repositories authenticate through the compiled trust root. A self-hosted first
install additionally requires explicit trust-on-first-use consent and a public verification key:

```bash
4sup install "<repository-address>" "<install-root>" \
  --trust-on-first-use --trusted-key="<key-id>:<public-key-base64url>"
```

Never place signing keys, API tokens, storage credentials, or connection strings in command
history or tracked configuration. Use the documented environment variables and an untracked
secret source instead.

## What is included

| Area | Projects |
|---|---|
| Domain and signed formats | `NFour.AutoUpdater.Core`, `NFour.AutoUpdater.Repository` |
| Storage | `Storage`, `Storage.Local`, `Storage.Http`, `Storage.S3`, `Storage.Ftp` |
| Publishing | `NFour.AutoUpdater.Publishing`, `Storage.Brokering` |
| Installation | `NFour.AutoUpdater.Client` |
| Operations | `NFour.AutoUpdater.Cli`, `NFour.AutoUpdater.Server`, `NFour.AutoUpdater.Gateway` |

The repository uses identity-encoded SHA-256 CAS blobs, detached Ed25519 envelopes,
deterministic variant resolution, streamed JSONL file tables, resumable verified downloads,
link-safe installation, and an atomic client ledger.

## Documentation

- [Documentation index](docs/README.md)
- [Getting started](docs/getting-started.md)
- [CLI reference](docs/cli.md)
- [Development and testing](docs/development.md)
- [Architecture](docs/Initial/03-architecture.md)
- [Repository format](docs/Initial/05-repository-format.md)
- [Security model](docs/Initial/10-security.md)
- [Normative contract](docs/Initial/18-normative-contract.md)

The maintained guides describe the current implementation. Documents under `docs/Initial`
capture the complete design, rationale, protocol contracts, and roadmap.
