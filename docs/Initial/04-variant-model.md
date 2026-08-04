# 04 — The Variant Model

This is the core of the design. Everything else is shaped to serve it.

## 1. The model in one paragraph

A **product** (`fourstory.client`) is a metapackage, not a file set. A **release** is a small
immutable lockfile that declares the variant **axes** and pins N independently versioned
**packages**, attaching to each pin a `when` predicate over those axes. Layer precedence is
**derived from axis rank**, never hand-picked. A client binds a **selection** — a point (or,
for multi-valued axes, a sub-cube) in axis space — evaluates every `when` predicate locally
against the one artifact it downloaded, sorts the matched packages by
`(derived layer, discriminator, package id)`, and merges their file tables lowest-first into a
**composed file set**: a pure `path -> (contentHash, owningPackage, policy)` map. That map is
the entire truth. Install is `compose(target) − state`; deletion is `state − compose(target)`;
a variant switch is recompose-and-diff. Manifest cost is O(Σ files across packages), never
O(Π axis cardinalities).

## 2. Why the "patch version" level was split

The brief says *"Create patch versions (contain a set of files)"*. That maps to a **package
version** — an immutable named set of files. But a client does not install one set of files;
it installs *the union of several*, chosen by its variant selection. That union needs its own
identity, lifecycle and validation, so it gets its own level: the **release**.

| Brief concept | This design | Immutable? |
|---|---|---|
| "a patch version" | **package version** — `fourstory.client.lang.de@1.4.7` | yes, once published |
| (implied) "what a client installs" | **release** — `fourstory.client@2026.02.15-a` | yes, once published |
| (implied) "what live points at" | **channel** — `live` | **no** — the only mutable object |

The payoff: a German string-table hotfix republishes one 1,188-file package and one 6 KB
release lock. It does not touch, re-hash or re-upload the 184,203-file core package.

## 3. Axes — the variant vocabulary

```csharp
public enum AxisCardinality { One, Many }

public sealed record AxisValue
{
    public required string Id { get; init; }                   // ^[a-z0-9][a-z0-9._-]{0,31}$
    public string? Display { get; init; }
}

public sealed record AxisDefinition
{
    public required string Name { get; init; }                 // "language"

    /// UNIQUE across the release. Drives layer derivation: a package selected by a
    /// higher-ranked axis composes LATER and therefore wins. This is the single
    /// tuning knob for "which axis dominates", and it is one integer in one place.
    public required int Rank { get; init; }

    /// Declaration order IS precedence order for Many-cardinality axes.
    public required ImmutableArray<AxisValue> Values { get; init; }

    public string? Default { get; init; }
    public required AxisCardinality Cardinality { get; init; }
    public required bool Required { get; init; }
    public string? DisplayName { get; init; }

    /// Removed value -> replacement. Applied BEFORE domain validation so a client
    /// pinned to a retired value migrates instead of hard-failing.
    public ImmutableDictionary<string, string> Retired { get; init; }
        = ImmutableDictionary<string, string>.Empty;

    public int IndexOf(string value);                          // -1 if unknown
}
```

### 3.1 Selection and predicate

```csharp
/// A point (One axes) or sub-cube (Many axes) in axis space.
public sealed record VariantSelection
{
    public required ImmutableSortedDictionary<string, ImmutableSortedSet<string>> Axes { get; init; }
    public ImmutableSortedSet<string> this[string axis] { get; }
    public bool Has(string axis, string value);

    /// "arch=x64;brand=4story;hd=off;language=de,en;ui=classic" — sorted, canonical.
    public string ToCanonicalString();
    public ContentHash SelectionId { get; }                    // sha256(ToCanonicalString())
}

/// Conjunction over axes; disjunction over values within an axis.
/// An axis absent from Constraints is unconstrained.
public sealed record AxisPredicate
{
    public required ImmutableSortedDictionary<string, ImmutableSortedSet<string>> Constraints { get; init; }
    public static AxisPredicate Always { get; }
    public bool IsAlways => Constraints.IsEmpty;

    /// True iff for EVERY constrained axis k: selection[k] intersects Constraints[k].
    public bool Matches(VariantSelection selection);

    /// Pairwise satisfiability, used by the publish gate.
    /// For each axis constrained by BOTH predicates:
    ///   Cardinality.One  -> the value sets must intersect;
    ///   Cardinality.Many -> always co-satisfiable (both can be selected at once).
    public bool CanCoexistWith(AxisPredicate other, IReadOnlyDictionary<string, AxisDefinition> axes);
}
```

