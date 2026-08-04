# 05 — Repository Format (normative)

This document is the wire/disk contract. It is the artefact a second implementation would be
written against, so it is deliberately precise. Anything under-specified here becomes an
interop bug later.

**Format version:** `schemaVersion: 1` on every document.

## 1. Directory layout

```
{root}/                                              # S3 prefix | FTP dir | local dir | HTTP root
  repo.json                                          # descriptor: layout, capabilities, trust roots

  blobs/                                             # ONE flat sharded CAS: content AND file-table shards
    sha256/7c/19/7c19a4f1…e8
    sha256/aa/10/aa10bd07…31                         # a file-table shard — shards are blobs too

  packages/
    fourstory.client.core/
      index.json                                     # newest-first, explicit order, never truncated
      index.0.json                                   # deep-history pages
      1.4.7/package.json                             # immutable, cache-forever
    fourstory.client.ui.classic/1.4.0/package.json
    fourstory.client.ui.modern/2.0.1/package.json
    fourstory.client.lang.common/1.2.0/package.json
    fourstory.client.lang.de/1.4.7/package.json

  products/
    fourstory.client/
      product.json                                   # editorial only: display names, icons
      channels/
        live.json                                    # THE mutable file. promote/rollback target
        ptr.json
      releases/
        index.json
        2026.02.15-a/
          release.lock.json                          # canonical, immutable
          release.bundle.json                        # lock + inlined manifests — the client's ONE fetch
          coverage.json                              # per-point matrix + digest

```

**Staging is NOT in this tree.** An earlier draft placed `_staging/{sessionId}/` inside `{root}`,
which contradicted invariant I-4 below. With a public bucket or a CDN fronting the root, that
turns any publisher credential into arbitrary file hosting on the game's own trusted domain and
leaks unreleased builds to anyone who can compute an expected file's digest. Staging lives in a
**separate bucket, a separate FTP root, or a prefix explicitly denied by bucket policy and CDN
origin rule** — see [16](16-publish-protocol.md) §7.3.

```
{stagingRoot}/                                       # SEPARATE from {root}; never served
  {sessionId}/{grantId}                              # key is the opaque grantId, NOT the hash
```

The key is the server-generated `grantId`, not the client-claimed content hash. A hash-derived
staging key is predictable, which lets anyone who can compute the digest of an expected file read
staged prerelease content directly, with no listing required.

And on the client:

```
{installRoot}/.4sup/
  state.jsonl                                        # head record = install lock; then owned files
  plan.json                                          # present only mid-apply ⇒ resumable
  staging/{sha256}                                   # staged blobs; moved into place on commit
  cache/                                             # digest-keyed metadata cache
```

### 1.1 Invariants

These are what make one tree work identically over four backends. Each is asserted by
`4sup verify-repo`.

| # | Invariant |
|---|---|
| I-1 | Every key is computable client-side from `(productId, releaseId, channel, packageId, version, hash, shardIndex)`. **No LIST, no query strings, no server-side computation on the read path, ever.** |
| I-2 | All keys are **lowercase**. The reference's `Products`/`products` split brain ([02](02-reference-review.md) §2.3) worked only on Windows. |
| I-3 | Exactly one type owns every key template (`RepositoryLayout`). Nothing bypasses it. |
| I-4 | Staging never happens inside a served prefix — enforced by a live `GET` against a known staging key requiring 403/404. The reference wrote `{sha}.tmp` into `blobs/`, briefly exposing partial blobs over HTTP. |
| I-8 | A blob exists in the CAS only after the API has **read its bytes from storage and verified the hash**. `blobs/query` reports a hash present only for placements with a non-null `verifiedAt`. See [16](16-publish-protocol.md) §5. |
| I-5 | Every artifact belongs to exactly one mutability class ([17](17-signed-documents.md) §3). Class A/B are immutable once written; class C is mutable but monotonic and signed; class D is mutable, unsigned, and **never trusted for an install decision**. |
| I-6 | No blob is ever served with an HTTP `Content-Encoding` header. See §2.3 — this is a day-one total-failure risk. |
| I-7 | Blobs are stored identity-encoded. Stored bytes ≡ content bytes; there is exactly one representation per hash ([18](18-normative-contract.md) §5). |

