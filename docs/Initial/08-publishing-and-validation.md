# 08 — Publishing, Validation and Lifecycle

## 1. The publishing pipeline

```
build tree ──▶ slice ──▶ hash ──▶ pack ──▶ push ──▶ release new ──▶ CHECK ──▶ release publish ──▶ promote
                 │        │        │        │            │            │                            │
             slice.yaml  cached   shards  dedup by    lockfile    THE GATE                     one write
                         by mtime         hash                   (exit 1)
```

Every step is idempotent and every step except `promote` writes only immutable objects.

## 2. Slicing — one build tree into many packages

`packaging/slice.yaml` — the single artifact a build engineer maintains:

```yaml
schemaVersion: 1
source: <build-root>
packages:
  - id: fourstory.client.core
    include: ["**/*"]
    exclude: ["bin/x86/**","bin/x64/**","ui/classic/**","ui/modern/**",
              "data/localized/**","textures/hd/**","brand/**"]
    policy:
      "config/*.ini": preserve
      "bin/**/*.sh": executable

  - id: fourstory.client.ui.classic
    include: ["ui/classic/**","data/ui/classic/main.dat"]
    rewrite: { "data/ui/classic/main.dat": "data/ui/main.dat" }

  - id: fourstory.client.lang.de
    include: ["data/localized/de/**"]
    requires: [{ id: fourstory.client.lang.common, minSequence: 1200 }]

unmatched: error        # every byte of the build lands in exactly one package
```

Three deliberate choices:

- **`unmatched: error`.** A file that matches no package is a build error, not a silent
  omission. This is the mechanical answer to "someone forgot to add the new asset directory".
- **`rewrite`.** The build tree can keep `ui/classic/main.dat` and `ui/modern/main.dat`
  separate while both packages *install* to `data/ui/main.dat`. That is what makes the
  same-virtual-path case authorable without a build-system change.
- **`policy` at slice time**, not glob rules at apply time. The publisher knows which files are
  config; the client should not have to evaluate a rule set. See
  [07](07-client-engine.md) §2.1.

Publishing generates a JSON Schema for `slice.yaml` **from the C# model** so it cannot drift,
and emits `# yaml-language-server: $schema=…` at the top of the file. The reference did this by
hand and the schema drifted.

### 2.1 Hashing at publish scale

- Single-pass multi-algorithm hashing (`sha256` + `md5` over one pooled buffer).
- A persisted `(path, size, mtime, fileIdentity, changeTime) → hash` index per source directory,
  so republishing a mostly unchanged 200k-file build re-hashes only what changed. On a multi-GB
  tree this is the difference between two seconds and five minutes.

  **It is a speed cache, never a correctness oracle** (review **H16**): a build tool can rewrite
  content while preserving size and mtime. File identity and change-time narrow the window but do
  not close it, so `--rehash-all` exists and **a release-signing build always full-hashes** unless
  the source tree carries a trusted content-addressed build manifest.
- No at-rest compression ([18](18-normative-contract.md) §5), so the hash pipeline emits exactly
  one size per file and there is no encoding decision to make.

### 2.2 Upload

Two-phase, carried over from the reference:

1. **Batched existence check.** One round trip per ~100 hashes rather than one HEAD per blob —
   the difference between a 30-second and a 20-minute publish. On S3 without a control plane
   the equivalent is a single prefix listing cached in memory; on FTP, an `MLSD` of the touched
   shard directories.
2. **Upload into `_staging/{sessionId}/`, then promote.** Gives atomicity (a release is only
   visible once its lock is written), a natural GC target (orphaned sessions), and credential
   scoping (a publishing credential can be restricted to its own session prefix).

Promotion maps to `CopyObject` on S3 (server-side, bytes never traverse the client), `RNFR`/
`RNTO` on FTP, and `File.Move` locally. **A large blob above 5 GiB needs `UploadPartCopy`, not
`CopyObject`** — a separate code path that must exist, not be discovered in production.

Where available, presigned PUT means the publishing client never holds account keys.

## 3. The publish gate

Two tiers with distinct, non-overlapping roles.

### 3.1 Tier 1 — pairwise co-satisfiability (BLOCKING)

A path collision always involves at least two packages, so testing **every ordered pair whose
`when` predicates are co-satisfiable** covers every collision. Combination enumeration is never
needed for *detection* — which is why the axis count can grow without cost.