`CanCoexistWith` is the load-bearing method of the publish gate. It is what lets
`ui.classic` and `ui.modern` legitimately ship the *same virtual path* without that being a
conflict: they are provably never co-selected, so the gate never even compares their file
tables.

### 3.2 Layer derivation

Precedence belongs to the **axis**, declared once, not to a hand-picked integer scattered
across requirement lines. Given ranks `arch 10, ui 20, language 30, hd 40, brand 50`:

```csharp
int layer = requirement.LayerOverride
         ?? (requirement.RankAs is { } ra   ? axes[ra].Rank * 1000
           : requirement.When.IsAlways      ? 0
           : requirement.When.Constraints.Keys.Max(k => axes[k].Rank) * 1000);

int discriminator =
      requirement.RankAs is not null ? -1
    : requirement.When.IsAlways      ? -1
    : axes[dominantAxis].IndexOf(selection[dominantAxis].Intersect(When[dominantAxis]).Min());
```

This reproduces exactly the ladder a human would hand-pick — core 0, `bin.x64` 10000, `ui.*`
20000, `lang.common` 30000/−1, `lang.*` 30000/valueIndex, `tex.hd` 40000, `brand.gamigo`
50000 — but *derives* it. Changing precedence is a one-line rank edit that shows up as a
coverage-matrix diff, and there is no unscoped global integer namespace for two teams to
collide in.

`RankAs` handles the `lang.common` case — always included, but must rank inside the language
band, above `core` and below every concrete language. `LayerOverride` exists as an escape
hatch and emits `PKG010 ExplicitLayer` (warning) so it stays rare and reviewed.

## 4. Packages and releases

```csharp
public enum PackageKind  { Content, Meta }
public enum PackageState { Draft, Published, Yanked }

/// Vocabulary from the reference's RuleAction (IO/Globbing/RuleAction.cs), but bound
/// at PUBLISH time to the file entry instead of matched by client-side globs at apply
/// time — which is what made the reference's rule resolution order-dependent.
public enum FileInstallPolicy
{
    Replace,     // write; delete when no longer owned
    Preserve,    // write only if absent; never overwrite; NEVER delete (user config, savegames)
    Executable   // Replace + chmod +x on POSIX
}

public sealed record PackageFileEntry
{
    public required VirtualPath Path { get; init; }
    public required ContentHash Content { get; init; }     // sha256 of UNCOMPRESSED bytes
    public required long Size { get; init; }               // uncompressed — install accounting
    public required long StoredSize { get; init; }         // transfer bytes — download accounting
    public required ContentEncoding Encoding { get; init; }
    public ContentHash? Md5 { get; init; }                 // cheap change detection only
    public FileInstallPolicy Policy { get; init; } = FileInstallPolicy.Replace;
}

public sealed record FileTableRef
{
    public required string Format { get; init; }           // "jsonl/v1"
    /// POWER OF TWO, recorded explicitly. Shard assignment is
    /// xxh3_64(path.Value) & (ShardCount - 1) — stable under insertion, so a one-file
    /// hotfix rewrites exactly one shard instead of shifting every boundary.
    public required int ShardCount { get; init; }
    public required ImmutableArray<FileTableShardRef> Shards { get; init; }
    public required ContentHash Digest { get; init; }
}

public sealed record PackageManifest
{
    public required int SchemaVersion { get; init; }
    public required PackageId Id { get; init; }
    public required PackageVersion Version { get; init; }
    public required PackageKind Kind { get; init; }
    public required PackageState State { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }

    public ImmutableArray<PackageId> Overrides { get; init; } = [];
    public ImmutableArray<PackageId> Conflicts { get; init; } = [];
    public ImmutableArray<PackageDependency> Requires { get; init; } = [];

    /// CONSERVATIVE superset — every Path must start with one of these. Lets the publish
    /// gate skip O(n) table intersection for pairs with disjoint prefix sets.
    public required ImmutableArray<string> PathPrefixes { get; init; }

    public required FileTableRef FileTable { get; init; }
    public required int FileCount { get; init; }
    public required long InstallSize { get; init; }
    public required long DownloadSize { get; init; }
}
```

### 4.1 Requirements and the lock

