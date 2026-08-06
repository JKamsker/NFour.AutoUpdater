# 11 — CLI (`4sup`)

Built on `Spectre.Console.Cli` (already pinned in the sibling repo) over a generic host.

## 1. Address grammar

```
[<repo-or-alias>/]<productId>[@<channel> | @release:<id>]
```

Examples, all valid:

```
remote/fourstory.client@live
local/fourstory.client@release:2026.02.15-a
https://patch.4story.com/live/fourstory.client@live
C:\mirror\fourstory.client@live
s3://4story-patches/live/fourstory.client@ptr
ftp://mirror.example.net/4story/fourstory.client@live
```

Two rules the reference got wrong and this fixes:

- **Backend selection comes from the URI scheme or the alias table only** — never a DNS probe.
  `PatchRepositoryFactory.FixupPathInfo` (`:289-316`) called `Dns.GetHostEntryAsync` to decide
  whether a bare token was a domain, which made URI parsing network-dependent and
  non-deterministic offline.
- **Variant coordinates are never positional.** Always `--select axis=value[,value]`. Popping
  segments from the right breaks the moment a coordinate becomes optional, and a five-axis
  product has no sane positional encoding.

### 1.1 Aliases and config

Layered, first-wins:

```
./4sup.json  →  %APPDATA%\4Story\4sup\config.json  →  %PROGRAMDATA%\4Story\4sup\config.json
```

with `[JsonExtensionData]` so a file written by a newer version round-trips through an older
binary, and synthesised `local` / `remote` aliases from `defaultLocalRepository` /
`defaultServer` unless the user has claimed those names. `default` is reserved.

Per-verb defaults: reads default to `local`, publishes default to `remote`. This is what makes
`4sup pkg publish fourstory.client.lang.de@1.4.7` work with no `--to`.

Writes go into the layer that already defines the key, else the outermost layer — so
`4sup config set` needs no `--scope` flag.

## 2. Authoring

```bash
# Slice a build tree into packages
4sup pkg slice --rules packaging/slice.yaml --from D:\build\4story --seq 10407

# Publish changed packages (idempotent; skips blobs that already exist)
4sup pkg publish fourstory.client.lang.de@1.4.7 --to remote

# Inspect
4sup pkg list   remote/fourstory.client.core
4sup pkg show   remote/fourstory.client.core@1.4.7 --files
```

## 3. Release

```bash
# Build a new lock from the previous one, bumping specific packages
4sup release new fourstory.client --as 2026.02.15-a --seq 418 --from 2026.02.14-a \
                 --bump fourstory.client.lang.de@1.4.7 \
                 --bump fourstory.client.ui.modern@2.0.1

# THE CI GATE — exit 1 on any Error diagnostic
4sup release check fourstory.client@2026.02.15-a --strict --against 2026.02.14-a \
                   --drift-tolerance 2%

# Show exactly what one selection resolves to, and why
4sup release explain fourstory.client@2026.02.15-a \
                     --select ui=modern,language=de,arch=x64

# The full coverage matrix
4sup release matrix fourstory.client@2026.02.15-a --json

# Sign and publish (separate identity holds the key — see 10-security.md §5)
4sup release publish fourstory.client@2026.02.15-a --sign 4s-2026
```

## 4. Promote and roll back

Both mutate a signed control document, so **both require a signer** — the API cannot author one
([17](17-signed-documents.md) §4.4). The first draft named a signer only on `release publish`,
which made the channel workflow unexecutable as written (review **H12**).

```bash
4sup channel promote  fourstory.client live --release 2026.02.15-a --sign 4s-2026
4sup channel rollback fourstory.client live --sign 4s-2026     # new pointer, older release
4sup channel show     fourstory.client live
4sup channel yank     fourstory.client --release 2026.02.15-a --effect block-install --sign 4s-2026

# Self-hosted repositories require an explicit trust-on-first-use consent.
4sup install https://patch.example/fourstory.client@live /opt/fourstory \
             --trusted-key=host-root:<base64url> --trust-on-first-use
```

Each constructs the payload locally at `channelSequence + 1`, signs it, and submits the envelope
for verify-and-place. `promote` additionally requires `IConditionalWriteStore`; on FTP or B2 it
demands `--force-unsafe-promote` and warns ([08](08-publishing-and-validation.md) §4.1).

## 5. Maintenance

