# Development and testing

## Local build

Use the solution-level commands so architecture tests and every project participate:

```bash
dotnet build AutoUpdater.sln
dotnet test AutoUpdater.sln
```

Release builds intentionally fail while the compiled development trust root is active. Acknowledge
that root only for local verification:

```bash
dotnet build AutoUpdater.sln --configuration Release \
  -p:NFourAllowDevelopmentTrustRoot=true
```

That property is not a production trust configuration. A production build must supply its own
reviewed trust-root material.

## Integration services

The default test run skips tests requiring external services. Start the checked-in development
stack when exercising all storage and persistence backends:

```bash
docker compose up -d postgres minio minio-init nginx vsftpd
```

Supply connection details from a private, untracked environment source before running tests.
The integration suites consume these variable names:

| Backend | Gate | Required connection variables |
|---|---|---|
| PostgreSQL | `FOURSUP_POSTGRES` | `FOURSUP_DATABASE` |
| MinIO/S3 | `FOURSUP_MINIO` | `FOURSUP_MINIO_URI`, `FOURSUP_MINIO_ACCESS_KEY`, `FOURSUP_MINIO_SECRET_KEY` |
| HTTP/nginx | `FOURSUP_HTTP` | `FOURSUP_HTTP_URI` |
| FTP/FTPS | `FOURSUP_FTP` | `FOURSUP_FTP_URI`, `FOURSUP_FTP_USER`, `FOURSUP_FTP_PASSWORD` |

Run the suite after setting every selected gate and its connection variables:

```bash
dotnet test AutoUpdater.sln
```

Stop the services when finished:

```bash
docker compose down
```

The Compose credentials are public test defaults. Never reuse them outside an isolated local or
ephemeral CI environment, and do not expose the development services to an untrusted network.

## Repository checks

The repository enforces maintainable C# file sizes. Run the check directly with Bash:

```bash
bash scripts/check-file-size.sh
```

Enable the tracked pre-commit hook for this clone:

```bash
git config core.hooksPath .githooks
```

The hook and CI reject undocumented hard-limit violations. Existing approved legacy warnings are
tracked in `scripts/file-size-legacy.tsv` and must not grow accidentally.

## Byte-stable fixtures

Files under `tests/vectors` and `schemas` are content-addressed or compared byte-for-byte.
`.gitattributes` marks them as binary (`-text`) so line-ending conversion cannot alter their
digests. Do not normalize those files or change their attribute policy.

Release bundles encode the exact pinned manifest bytes. If the bundle format changes, update the
release-bundle schema and regenerate affected vectors together.

## CI coverage

The hosted matrix runs:

- unit tests on Windows, Ubuntu, Apple Silicon macOS, and Intel macOS;
- PostgreSQL, MinIO, nginx, and FTP integration tests;
- the release trust-root guard;
- SBOM generation and validation;
- diff hygiene and C# file-size enforcement.

When changing cross-platform filesystem or POSIX interop, a local Windows run is not sufficient.
The two hosted macOS jobs are the execution proof for Darwin-specific behavior.

## Further reference

- [Testing strategy](Initial/12-testing.md)
- [Architecture](Initial/03-architecture.md)
- [Security model](Initial/10-security.md)
- [Review disposition](Initial/19-review-disposition.md)