```csharp
public sealed record PackageRequirement
{
    public required PackageId Package { get; init; }
    public AxisPredicate When { get; init; } = AxisPredicate.Always;

    /// Rank AS IF selected by this axis, while remaining unconditional.
    public string? RankAs { get; init; }

    /// Escape hatch only; emits PKG010.
    public int? LayerOverride { get; init; }

    /// Overrides declared HERE rather than inside the (immutable, already-published)
    /// package manifest, so introducing a new lower-layer package never forces
    /// republication of packages whose bytes did not change.
    public ImmutableArray<PackageId> Overrides { get; init; } = [];

    public bool Optional { get; init; }
}

/// Denormalised pin: Requires/Overrides/Conflicts are COPIED here at build time so the
/// client validates the entire dependency closure from ONE downloaded file, before
/// fetching any package manifest.
public sealed record LockedPackage
{
    public required PackageId Id { get; init; }
    public required PackageVersion Version { get; init; }
    public required string ManifestPath { get; init; }
    public required ContentHash ManifestDigest { get; init; }
    public required int FileCount { get; init; }
    public required long InstallSize { get; init; }
    public required long DownloadSize { get; init; }
    public ImmutableArray<PackageDependency> Requires { get; init; } = [];
    public ImmutableArray<PackageId> Overrides { get; init; } = [];
    public ImmutableArray<PackageId> Conflicts { get; init; } = [];
}

public sealed record ReleaseLock
{
    public required int SchemaVersion { get; init; }
    public required string ProductId { get; init; }
    public required string ReleaseId { get; init; }            // free-form, e.g. "2026.02.15-a"
    public required long Sequence { get; init; }               // the ordering authority
    public required ReleaseState State { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }

    public required ImmutableArray<AxisDefinition> Axes { get; init; }
    public required ImmutableArray<PackageRequirement> Requirements { get; init; }
    public required ImmutableArray<LockedPackage> Packages { get; init; }

    public string? MinimumInstalledRelease { get; init; }
    public required ContentHash CoverageDigest { get; init; }
    public ManifestSignature? Signature { get; init; }
}

/// The lock plus every referenced PackageManifest inlined. ~20 KB gzipped for a
/// 13-package release. Cuts cold-install metadata from ~33 requests to 3 + shards,
/// which is the difference between a usable and an unusable FTP mirror.
/// Each inline copy is verified against LockedPackage.ManifestDigest — it is an
/// optimisation, never a trust shortcut.
public sealed record ReleaseBundle
{
    public required ReleaseLock Lock { get; init; }
    public required ImmutableDictionary<PackageId, PackageManifest> Inline { get; init; }
}
```

## 5. The resolution algorithm — 13 steps

Input: `(repositoryUri, productId, channel | releaseId, requestedSelection)`.
Output: an exact `path → blob` map plus an exact delete set.
**Steps 1–8 require exactly one downloaded artifact** (plus the descriptor and channel).

**1 — Address.** `PathParser` yields `RepositoryAddress(Backend, BaseUri, ProductId, ReleaseRef)`
from `remote/fourstory.client@live`, `https://patch.4story.com/live/fourstory.client@release:2026.02.15-a`,
or `C:\mirror\fourstory.client@live`. Backend comes from the URI scheme or an explicit alias
table **only** — never a DNS probe, which is what made the reference's parsing
network-dependent and non-deterministic offline (`PatchRepositoryFactory.cs:289-316`). Variant
coordinates are **never positional**: always `--select axis=value[,value]`.

**2 — Descriptor.** `GET {base}/repo.json`. Supplies the layout templates, content hash
algorithm, canonical encoding, capability flags and trust roots. A missing or unparseable
descriptor is a single hard failure — no port-9090 retry, no `/Patch` suffix retry
(`CapabilitiesResolver.cs:50-102`).

**3 — Channel deref** (skipped if an explicit `releaseId` was given). `GET layout.Channel(productId, channel)`,
verify `Signature` against the pinned trust root ([10](10-security.md) §3), take `ReleaseId`
and `ReleaseDigest`.

**4 — Release bundle.** `GET layout.ReleaseBundle(productId, releaseId)`. Assert
`sha256(lockSection) == ChannelPointer.ReleaseDigest`; verify `ReleaseLock.Signature`;
verify **each inlined manifest** against its `LockedPackage.ManifestDigest`. Immutable, so
cache forever by digest. Reject `State != Published` unless `--allow-draft`. If the bundle
404s (older repository), fall back to `release.lock.json` plus per-package manifest GETs — the
bundle is a pure optimisation, never a correctness dependency.

