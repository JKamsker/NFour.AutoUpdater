# CLI reference

`4sup` is the command-line interface for client installation, package and release authoring,
repository maintenance, and operational inspection. This page summarizes the commands present
in the current executable; the [Initial CLI design](Initial/11-cli.md) explains the rationale.

## Invocation

```text
4sup <command> [arguments] [options]
```

Global options recognized before dispatch include:

| Option | Purpose |
|---|---|
| `--help`, `-h` | Print command overview |
| `--version` | Print the CLI version |
| `--config <path>` | Add a highest-precedence configuration file |
| `--contentRoot <path>` | Set the root used for repository-local configuration |
| `--insecure-transport` | Explicitly permit a configured plaintext FTP write transport |

## Repository addresses

The address grammar is:

```text
[<repository-or-alias>/]<product-id>[@<channel> | @release:<release-id>]
```

Examples:

```text
remote/fourstory.client@live
local/fourstory.client@release:2026.02.15-a
https://updates.example.test/repository/fourstory.client@live
s3://release-bucket/repository/fourstory.client@ptr
```

Backend selection comes from the URI scheme or a configured alias. Variant coordinates are
always repeated `--select <axis>=<value>` options.

## Client commands

| Command | Form |
|---|---|
| Plan an installed update | `4sup plan <install-root> [--select <axis>=<value>] [--json]` |
| Install | `4sup install <repository-address> <install-root> [--select <axis>=<value>]` |
| Update | `4sup update <install-root>` |
| Switch variant | `4sup switch <install-root> --select <axis>=<value>` |
| Roll back | `4sup rollback <install-root> --to <release-id>` |
| Status | `4sup status <install-root> [--json]` |
| Verify | `4sup verify <install-root> [--repair] [--rehash-cas] [--json]` |
| Explain ownership | `4sup explain <install-root> --path <virtual-path>` |
| Update daemon | `4sup daemon run [<repository-address>] <install-root> [--once] [--interval <seconds>]` |

Mutating client commands support `--dry-run`; use `install --dry-run` to preview a first install.
`plan` previews changes to an installation that already has a ledger. An install root is pinned
to one repository, and rebinding it requires an explicit `--yes` acknowledgement.

`verify --rebuild-state` adopts an existing tree only against an explicit trusted repository
target when no valid ledger exists:

```bash
4sup verify "<install-root>" --rebuild-state \
  --repository "<repository-address>" --select arch=x64
```

## Build layout and package commands

| Command | Purpose |
|---|---|
| `4sup slice lint [<build-root>] [--axis <name>] [--warnings-as-errors]` | Enforce `LAY001`–`LAY004` |
| `4sup pkg schema` | Print the slice-rules JSON Schema |
| `4sup pkg slice --rules <slice.yaml> [--from <build-root>] [--seq <sequence>]` | Slice and build local packages |
| `4sup pkg list <repository>/<package>` | List published package versions |
| `4sup pkg show <repository>/<package>@<version> [--files]` | Inspect a manifest |
| `4sup pkg publish <package>@<version> --rules <slice.yaml> [--to <target>]` | Publish through the configured target |

Brokered publishing reads its API token from `FOURSUP_API_TOKEN` when `--token` is absent. Prefer
the environment variable so credentials do not enter shell history.

## Release and channel commands

| Command group | Subcommands |
|---|---|
| `release` | `new`, `check`, `explain`, `matrix`, `publish` |
| `channel` | `show`, `promote`, `rollback`, `yank` |

Release and channel writes require signing authority. Private key material is loaded from
`FOURSUP_SIGNING_KEY_<key-id>` or `FOURSUP_SIGNING_KEYS` and must not be supplied as a command
argument.
Read the [signed-document contract](Initial/17-signed-documents.md) and
[security model](Initial/10-security.md) before using these operations.

## Repository operations

| Command | Purpose |
|---|---|
| `4sup verify-repo <repository> [--deep] [--rehash-cas]` | Validate repository structure and content |
| `4sup gc <repository> [--dry-run] [--keep-releases <count>] [--min-age <duration>]` | Collect unreachable objects |
| `4sup prune <repository> ...` | Alias for the garbage-collection workflow |
| `4sup mirror <source> <destination> [--channel <name>] [--delete]` | Synchronize repository objects |

Start garbage collection with `--dry-run`. Deletion policy and GC ownership are deployment
decisions; see [publishing and validation](Initial/08-publishing-and-validation.md).

## Configuration

```bash
4sup config show
4sup config set <key> <json-or-string-value>
```

Configuration is layered, first value wins:

1. the path supplied through `--config` or `FOURSUP_CONFIG_PATH`;
2. `4sup.json` under the content root;
3. the user application-data configuration;
4. the machine application-data configuration.

`defaultLocalRepository` synthesizes the `local` alias and `defaultServer` synthesizes the
`remote` alias. The alias name `default` is reserved.

## Development helpers

```bash
4sup parse-address <address>
4sup select --select=<axis>=<value> [--select=<axis>=<value>]
4sup verify-path <virtual-path>
```

## Exit codes

| Code | Meaning |
|---:|---|
| 0 | Success |
| 1 | Validation diagnostics |
| 2 | Invalid usage |
| 3 | Backend failure |
| 4 | Precondition or authorization failure |
| 5 | Integrity failure |
| 6 | Concurrent install conflict |
| 7 | Cancelled |
