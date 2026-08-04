# 19 — External Review Disposition

Source: `4sup-spec-review.md`, 2026-08-04. Scope: README and `01`–`16`.
Verdict given: *architectural direction approved; implementation against the current text not
approved.*

Eight of its specific claims were re-checked against the text before acting; all eight held.
**All ten critical findings are accepted.** This document records where each was resolved so a
re-review can check the redline rather than re-derive it.

## Critical

| # | Finding | Disposition | Where |
|---|---|---|---|
| C1 | Storage-enforced CAS incompatible with compressed blobs | **Fixed** — at-rest compression removed; stored bytes ≡ content bytes, so the enforced checksum genuinely is the key | [18](18-normative-contract.md) §5, [05](05-repository-format.md) §2.2, [16](16-publish-protocol.md) §5 |
| C2 | Signature byte model ambiguous and recursive | **Fixed** — one detached envelope, base64url payload, domain-separated signing input, no implementation choice | [17](17-signed-documents.md) §2 |
| C3 | Rollback conflicts with sequence anti-replay | **Fixed** — `channelSequence` separated from `releaseSequence`; rollback is a *new* signed pointer at a higher channel sequence naming an older release | [17](17-signed-documents.md) §4 |
| C4 | Immutability contradicts yanking and indexes | **Fixed** — four mutability classes; yank is a signed revocation document, not a mutation | [17](17-signed-documents.md) §3, §4.5 |
| C5 | No rule for equal layer, different discriminator | **Fixed** — full precedence-tuple table incl. `RES007`; discriminator now the minimum *declared value index*, not a lexical `Min()` | [04](04-variant-model.md) §3.2, §5 step 11 |
| C6 | `FileSetId` omits install policy | **Fixed** — framing covers path, hash, owner, policy, kind, mode with exact byte encoding | [18](18-normative-contract.md) §4 |
| C7 | "Pure" planner performs filesystem I/O | **Fixed** — `ITreeScanner` produces an `ObservedTreeSnapshot` that is an explicit planner input; `Adopt` state added so the ledger stops asserting unverified content | [07](07-client-engine.md) §1, §2.1 |
| C8 | Compressed download/resume/staging underspecified | **Fixed by C1** — identity encoding makes range offsets content offsets; resume is byte-append with a streaming hash | [07](07-client-engine.md) §3.3 |
| C9 | Hardlink-first materialisation corrupts the shared CAS | **Fixed** — reflink-first, copy-default; hardlinks only under an opt-in `immutable-install` profile; CAS entries re-verified on drift | [07](07-client-engine.md) §5, §5.1 |
| C10 | Lexical path validation does not stop privileged escape | **Fixed** — handle-based traversal, no-follow at every component, parent-identity re-check before mutation, root preconditions | [07](07-client-engine.md) §3.7 |

### Where the review's proposed fix was not adopted

**C1, option 1 (dual digest).** Carrying `storedHash` alongside `contentHash` does *not* close
the hole. Binding `storedHash` proves the uploaded bytes are what the publisher declared; it
proves nothing about what they decompress to, so a publisher can declare an honest `storedHash`
for garbage plus an arbitrary `contentHash` and still have garbage promoted to that key. Only
two things actually close it — stored ≡ content, or the server decodes and hashes. Identity-only
storage was chosen because it *also* eliminates C8 and removes an entire representation axis from
the format.

## High

