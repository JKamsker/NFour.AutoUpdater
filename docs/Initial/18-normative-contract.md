# 18 — Normative Contract: Identifiers, Digests, Schemas (normative)

> Added in the redline responding to review findings **H1, H2, H6, M5, M13, M16**, and the parts
> of **C6** concerning exact byte framing. This document is normative.

The review's H1 was that [05](05-repository-format.md) "calls itself sufficient for a second
implementation" while leaving required-vs-optional fields, types, null handling, duplicate keys,
identifier grammar, Unicode normalisation and digest coverage undefined. This document supplies
the missing contract; `05` remains the *illustrative* walkthrough.

## 1. Identifier grammar

Every identifier that appears in an object key is restricted so that it is safe on S3, FTP, NTFS,
ext4 and APFS without escaping. **No percent-encoding anywhere** — encoding rules are where
interop bugs breed.

```
segment    = lowercase-alnum *( ("." / "-") lowercase-alnum )
lowercase-alnum = %x61-7A / %x30-39                      ; a-z 0-9
```

| Identifier | Grammar | Max | Notes |
|---|---|---|---|
| `productId` | `segment` | 128 | e.g. `fourstory.client` |
| `packageId` | `segment` | 128 | |
| `channel` | `segment` | 64 | `live`, `ptr`, `realm-phoenix` |
| `repositoryId` | `segment` | 64 | |
| `keyId` | `segment` | 64 | |
| `releaseId` | `segment` | 64 | `2026.02.15-a` is legal; `2026/02` is not |
| `PackageVersion.Label` | `segment` | 64 | **Path-bearing, therefore constrained** |
| `axis name`, `axis value` | `segment` | 32 | |

Rejected everywhere: uppercase, `.` or `..` as a whole segment, leading/trailing `.` or `-`,
consecutive `..`/`--`, path separators, control characters, whitespace, and any non-ASCII.

This tightens an earlier claim that version labels were "free-form" and "human-friendly". They
appear in `packages/{id}/{version}/package.json`, so they are path-bearing and cannot be
free-form. `2026-w32-hotfix` and `christmas-event` remain legal; `2026 W32 (hotfix)` does not.

**Ordering never parses the label.** `PackageVersion.Sequence` is the sole ordering authority
(§6).

## 2. Digest domain registry

Every digest field has exactly one defined input. A generic "digest" is never permitted.

| Field | Algorithm | Input bytes |
|---|---|---|
| `PackageFileEntry.h` | sha256 | The file's content bytes, as installed |
| `FileTableShardRef.digest` | sha256 | The shard blob's bytes (JSONL, as stored) |
| `FileTableRef.digest` | sha256 | `shard[0].digest ‖ shard[1].digest ‖ …` raw 32-byte digests, in ascending `index` order |
| `LockedPackage.manifestDigest` | sha256 | The `package.json` file bytes as stored |
| `ChannelPointer.releaseDigest` | sha256 | The release-lock **envelope** bytes (§[17](17-signed-documents.md) §2) |
| `ReleaseLock.coverageDigest` | sha256 | `coverage.json` bytes **with the `digest` member absent** (§2.1) |
| `VariantSelection.selectionId` | sha256 | UTF-8 of `ToCanonicalString()` (§3) |
| `ComposedFileSet.fileSetId` | sha256 | §4 |
| `BlobLocator` / CAS key | sha256 | The blob's content bytes (§5) |

### 2.1 Self-reference

`coverage.json` contains a `digest` member, so the digest is computed over the document **with
that member removed** — not with it empty, not with a placeholder. Producers serialise without
it, hash, then insert. Consumers remove it, hash, compare.

## 3. Canonical selection string

```
ToCanonicalString() = axis1 "=" v1 "," v2 ";" axis2 "=" v3 …
```

- Axes sorted by **name**, ordinal ascending.
- Values within an axis sorted **ordinal ascending** (not by declaration order — this string is
  an identity, not a precedence).
- No spaces. No trailing separator. Axes with an empty value set are **omitted**.
- Encoded UTF-8. Because §1 forbids non-ASCII in axis names and values, this is ASCII in practice.