```bash
4sup gc <repo> --keep-releases 20 --min-age 24h --include-staging --dry-run
4sup mirror <src> <dst> --channel live --delete    # --delete propagates GC to the mirror
4sup verify-repo <repo> --deep                     # digests, shards, AND live cache-header checks
4sup prune <repo> --keep-releases 20
```

`--dry-run` on `gc` emits the **exact deletion list**, not a count. GC output is auditable
because the reference's silently-did-nothing GC ([02](02-reference-review.md) §2.4) was
undetectable precisely because nothing observed it.

## 6. Client

Each `--select` carries **exactly one axis**. Multiple values for a `Many` axis are repeated
flags, never comma-separated — a comma cannot separate both axes and values without ambiguity
(review **H12**).

```bash
4sup install "remote/fourstory.client@live" "C:\Games\4Story" \
             --select arch=x64 --select ui=classic \
             --select language=de --select language=en

4sup update  "C:\Games\4Story"
4sup switch  "C:\Games\4Story" --select ui=modern
4sup rollback "C:\Games\4Story" --to 2026.02.14-a  # local rollback; target is explicit

# Dry run: writes, deletes, bytes, peak space per volume. Touches nothing.
4sup plan    "C:\Games\4Story" --select ui=modern --json

# Support tools
4sup status  "C:\Games\4Story" --json
4sup explain "C:\Games\4Story" --path data/ui/main.dat
4sup verify  "C:\Games\4Story" --repair --rebuild-state
```

### 6.1 `explain` is a first-class verb

```
$ 4sup explain "C:\Games\4Story" --path data/ui/main.dat

data/ui/main.dat
  owner   fourstory.client.ui.classic@1.4.0   layer 20000  disc 0   ◀ WINNER
          reason: highest layer among candidates; override of core declared on requirement

  candidates
    fourstory.client.core@1.4.7               layer     0  disc -1  shadowed
    fourstory.client.ui.classic@1.4.0         layer 20000  disc  0  selected
    fourstory.client.ui.modern@2.0.1          layer 20000  disc  1  NOT selected (ui=classic)

  content sha256:5501cc31…   88101 bytes   on disk: matches
```

This is the support-desk tool. "Why does this player have the wrong UI file?" must be
answerable in one command, not by reading manifests. Its release-side twin,
`release explain`, answers the same question before shipping.

## 7. Exit codes

One stable code per condition category, no overlaps. The first draft had `3` and `75` both
meaning "retryable", which is two codes for one condition (review **H12**).

| Code | Meaning | Retryable |
|---|---|---|
| 0 | Success | — |
| 1 | Validation failed — one or more `Error` diagnostics | no |
| 2 | Usage error | no |
| 3 | Network or backend failure | **yes** |
| 4 | Precondition failed — free space, locked file, `minimumClientVersion`, unsafe root | no |
| 5 | Signature, digest or integrity verification failed | **never** |
| 6 | Concurrency — lock held, or a conditional write lost its race | **yes** |
| 7 | Cancelled by the user | no |

Distinguishing 3 from 5 matters most: a CI pipeline retries a network failure and must **never**
retry an integrity failure — a retry loop against a poisoned mirror is how a transient
compromise becomes a sustained one.

## 8. Output

- Human output through Spectre with progress bars and colour; `--json` on every read verb emits
  a stable machine-readable document for scripting.
- `--quiet` suppresses everything but errors; `--verbose` adds per-file trace.
- Diagnostics always render as `CODE severity subject: message`, so they are greppable and map
  1:1 to the tables in [04](04-variant-model.md) §6 and [08](08-publishing-and-validation.md) §3.
- Colour is disabled automatically when stdout is redirected or `NO_COLOR` is set.

## 9. Hosting notes

- Long-running verbs (`install`, `update`, `daemon run`) start the background pipeline
  explicitly; short verbs do not. This prevents the common bug where `4sup --help` also spins up
  every `BackgroundService`. The reference solved this with "controllable" hosted services that
  do not auto-start, and it is worth copying.
- Global flags that must influence host construction itself (`--config`, `--contentRoot`) are
  read by a pre-pass parser before the host is built.
- The loader/self-update path invokes the tool **out of process** with typed argument builders,
  so there is zero assembly-load coupling and zero version skew between a host and the tool it
  drives ([07](07-client-engine.md) §7).
