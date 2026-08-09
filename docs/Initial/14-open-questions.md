# 14 — Open Questions

Decisions that need a human answer. Each carries a recommendation so nothing is blocked on
discussion alone — but the ones marked **BLOCKING** change work already scheduled in
[13-roadmap.md](13-roadmap.md) and should be settled before Phase 1 ends.

## Status index

An external review found this document mixed genuinely-open items with ones already decided
elsewhere (review **M2**). Every item is now classified, and a **decided** item is a pointer to
its authoritative record, not a discussion.

| # | Topic | Status |
|---|---|---|
| Q1 | Engine globs `ui/**` at startup? | **ASSUMED — globbing.** Unconfirmed, but the client is ours to change if not; see Q1 |
| Q2 | Build-tree partitioning / `slice.yaml` ownership | **DECIDED — convention defined.** No concept existed; one is now specified and enforced ([20](20-build-tree-layout.md)) |
| Q3 | UI packages: full subtree or thin overlay | **DECIDED — full subtree.** The two UIs do not share bulk paths today; see Q3 |
| Q4 | Which axes are post-install switchable | **DECIDED.** `language`, `ui`, and `hd`; see Q4 |
| Q5 | Ed25519 signing in v1 | **DECIDED — yes.** Architecturally mandatory; the envelope is normative in [17](17-signed-documents.md) and is a Phase 0 exit criterion |
| Q6 | Deployment topology and GC ownership | **DECIDED.** Deployment-configurable, retaining everything by default; see Q6 |
| Q7 | Small-file bundling in v1 | **DEFERRED — backlog.** Out of scope for v1; see Q7 |
| Q8 | Sub-file delta / CDC | **DEFERRED.** Reserve the `d` field; revisit in Phase 8 |
| Q9 | Symlinks, exec bits, empty directories | **DECIDED.** Symlinks rejected at publish; `pol:"executable"` + `mode`; empty dirs via `k:"dir"` ([05](05-repository-format.md) §8) |
| Q10 | Naming (namespace and CLI) | **DECIDED — `NFour.AutoUpdater.*`; see Q10** |
| Q11 | Re-run the third variant design | **CLOSED — no.** Nothing depends on it |
| Q12 | Migration from an existing patcher | **DECIDED — adopt the existing live tree; see Q12** |
| Q13 | Telemetry consent | **DECIDED.** Off by default and deployment-configurable; see Q13 |
| Q14 | Who operates special servers | **DECIDED — first-party only.** Channel per realm; federated overlay explicitly out of scope; see Q14 |
| — | At-rest compression | **DECIDED — removed.** Identity-only CAS ([18](18-normative-contract.md) §5) |
| — | Device targeting | **DECIDED — none.** Channels only ([09](09-control-plane-server.md) §2.3) |
| — | Rollback mechanics | **DECIDED.** New signed pointer at a higher `channelSequence` ([17](17-signed-documents.md) §4) |

---

## Q1 — Does the 4Story client tolerate stale variant files on disk? **BLOCKING**

The entire deletion design assumes it does **not**: that the engine globs `ui/**` at startup and
would load a mix of classic and modern atlases if both were present.

- If the engine instead loads by **explicit manifest path**, the never-delete degradation on
  ledger loss is harmless and the recovery path relaxes.
- If it **globs**, then `verify --rebuild-state` must be wired into the launcher's startup
  health check, not left as a manual command — because a lost ledger would otherwise produce a
  visibly broken game rather than a slightly-larger install.

**Needs:** someone with TClient engine knowledge.
**Recommendation:** assume globbing until proven otherwise; it is the safe assumption and the
cost of being wrong is one extra health check.

### Answer — assume globbing (2026-08-06)

The engine's actual behaviour is not known, but the client is ours to modify
(the NFourServer tree), so if it turns out to load by explicit manifest path
we can keep it that way, and if it globs we can leave it globbing. Either way the safe
assumption is the one that costs least when wrong.

**Consequence to implement:** `verify --rebuild-state` is wired into the launcher's startup
health check rather than left as a manual command. On a lost ledger a globbing engine would
otherwise load a mix of classic and modern assets and present as a visibly broken game; with
the health check the worst case is a slightly larger install.

This is a cheap insurance policy, not a blocker. It stays valid whichever way the engine
actually behaves, so nothing further needs confirming before Phase 1 ends.

---

## Q2 — How is the 200k-file build tree actually partitioned, and who owns `slice.yaml`? **BLOCKING**

This is the single largest recurring cost of the design and it is an organisational question,
not a technical one.

Specifically: **is the localized data already segregated by directory**
(`data/localized/{lang}/**`), or is it interleaved with shared data?