### 1.2 Required cache headers

Asserted by `verify-repo` with a **live HTTP request against the origin**, not a config lint.

| Prefix | `Cache-Control` |
|---|---|
| `blobs/**`, `packages/**/package.json`, `products/*/releases/*/**` | `public, max-age=31536000, immutable` |
| `products/*/channels/*.json` | `max-age=30, must-revalidate` |
| `repo.json` | `max-age=300` |

> A wildcard `immutable` rule applied across the whole bucket **disables channel rollback
> during an incident** — the one moment it is needed. This is the highest-severity operational
> failure mode in the design and it gets its own diagnostic, `PKG011 CacheHeaderRisk`.

## 2. The blob store

### 2.1 Addressing

```
blobs/{alg}/{hash[0:2]}/{hash[2:4]}/{hash}
```

- `{alg}` — `sha256` today. It is tagged so blake3 or sha512 can coexist without a new store;
  the reference hardcoded sha512 into the path shape and could not evolve. SHA-256 is chosen
  because it is the only strong digest S3 can enforce at PUT, which lets storage validate the
  CAS key itself ([16](16-publish-protocol.md) §5.1).
- `{hash}` — **lowercase** hex of the hash of the blob's bytes.
- **No encoding suffix.** Blobs are stored identity-encoded; stored bytes ≡ content bytes.
  See [18](18-normative-contract.md) §5 for why at-rest compression was removed.
- 2+2 hex sharding = 65,536 leaf directories. Free on S3; necessary on NTFS and ext4, which
  degrade badly past ~10^5 entries in one directory. Note the cost on FTP (§ [06](06-storage-backends.md) §2.2).

A blob's identity is its content, and its stored representation is identical to its content.
There is exactly one representation per hash, so a manifest entry cannot disagree with a stored
object about encoding — the failure mode the reference had, where two versions referencing one
digest disagreed on `FileMetaDataDto.IsCompressed` and one of them would 404.

### 2.2 No at-rest compression

Blobs are stored exactly as their content. This is normative — see
[18](18-normative-contract.md) §5. Briefly:

- It is what makes S3's `x-amz-checksum-sha256` genuinely *be* the CAS key, so
  `StorageEnforced` integrity is sound rather than merely asserted.
- It makes range offsets content offsets, so resume is a byte-append with a streaming hash and
  the staging path `{sha256}` is unambiguous.
- The cost is small in practice: the previous policy only attempted compression above 10 MiB and
  discarded any result above a 0.75 ratio, and a game client's large assets — DDS, packed
  archives, audio — are already compressed.

Consequently `PackageFileEntry` has **one** size field, and `RepositoryDescriptor.canonicalEncoding`
and the `PKG013 EncodingFork` diagnostic no longer exist.

**Transport compression remains forbidden** (§2.3). That rule concerns the HTTP `Content-Encoding`
header and is unrelated to at-rest encoding.

### 2.3 The Content-Encoding hazard — TIER 1

Blobs are stored and served as raw content bytes with no encoding of any kind. If the origin applies
transport compression — nginx `gzip on` / `gzip_static on`, CloudFront or Cloudflare
auto-compression, or an uploader setting `Content-Encoding` on the S3 object — then:

- a .NET client with `AutomaticDecompression` enabled receives **decoded** bytes, so the
  SHA-256 check fails **100% of the time**; and
- `Range` offsets refer to **encoded** octets, so every resume is wrong.

The reference never configured decompression on its patch clients at all (the only
`AutomaticDecompression` in the tree is `Apro.AutoUpdater.PlayGround/Program.cs:19`) and
survived purely because Azure Blob does not set the header. That luck is not portable to nginx
or Cloudflare.

**Required, non-negotiable:**

1. `HttpClientHandler.AutomaticDecompression = DecompressionMethods.None` on every blob client.
2. Any `Content-Encoding` header on a blob response is a **hard error** naming the offending key.
3. Publishing must never set `Content-Encoding` on an uploaded blob.
4. `verify-repo` issues a live `HEAD` and fails on `Content-Encoding`, or on a missing
   `Accept-Ranges`.