For P packages: O(P²) cheap predicate tests, narrowed by disjoint `pathPrefixes`, then a sorted
merge over the union of shards only for surviving pairs. Sub-second for 13 packages at 200k
paths each.

#### Completeness is over (pair × discriminator outcome), not over pairs

The first draft claimed pairwise testing was complete, full stop. Review finding **H5** showed
that is not sufficient: **the precedence tuple depends on the selection**, because the
discriminator is derived from which values of a `Many` axis were chosen
([04](04-variant-model.md) §3.2). A pair can be legal under one discriminator outcome and
`RES007` under another.

Concretely: `lang.de` and `lang.en` are both layer 30000. Under `language=de` only one is
selected and there is no pair at all. Under `language=de,en` both are selected, they sit at the
same layer with discriminators 1 and 0, and any shared path needs a declared override. Testing
"the pair" without saying *at which discriminators* proves nothing about the second case.

So the gate iterates, for each co-satisfiable pair, over the **distinct precedence-tuple outcomes**
that pair admits:

```
for each ordered pair (A, B) with CanCoexistWith(A.when, B.when):
    for each (dA, dB) in DistinctDiscriminatorOutcomes(A, B):
        assert the §11 composition rules accept (A@dA, B@dB) for their overlapping paths
```

`DistinctDiscriminatorOutcomes` is small and computable without enumerating selections. For a
pair constrained on the same `Many` axis it is bounded by the number of declared values on that
axis; for every other shape it is a single outcome, because a `One` axis admits exactly one value
per selection and an unconstrained axis yields discriminator −1. In the 4Story release that is
5 outcomes for the language pairs and 1 for everything else.

**The invariant the gate must establish:** a repository that passes Tier 1 cannot produce
`RES004`, `RES006` or `RES007` for *any* legal selection. That is stated as a property test in
[12](12-testing.md) §5 — generate random axis sets, requirements and file tables, run the gate,
then exhaustively resolve every legal selection and assert no fatal composition diagnostic ever
fires on an accepted repository.

#### Dependency implication

`PKG007` requires that wherever a dependent is selectable, its dependency is too. That is
predicate implication — `A.when ⟹ B.when` — not equality, and it is decidable directly on the
predicate structure: for every axis constrained by `B.when`, `A.when` must constrain that axis to
a **subset** of `B.when`'s values (an axis unconstrained by `A` fails unless `B` admits every
declared value of it).

#### Slicer overlap

`unmatched: error` catches files that land in no package. The converse — a file matching **two**
package include globs — is `PKG014b OverlappingSlice` and is equally fatal, because it silently
duplicates content across packages and produces a same-layer collision downstream.

| Code | Severity | Meaning |
|---|---|---|
| `PKG001 AmbiguousLayer` | Error | Co-satisfiable pair, same derived layer+discriminator, overlapping paths |
| `PKG002 UndeclaredOverride` | Error | Different layers, overlap, but the higher package's effective overrides omit the lower |
| `PKG003 UnreachableRequirement` | Error | `when` references an axis or value the release does not declare |
| `PKG004 InertAxisValue` | Warning | An axis value that no requirement mentions |
| `PKG005 DanglingPin` | Error | A requirement references a package absent from `packages[]` |
| `PKG006 MutualConflictSelectable` | Error | Two conflicting packages are co-satisfiable |
| `PKG007 UnsatisfiedRequires` | Error | A dependency is not selectable wherever the dependent is |
| `PKG008 InconsistentLayer` | Error | Two co-satisfiable requirements derive different layers for one package |
| `PKG009 CaseOnlyPathCollision` | Error | Two paths differing only by case, within a package or a co-satisfiable pair |
| `PKG010 ExplicitLayer` | Warning | A `layerOverride` was used |
| `PKG011 CacheHeaderRisk` | Error | (`verify-repo`) a channel pointer served with an immutable/long-max-age header |
| `PKG012 NonPortablePath` | Error | Windows reserved name, trailing dot/space, illegal character, or `MAX_PATH` overflow |
| `PKG015 ManifestDigestMismatch` | Error | A pinned manifest's bytes do not match `manifestDigest` |
| `PKG016 DenormalisationMismatch` | Error | A `LockedPackage` copy disagrees with its `PackageRequirement` ([04](04-variant-model.md) §4.2) |
| `PKG014b OverlappingSlice` | Error | A source file matched more than one package's include globs |
| `PKG014 UnmatchedSourceFile` | Error | A build-tree file matched no package (`unmatched: error`) |