**5 — Bind the selection.** For each axis in rank order:

- Apply `Retired` remapping **first**; emit `SEL005 ValueRetired` (Info) carrying old→new so
  the launcher can persist the migration.
- User supplied values ⇒ keep. None supplied ⇒ `Default`. No default and `Required` ⇒
  `SEL001 MissingAxis` (fatal; the message lists the legal values).
- Any value not in `Values` after remapping ⇒ `SEL002 UnknownAxisValue` (fatal, with the legal
  list). **Never silently defaulted** — silently defaulting swaps a player's entire language
  without consent.
- `Cardinality.One` with more than one value ⇒ `SEL003 CardinalityViolation` (fatal).
- Axis named by the user but not declared by this release ⇒ `SEL004 UnknownAxis` (**warning**,
  ignored). This is the forward/backward-compatibility hinge: an old launcher still passing
  `--select region=eu` against a release that dropped the axis keeps working.

**6 — Requirement evaluation and layer derivation.** Iterate `Requirements` in file order.
On a match, resolve the pin from `PackagesById` (absent ⇒ `RES001 DanglingPin`, fatal, or
warning-and-skip when `Optional`), then compute `layer` and `discriminator` per §3.2. A package
matched by several requirements is included **once** with `layer = max`; `release check`
rejects any release where two co-satisfiable requirements derive different layers for one
package (`PKG008`), so `max` never actually arbitrates on a healthy repository.

**7 — Dependency validation (never solving).** Using the **denormalised** `Requires` on the
lock: every dependency id must also be selected, and its `Sequence` must fall in
`[MinSequence, MaxSequence]` — else `RES002 UnsatisfiedRequirement` (fatal). Any selected pair
where either declares the other in `Conflicts` ⇒ `RES005 DeclaredConflict` (fatal).
**Nothing is auto-added.** Refusing to run a solver on the client is what keeps the read path
implementable over "GET a static file by path": the solving already happened when a human ran
`4sup release new`, and its answer is the lockfile.

**8 — Total order.** Sort by `Layer` ASC, then `Discriminator` ASC, then `PackageId.Value`
**ordinal** ASC. The id tiebreak makes the order *total*, so composition is bit-identical on
every client on every OS forever — which is what makes `FileSetId` a stable comparable
identity (NFR-4).

**9 — Package manifests.** From `bundle.Inline` (zero network) or `GET ManifestPath` plus a
digest assertion. Immutable ⇒ cached by digest.

**10 — File tables.** For each `FileTableShardRef`, `GET layout.Blob(shard.Digest, canonicalEncoding)`,
verify the digest, stream as JSONL. Never materialise a whole table. Because shards are
content-addressed and hash-bucketed, `core@1.4.8` touching 12 files shares 15 of its 16 shards
with `1.4.7` and the client re-downloads one.

**11 — Compose.** Walk packages in step-8 order, lowest layer first. For each entry:

- `VirtualPath.TryCreate` fails ⇒ `RES003 IllegalPath` (fatal; rejects the whole repository).
- `map[path]` empty ⇒ take ownership.
- Occupied by a **lower** layer ⇒ record the previous owner in `Shadowed`, take ownership.
- Occupied at the **same** layer *and* discriminator ⇒ `RES004 UnexpectedCollision` (fatal).
  Unreachable on a repository that passed `release check`; the client enforces it anyway rather
  than silently picking a winner.

`FileSetId = sha256` over canonical `path\0hash\0owner\n` triples in ordinal path order.

**12 — Plan.** Read `state.jsonl` into `current`, then:

```
foreach (path, want) in target.Files:
    if !current.TryGetValue(path, out have):
        if want.Policy == Preserve && File.Exists(path) -> Keep      (adopt into ledger)
        else                                            -> Write
    elif have.Content != want.Content:
        if want.Policy == Preserve                      -> Keep
        else                                            -> Write
    else                                                -> Keep      (re-hash only if (size, mtime) drifted)

foreach (path, had) in current:
    if target.Files.ContainsKey(path): continue
    if had.Policy == Preserve                           -> Orphan    (keep + log)
    else                                                -> Delete    ◀── the variant-switch delete set
```