## 3. `repo.json` — the descriptor

```json
{
  "schemaVersion": 1,
  "repositoryId": "4story-live",
  "generatedAt": "2026-02-15T10:00:04Z",
  "layout": {
    "blobTemplate":         "blobs/{alg}/{h0:2}/{h2:2}/{hash}",
    "packageTemplate":      "packages/{packageId}/{version}/package.json",
    "packageIndexTemplate": "packages/{packageId}/index{page}.json",
    "channelTemplate":      "products/{productId}/channels/{channel}.json",
    "releaseTemplate":      "products/{productId}/releases/{releaseId}/release.lock.json",
    "releaseBundleTemplate":"products/{productId}/releases/{releaseId}/release.bundle.json",
    "coverageTemplate":     "products/{productId}/releases/{releaseId}/coverage.json",
    "releaseIndexTemplate": "products/{productId}/releases/index{page}.json"
  },
  "contentHashAlgorithm": "sha256",
  "capabilities": ["read", "range"],
  "integrityGuarantee": "verified",
  "products": ["fourstory.client"],
  "blobBaseUrls": [
    "https://cdn.4story.com/live/",
    "https://cdn2.4story.com/live/"
  ],
  "minimumClientVersion": "1.0.0",
  "trustedKeys": [
    { "keyId": "4s-2026", "algorithm": "ed25519", "publicKey": "MCowBQYDK2VwAyEA…" }
  ]
}
```

`blobBaseUrls` is an **ordered array**, not a single string, with sticky failover and the rule
*the first mirror serving byte-correct content wins*. The reference declared exactly this
indirection (`defaultCdn` + per-file `cdn`) and then ignored it in both readers
([02](02-reference-review.md) §2.8); an array with defined failover semantics is the minimum
that avoids repeating that.

`integrityGuarantee` is `verified` when every blob in the CAS was hash-verified server-side
before promotion, and `reduced` when the origin is a backend where that cannot be enforced —
in practice FTP, where the publisher's credential is tree-wide and can write `blobs/**` directly.
`verify-repo` reports the weaker property rather than implying the stronger one. See
[16](16-publish-protocol.md) §5.2.

`trustedKeys` in this document is **advisory only** — the real trust root is pinned in the
client. See [10-security.md](10-security.md) §3; a key list fetched over the channel it is
meant to authenticate authenticates nothing.

## 4. `products/{productId}/channels/{channel}.json`

The only mutable object in a repository.

```json
{
  "schemaVersion": 1,
  "productId": "fourstory.client",
  "channel": "live",
  "releaseId": "2026.02.15-a",
  "releaseSequence": 418,
  "releaseDigest": "sha256:c4d0f1a9b73e2c5580ab41ff9d2e6c17b8a40c3e77d1965f2b0ce8143a7f9d22",
  "channelSequence": 419,
  "supersedesChannelSequence": 418,
  "minimumClientVersion": "1.0.0",
  "updatedAt": "2026-02-15T10:00:00Z",
  "signature": { "keyId": "4s-2026", "algorithm": "ed25519", "value": "n1Qk…" }
}
```

Rollback is a **new signed pointer** at `channelSequence + 1` naming an older `releaseId`, not a
restored old document — see [17](17-signed-documents.md) §4. Restoring the old bytes would move
the sequence backwards and every client that had seen the newer pointer would reject it.

## 5. `release.lock.json`

The metapackage. **~6 KB describes all 496 legal selections.**

