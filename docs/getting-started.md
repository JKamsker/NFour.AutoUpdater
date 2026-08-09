# Getting started

This guide builds the repository and exercises read-only or local validation paths before any
publishing or installation operation.

## Prerequisites

- .NET 10 SDK
- Git
- Docker with Compose, only for backend integration tests

Confirm the SDK and restore through the normal build:

```bash
dotnet --version
dotnet build AutoUpdater.sln
```

## Run the CLI

During development, place CLI arguments after the `--` separator:

```bash
dotnet run --project src/NFour.AutoUpdater.Cli -- --help
```

The examples below assume a published executable named `4sup`. The same arguments can be passed
through `dotnet run`.

## Validate inputs locally

Parse a repository address:

```bash
4sup parse-address "https://updates.example.test/repository/fourstory.client@live"
```

Canonicalize a variant selection:

```bash
4sup select --select=arch=x64 --select=language=de --select=ui=classic
```

Validate a portable manifest path:

```bash
4sup verify-path "bin/game.exe"
```

Validate the package layout of a build tree:

```bash
4sup slice lint <build-root>
4sup slice lint <build-root> --axis language --warnings-as-errors
```

The layout command reports `LAY001` through `LAY004`. See the
[build-tree layout specification](Initial/20-build-tree-layout.md) for the convention.

## Inspect an installation

These commands do not require authoring credentials:

```bash
4sup status "<install-root>"
4sup status "<install-root>" --json
4sup verify "<install-root>"
4sup explain "<install-root>" --path "data/ui/main.dat"
```

`verify` checks ledger-owned files against their expected content hashes. Add `--repair` only
when the installation already has enough repository and trust information to run an update.

## Plan and install

Preview a first install by adding `--dry-run` to the install command:

```bash
4sup install "<repository-address>" "<install-root>" --dry-run \
  --select arch=x64 --select language=de --json
```

Install after reviewing the plan:

```bash
4sup install "<repository-address>" "<install-root>" \
  --select arch=x64 --select language=de
```

Subsequent operations use the repository and selection pinned in the install ledger:

```bash
4sup plan "<install-root>" --select language=en
4sup update "<install-root>"
4sup switch "<install-root>" --select language=en
4sup rollback "<install-root>" --to <release-id>
```

For a self-hosted repository not signed by the compiled first-party root, the initial install
must explicitly add both options below:

```bash
--trust-on-first-use --trusted-key="<key-id>:<public-key-base64url>"
```

The verification key is public. The corresponding private signing key must never be stored in
the repository, configuration file, command line, or shell history.

## Next steps

- Configure aliases and learn the complete command surface in the [CLI reference](cli.md).
- Run backend integration tests with [development and testing](development.md).
- Read the [security model](Initial/10-security.md) before publishing or operating a repository.
