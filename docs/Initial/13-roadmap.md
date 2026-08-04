# 13 — Roadmap

> **Revised.** The management API is no longer a late optional phase. It is the authoring
> authority, so it lands with publishing (Phase 3) rather than after the client. What stays late
> is *telemetry and the content gateway*, which are genuinely optional. Device targeting was
> removed entirely — see [09](09-control-plane-server.md) §2.3.

## Sequencing principle

Build the **correctness floor first**, then widen. Concretely: the pure resolution algorithm
before any I/O; the local backend before any remote one; a working install-and-switch against a
static local repository before signing, before the server, before a GUI.

**The trust boundary comes first, not last.** An earlier ordering put signing in Phase 6, after
real publishing and client work — which meant the first real repository would be built against a
format whose signed-envelope bytes, digest domains and key lifecycle were still unfixed, and
every artifact would need reissuing (review **H11**). Signed-envelope format, digest-domain
registry, identifier grammar, content-identity rules and rollback semantics are now **Phase 0/1
exit criteria**, and the first end-to-end repository is built from **signed golden fixtures**
before any production publishing path exists.

Small-file bundling remains a format decision that cannot be retrofitted and must be settled
before Phase 3 ([14](14-open-questions.md) Q7).

---

## Phase 0 — Decisions and scaffold

**Goal:** nothing is built on an assumption that a human should have made.

- Resolve the blocking open questions: Q1 (engine glob behaviour), Q2 (build-tree
  partitioning), Q3 (UI package shape), Q7 (bundling), Q10 (naming), Q14 (special servers).
- **Freeze the signed envelope** ([17](17-signed-documents.md)): payload framing, domain
  separation, `channelSequence` vs `releaseSequence`, mutability classes.
- **Freeze the normative contract** ([18](18-normative-contract.md)): identifier grammar, digest
  domain registry, `FileSetId` framing, JSON rules, identity-only content rules.
- **Publish the JSON Schemas and golden vectors** — generated from the C# models, with valid and
  invalid fixtures for each document.
- Solution scaffold, `Directory.Build.props`, `Directory.Packages.props`, `.editorconfig`
  inherited from the sibling repo conventions.
- CI skeleton on Gitea: build, test, secret scan, architecture tests, **schema validation**.
- Architecture tests **first**, so the layering in [03](03-architecture.md) §3 is enforced from
  commit one rather than retrofitted.

**Exit:** `dotnet build` and `dotnet test` green on an empty but correctly layered solution, and
the signed-envelope golden vectors verify against a throwaway reference implementation. **No
byte-level format decision remains open after this phase.**

---

## Phase 1 — Core domain (pure, no I/O)

**Goal:** the variant model is provably correct before anything can read a byte.

- `ContentHash`, `BlobLocator`, `PackageId`, `PackageVersion`, `VirtualPath` — with the full
  Windows-hostile-path rule set.
- Axes, `VariantSelection`, `AxisPredicate` including `CanCoexistWith`.
- `PackageManifest`, `ReleaseLock`, `ReleaseBundle`, `ChannelPointer` records + JSON contracts.
- `IVariantResolver` — all 13 resolution steps that do not require I/O (1, 5–8).
- `IFileSetComposer` composition rules and `FileSetId`.
- `IInstallPlanner` — the complete delete-set truth table.
- Diagnostics with stable codes.

**Exit:** `Core.Tests` at 90% line+branch. The delete-set truth table is exhaustive. Worked
examples [04](04-variant-model.md) §7.1–7.4 pass as tests. **No project reference, no
`System.IO` beyond `Stream`.**

---

## Phase 2 — Storage port + Local backend

**Goal:** one narrow port, proven against two implementations before a third exists.

- `IReadableObjectStore` and the capability interfaces.
- `RepositoryLayout` as the single key authority.
- `Storage.Memory` and `Storage.Local`.
- The **conformance suite itself**, written against these two.
- `FaultyObjectStore` fault-injection decorator.
- `IPackageRepository` read path over the port; capability intersection.
- The `Func<long, Task<Stream>>` resumable-stream decorator, correctly implemented.

**Exit:** conformance suite green on InMemory + Local. Resume-after-fault passes with and
without a validator.

---

## Phase 3 — Management API, brokered publishing, first real repository

**Goal:** a repository exists that a client can read, produced through the real publish path.

- **Management API skeleton**: database schema, deny-by-default authorization with the
  startup coverage check, publisher and operator identities.
- **Brokered upload pipeline** ([16](16-publish-protocol.md)): batched existence probe, publish
  sessions with quotas, per-object grants, direct-to-storage upload, seal.
- **Mandatory server-side verification** before promotion, and the `verifiedAt` placement
  ledger. `blobs/query` reports present only for verified placements.
- **Server-side promotion**: `CopyObject` with `MetadataDirective=REPLACE`, `UploadPartCopy`
  above 5 GiB, local rename.
- **Verify-and-place** for signed documents; the API must have no signing primitive at all.
- CAS repair path: `POST /blobs/{hash}/quarantine`.
- Static projection published from the database.
- Hashing pipeline with the `(path, size, mtime) → hash` cache.
- `slice.yaml` + slicer with `unmatched: error`, `rewrite`, and `policy`.
- File-table sharding — `xxh3_64` pinned and golden-vectored.
- Identity-only CAS; no encoding decision in the hash pipeline.
- `release new`, `release check` (**both tiers**), `release publish`, `coverage.json`.
- `channel promote` / `rollback` with conditional write.
- Signature **fields and canonicalisation** wired in, even if the first key is a dev key.
- The golden-repository fixture, generated by this pipeline and then frozen.