`arch=x64;brand=4story;hd=off;language=de,en;ui=classic`

## 4. `FileSetId` framing

Fixes review finding **C6** — the earlier definition omitted install policy, so a file flipped
from `Replace` to `Preserve` produced an identical `FileSetId` despite different overwrite,
deletion and verification behaviour.

Input is the concatenation, over all entries **sorted by `path` ordinal ascending**, of:

```
path 0x00 "sha256:" hexlower 0x00 owner 0x00 policy 0x00 kind 0x00 mode 0x0A
```

| Field | Encoding |
|---|---|
| `path` | UTF-8 of `VirtualPath.Value`, NFC |
| hash | lowercase hex, algorithm-prefixed |
| `owner` | UTF-8 of the owning `PackageId` |
| `policy` | exactly `replace` \| `preserve` \| `executable` |
| `kind` | exactly `file` \| `dir` |
| `mode` | POSIX octal (`0644`, `0755`) or empty if unspecified |

Every record ends with `0x0A`, including the last. An empty set hashes the empty byte string.

Enum spellings are **fixed lowercase strings**, not integers — an integer enum silently
renumbers when a member is inserted.

## 5. Content identity: identity encoding only

Fixes review finding **C1**, which found that a CAS keyed on the *uncompressed* hash cannot be
storage-enforced when the stored object is *compressed*: S3 checksums the uploaded body, so the
value it validates is not the value the key asserts.

**Normative rule: blobs are stored identity-encoded. Stored bytes ≡ content bytes.**

```
blobs/sha256/{hash[0:2]}/{hash[2:4]}/{hash}
```

No `{enc}` suffix. No `.gz`. No `.zst`.

Consequences, all of which simplify the format:

| | Effect |
|---|---|
| **C1 closed** | `x-amz-checksum-sha256` now genuinely *is* the CAS key. `StorageEnforced` is sound on S3 single PUT rather than merely claimed to be. |
| **C8 closed** | Range offsets are content offsets. Resume is byte-append with a streaming hash. `.4sup/staging/{sha256}` is unambiguous — there is only one representation. |
| **`PackageFileEntry`** | `z` (storedSize) and `e` (encoding) are **removed**. `s` is the only size. |
| **PKG013** | The `EncodingFork` diagnostic and `RepositoryDescriptor.canonicalEncoding` are removed — there is nothing to fork. |
| **GC** | Marks `contentHash` alone, which closes review finding **H7** (a mark set of `(hash, canonicalEncoding)` pairs missed live identity-encoded blobs and would sweep them). |

The cost is bandwidth on compressible blobs. It is small in practice: the previous policy only
attempted compression above 10 MiB and discarded any result above a 0.75 ratio, and the dominant
large assets in a game client (DDS textures, packed archives, audio) are already compressed.

**Transport compression remains forbidden** ([05](05-repository-format.md) §2.3) — that rule was
never about at-rest encoding and is unaffected.

> If at-rest compression is ever reintroduced, it must come back as an explicit per-repository
> mode that forces `ServerVerified` for every compressed object *and* specifies the two-stage
> encoded-then-decoded staging protocol. It cannot be bolted on.

## 6. Sequence allocation

Fixes review finding **M11**.

| Sequence | Scope | Allocation |
|---|---|---|
| `PackageVersion.Sequence` | per `packageId` | Allocated by the API, strictly increasing, gap-permitted |
| `releaseSequence` | per `productId` | Allocated by the API, strictly increasing |
| `channelSequence` | per `(productId, channel)` | Allocated by the API, strictly increasing |
| `keySequence`, `revocationSequence` | per repository | Allocated by the signer, strictly increasing |

Allocation is a **control-plane invariant**, enforced by a unique constraint and allocated inside
the publishing transaction. Manual assignment is rejected. Concurrent publication serialises on
the constraint; the loser retries with a fresh value.

`minimumInstalledRelease` (review **M10**) is expressed as a **`releaseSequence` integer**, not a
release-id string, so the comparison is total and stable. An installed release whose sequence is
unknown to the client — because it rolled back locally, or the release was pruned — is treated as
*not satisfying* the floor, and the client performs a full resolve rather than an incremental one.