- Segregated → slicing is a dozen glob rules and the estimate holds.
- Interleaved → slicing needs either a build-system change or per-file rules, and the estimate
  for the first release changes materially.

**Needs:** the build owner.
**Recommendation:** if interleaved, fix it in the build rather than in the slicer. A packaging
tool that needs per-file rules for 200k files is a packaging tool nobody maintains.

### Answer — define the convention (2026-08-06)

There is no partitioning concept in the build today, so the question of "is it segregated or
interleaved" has no answer yet to discover. The convention to build toward is therefore
specified rather than inferred: **[20-build-tree-layout.md](20-build-tree-layout.md)**.

In short — a file's package is decided by its directory and nothing else:

```
common/                   axis-independent content
axis/<axis>/<value>/      content selected by one axis value
```

This keeps the slice at roughly a dozen rules regardless of file count, which is the property
that matters; the alternative is per-file rules, and 200k of those is the configuration nobody
maintains.

Four invariants are enforced by `BuildTreeLayout` (`LAY001`–`LAY004`), the most load-bearing
being **LAY003**: two axes may not claim the same install path. That is the Q4
path-disjointness constraint made mechanical instead of remembered.

**Ownership:** `slice.yaml` is generated from the tree, so what needs an owner is the
build-output layout. Whoever adds a file picks its directory; the checks enforce that a choice
was made.

---

## Q3 — Do the two UIs occupy the same virtual paths for their bulk assets, or only for a handful of entry points? **BLOCKING**

The design handles both, but the answer decides the shape of the packages and the real switch
cost a player experiences:

| If… | Then `ui.classic`/`ui.modern` are… | Consequence |
|---|---|---|
| bulk assets share paths | ~4,800-file packages that fully replace a subtree | cheap, clean, larger switch download |
| only entry points share paths | thin override packages over a shared base | more blob dedup, more `overrides` declarations, more `PKG002` surface |

**Needs:** whoever built the second UI.
**Recommendation:** full-subtree packages unless the shared base is genuinely large. The
override surface is where mistakes hide.

### Answer — full-subtree packages (2026-08-06)

The two UIs do not currently occupy the same virtual paths. Shared-path UI switching is a
future use case, not a present one; comparable projects achieve it by patching binaries
instead.

So `ui.classic` and `ui.modern` are full-subtree packages that replace their own trees. No
`overrides` declarations are needed between them, and the `PKG002` shadowing surface stays
empty for this axis — which is the outcome worth having, because that surface is where
packaging mistakes hide.

**If shared bulk paths arrive later**, this becomes the thin-overlay row of the table above:
the format already supports it (`overrides` plus layer ordering), so it is a repackaging job
rather than a redesign. Nothing here forecloses it.

---

## Q4 — Which axes must be switchable post-install without redownloading unrelated content?

`language` and `ui` are stated. Open:

- Is `hd` a launcher toggle?
- Is `arch` ever changed in place (a 32→64-bit migration)?
- Is `brand` ever changed on an existing install?

Every axis marked switchable needs its packages kept **path-disjoint** from the packages of
other axes, which constrains the slice.

**Recommendation:** `language`, `ui`, `hd` switchable; `arch` and `brand` install-time only.

### Answer — language, UI, and HD are switchable (2026-08-09)

`language`, `ui`, and `hd` are post-install switches. `arch` and `brand` are fixed when the
installation is created. The switchable packages must therefore remain path-disjoint from
other axes; LAY003 enforces that constraint in the build-tree layout.

---

## Q5 — Does ed25519 signing ship in v1?

The format reserves `ManifestSignature` and `TrustedKeys` on every artifact. The reference had
**no signing at all** while executing downloaded binaries and post-install scripts.

**Recommendation: yes, in v1.** It is cheap, the trust root is one file, and the alternative —
adding it after a population is deployed — means either a flag day or accepting unsigned
clients forever. If deferred, the fields must stay reserved *and populated with a dev key* so
the code path is exercised from day one.

---

## Q6 — Deployment topology: where is the authoritative repository, and who runs GC?

GC requires `IListableObjectStore`, so it is **impossible against the HTTP read-only mirror by
design**, and effectively unusable over FTP (65,536 `MLSD` round trips — [06](06-storage-backends.md) §4.2).

That means the authoritative writable repository is S3 or local, GC runs there, and mirrors are
re-synced afterwards with a **delete-propagating** sync. A mirror that is not delete-propagating
accumulates orphans forever.

**Needs confirmation:** that this matches the intended topology, plus the retention policy —
suggested: keep the last N releases, plus every channel-referenced release, plus a minimum blob
age ≥ the maximum publish duration.

### Answer — configurable topology with conservative retention (2026-08-09)