```json
{
  "schemaVersion": 1,
  "productId": "fourstory.client",
  "releaseId": "2026.02.15-a",
  "sequence": 418,
  "state": "published",
  "createdAt": "2026-02-15T09:12:44Z",
  "minimumInstalledRelease": "2025.11.01-a",
  "coverageDigest": "sha256:6a1f88d0…",

  "axes": [
    { "name": "arch",     "rank": 10, "cardinality": "one",  "required": true,
      "default": "x64",     "displayName": "Architecture",
      "values": [ { "id": "x64" }, { "id": "x86" } ] },

    { "name": "ui",       "rank": 20, "cardinality": "one",  "required": true,
      "default": "classic", "displayName": "Interface",
      "values": [ { "id": "classic", "display": "Classic" },
                  { "id": "modern",  "display": "Reforged" } ] },

    { "name": "language", "rank": 30, "cardinality": "many", "required": true,
      "default": "en",      "displayName": "Language",
      "values": [ { "id": "en" }, { "id": "de" }, { "id": "fr" },
                  { "id": "tr" }, { "id": "pl" } ],
      "retired": { "ru": "en" } },

    { "name": "hd",       "rank": 40, "cardinality": "one",  "required": false,
      "default": "off",     "displayName": "HD textures",
      "values": [ { "id": "off" }, { "id": "on" } ] },

    { "name": "brand",    "rank": 50, "cardinality": "one",  "required": false,
      "default": "4story",  "displayName": "Publisher",
      "values": [ { "id": "4story" }, { "id": "gamigo" } ] }
  ],

  "requirements": [
    { "package": "fourstory.client.core",         "when": {} },
    { "package": "fourstory.client.bin.x64",      "when": { "arch": ["x64"] } },
    { "package": "fourstory.client.bin.x86",      "when": { "arch": ["x86"] } },
    { "package": "fourstory.client.ui.classic",   "when": { "ui": ["classic"] },
      "overrides": ["fourstory.client.core"] },
    { "package": "fourstory.client.ui.modern",    "when": { "ui": ["modern"] },
      "overrides": ["fourstory.client.core"] },
    { "package": "fourstory.client.lang.common",  "when": {}, "rankAs": "language" },
    { "package": "fourstory.client.lang.de",      "when": { "language": ["de"] },
      "overrides": ["fourstory.client.core", "fourstory.client.lang.common"] },
    { "package": "fourstory.client.tex.hd",       "when": { "hd": ["on"] }, "optional": true,
      "overrides": ["fourstory.client.core"] },
    { "package": "fourstory.client.brand.gamigo", "when": { "brand": ["gamigo"] },
      "overrides": ["fourstory.client.core", "fourstory.client.ui.classic",
                    "fourstory.client.ui.modern", "fourstory.client.tex.hd"] }
  ],

  "packages": [
    { "id": "fourstory.client.core", "version": "1.4.7", "sequence": 10407,
      "manifest": "packages/fourstory.client.core/1.4.7/package.json",
      "manifestDigest": "sha256:9f2c81b0…",
      "fileCount": 184203, "installSize": 41230884112, "downloadSize": 18844120031,
      "requires": [], "overrides": [], "conflicts": [] },

    { "id": "fourstory.client.ui.modern", "version": "2.0.1", "sequence": 20001,
      "manifest": "packages/fourstory.client.ui.modern/2.0.1/package.json",
      "manifestDigest": "sha256:77aa93c2…",
      "fileCount": 4812, "installSize": 1904331021, "downloadSize": 1211004882,
      "requires": [ { "id": "fourstory.client.core", "minSequence": 10400 } ],
      "overrides": [], "conflicts": ["fourstory.client.ui.classic"] }
  ],

  "signature": { "keyId": "4s-2026", "algorithm": "ed25519", "value": "Zq83…" }
}
```

`requires` / `overrides` / `conflicts` are **denormalised onto the pin** so the client can
validate the whole dependency closure from one downloaded file, before fetching any package
manifest.

## 6. `release.bundle.json`

Byte-identical `lock` section plus every referenced `PackageManifest` inlined under `inline`.
~20 KB gzipped for 13 packages. Cold-install metadata drops from ~33 requests to 3 + shards.

```json
{
  "schemaVersion": 1,
  "lock": { "…exact bytes of release.lock.json…" },
  "inline": {
    "fourstory.client.core":       { "…PackageManifest…" },
    "fourstory.client.ui.modern":  { "…PackageManifest…" }
  }
}
```

Each inlined manifest is verified against its `LockedPackage.manifestDigest`. It is an
optimisation, **never** a trust shortcut: a bundle with a valid signed lock section and
tampered inline manifests must fail.