## 7. JSON rules

Apply to every document in every class.

| Rule | Behaviour |
|---|---|
| **Duplicate keys** | **Hard error.** Not last-wins. A duplicate key is a signature-evasion primitive. |
| **Unknown fields** | Class A/B/C (trusted): **reject**. Class D (derived): ignore. Per [05](05-repository-format.md) §11. |
| **Unknown `schemaVersion`** | Reject, with a message naming `minimumClientVersion`. |
| **`null`** | Never valid. An absent optional field is omitted, never null. |
| **Numbers** | Integers only, within `int64`. No floats, no exponents, no leading zeros. |
| **Strings** | Valid UTF-8, Unicode **NFC**. Lone surrogates rejected. |
| **Empty arrays/objects** | Legal and distinct from absent. |
| **Byte fields** | base64url unpadded (RFC 4648 §5), never standard base64. |
| **Timestamps** | RFC 3339 UTC with a literal `Z`, second precision. Never a local offset. |

### 7.1 Schemas are test fixtures

A versioned JSON Schema (2020-12) ships for every document under `schemas/`, and the test suite
validates the golden repository against them ([12](12-testing.md)). Schemas are generated from the
C# models so they cannot drift — the reference hand-wrote a schema and it drifted.

Valid *and invalid* fixtures ship for each: `schemas/fixtures/{doc}/valid-*.json` and
`invalid-*.json`, each invalid one naming the rule it violates.

## 8. Paths, case and time

### 8.1 Case-collision key (review **M13**)

Invariant-lowercase is not identical to any real filesystem's comparison. The publish gate uses a
deliberately **conservative** collision key — it over-rejects rather than risk a silent merge:

```
FoldedKey = NFC(path) → Unicode simple case-fold → strip trailing dots and spaces per component
```

Two entries whose `FoldedKey` matches but whose `Value` differs are `PKG009`. This rejects some
pairs that would be distinct on ext4; that is the correct trade, because the alternative is an
install that silently loses a file on Windows.

### 8.2 Path length (review **M14**)

Publish-time validation uses a portable budget (260 chars against a nominal root). It cannot know
the real root, so the **client re-validates against the actual absolute install root plus the
longest staging and rename-aside suffix** before download begins, and fails with exit code 4
(precondition) rather than part-way through an apply.

### 8.3 mtime (review **M16**)

`state.jsonl` `mt` is **UTC epoch seconds**, integer. It is a *cache key only* — never integrity
evidence. Comparison uses a **±2 s tolerance** to survive FAT/exFAT 2-second granularity and
network filesystems. Any mismatch triggers a re-hash; a match skips it. The hash is always the
authority.

## 8.4 Retired axis values (review **M12**)

`AxisDefinition.Retired` maps a removed value to a replacement. Closure rules:

- Remapping is applied **transitively** to a fixed point, with a hard cap of 8 hops.
- A **cycle** is `PKG017 RetirementCycle` (Error) at publish. Cycles are detected on the whole
  map, not per lookup.
- **Convergence is legal**: several retired values may map to one live value.
- A replacement that is itself retired is resolved onward; a replacement that is **absent from
  `Values`** after resolution is `PKG018 RetirementDeadEnd` (Error).
- Publish validation resolves every retirement to a terminal live value and **stores the
  resolved map**, so a client never walks a chain at resolve time.

## 9. Diagnostic code stability (review **M5**)

One code, one condition. `PKG005` previously covered both `DanglingPin` and `DigestMismatch`;
these are now separate:

| Code | Condition |
|---|---|
| `PKG005 DanglingPin` | A requirement references a package absent from `packages[]` |
| `PKG015 ManifestDigestMismatch` | A pinned manifest's bytes do not match `manifestDigest` |

Codes are **append-only**. A retired code is reserved forever and never reused for a different
condition. The full registry lives in [08](08-publishing-and-validation.md) §3 and
[04](04-variant-model.md) §6; this rule governs both.