Topology, GC scheduling, and retention are deployment configuration rather than compiled product
policy. Every backend may participate in the role its capabilities support, but configuration
does not manufacture capabilities: HTTP remains a read-only mirror, and FTP's listing cost makes
it unsuitable for unattended large-repository GC. S3 and local storage can be authoritative GC
targets.

The default retains every release and performs no destructive GC. Operators may explicitly
configure pruning, minimum blob age, and a GC owner. When deletion is enabled, every mirror must
use delete-propagating synchronization after GC. This default makes an omitted setting retain
data instead of silently deleting it.

---

## Q7 — Small-file bundling: v1 or deferred? **DEFERRED — backlog**

### Answer — deferred (2026-08-06)

Out of scope for v1; tracked in the backlog.

Recording the consequence honestly, because this one is hard to retrofit: v1 ships one object
per file. A package with many small files therefore costs one request each on first install,
which is the case that makes FTP and other per-request-expensive backends slow. The
`fileTable` row format already reserves room for a bundle reference, so adding bundling later
is an additive format change rather than a breaking one — but every client that shipped before
it will not understand bundles, so the rollout needs a `minimumClientVersion` bump.

**Backlog entry:** small-file bundling — pack sub-N-KB files into shared bundle objects,
addressed by (bundle digest, offset, length).

Without bundling, 50k changed files is 50k GETs:

| Backend | Cost |
|---|---|
| HTTP/2 @ 30 ms RTT | minutes of pure latency |
| FTPS, 4-connection cap | **hours of handshakes** |
| S3 @ 100k players | ~5×10⁹ requests ≈ **$2,000 per patch** in request charges alone |

Bundling changes `PackageFileEntry` and the blob key space, so **retrofitting it invalidates
every published manifest.**

**Recommendation: ship a minimum viable version in v1** — bundle blobs under ~256 KB into
`bundles/{bundleHash}` with `(bundleHash, offset, length)` recorded per entry, fetched with one
GET plus client-side slicing. Without it, FTP is not a real backend and S3 is expensive.

---

## Q8 — Sub-file delta / content-defined chunking: design now or explicitly defer? **BLOCKING — format decision**

Content addressing dedupes identical files and does nothing for a 2 GB packed archive where
4 MB changed — which is the normal shape of a game content patch. Steam, Battle.net and Epic all
do content-defined chunking for exactly this reason.

Two options:

- **(a) Full CDC** — `PackageFileEntry.Content` becomes a chunk list and the blob store becomes
  a chunk store. The real answer; a large change.
- **(b) Optional delta blobs** — `deltas/{fromHash}_{toHash}` (zstd `--patch-from` or bsdiff)
  with whole-file fallback. Far cheaper, composes with everything else.

**Recommendation: defer, but reserve the `d` field now** ([05](05-repository-format.md) §8) and
plan for (b) in Phase 8. A decision to defer is fine; drifting into it is not.

---

## Q9 — Symlinks, executable bits and empty directories

A manifest currently describes only regular files. Open cases:

- A package published from Windows (no mode bits) installed on Linux.
- Symlinks inside a macOS `.app` bundle.
- Directories the game needs to exist but that contain no files (`logs/`, `screenshots/`).

**Recommendation:** v1 **rejects symlinks at publish** with a diagnostic, keeps `Executable` as
a policy value, and adds an explicit `dir` entry kind for required-empty directories. If
symlinks are ever supported, the target string becomes a security boundary — a symlink pointing
outside the install root is an arbitrary-write primitive.

---

## Q10 — Naming

The project uses `NFour.AutoUpdater.*` namespaces and a `4sup` CLI. This matches the sibling
repository's `NFour` prefix (`NFourServer`, `FourSer.Gen`) while retaining the concise
product-facing command name.

### Answer — NFour namespace, 4sup CLI (2026-08-09)

Projects, namespaces, assemblies, and MSBuild properties use `NFour.AutoUpdater.*`. The command
name remains `4sup`; it is a product-facing executable name rather than a .NET namespace.

---

## Q11 — Re-run the third variant design?

Three variant models were commissioned; the third ("layered overlays / OCI-style whiteouts")
failed to produce a usable submission and was scored zero rather than silently treated as a
two-way comparison. Nothing in the recommended design depends on it.

**Recommendation:** not worth blocking on. The overlay model's distinctive mechanism —
whiteout markers for deletion — is strictly worse here than owner-attributed ledger deletion,
because a whiteout is state that must be *maintained* rather than *recomputed*. Re-run only if
the chosen model hits an obstacle in Phase 1.

---

## Q14 — Who operates "special servers", and what may they ship? **BLOCKING for that feature**

Per-device version control is out ([09](09-control-plane-server.md) §2.3) — everyone gets the
latest build of their channel. The one thing that genuinely varies per player is **which game
server they join**, where a realm may ship custom UI, maps or data. That is a composition
problem, and which of three models applies depends entirely on who runs those realms:

