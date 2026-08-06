# 4sup AutoUpdater

This repository implements the Initial 4sup specification in .NET 10. It is split into pure
domain, repository-format, storage, publishing, client, server, gateway, cross-platform client safeguards,
and CLI projects.

## Build and test

```bash
dotnet build AutoUpdater.sln
dotnet test AutoUpdater.sln
```

The in-memory and local stores are exercised without infrastructure. The S3 conformance test
uses the checked-in MinIO compose service:

```bash
docker compose up -d
FOURSUP_MINIO=1 dotnet test AutoUpdater.sln
```

MinIO is only a test dependency; production reads and writes are capability-separated through
the storage interfaces. The repository uses identity-encoded SHA-256 CAS blobs, strict JSON
validation, detached Ed25519 envelopes, deterministic variant resolution and file-set identity,
streamed JSONL file tables, resumable verified downloads, and an atomic client ledger.

The CLI is currently usable for the format-focused helpers:

```bash
dotnet run --project src/FourSaas.AutoUpdater.Cli -- parse-address 'https://host/product@live'
dotnet run --project src/FourSaas.AutoUpdater.Cli -- select --select=language=de --select=ui=classic
dotnet run --project src/FourSaas.AutoUpdater.Cli -- verify-path 'bin/game.exe'
```