## 7. `packages/{id}/{version}/package.json`

```json
{
  "schemaVersion": 1,
  "id": "fourstory.client.ui.modern",
  "version": "2.0.1",
  "sequence": 20001,
  "kind": "content",
  "createdAt": "2026-02-11T14:03:02Z",
  "overrides": [],
  "conflicts": ["fourstory.client.ui.classic"],
  "requires": [ { "id": "fourstory.client.core", "minSequence": 10400 } ],
  "pathPrefixes": ["ui/", "data/ui/", "shaders/ui/"],
  "fileCount": 4812,
  "installSize": 1904331021,
  "downloadSize": 1211004882,
  "fileTable": {
    "format": "jsonl/v1",
    "shardCount": 1,
    "digest": "sha256:41ab55c9…",
    "shards": [
      { "index": 0, "digest": "sha256:aa10bd07…", "count": 4812, "size": 402118 }
    ]
  },
  "metadata": { "buildId": "tc-88412", "vcsRef": "9a41c0e" }
}
```

There is **no `state` field**. Draft/published/yanked is a lifecycle property owned by the
control plane and, for yank, by the signed revocation document ([17](17-signed-documents.md)
§4.5) — an immutable content object cannot carry a mutable lifecycle flag (review **M9**).

`pathPrefixes` is a conservative superset — every path in the table must start with one of
them. It lets the publish gate skip O(n) table intersection for pairs with disjoint prefixes.

## 8. File-table shards — `jsonl/v1`

A shard **is a blob**, stored in the same CAS. One JSON object per line, sorted by path within
the shard. Two-character keys keep a 200,000-row table small.

```jsonl
{"p":"data/ui/main.dat","h":"sha256:7c19a4f1…","s":88213,"m":"md5:9a3f10c2…"}
{"p":"shaders/ui/blur.fx","h":"sha256:0e77b311…","s":4102}
{"p":"ui/modern/atlas/000.dds","h":"sha256:22be9017…","s":16777216}
{"p":"ui/modern/config/default.ini","h":"sha256:0e7742aa…","s":1204,"pol":"preserve"}
{"p":"logs","k":"dir","s":0}
{"p":"bin/run.sh","h":"sha256:31cc90a1…","s":412,"pol":"executable","mode":"0755"}
```

| Key | Required | Meaning |
|---|---|---|
| `p` | yes | Virtual path, NFC, ordinal-compared |
| `h` | yes for `k:file` | Content hash. Also the CAS key and the transfer size basis |
| `s` | yes | Size in bytes. One size field only — stored ≡ content |
| `k` | no | Entry kind: `file` (default) or `dir` (a directory that must exist though it contains no files) |
| `m` | no | md5, cheap change detection only, never integrity |
| `pol` | no | Install policy; omitted ⇒ `replace` |
| `mode` | no | POSIX mode, octal string. Meaningful with `pol:"executable"`; ignored on Windows |

`k:"dir"` closes review finding **M9/Q9**'s empty-directory gap — `logs/` and `screenshots/` can
be declared. Symlinks remain rejected at publish; there is no entry kind for them.

Every field that can affect apply behaviour is covered by `FileSetId`
([18](18-normative-contract.md) §4).

**Reserved for v2, must be present in the spec now** (see [14](14-open-questions.md) Q7/Q8):

| Key | Meaning |
|---|---|
| `b` | bundle reference `{ "h": bundleHash, "o": offset, "l": length }` for small-file bundling |
| `d` | delta source `{ "from": hash, "h": deltaHash, "alg": "zstd-patch" }` |

### 8.1 Shard assignment — pinned

```
shardIndex = xxh3_64(utf8(VirtualPath.Value)) & (shardCount - 1)
```

Specifically: `System.IO.Hashing.XxHash3`, **seed 0**, UTF-8 bytes of `VirtualPath.Value`,
little-endian interpretation. It is **XXH3**, not XXH64. `shardCount` is a power of two,
recorded explicitly in the manifest.

This is baked into every published manifest, so a silent implementation change makes every
existing shard unreadable. It gets a golden-vector test ([12](12-testing.md) §4).