| # | Finding | Disposition |
|---|---|---|
| H1 | Format illustrative, not normative | **Fixed** — [18](18-normative-contract.md); schemas generated from the C# models and used as test fixtures |
| H2 | Identifier grammar missing | **Fixed** — [18](18-normative-contract.md) §1; version labels constrained because they are path-bearing |
| H3 | `ContentHash` not guaranteed immutable/valid | **Fixed** — fixed 32-byte inline buffer, not `ReadOnlyMemory`; parse-only construction ([03](03-architecture.md) §4.1) |
| H4 | Override metadata has competing authorities | **Fixed** — `PackageRequirement.Overrides` is sole authority; `PackageManifest.Overrides` removed; `PKG016` catches denormalisation drift ([04](04-variant-model.md) §4.2) |
| H5 | Pairwise validation not proven complete | **Fixed** — completeness restated over (pair × discriminator outcome) with a property test as the obligation ([08](08-publishing-and-validation.md) §3.1) |
| H6 | Digest domains undefined | **Fixed** — digest registry, incl. `coverage.json` self-reference exclusion ([18](18-normative-contract.md) §2) |
| H7 | GC can delete live identity-encoded blobs | **Fixed** — mark by `contentHash` alone; supported-update-window retention added ([08](08-publishing-and-validation.md) §5.2) |
| H8 | Mirror assumes cross-origin operations that don't exist | **Fixed** — same-store promotion separated from cross-store mirroring; native copy is a probe-gated optimisation |
| H9 | Conformance tests ignore declared capabilities | **Fixed** — capability-conditioned; unsupported ops must be absent from the typed surface; composite profiles tested ([12](12-testing.md) §2) |
| H10 | `If-Range` vs `412` confusion | **Fixed** — correct status table; weak ETag disables resume ([07](07-client-engine.md) §3.3) |
| H11 | Roadmap postpones the trust boundary | **Fixed** — envelope, digest domains, grammar and content identity are Phase 0/1 exit criteria; first repository built from signed golden fixtures ([13](13-roadmap.md)) |
| H12 | CLI cannot satisfy the signed-channel workflow | **Fixed** — `promote`/`rollback`/`yank` all require `--sign`; local rollback takes an explicit `--to`; repeated `--select`; disjoint exit codes ([11](11-cli.md)) |
| H13 | Key rotation/revocation/TOFU incomplete | **Fixed** — signed `keys.json` chained from an already-trusted key, monotonic, overlapping windows, never-empty rule, two trust profiles ([17](17-signed-documents.md) §5) |
| H14 | Unsigned layout templates need trust bounds | **Fixed** — placeholder allowlist, relative keys only, same-origin mirrors, SSRF bounds server-side ([10](10-security.md) §6.1) |
| H15 | Bounded-memory claim needs a budget | **Fixed** — NFR-5 restated as a measured budget with a supported file ceiling |
| H16 | Build hash cache can accept changed bytes | **Fixed** — file identity added to the cache key; full-rehash required for release-signing builds |
| H17 | Hook failure semantics not transactional | **Fixed** — hook state tracked separately with idempotency keys; the updater no longer claims to roll back side effects ([08](08-publishing-and-validation.md) §7.1) |
| H18 | Gateway auth conflicts with the anonymous invariant | **Fixed** — two named distribution profiles; tokens never enter signed identity ([10](10-security.md) §6.2) |

## Medium

M1 (stale README), M3 (dead anchor), M4 (duplicated FR-1 table), M5 (`PKG005` split into
`PKG005`/`PKG015`), M6 (Core/I-O layering — `FileInfo` overloads moved out of Core), M8 (API
repository scoping), M9 (package lifecycle out of immutable manifests), M10
(`minimumInstalledRelease` as a sequence integer), M11 (sequence allocation as a control-plane
invariant), M13 (conservative case-fold collision key), M14 (client-side path-length re-check),
M16 (mtime units and tolerance), M17 (reconciler authority by class) — **all fixed**, mostly in
[18](18-normative-contract.md) and [09](09-control-plane-server.md).

M2 (open questions contain resolved/contradictory items) — **fixed** by reclassifying
[14](14-open-questions.md); M12 (retired-axis closure) and M15 (preserve verify/repair semantics)
and M18 (anonymous API view identity) — **fixed** in [18](18-normative-contract.md) §8.4,
[07](07-client-engine.md) §2.1 and [17](17-signed-documents.md) §2.3 respectively.

## Exit criteria status

The review's twelve "implementation-ready" criteria:

| # | Criterion | Status |
|---|---|---|
| 1 | One signed-envelope format with cross-language golden vectors | **Specified**; vectors are a Phase 0 deliverable |
| 2 | Compression and S3 upload integrity reconciled, proven by negative tests | **Specified** — resolved by removal; negative test is a Phase 3 exit gate |
| 3 | Rollback/yank/rotation use new monotonic signed control documents | **Specified** |
| 4 | Composition defines every case; `FileSetId` covers all apply semantics | **Specified** |
| 5 | Every document versioned-schema'd; every digest has a byte domain | **Specified**; schemas are a Phase 0 deliverable |
| 6 | All path-bearing identifiers have a canonical safe grammar | **Specified** |
| 7 | Planner receives explicit observations; preserve adoption truthful | **Specified** |
| 8 | Compressed resume executable as written | **N/A** — no compressed representation exists |
| 9 | Elevated writes cannot escape via links/reparse points | **Specified**; adversarial tests enumerated |
| 10 | Shared CAS not mutable through installed files by default | **Specified** |
| 11 | GC marks exact live representations; retention policy | **Specified** |
| 12 | Capability-conditioned backend tests; roadmap starts with signed fixtures | **Specified** |

All twelve are addressed **in specification**. Four (1, 2, 5, 9) carry deliverables — golden
vectors, JSON Schemas, the negative integrity test, and the adversarial path suite — that are
scheduled but not yet written, and none of them can be declared satisfied until they exist and
pass.

## Findings not accepted as defects

None. Every finding was either fixed or, in the single case of C1's recommended option, fixed by
a different mechanism that the review itself identified as sound in a subclause.