`PKG009` is not cosmetic: on Windows the two paths are one file and the composed set silently
loses one, while the S3 mirror carries two distinct objects.

`PKG012` is what stops a Linux build agent legally publishing `data/aux.dat` and bricking every
Windows client.

### 3.2 Tier 2 — coverage enumeration (REPORT)

Enumerate every legal selection point, resolve and compose each, and emit `coverage.json`
([05](05-repository-format.md) §9). Above 4,096 points, degrade to a deterministic pairwise
covering array over axis values and set `"mode": "sampled"`.

**The gate never weakens; only the report does.** That split matters: the reference designs that
relied on enumeration alone became unprovable past their cardinality limit.

```bash
4sup release check fourstory.client@2026.02.15-a --strict --against 2026.02.14-a
```

diffs the matrix and fails on file-count or install-size drift beyond `--drift-tolerance`. This
is the **only** mechanical defence against a mis-sliced package silently gaining or losing
thousands of files, and against the whole class of "someone forgot a `when` clause".

### 3.3 CI wiring

```
pkg slice
  └─▶ pkg publish            (changed packages only)
        └─▶ release new --from $(current) --bump …
              └─▶ release check --strict --against $(current)     ◀── EXIT 1 BLOCKS THE PIPELINE
                    └─▶ release publish
                          └─▶ (manual/gated) channel promote
```

`release check` is the gate. It runs before anything mutable is touched, and `channel promote`
is a separate, explicitly gated step — because promotion is the only irreversible-in-effect
operation in the pipeline.

## 4. Lifecycle operations

| Operation | Implementation | Cost |
|---|---|---|
| **Promote** | sign a new channel pointer at `channelSequence + 1`; API verifies and places | one signed PUT |
| **Rollback** | sign a new channel pointer at `channelSequence + 1` naming an **older** `releaseId` | one signed PUT — no republication, no rebuild |
| **Yank** | append a signed entry to `revocations.json` at `revocationSequence + 1` ([17](17-signed-documents.md) §4.5) | one signed PUT |
| **Prune** | delete release directories beyond `--keep-releases N`, never touching channel-referenced or `retain: true` releases | N deletes |
| **GC** | mark-and-sweep over blobs (§5) | requires `IListableObjectStore` |
| **Mirror** | read from source, verify hash, conditional write to destination. Provider-native copy is an **optimisation only when a capability probe proves both ends are the same store** — S3 `CopyObject` requires same provider/account/region, and FTP `RNFR`/`RNTO` renames *within one server* and cannot mirror to another origin at all (review **H8**) |

### 4.1 Promotion safety

`channel promote` is **gated on `IConditionalWriteStore`** and uses compare-and-swap
(`If-Match` on S3, `FileMode.CreateNew` lock on local). Without it, two concurrent promotes lose
an update and the channel silently regresses — breaking rollback exactly when it is
needed.

On FTP, and on B2's S3 endpoint, there is no CAS. Promotion there requires an explicit
`--force-unsafe-promote` and logs a warning; **FTP repositories are declared single-publisher,
in writing** ([06](06-storage-backends.md) §4.1).

## 5. Garbage collection

### 5.1 The algorithm

```
MARK:   channel pointers (all channels, all products)
      + the last N releases per product
      + any release with retain: true
   ─▶ LockedPackage.ManifestDigest for each pinned package
   ─▶ FileTableRef shard digests
   ─▶ every entry's contentHash (H7: never a guessed encoding — one representation per hash)

SWEEP:  EnumerateBlobsAsync() minus the mark set,
        filtered by minimum blob age,
        into QUARANTINE (_trash/{date}/), not straight to delete
```

### 5.2 Safety rules — every one of these is load-bearing

- **`_staging/**` is immune to the blob sweeper** and is swept only by its own age rule.
- **`minAge` ≥ the maximum expected publish duration.** A publish in flight uploads blobs
  *before* the release lock exists, so a concurrent GC with too small a `minAge` deletes blobs
  belonging to an unfinished release. A publish lease/heartbeat makes an in-flight upload
  visible to the sweeper.
- **Quarantine, then delete.** GC deletes are unrecoverable. Move to `_trash/{date}/`, delete
  after N days, and emit a machine-readable deletion manifest.
