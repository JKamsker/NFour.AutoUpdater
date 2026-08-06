# 01 — Scope and Requirements

## 1. Problem statement

4Story ships a large native game client (order of 10^5 files, tens of GB) that must be kept
current on player machines over unreliable consumer connections. The same distribution
machinery is also wanted for 4SaaS server artifacts and as a general-purpose deployment
tool. Three properties make this harder than a conventional updater:

1. **The content is served from storage we do not control the semantics of.** A patch mirror
   may be an S3 bucket, an FTP server, a plain nginx static root, or a local/UNC directory.
   Two of those cannot list a directory, one cannot do conditional requests at all, and only
   one can do compare-and-swap.
2. **The same product ships in many simultaneous variants.** Five languages, two mutually
   exclusive UIs whose assets occupy *the same virtual paths*, x86/x64, an optional HD
   texture set, and publisher branding. A player must be able to switch a variant after
   install without redownloading the other 40 GB.
3. **Wrong deletions destroy player data.** Savegames, user configs, screenshots and mods
   live inside the install root. An updater that deletes "files not in the patch" without
   knowing what it put there is a data-loss incident waiting to happen.

## 2. Consumers

All three are in scope. The library is the product; the other two are its first callers.

| Consumer | Needs |
|---|---|
| **Game client patcher** | Variant selection and post-install switching, progress/pause/resume, explicit locked-file policies while the launcher runs, self-update of the updater, resumable multi-GB downloads |
| **Server / deployment artifacts** | Headless publish from CI, rollback, staged rollout, exit codes suitable for pipeline gating; Windows service/process control is out of scope |
| **Reusable library + CLI** | A narrow public surface, no Windows-only types on the cross-platform path, an in-memory backend so consumers can test without infrastructure |

## 3. Functional requirements

### FR-1 — Storage backends

Backends are **dumb byte stores**. A repository has **two endpoints** — an anonymous read URL and
an authenticated write transport, which need not be the same protocol.

| Backend | Read | Write | Notes |
|---|---|---|---|
| Local file system | ✔ | ✔ | Reference implementation for the commit protocol |
| S3 and S3-compatible (MinIO, R2, B2) | ✔ | ✔ per-object presigned grant | The only backend with CAS, server-side copy and scoped grants |
| FTP / FTPS | — | ✔ admin creds, single-publisher | **Write transport only**, paired with a public HTTP read URL. Reduced integrity guarantee |
| HTTP(S) read-only | ✔ | ✘ | The default read path; also a mirror target |
| Content gateway (optional) | ✔ | ✘ by default | Fronts any backend: TLS, caching, FTP pooling, optional read auth |

The read path must work with nothing but "GET a key I computed myself, optionally with a byte
range." No listing, no query strings, no server-side computation, no credentials. See
[06-storage-backends.md](06-storage-backends.md).

### FR-2 — Patch versions

A patch version is an immutable, named set of files with metadata. In the new model this is a
**package version**; a **release** composes several package versions into the thing a client
installs. See [04-variant-model.md](04-variant-model.md) §2 for why the level was split.

Required lifecycle: `create → add files → publish (immutable) → promote to channel → rollback`,
plus `yank` and `prune`.

### FR-3 — Flat content-addressed repository

All content lives in one blob store shared by every product, version and variant, keyed by the
SHA-256 of its bytes and stored **identity-encoded** ([18](18-normative-contract.md) §5). Identical bytes stored once, regardless of which variant or
version references them. The hash algorithm must be explicit in the key so a future migration
to blake3/sha256 does not require a new store. See [05-repository-format.md](05-repository-format.md) §2.

### FR-4 — Variants

A product declares independent **axes**. A client selects a point in axis space and receives
exactly the right file set. Concretely for the 4Story client:

| Axis | Cardinality | Values | Post-install switchable |
|---|---|---|---|
| `arch` | one | x64, x86 | no (see [14](14-open-questions.md) Q4) |
| `ui` | one | classic, modern | **yes** — the driving case |
| `language` | many | en, de, fr, tr, pl | **yes** |
| `hd` | one | off, on | yes |
| `brand` | one | 4story, gamigo | no |

Hard constraints the model must satisfy:

- **Dedup.** Files identical across variants occupy one blob.
- **Same path, different content.** `ui.classic` and `ui.modern` both ship `data/ui/main.dat`;
  resolution must be deterministic and total.
- **Correct deletion on switch.** Switching classic→modern must delete `ui/classic/**` and
  must not delete a savegame.
- **No combinatorial explosion.** 2 × 2 × (2^5−1) × 2 × 2 = 496 legal selections must not
  produce 496 manifests.
- **Static resolution.** All of the above with only `GET`.

### FR-5 — Patch management

Publish, validate, promote, rollback, prune, garbage-collect, mirror, verify, explain.
Publishing must be gated by mechanical validation that a release is total and unambiguous —
a release that would produce a wrong install must fail CI, not the player. See
[08-publishing-and-validation.md](08-publishing-and-validation.md).

### FR-6 — Management API

The API is the **authoring authority**: it owns the database (products, packages, releases,
axes, requirements, pins, channels, blob placement) and brokers write access to storage. It
publishes a static projection of its state so players can resolve and install without it.

Hard constraints:

- **No client-facing API request or response carries payload bytes.** The API mints per-object
  grants (S3 presigned PUT, Azure SAS) and moves bytes with server-side copy.
- **Reads are anonymous; auth exists only on the write path.** Players hold no credential.
- **No device targeting.** Every device gets the latest version of the channel it follows. There
  is no device registry, no per-device pinning, and no update queue. Rollout is
  `promote ptr → live`. Server-scoped content ("special servers") is a *composition* problem
  handled by the variant model, not a targeting problem.