**Exit:** `4sup release check --strict` produces every `PKG*` code on a purpose-built bad
release and none on a good one. Golden repo committed. **A grant for hash H used to upload
content X is rejected at verification and never reaches the CAS** — the single most important
test in this phase.

---

## Phase 4 — Client engine

**Goal:** install, update, switch and roll back correctly against a local repository.

- Fetch with dedup, per-blob verification, mirror failover.
- The staging barrier — nothing touches the tree until every blob is verified.
- Hardlink-first materialisation; the local CAS as a specified subsystem.
- Single durable commit; the crash-recovery table executed as tests.
- Delete + directory pruning; `Preserve`/`Orphan` handling.
- Locked-file policy ladder; `Client.Windows` service/process tickets.
- Free space bucketed by volume; the same-volume staging assertion.
- `verify --repair --rebuild-state`.
- Progress and cancellation contracts.
- `4sup install / update / switch / plan / status / explain`.

**Exit:** a 4Story client installs, switches `ui=classic → modern`, and rolls back — with an
edited `Preserve` config surviving all three. Every crash-recovery row is a passing test.

---

## Phase 5 — Remote backends

**Goal:** the three remaining backends, each proven by the same suite.

- `Storage.S3` — conditional writes, multipart, server-side copy (including `UploadPartCopy`
  above 5 GiB), presigned PUT.
- `Storage.Http` — read-only, `AutomaticDecompression = None`, `Content-Encoding` as a hard
  error.
- `Storage.Ftp` — `FEAT`-gated capability detection, `REST` resume, `RNTO` publish, connection
  cap respected via `RecommendedParallelism`, single-publisher declared.
- `verify-repo --deep` with **live** cache-header assertions (`PKG011`).
- `mirror` with `--delete`.
- GC with quarantine and an auditable manifest.

**Exit:** conformance suite green on all five backends including the `gzip on` nginx variant.
An orphan blob is collected in the golden repo. A full install completes over each backend.

---

## Phase 6 — Security hardening

**Goal:** the system is a trustworthy code-delivery channel.

- Ed25519 signing on the channel pointer and release lock.
- Trust root pinned in the binary + TOFU into the ledger.
- Key rotation windows and a monotonic revocation list.
- Freshness checks against `releaseSequence` and `updatedAt`.
- Key custody: `release publish --sign` as a separately gated identity.
- Self-update of `4sup` with the symlink fallback the reference lacked.

**Exit:** a tampered blob, a tampered manifest, a tampered bundle inline, a resigned-with-wrong-key
pointer, and a replayed old release are each rejected with a distinct exit code.

---

## Phase 7 — Telemetry and the content gateway

**Goal:** operational visibility and a deployment option, neither load-bearing.

> **There is no device targeting phase.** Every device gets the latest version of the channel it
> follows; rollout is `promote ptr → live`, which is already delivered by Phase 3. No device
> registry, no per-device assignment, no update queue, no rollout percentages. See
> [09](09-control-plane-server.md) §2.3.

- Anonymous, rate-limited telemetry ingest with retention. **Diagnostic only** — never an
  automatic gate, because anonymous clients cannot be trusted to report their own outcomes.
- **Content gateway** ([06](06-storage-backends.md) §6) as a separate deployable: read-only,
  Range pass-through, digest-keyed cache, FTP connection pooling, guaranteed no
  `Content-Encoding`, optional read auth for non-public channels.
- Server-scoped content in whichever form Q14 selects — channel-per-realm and axis-per-realm need
  no new machinery; federated overlays are a separate project with a security review of their own.

**Exit:** **with the API switched off, every player operation still works** against the static
mirror — cold install, update, variant switch, rollback, verify. That is the acceptance test for
the boundary and it survives every revision so far.

---

## Phase 8 — Bundling and delta (conditional)

Only if Q7/Q8 were answered "reserve, decide later" rather than "ship in v1".

- Small-file bundling: `bundles/{bundleHash}` with `(hash, offset, length)` per entry, one GET
  plus client-side slicing, or a multi-range GET.
- Optional delta blobs `deltas/{fromHash}_{toHash}` (zstd `--patch-from`) with whole-file
  fallback.
- Content-defined chunking if the delta numbers justify it.

> If Q7 is answered "ship in v1" — the recommendation — this work merges into Phase 3 and this
> phase disappears. **Bundling changes the file-table schema, so doing it after real content is
> published means invalidating every manifest.**

---

## Phase 9 — Launcher integration

- Named-pipe IPC with streaming progress and the in-process transport swap.
- Variant selection UI contract (what the launcher needs to render the axis list).
- Startup health check wiring, if Q1 says the engine globs.

---

## Dependency graph

```
P0 ─▶ P1 ─▶ P2 ─▶ P3 ─▶ P4 ─▶ P5 ─▶ P6 ─▶ P7 ─▶ P9
            │      │            ▲
            └──────┴─ P8 ───────┘   (merges into P3 if Q7 = "v1")
```

Phases 5 and 6 can overlap; 6 and 7 can overlap. Nothing overlaps with 1–4, which are strictly
sequential because each is the substrate for the next.

## Definition of done, applied at every phase

Per `rules/code-review.md` and `rules/testing.md`:

- Tests written first for new behaviour; coverage thresholds met ([12](12-testing.md) §10).
- `code-reviewer` and, for anything touching signing, trust, paths or deletion,
  `security-reviewer`.
- No `Error`-severity architecture-test violations.
- Documentation updated **in the same change** — `rules/documentation.md` treats it as a
  deliverable, and these design documents are the spec that implementation must be reconciled
  against.