Then prune directories that became empty and contained only owned files.

**Files on disk that are not in the ledger are NEVER deletion candidates.** Savegames, crash
dumps, mods and user configs are structurally safe. This is the fix for the reference's
`deleteFilesNotInPatch` ([02](02-reference-review.md) §2.5).

**13 — Apply.** Detailed in [07-client-engine.md](07-client-engine.md) §3.

## 6. Diagnostic codes (resolution)

| Code | Severity | Meaning |
|---|---|---|
| `SEL001 MissingAxis` | Error | Required axis with no value and no default |
| `SEL002 UnknownAxisValue` | Error | Value not in the axis domain after retirement remapping |
| `SEL003 CardinalityViolation` | Error | Multiple values on a `One` axis |
| `SEL004 UnknownAxis` | Warning | Axis not declared by this release; ignored |
| `SEL005 ValueRetired` | Info | Value remapped; carries old→new for the launcher to persist |
| `RES001 DanglingPin` | Error | Requirement references a package not in `packages[]` |
| `RES002 UnsatisfiedRequirement` | Error | Dependency not selected, or sequence out of range |
| `RES003 IllegalPath` | Error | A manifest path violates `VirtualPath` rules |
| `RES004 UnexpectedCollision` | Error | Same path at same layer and discriminator |
| `RES005 DeclaredConflict` | Error | Two selected packages declare each other in `Conflicts` |

## 7. Worked examples

### 7.1 The two UIs at the same virtual path

- `core@1.4.7` (layer 0) ships `data/ui/main.dat → blobCORE`.
- `ui.classic@1.4.0` (layer 20000, requirement declares `overrides: [core]`) ships
  `data/ui/main.dat → blobCLASSIC` plus `ui/classic/**`.
- `ui.modern@2.0.1` (layer 20000, `overrides: [core]`) ships `data/ui/main.dat → blobMODERN`
  plus `ui/modern/**`.

`ui.classic` and `ui.modern` are **provably never co-selected** — `when: ui=[classic]` versus
`when: ui=[modern]` on a `Cardinality.One` axis with disjoint value sets, so
`CanCoexistWith == false`. Their shared path is therefore not a conflict and is never even
tested by the gate. Each collides with `core` at 20000 versus 0, and each declares the
override, so it is legal, checked, and the higher layer wins.

A blob shipped by *both* UI packages — a shared font, an unchanged shader — is **one blob** in
the CAS.

### 7.2 A variant switch, concretely

`4sup switch "C:\Games\4Story" --select ui=modern` on a live install:

| Step | Cost |
|---|---|
| 1–4 (address, descriptor, channel, bundle) | cache hits — same release, immutable digests |
| 6 (requirement evaluation) | one requirement match swaps |
| 9 (manifests) | from the cached bundle — zero network |
| 10 (file tables) | only `ui.modern`'s shards — ~400 KB |
| 11 (compose) | pure CPU |

Resulting plan: `Write data/ui/main.dat (blobMODERN)`, `Write ui/modern/**` (4,811 files),
`Delete ui/classic/**` (3,204 files). `core`, `bin.x64` and `lang.*` are all `Keep` —
untouched. If the player previously ran modern, most modern blobs are still in the local CAS
and the switch is nearly offline.

### 7.3 Un-shadowing, for free

Suppose `ui.classic` overrode `data/ui/main.dat` and `ui.modern` does **not** ship that path
at all. The new composition has `core` as the owner again, so the diff emits
`Write data/ui/main.dat (blobCORE)`.

Because ownership is **recomputed** from the manifests on every operation rather than
maintained as a mutable stack, revealing a shadowed lower layer is automatic. This is precisely
the case `dpkg` diversions get wrong, and the reference cannot express it at all.

### 7.4 Lost or corrupt ledger

`Plan(target, current: null)` emits **zero `Delete` operations**. The degradation is always
"leave unknown files alone", never "delete something we did not put there". Recovery, in order:

1. Re-resolve the previous `install.lock.json` and compose it to reconstruct `current` exactly.
   This is fully deterministic because `(releaseDigest, selectionId) → fileSetId` is a function.
2. `4sup verify --rebuild-state`, which hash-scans the tree and matches observed hashes against
   the composed set.
3. Accept stale files and warn.

