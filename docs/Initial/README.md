# AutoUpdater — Initial Plan

Design documents for the 4SaaS AutoUpdater: a content-addressed, multi-backend patch
distribution and installation system for the 4Story client, 4SaaS server artifacts, and
general-purpose deployment.

**Status:** implementation baseline. The repository is being reconciled against this normative
document set; deferred features remain explicitly listed in [14-open-questions.md](14-open-questions.md).

## Reading order

| # | Document | What it answers |
|---|---|---|
| 01 | [Scope and requirements](01-scope-and-requirements.md) | What we are building, for whom, and what is explicitly out of scope |
| 02 | [Reference review](02-reference-review.md) | What we learned from `Apro.AutoUpdater_1` — keep list and drop list |
| 03 | [Architecture](03-architecture.md) | Solution layout, layering rules, core domain types |
| 04 | [Variant model](04-variant-model.md) | **The centerpiece.** Axes, packages, layer derivation, the resolution algorithm |
| 05 | [Repository format](05-repository-format.md) | Normative on-disk/on-wire spec: directory layout, every JSON schema, the blob CAS |
| 06 | [Storage backends](06-storage-backends.md) | S3 / FTP / HTTP / Local capability matrix and the port design that survives all four |
| 07 | [Client engine](07-client-engine.md) | Planner, applier, install ledger, crash safety, locked files, local CAS |
| 08 | [Publishing and validation](08-publishing-and-validation.md) | Slicing, the publish gate and its diagnostic codes, GC, promote/rollback |
| 09 | [Management API](09-control-plane-server.md) | The authoring authority: what the database owns, the static projection, anonymous reads, auth |
| 16 | [Publish protocol](16-publish-protocol.md) | **Read after 08.** Brokered upload, per-object grants, server-side verification and promotion |
| 10 | [Security](10-security.md) | Threat model, signing, trust-root pinning, canonicalization |
| 11 | [CLI](11-cli.md) | The `4sup` command surface |
| 12 | [Testing](12-testing.md) | Backend conformance suite, golden repository, fault injection |
| 13 | [Roadmap](13-roadmap.md) | Phases, sequencing, what ships when |
| 14 | [Open questions](14-open-questions.md) | Decisions required from the product owner, with recommendations |
| 15 | [Risk register](15-risks.md) | Ranked risks and their mitigations |
| 17 | [Signed documents](17-signed-documents.md) | **Normative.** Envelope format, sequences, mutability classes, rollback, yank, key lifecycle |
| 18 | [Normative contract](18-normative-contract.md) | **Normative.** Identifier grammar, digest domains, `FileSetId` framing, JSON rules, identity-only content |
| 19 | [Review disposition](19-review-disposition.md) | External review findings and where each was resolved |

## Topology

```
publisher ──auth──▶ MANAGEMENT API ──mints grant──▶ publisher ──bytes──▶ STORAGE
                    (owns the DB)                                          │
                          │ verify + server-side copy ────────────────────▶│
                          │ publishes static projection ──────────────────▶│
                                                                           │
                                              player ◀──anonymous GET──────┘
                                          (no credentials, ever)
```

**The API manages; storage stores bytes; players read anonymously.** No client-facing API request
or response ever carries payload bytes. The API brokers *access* — presigned PUT on S3, the
publisher's own admin credential on FTP — and moves bytes with server-side copy. It never authors
or signs a release lock or a channel pointer; those are signed outside it and submitted as opaque
bytes for verify-and-place, so an API compromise cannot produce an installable release.

A repository has **two endpoints**: an anonymous read URL and an authenticated write transport,
which need not be the same protocol. FTP is a write transport paired with a public HTTP read URL.
An optional [content gateway](06-storage-backends.md#6-the-content-gateway-optional) can front
any backend when you want TLS, caching, FTP connection pooling, or read auth you control.

## The one-paragraph summary

A **product** is a metapackage, not a file set. A **release** is a small immutable lockfile
that declares the variant **axes** (language, ui, arch, hd, brand) and pins N independently
versioned **packages**, attaching a `when` predicate over those axes to each pin. A client
binds a **selection** — a point in axis space — evaluates every predicate locally, sorts the
matched packages by a layer **derived from axis rank**, and merges their file tables into a
composed `path -> (contentHash, owningPackage, policy)` map. That map is the entire truth:
install is `compose(target) - state`, deletion is `state - compose(target)`, and a variant
switch is recompose-and-diff. All content lives in one flat content-addressed blob store, so
files shared between variants cost one copy. Every key a client fetches is computed
client-side, so a plain S3 bucket, an nginx static root, an FTP mirror and a local directory
are interchangeable. A management API owns authoring and brokers write access to storage, but is
never on the read path and never authors or signs a release lock or a channel pointer.

## How this maps to the original brief

| Requirement | Where it is answered |
|---|---|
| Multiple backends (S3, FTP, Local, HTTP read-only) | [06](06-storage-backends.md) — capability-split port, one conformance suite |
| Patch versions containing a set of files | [04](04-variant-model.md) §2 — a **package version**; a **release** composes many |
| Flat file repository identified by sha256/md5/… | [05](05-repository-format.md) §2 — sharded, algorithm-tagged, identity-encoded CAS |
| Multiple variants (languages, 2 UIs over the same paths) | [04](04-variant-model.md) — the whole document |
| Patch management | [08](08-publishing-and-validation.md) and [09](09-control-plane-server.md) |

## Provenance

The reference implementation studied is `D:\File\repos\work\Apro\_Apro\Apro\Apro.AutoUpdater_1`
(read-only; nothing in it was modified). It was analysed across seven areas — repository
abstraction, local flat repository, remote backends, client install engine, server domain
model, CLI surfaces, and tests/CI. Three competing variant models were designed
independently and scored; the recommended design is a synthesis. See
[02-reference-review.md](02-reference-review.md) for the full keep/drop assessment and
[04-variant-model.md](04-variant-model.md) §9 for why the winning model was chosen.