- **The API never authors or signs a release lock or a channel pointer** — otherwise a compromise
  yields arbitrary signed releases.
- **With the API down, every player operation still works** against a mirror.

See [09-control-plane-server.md](09-control-plane-server.md) and
[16-publish-protocol.md](16-publish-protocol.md).

## 4. Non-functional requirements

| ID | Requirement |
|---|---|
| NFR-1 | **Crash safety.** Power loss at any point leaves a state that either converges on re-run or is exactly the pre-update state. Never a half-installed tree with no record of it. |
| NFR-2 | **Integrity and authenticity.** Every byte verified against its content hash on arrival. Every mutable pointer signed, with the trust root outside the repository. |
| NFR-3 | **Resumability.** A 40 GB download interrupted at 39 GB resumes, and knows the difference between "network hiccup" and "the object changed". |
| NFR-4 | **Determinism.** `(releaseDigest, selectionId)` determines the composed file set bit-identically on every OS, forever. Support can reproduce a player's exact content from two hashes. |
| NFR-5 | **Bounded working set.** File tables stream and are never fully materialised. Composition, the plan and the ledger are O(files-in-selection) by design — budgeted at ≤ 400 MB for 200k files, with a stated supported ceiling of 10⁶ files per selection. Beyond that, external sort. This is a *budget*, not an O(1) claim (review **H15**). |
| NFR-6 | **Cross-platform.** Windows is the primary client target; Linux is a first-class publish/serve target. No Windows-only type on the shared path. |
| NFR-7 | **Testability without infrastructure.** The full backend surface exercisable in-memory; containers only for conformance runs. |
| NFR-8 | **Forward compatibility.** Every document carries `schemaVersion` and a defined unknown-field policy. An old client must fail loudly and legibly, never silently wrongly. |

## 5. Explicit non-goals

- **A package manager for third-party software.** Single-publisher, single trust root.
- **Dependency solving on the client.** Solving happens once, at publish, and its answer is
  the lockfile. The client validates; it never searches. This is what keeps the read path
  implementable over static GET.
- **Peer-to-peer / BitTorrent distribution.**
- **In-place migration from the reference's repository format.** The reference is a design
  input, not a data source. If Apro repositories ever need importing, that is a one-off tool.
- **A GUI.** The launcher is a separate consumer; this project ships the library and CLI, and
  an IPC surface the GUI can drive. Avalonia is available in the sibling repo if a reference
  GUI is later wanted.

## 6. Deferred, with the hooks reserved now

These are **not** in v1, but the format reserves the fields so adding them is not a breaking
change. Both must be *decided* before v1 ships because both alter the file-table schema —
see [14-open-questions.md](14-open-questions.md) Q7 and Q8.

- **Sub-file delta / content-defined chunking.** Content addressing dedupes identical files
  and does nothing for a 2 GB archive where 4 MB changed — the normal shape of a game content
  patch. Retrofitting means a second blob namespace or a schema bump.
- **Small-file bundling.** 50k changed tiny files is 50k GETs: minutes of latency on HTTP/2,
  hours on FTPS with a 4-connection cap, and roughly $2,000 in S3 request charges per patch at
  100k players. Recommended for v1; see [15-risks.md](15-risks.md) R-3.

## 7. Technology decisions

Settled, matching the 4SaaS sibling repo (`D:\File\repos\4Story\4SaaS\4SaaS`):

| Decision | Value | Rationale |
|---|---|---|
| Runtime | .NET 10, `net10.0` | Sibling repo standard; cross-platform; strong `System.IO.Pipelines`/`Channels` |
| Language | C#, file-scoped namespaces, NRT enabled, warnings as errors | `rules/csharp.md` |
| Packages | Central via `Directory.Packages.props`, pinned, no version < 3 days old | `rules/csharp.md` §5 |
| Tests | xUnit v3, `Microsoft.NET.Test.Sdk`, coverlet | Sibling repo standard |
| CLI | `Spectre.Console.Cli` | Already a pinned dependency in the sibling repo |
| DI / config / logging | `Microsoft.Extensions.*` 10.0.0 | Sibling repo standard |
| Server persistence | EF Core + Npgsql (PostgreSQL) | Sibling repo has already cut over to Postgres |
| Architecture enforcement | `NetArchTest.Rules` | Already pinned; used to enforce the layering in [03](03-architecture.md) |

**Naming is provisional.** Documents use `FourSaas.AutoUpdater.*` and a `4sup` CLI. The
sibling repo prefixes with `NFour` (`NFourServer`, `FourSer.Gen`). Aligning is a trivial
rename but should be decided before the first commit — [14](14-open-questions.md) Q10.

## 8. Glossary

| Term | Meaning |
|---|---|
| **Blob** | An immutable byte sequence in the CAS, addressed by the hash of its *uncompressed* content |
| **Package** | An independently versioned, immutable set of files. The unit of authoring and release |
| **Release** | A lockfile pinning package versions plus the axis declarations and `when` predicates |
| **Channel** | A named mutable pointer to a release (`live`, `ptr`, `internal`). The only mutable object in a repository |
| **Axis** | An independent variant dimension (`language`, `ui`) |
| **Selection** | A client's chosen point in axis space |
| **Layer** | Composition precedence, **derived** from axis rank. Higher layers overwrite lower |
| **Composed file set** | The resolved `path -> (hash, owner, policy)` map. The single source of truth for an install |
| **Ledger** | `state.jsonl` — the client's record of every file it owns, with its owning package. The deletion authority |
| **Variant switch** | Changing a selection on an existing install; a diff, not a reinstall |