Hash bucketing rather than sorted contiguous ranges is deliberate: a one-file hotfix rewrites
exactly **one** shard, and every other shard blob is byte-identical to the previous version and
already in the client's digest-keyed cache. Sorted ranges would shift every boundary when a
file is inserted near the front, costing a full file-table re-download for every client.

## 9. `coverage.json`

```json
{
  "schemaVersion": 1,
  "releaseId": "2026.02.15-a",
  "mode": "exhaustive",
  "pointCount": 496,
  "digest": "sha256:6a1f88d0…",
  "points": [
    { "selection": "arch=x64;brand=4story;hd=off;language=de;ui=classic",
      "packages": ["fourstory.client.core","fourstory.client.bin.x64",
                   "fourstory.client.ui.classic","fourstory.client.lang.common",
                   "fourstory.client.lang.de"],
      "fileCount": 190203, "installSize": 43902114440, "downloadSize": 20114882031,
      "fileSetId": "sha256:8e44b019…" }
  ]
}
```

`mode` is `exhaustive` below 4,096 points and `sampled` (deterministic pairwise-covering array)
above. `release check --against <prev>` diffs this table and fails on drift beyond tolerance.
It is the only mechanical defence against a mis-sliced package silently gaining or losing
thousands of files.

## 10. `.4sup/state.jsonl` — the client ledger

**One file, one commit point.** The install lock is the head record; owned files follow.
Two files would mean two renames and therefore a window where the lock describes release B
while the ledger describes release A.

```jsonl
{"k":"lock","schemaVersion":1,"repositoryUri":"https://patch.4story.com/live","productId":"fourstory.client","channel":"live","releaseId":"2026.02.15-a","releaseDigest":"sha256:c4d0f1a9…","selection":{"arch":["x64"],"brand":["4story"],"hd":["off"],"language":["de","en"],"ui":["classic"]},"selectionId":"sha256:1f0a77b2…","fileSetId":"sha256:8e44b019…","packages":[{"id":"fourstory.client.core","version":"1.4.7","layer":0,"manifestDigest":"sha256:9f2c81b0…"}],"appliedAt":"2026-02-15T10:14:22Z"}
{"k":"f","p":"bin/4story.exe","h":"sha256:aa0192ff…","s":8814912,"o":"fourstory.client.bin.x64","mt":1770000123,"os":8814912}
{"k":"f","p":"data/ui/main.dat","h":"sha256:5501cc31…","s":88101,"o":"fourstory.client.ui.classic","mt":1770000131,"os":88101}
{"k":"f","p":"config/user.ini","h":"sha256:0e7742aa…","s":1204,"o":"fourstory.client.core","pol":"preserve","mt":1770000160,"os":1391}
```

| Key | Meaning |
|---|---|
| `o` | **owner** — the package that installed this path. Makes deletion attributable |
| `pol` | `preserve` protects `config/user.ini` from ever being deleted or overwritten |
| `mt`, `os` | observed mtime and size — the cheap stat pair that gates re-hashing |

Note `os` (1391) differs from `s` (1204) on the preserved config: the user edited it, the
updater recorded that, and it will neither overwrite nor delete it.

## 11. Versioning and compatibility

| Document | Unknown `schemaVersion` | Unknown fields |
|---|---|---|
| `repo.json` | refuse, with a message naming `minimumClientVersion` | ignore |
| `channels/*.json` | refuse | ignore |
| `release.lock.json` / `bundle` | refuse | **reject** — an unknown field may change resolution |
| `package.json` | refuse | **reject** |
| file-table shard | refuse (from `fileTable.format`) | ignore unknown keys |
| `state.jsonl` | refuse and offer `--rebuild-state` | preserve |
| config files | accept | **preserve** via `[JsonExtensionData]` |

Rationale for the split: a client that silently ignores an unknown field in a *resolution*
document may compose the wrong file set and delete a player's data. A client that ignores an
unknown field in a descriptor merely misses an optimisation.

Both `repo.json` and `ChannelPointer` carry `minimumClientVersion`, so a repository can refuse
old clients with an actionable message instead of letting them misinterpret it.