| If realms are… | Model | New machinery |
|---|---|---|
| Few, and you publish for all of them | **Channel per realm** — `channels/realm-phoenix.json` pins base packages + `realm.phoenix` | none |
| Few, centrally known, rarely changing | **Axis** — `server` axis, one release covers every realm | none |
| Operated by **third parties** | **Federated overlay** — base repo + the realm's own repo, composed client-side | a lot, plus a security cliff |

**Recommendation: channel per realm.** It needs nothing that does not already exist, base blobs
dedupe across every realm automatically, and switching realms is an ordinary variant diff that is
nearly offline after the first time. The cost is re-cutting realm releases when the base updates
— but a release lock is ~6 KB and `release new --from <base> --bump` is one command, so it
automates cleanly.

### Answer — first-party realms, channel per realm (2026-08-06)

All realms are operated by us. That settles it on the top row of the table above: **a channel
per realm**, needing nothing that does not already exist. Base blobs dedupe across every realm
automatically, and switching realms is an ordinary variant diff that is nearly offline after
the first time.

The cost is re-cutting realm releases when the base updates, which automates cleanly — a
release lock is ~6 KB and `release new --from <base> --bump` is one command.

**The federated-overlay model is explicitly out of scope**, and should stay out unless the
operating model changes. Everything below records why, so that a future "can we just let
partners run realms?" is answered with the actual cost rather than re-derived.

**If third parties operate realms, this becomes a different project.** A federated overlay lets a
server operator write files into a player's install — that is handing arbitrary third parties a
code-delivery channel to your players. It would require, at minimum:

- a **separate trust root per realm**, with a visible consent step on first join;
- overlay packages **confined to data paths** — never `bin/**`, never anything executable;
- the path-prefix allowlist enforced at **compose** time, not publish time, since the publisher
  is no longer trusted.

Also needs answering: **does the engine support a priority/override directory?** If realm content
can live side-by-side in a separate folder the game loads by precedence, switching realms is
instant and needs no patching at all. If everything must occupy the real install paths, every
realm switch is a file-set diff. Same class of question as Q1 and it materially changes the UX.

---

## Q12 — Migration from any existing distribution mechanism

Is there an existing 4Story patcher with an installed base? If so, the first release needs a
transition path: either a one-time full validation pass that adopts an existing install into a
ledger (`verify --rebuild-state` against a synthetic release), or a clean reinstall.

**Needs:** product owner. Not blocking for Phases 0–4, but it shapes the first real rollout.

### Answer — adopt the existing live installation (2026-08-09)

An existing installed base is confirmed. The inspected installation has a live game tree plus
two legacy launcher state roots: `.fourstory-launcher`, with current/previous release pointers
and version snapshots, and `.pr-launcher`. The live root is authoritative for migration; the
active legacy snapshot contains only the launcher's versioned subset, not the complete game.

The first NFour release uses in-place adoption rather than requiring a reinstall:

1. Publish a synthetic baseline release from the authoritative build that produced the legacy
   client. Sign it with the NFour trust root; do not import or implicitly trust legacy launcher
   envelopes.
2. Stop the game and legacy launcher, then run `4sup verify <install-root> --rebuild-state`
   against that explicit baseline release and the installation's selected axes.
3. Hash every path in the composed baseline. Commit the new ledger only if every required
   replaceable file matches. Preserve-policy differences become adopted/unmanaged entries;
   unrelated files remain unowned.
4. Keep `.fourstory-launcher`, `.pr-launcher`, crash dumps, backups, and other unmatched content
   outside the new ledger. They are neither proof of installed state nor deletion candidates.
5. Disable the legacy launcher's update path after adoption so two updaters cannot race. Retain
   its current and previous snapshots until one NFour-managed update and launch succeeds; cleanup
   is a later, explicit operation.

If validation fails, the migration reports the mismatched paths and offers targeted repair or a
clean reinstall. It never writes a partial ledger that claims unverified legacy bytes.

---

## Q13 — Telemetry consent

Telemetry needs a consent surface, and consent requirements differ by publisher and region
(`brand=gamigo` may have different obligations than `brand=4story`).

**Needs:** whoever owns the privacy position.
**Recommendation:** default off, opt-in, with the failure-rate signal that gates rollout
collected from an anonymous aggregate that carries no device identity.

### Answer — off by default, configurable per deployment (2026-08-09)

Telemetry is disabled unless a deployment explicitly enables it. The setting is configurable
per publisher/brand so the deployment can apply its own regional consent requirements. Enabling
collection does not make telemetry authoritative: it remains diagnostic-only and does not gate
rollout. No client silently changes from the off default because a server begins accepting
events.
