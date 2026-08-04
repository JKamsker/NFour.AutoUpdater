# 12 — Testing Strategy

## 1. What the reference teaches

The reference has **230 test methods**, and
`Apro.AutoUpdater.Lib/Repositories/Repositories/` — 47 source files, the entire product —
has **zero**. Two of its three test projects contain no tests at all (`Apro.Interop.Tests` is
one file: `global using Xunit;`). Three different manifest shapes sit in `SampleData/` and no
test reads any of them.

That inversion is why its GC silently did nothing for years, why `deleteFilesNotInPatch`
deleted nothing, and why `defaultCdn` was documented at length and ignored by both readers.

The rule adopted here: **the repository format, the resolution algorithm, and the delete-set
computation are the product.** They get tested first and hardest. Everything else is
supporting cast.

## 2. Backend conformance suite — written before any backend

`FourSaas.AutoUpdater.Storage.Tests` is **one** xUnit theory suite, parameterised over every
backend, asserting identical observable behaviour.

| Backend | Harness |
|---|---|
| InMemory | none |
| Local | temp directory |
| S3 | MinIO Testcontainer |
| FTP | vsftpd container |
| HTTP | nginx container serving a local tree read-only |

Asserted, for each:

- Idempotent `PutAsync` — writing identical content twice is a no-op.
- `PutIfAbsentAsync` under **concurrency** — N parallel writers, exactly one wins.
- Range read at offset 0, mid-object, and near EOF.
- **Resume after a fault-injected `IOException`**, with and without a validator.
- A backend that **ignores** `Range` and answers 200 is detected via
  `ReadResult.ActualStartOffset`, not silently drained.
- `HeadAsync` returns size and validator in one round trip; returns null for a missing key.
- List with and without a delimiter (where supported); absence of `IListableObjectStore` where
  not.
- Delete, including deleting a missing key.
- **A blob written and read back is byte-identical with no transport encoding applied.** This
  is the TIER-1 `Content-Encoding` hazard ([05](05-repository-format.md) §2.3); the nginx
  container is configured *with* `gzip on` in one variant specifically to prove the client
  rejects it.
- Declared `StorageCapabilities` match the implemented capability interfaces, both directions.

The in-memory backend gives most of this coverage with **zero containers**, so the fast inner
loop stays fast. This is the reference's `IFileProvider`/`VirtualFileProvider` trick,
generalised — and it was one of the genuinely good ideas in it.