Whether (3) is acceptable depends on whether the 4Story engine globs `ui/**` at startup — see
[14-open-questions.md](14-open-questions.md) Q1.

## 8. Scale

| Quantity | Value |
|---|---|
| Legal selection points (5 langs many-valued, 2 ui, 2 arch, 2 hd, 2 brand) | 496 |
| `release.lock.json` describing all of them | ~6 KB |
| Package manifests | 13 × ~1–2 KB |
| `release.bundle.json` gzipped | ~20 KB |
| Cold-install metadata requests | 3 + shards |
| Adding a sixth language | 1 axis value + 1 requirement line + 1 package |
| Adding a whole new axis | 1 axis declaration; precedence slots in automatically |

Manifest cost is **additive in files**, not multiplicative in axes. That is the property the
whole design exists to obtain.

## 9. Why this model, and not the alternatives

Three models were designed independently against the same brief and scored on five axes.
(A third, "layered overlays / OCI-style whiteouts", failed to produce a usable submission and
was scored zero rather than silently dropped; nothing here depends on it — see
[14](14-open-questions.md) Q11.)

| Model | Dumb client | Dedup | Switch cost | Manifest scale | Authoring | Total |
|---|---|---|---|---|---|---|
| Tagged single manifest (predicate-filtered, axis-sharded) | 9 | 10 | 9 | 8 | **4** | 40 |
| **Composed sub-packages with release locks** | 8 | 9 | 9 | **10** | **7** | **43** |

Two factors decided it.

**Deletion correctness has no invariant to violate.** Composed packages attribute every
installed file to an owning package, so `delete = state − compose(new)` is exact *by
construction*. The tagged-manifest model's headline feature — a 20 KB variant-switch fast path
that skips the shared shard — is conditional on a "shard closure" invariant, and that design's
own failure list admits that violating it yields a **wrong delete set**: holes punched in a
live install. Buying a 20 KB read at the cost of a correctness precondition on the delete path
is a bad trade for a game client.

**Authoring ergonomics decide whether the model is maintained or rots.** Packages, a `when`
predicate and a derived layer are teachable to a build engineer in ten minutes. The tagged
model requires reasoning about a rank-lexicographic specificity comparator over
hyper-rectangles, selector hoisting, shard closure and inheritance chains — it needs an
`explain` command precisely because resolution is not readable off the manifest by eye, and a
one-line rank edit silently reinterprets the entire corpus. A forgotten `--when` on an
`add-dir` ships 3,100 German string tables to every client on earth **with no error at all**.

### 9.1 What was taken from the runner-up

The winning model is not adopted unmodified. Six mechanisms were grafted in:

| Grafted | From | Why |
|---|---|---|
| **Axis-derived layers** | tagged model's `AxisDefinition.Rank` | Replaces a flat global `layer` integer namespace where two teams both picking 300 is caught only by accident |
| **Retired axis values with remapping** | tagged model's `retired: {"ru":"en"}` | Dropping a *value* would otherwise hard-fail every Russian client at the next update |
| **Path-hash shard bucketing** | tagged model's `xxh3_64(path) % pageCount` | Sorted contiguous shards are unstable under insertion — one file added near the front shifts every boundary and invalidates all 20 shard blobs |
| **The release bundle** | tagged model's "one index file" instinct | Fixes the composed model's worst weakness: 33 serialised round trips on FTP before the first content byte |
| **Coverage matrix as a digested artifact** | both, tagged model's digest | The only mechanical defence against a mis-sliced package silently gaining or losing 3,000 files |
| **Two-tier publish proof** | pairwise gate (composed) + point enumeration (tagged) | Pairwise is complete and sub-second, so it is the blocking gate; enumeration is the report, degrading to sampling above 4,096 points so the gate never weakens |

And three of the winning model's own flaws were fixed rather than inherited:

- **Retroactive `overrides` churn** — killed by allowing overrides on the release
  *requirement* instead of inside the immutable package manifest.
- **The encoding fork** (`{hash}.gz` and `{hash}.zst` for the same content, where GC can
  later half-collect and 404 an entire variant) — killed by one canonical encoding per
  repository, declared in `repo.json` and gate-checked.
- **The CDN caching the channel pointer** — the highest-severity operational risk in either
  design, because it disables the rollback lever during an incident — becomes an asserted
  live-HTTP check in `verify-repo` (`PKG011`).
