# 14 — Open Questions

Decisions that need a human answer. Each carries a recommendation so nothing is blocked on
discussion alone — but the ones marked **BLOCKING** change work already scheduled in
[13-roadmap.md](13-roadmap.md) and should be settled before Phase 1 ends.

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

---

## Q4 — Which axes must be switchable post-install without redownloading unrelated content?

`language` and `ui` are stated. Open:

- Is `hd` a launcher toggle?
- Is `arch` ever changed in place (a 32→64-bit migration)?
- Is `brand` ever changed on an existing install?

Every axis marked switchable needs its packages kept **path-disjoint** from the packages of
other axes, which constrains the slice.

**Recommendation:** `language`, `ui`, `hd` switchable; `arch` and `brand` install-time only.

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

---

## Q7 — Small-file bundling: v1 or deferred? **BLOCKING — format decision**

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

Documents use `FourSaas.AutoUpdater.*` and a `4sup` CLI. The sibling repo prefixes with
`NFour` (`NFourServer`, `FourSer.Gen`). A trivial rename now; annoying later.

**Recommendation:** decide before the first commit. `NFour.AutoUpdater.*` is more consistent
with the sibling repo; `4sup` is a good CLI name either way.

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

---

## Q13 — Telemetry consent

Telemetry needs a consent surface, and consent requirements differ by publisher and region
(`brand=gamigo` may have different obligations than `brand=4story`).

**Needs:** whoever owns the privacy position.
**Recommendation:** default off, opt-in, with the failure-rate signal that gates rollout
collected from an anonymous aggregate that carries no device identity.