Container-backed theories are gated by a `[S3Fact]`/`[FtpFact]`-style conditional attribute
(the reference's `OsOnlyFact` pattern), but — unlike the reference — **skips are reported**, not
silently swallowed. A CI run where every S3 test skipped must be visibly different from one
where they passed.

## 3. Golden repository

`FourSaas.AutoUpdater.GoldenRepo.Tests` carries a **checked-in v1 repository tree**: real
manifests, a handful of small blobs, two products, three releases, and five variant axes.

It asserts:

1. The current reader opens it and resolves every documented selection point to the expected
   `fileSetId`.
2. The current writer produces **byte-identical** output for the same inputs.
3. **A deliberately orphaned blob is collected by GC** — the direct countermeasure to
   [02](02-reference-review.md) §2.4.
4. A deliberately non-portable path (`data/aux.dat`) is rejected by `PKG012`.
5. A deliberate case-only collision is rejected by `PKG009`.
6. A schema-version bump is refused with the documented message.

This is the single most valuable test category the new system needs, and the reference has
exactly one instance of the pattern (`MigrationTests` with a checked-in `litedb_V0.db`).

## 4. Pinned-format tests

Anything baked into published artifacts gets a golden-vector test, because a silent
implementation change makes existing data unreadable:

- `xxh3_64` shard assignment — `System.IO.Hashing.XxHash3`, seed 0, UTF-8, little-endian;
  **XXH3, not XXH64** ([05](05-repository-format.md) §8.1).
- `VariantSelection.ToCanonicalString()` ordering.
- `FileSetId` triple canonicalisation.
- RFC 8785 canonicalisation of every signed document, with vectors from the RFC itself.
- `ContentHash` string round-trip.

## 5. Pure-domain tests (`Core.Tests`)

No I/O, no temp directories, no fixtures — literals only. This is where the highest-risk logic
lives, and it is fully testable because `IVariantResolver` is pure and synchronous
([03](03-architecture.md) §4.5).

Coverage targets:

- Every diagnostic code in [04](04-variant-model.md) §6 and
  [08](08-publishing-and-validation.md) §3 has at least one test that produces it and one that
  does not.
- `AxisPredicate.CanCoexistWith` across the `One`/`Many` cardinality matrix — this is the
  load-bearing method of the publish gate.
- Layer derivation, including `rankAs`, `layerOverride`, and the multi-requirement `max` case.
- Total-order stability: the same inputs in a different declaration order produce the same
  composition.
- **The delete-set table**, exhaustively — every combination of
  `(in target?, in ledger?, content equal?, policy)`. This is the code path that can destroy
  player data; it deserves a truth table, not spot checks.
- `Plan(target, current: null)` emits zero `Delete` operations. Asserted directly, because it
  is the safety net for ledger loss.
- Un-shadowing: a package ceasing to ship a path produces a `Write` from the lower layer
  ([04](04-variant-model.md) §7.3).

## 6. Fault injection

Promote the reference's best test — `HttpResiliencyTest.FailingMemoryStream`, a stream that
throws `IOException` every Nth read and records the position it was recreated at — to a
first-class utility: `FaultyObjectStore`, a decorator over **any** `IReadableObjectStore`.

Injectable faults: read failure at byte N, truncated response, wrong content (same length),
`Range` ignored, validator changed mid-transfer (→ must restart from zero, not splice),
connection cap exceeded, slow trickle.

A multi-backend resumable downloader lives or dies on this, and it tests deterministically with
no network and no flakiness.

## 7. Crash-safety tests

The apply protocol's recovery table ([07](07-client-engine.md) §3.5) is executed, not asserted
in prose. A test harness aborts the apply at each defined point and asserts the documented
recovery:

| Abort point | Assertion |
|---|---|
| During fetch | staged blobs are reused by hash on re-run |
| During materialise | re-run converges; no duplicate writes |
| During delete | re-run is a no-op on already-deleted files |
| Between the two writes that would exist if the ledger were two files | **cannot happen** — asserted structurally by there being one commit rename |
| After commit, before `plan.json` removal | re-run is a no-op |

Plus: a `Preserve` file the user has edited survives an update, a variant switch, and a
rollback.

## 8. Property-based tests

Where the invariant is easier to state than to enumerate (FsCheck or equivalent):

- Composition is order-independent given the same total order.
- `compose(A) − compose(A) = ∅` — re-applying the same release is a no-op.
- `apply(switch(A→B)); apply(switch(B→A))` returns to the exact `fileSetId` of A.
- Any path accepted by `VirtualPath.TryCreate` round-trips through `Path.Combine` under a
  plausible install root without escaping it.
- Blob dedup: a file set containing N copies of identical content yields exactly one
  `BlobsToFetch` entry.

## 9. Characterisation tests for third-party behaviour

Behaviour we depend on but do not control, in a **separate, clearly labelled, opt-in project**
(the reference mixed these into its main unit assembly):

- S3 multipart-upload and conditional-write semantics, including MinIO/R2/B2 divergence.
- FTP `REST` resume, `FEAT` detection, and `RNTO` overwrite behaviour per server.
- nginx and CDN `Range` + `ETag` behaviour, especially with `gzip on`.

These are slow, network-dependent, and legitimately allowed to fail when an endpoint is
unavailable — which is exactly why they must not gate the main suite.

## 10. Coverage and gates

| Gate | Threshold |
|---|---|
| `Core` line + branch coverage | **90%** — it is pure and there is no excuse |
| `Repository`, `Publishing`, `Client` | 80% (the global rule) |
| Storage backends | conformance suite green on **all five**, not a coverage number |
| Architecture tests | zero violations ([03](03-architecture.md) §3) |
| Secret scan | zero findings |

CI runs the fast suite (in-memory + local + pure) on every push, and the container-backed
conformance suite plus golden-repo tests on every PR to `dev`.

Per `rules/git.md` as applied in the sibling repo: the integration base branch is `dev`, and
the tracker is Gitea, not GitHub.

## 11. What is deliberately not tested by unit tests

- Spectre console rendering — covered by `--json` output tests instead.
- The GUI/IPC transport is covered by running the entire RPC stack **in-process**
  ([07](07-client-engine.md) §8), which makes it ordinary test surface rather than an
  integration problem.
- Real S3/CDN cost and latency behaviour — that is a load test, tracked separately.