- **`--dry-run` emits the exact deletion list**, not a count.
- **Mark exact locators, never a guessed encoding.** The mark set is the set of `contentHash`
  values reachable from live releases. An earlier draft marked `(contentHash, canonicalEncoding)`
  pairs, so a live blob stored under any other representation was never marked and would be
  swept — a data-loss bug (review **H7**). Identity-only storage
  ([18](18-normative-contract.md) §5) removes the ambiguity entirely: one hash, one object.
- **Retention must cover supported clients, not just channel heads.** Marking from channel heads
  plus last-N can delete metadata and blobs that a long-offline client still needs to reach the
  current release. Retention is therefore `max(last N releases, every release referenced by a live
  channel, every release published within the supported update window)` — the window being how
  far behind a client may fall and still update in one step. A client older than the window is
  told to reinstall rather than silently 404ing.
- **GC is impossible against the HTTP read-only mirror by design, and says so.** The
  authoritative writable repository is where GC runs; mirrors are re-synced afterwards with a
  **delete-propagating** sync. Any mirror that is not delete-propagating accumulates orphans
  forever.
- On FTP a full sweep is 65,536 `MLSD` round trips — hours, with no resumability. Documented as
  effectively unsupported ([06](06-storage-backends.md) §4.2).

### 5.3 Why this gets its own safety section

The reference's `CleanupBlobsAsync` (`LocalPatchRepository.cs:114-115`) built the keep-set
correctly and then swept `_pathResolver.ProductsPath` — while blobs live under `BlobsPath`. It
matched nothing, deleted nothing, and **reported success to three call sites for years**.

The countermeasure is not care; it is a test. The golden-repository fixture
([12](12-testing.md) §3) contains a **real orphan blob**, and a test asserts it is collected.
GC output is auditable so a human can verify what happened.

## 6. Publish-side validation of the payload

Carried over from the reference and extended: an `IContentValidator` composite runs at publish
time, so a bad artifact is rejected on push rather than on install.

```csharp
public interface IContentValidator
{
    bool CanValidate(PackageFileEntry entry);
    ValueTask<ValidationResult> ValidateAsync(PackageFileEntry entry, Stream content, CancellationToken ct);
}
```

Initial validators: install-policy manifest well-formedness, JSON/YAML parse checks on declared
config files, and PE-header sanity on `bin/**`. The reference ran these only at publish; here
they **also** run on the client before commit, because a mirror can serve a byte-correct blob
that is nonetheless the wrong artifact for this release.

## 7. Post-install hooks — deferred to Phase 4, designed now

A patch that needs a VC++ redistributable, a registry write or a config migration has no
mechanism without hooks. But this is a **code-execution primitive**, and the reference's version
is the single most serious defect in it: `UpdateExecutor.RunScriptsAsync` swallowed every script
exception and never checked the process exit code, so a failed hook left the update marked
successful — in a system with no signing at all.

Preconditions before hooks ship:

1. Signing is live and the trust root is pinned ([10](10-security.md)).
2. Script identity is a **content hash inside the signed manifest**, not a path.
3. No shell-string parsing — an argv array, explicitly.
4. Explicit failure semantics: exit code checked, non-zero fails the apply, and the apply rolls
   back to the previous ledger.
5. Hooks run **after** commit, never between materialise and commit, so a hook failure cannot
   leave an unrecorded tree.

### 7.1 Hook failure is not file rollback

The first draft said a non-zero hook exit "fails the apply and rolls back to the previous
ledger". That promises something unachievable: hook side effects — a registry write, a redist
install, a database migration — are **not** reversible by restoring a file ledger, and a crash
between commit and hook completion leaves the files new and the external state unknown
(review **H17**).

The honest model separates the two recoveries:

- **File state** is committed and stays committed. It is already correct and verified.
- **Hook state** is tracked separately in `.4sup/hooks.jsonl`: each hook has an **idempotency
  key** derived from `(releaseId, selectionId, hookContentHash)`, and a status of
  `pending | running | succeeded | failed`.
- A crash leaves `running`; the next launch re-runs it, which is safe **only because hooks are
  required to be idempotent** — that is a contract on the hook author, stated and testable, not
  an assumption.
- A `failed` hook surfaces to the launcher as a degraded state with the diagnostic, and blocks
  launch only if the hook declared `required: true`.
- Compensation, where a hook needs it, is a **separate declared hook**, not an automatic
  inversion the updater invents.

The updater never promises to undo an arbitrary side effect.
