# 16 — Publish Protocol: Brokered Upload and Promotion

> Logically this sits between [08 — Publishing and Validation](08-publishing-and-validation.md)
> and [09 — Management API](09-control-plane-server.md). It is numbered 16 because it was added
> after the first draft, when the architecture was corrected to make the API the management
> authority.

## 1. The topology

```
                    ┌──────────────────────────────────────────┐
                    │  MANAGEMENT API  (owns the database)     │
   publisher ──1──▶ │  products · packages · releases · axes   │
   (authenticated)  │  channels · grants · placement ledger    │
                    └───┬──────────────────────────────┬───────┘
                        │ 2. mints capability          │ 4. server-side
                        │    (never bytes)             │    verify + copy
                        ▼                              ▼
   publisher ──3──────────────────▶ ┌──────────────────────────────────┐
   uploads DIRECTLY to storage      │  STORAGE BACKEND (dumb bytes)    │
                                    │  _staging/… → blobs/… → CAS      │
                                    └──────────────┬───────────────────┘
                                                   │ 5. anonymous GET
                                                   ▼
                                            player client
                                          (no credentials, ever)
```

Three roles, and they are deliberately not the same component:

| Role | Holds | Never |
|---|---|---|
| **Management API** | The database, its own storage credentials, grant ledger | Payload bytes on a client-facing path; the signing key |
| **Storage backend** | Bytes | Any knowledge of products, versions, variants |
| **Player client** | A pinned public key | Any credential at all |

## 2. The byte rule, stated precisely

Two readings of "no binary through the API" circulated during design and they are not the same
rule. This is the one that holds:

> **No client-facing API request or response ever carries payload bytes.**
>
> The API *does* hold storage credentials and *does* move bytes — server-side copy for
> promotion, and streaming reads for verification. That is server↔storage traffic on the API's
> own network path. It is not client↔API traffic and it does not scale with the number of
> players.

The weaker reading — "the API relays no bytes it did not author" — must be rejected, because it
licenses the API to author and sign release documents, which turns it into a signing oracle
(§6.1).

## 3. The pipeline

```
1. PROBE      POST /repositories/{repo}/blobs/query   { sha256: [...] }
              → per hash: present | absent
              Batched, ~100 hashes per call. One round trip instead of one HEAD per blob.

2. SESSION    POST /repositories/{repo}/publish/sessions
              → { sessionId, backendId, maxObjects, maxTotalBytes, expiresAt }

3. GRANTS     POST /publish/sessions/{id}/grants   { items: [{ sha256, storedLength, ... }] }
              → per item: an UploadGrant (see §4)
              Scoped to _staging/{sessionId}/{grantId}. Single-use. Short TTL.

4. UPLOAD     client → storage, DIRECTLY. Bytes never touch the API.

5. SEAL       POST /publish/sessions/{id}/seal
              No further grants may be minted against this session.

6. VERIFY     API streams each staged object FROM STORAGE, computes SHA-256.
              ◀── MANDATORY. See §5. Mismatch ⇒ reject, quarantine, do not promote.

7. PROMOTE    Server-side copy _staging/{sessionId}/{grantId} → blobs/{alg}/{aa}/{bb}/{hash}
              S3 CopyObject / UploadPartCopy · Azure StartCopyFromUri · FTP RNFR+RNTO · local rename
              Bytes never traverse the API here either.

8. REGISTER   POST /packages/{id}/versions/{v}/files
              Records BlobPlacement rows with verifiedAt set.

9. PUBLISH    POST /packages/{id}/versions/{v}/publish     → immutable
```

Steps 1–4 and 7 are the reference's shape, which was correct. Steps 5, 6 and the `verifiedAt`
column are what the reference lacked entirely.

## 4. Grants are four different things, not one

This is the central abstraction. A grant is **not** one mechanism with four implementations;
the mechanisms have genuinely different security properties and must not share a code path.

```csharp
public abstract record UploadGrant
{
    public required GrantId              GrantId        { get; init; }   // opaque, server-generated
    public required BackendId            Backend        { get; init; }
    public required ObjectKey            StagingKey     { get; init; }
    public required ContentDigest        ExpectedDigest { get; init; }   // sha256, uncompressed
    public required long                 ExpectedLength { get; init; }   // of the STORED bytes
    public required DateTimeOffset       ExpiresAt      { get; init; }
    public required IntegrityEnforcement Enforcement    { get; init; }

    /// S3 presigned PUT (SigV4) / Azure SAS. A true capability.
    public sealed record HttpPut(Uri Uri, ImmutableArray<HttpHeaderRequirement> RequiredHeaders)
        : UploadGrant;

    /// S3 objects > 5 GiB — presigned PUT cannot carry them.
    public sealed record HttpMultipart(MultipartUploadId UploadId, long PartSize,
                                       ImmutableArray<PresignedPart> Parts, Uri? AbortUri)
        : UploadGrant;

    /// S3 POST policy — the only shape that expresses content-length-RANGE.
    public sealed record HttpPostForm(Uri Uri, ImmutableArray<KeyValuePair<string,string>> Fields)
        : UploadGrant;

    /// FTP. NOT a capability: the credential is tree-wide, non-expiring, reusable,
    /// and already held by the publisher. The API returns only the destination key.
    public sealed record PublisherCredentialed(ObjectKey Key) : UploadGrant;

    /// Local, shared-filesystem mode. An ACL, not a token.
    public sealed record LocalPath(string AbsolutePath) : UploadGrant;
}
```

A closed hierarchy on purpose: adding a backend with a genuinely new grant shape must be a
compile error at every consumer, not a runtime surprise.

| Backend | Grant | Scoping | Expiry | Promotion |
|---|---|---|---|---|
| S3 / MinIO / R2 | presigned PUT (SigV4) | **per object** | 15 min | `CopyObject`, `UploadPartCopy` > 5 GiB |
| Azure Blob | SAS | **per object** | 15 min | `StartCopyFromUriAsync` |
| FTP / FTPS | *none* — publisher's own admin credential | tree-wide, all-or-nothing | none | `RNFR`/`RNTO` |
| Local | path handoff (filesystem ACL) | directory | none | `File.Move` |
| HTTP read-only | **impossible** — not a write target | — | — | — |

"No grant possible" is a **type-system fact**, not a runtime `NotSupportedException`. That is
precisely the mistake [06](06-storage-backends.md) §3 calls out in the reference's
`HttpPatchRepository.ListProductsAsync:30`, which throws mid-enumeration.

## 5. The integrity invariant — the most important rule in this document

> **The API may write an object at a key derived from client-supplied data only when either
> (a) storage has cryptographically enforced that exact key, or (b) the API has read the bytes
> that key names.**

Without this, promotion is a **confused deputy**: the publisher has write authority over exactly
one staging prefix and none over `blobs/**`, yet it chooses both the source and the destination
key, and the API executes the write with *its own* `blobs/**` authority on the publisher's
instructions.

### 5.1 Why the CAS is keyed by SHA-256

The first draft keyed the CAS by SHA-512 and bound `x-amz-checksum-sha256` into the presigned
PUT. **That combination is unsound**: a publisher declares a truthful SHA-256 for content `X` and
an arbitrary SHA-256 `H`, passes storage validation, and the API writes `X` at the key asserting
`sha256 = H`. Storage validated something that was not the key.

**Re-keying the CAS to SHA-256 closes the hole structurally rather than patching it**, because
the bound checksum *becomes* the key. S3's additional-checksum support (CRC32, CRC32C, CRC64NVME,
SHA-1, **SHA-256**) means a presigned PUT with `x-amz-checksum-sha256` in its signed headers
forces the client to send that exact value and makes S3 reject a mismatching body. Storage now
enforces the CAS key itself.

Four consequences, all favourable:

| | Effect |
|---|---|
| **Integrity** | Branch (a) of the invariant becomes reachable. The confused deputy is closed by construction on the primary backend, not by a compensating read. |
| **Cost** | No mandatory egress to re-hash every new blob. On an 18 GB patch that is 18 GB of egress per publish that simply does not happen. |
| **Manifest size** | Hex digests halve — 64 chars instead of 128. Across a 200k-entry file table that is ~12.8 MB of hash characters down to ~6.4 MB before compression. |
| **Client speed** | SHA-256 has hardware acceleration on every current x64 and ARMv8 CPU (SHA-NI / ARMv8 crypto extensions); SHA-256 has none on x86 and is software-only. On modern player hardware SHA-256 is typically *faster* despite processing half the data per round. |

Collision resistance is not a real consideration here: SHA-256's 128-bit collision resistance is
far beyond what a content-addressed store needs, and SHA-256's margin buys nothing.

The algorithm was already tagged in the value type and in the key template
(`blobs/{alg}/{aa}/{bb}/{hash}`), so this is a configuration change, not a redesign — which is
exactly the property that design decision existed to provide.

### 5.2 Integrity mode per backend

`IntegrityMode.ClientAsserted` is **deleted** — nobody verifying is never acceptable.
`StorageEnforced` is reinstated, but **only** where the enforced checksum is the CAS key.

| Backend | Mode | Why |
|---|---|---|
| S3 / MinIO / R2, single PUT ≤ 5 GiB | **StorageEnforced** | `x-amz-checksum-sha256` is the CAS key. Sound. |
| S3 multipart > 5 GiB | **ServerVerified** | Multipart SHA-256 is `COMPOSITE` — a checksum of part checksums, not of the object. S3's full-object checksum mode covers CRC only. The composite value is **not** the CAS key. |
| Azure Blob | **ServerVerified** | Only `Content-MD5` is enforceable, and MD5 is collision-broken — an attacker can craft two files sharing one. |
| B2 (S3 endpoint) | **ServerVerified** | Conditional writes and checksum support are unreliable; do not assume S3 parity. |
| Local | **ServerVerified** | Cheap — a local read, no egress. |
| FTP | **Reduced** (§5.3) | No enforcement of any kind, and the publisher already holds tree-wide write. |

The mode is **computed by the broker** from backend capability and object size. It is never
requested by the client — that is precisely how the reference let clients assert their own
`Size`, `Md5`, `Sha512` and `ILIdentity` straight into a `FileReference` row
(`AzureBlobManager.cs:147-160`).

`4sup publish --explain` prints the mode and the rationale for every object, so a publisher can
see which branch of the invariant was taken and why.

### 5.3 The one honest exception: FTP-as-origin

On FTP the publisher holds a tree-wide admin credential — it can already write `blobs/**` and
`channels/**` directly, bypassing the API entirely. There is no server-side control that can
prevent a malicious FTP publisher from poisoning the CAS, because the protocol has no scoping to
build one from.

So FTP-as-origin is an explicitly **reduced-guarantee** mode:

- The API holds a **read-only** FTP credential and still verifies. This catches accidental
  corruption and a *different* publisher's mistake — it is not useless — but it is not a
  boundary against a malicious publisher.
- `repo.json` carries `"integrityGuarantee": "reduced"` so `verify-repo` reports the weaker
  property rather than implying the stronger one.
- **What still holds:** the client-side chain. Signed release lock + pinned trust root +
  SHA-256 verified as bytes land means a poisoned FTP origin is a **denial of service, not code
  execution** — provided the release lock is signed outside the API (§6.1). That is the design's
  real strength and it is what makes the accepted tradeoff acceptable.
- Where the FTP server supports per-directory ACLs, give publisher accounts write on
  `_staging/`, `blobs/`, `releases/` and **read-only on `channels/` and `repo.json`**. That one
  split downgrades a *leaked* credential from a freeze attack (§7.2) to content DoS. Recommended
  wherever the daemon allows it; not achievable on all shared hosting.

## 6. What the API may and may not author

### 6.1 The signing oracle, avoided

If the API assembles `release.lock.json` from database rows and asks a signer to sign it, then
an API compromise yields **arbitrary signed releases** — remote code execution on every player
machine. Key custody would be decorative: moving the key offline bounds *custody* but not
*control of the signing input*. The API would become strictly more valuable than the signing key.

So the projection splits in two:

| API **may** author (unsigned, mechanically derivable) | API **may not** author |
|---|---|
| `repo.json` | `release.lock.json` |
| `packages/*/index.json`, `products/*/releases/index.json` | `products/*/channels/*.json` |
| `product.json` | the key revocation list |
| `release.bundle.json` (recomputable from lock + manifests) | |
| `coverage.json` | |

For the two signed documents the API's role is **verify-and-place**, never generate-and-sign:

1. The publisher/operator CLI authors the canonical bytes locally.
2. The gated signing identity signs those exact bytes.
3. The signed document is submitted to the API as **opaque bytes**.
4. The API verifies the signature against the trusted key list, then stores it **byte-for-byte,
   never reserialised**.

Step 4's byte-for-byte rule is not fussiness: any reserialisation — property order, whitespace,
a `System.Text.Json` version bump — invalidates the signature, and [10](10-security.md) §4 notes
this class of bug is unfixable after publication.

### 6.2 The signer must not sign what it was handed

Even with the key offline, if the signing input arrives from the API then the API still controls
what gets signed. So `release publish --sign`:

- reconstructs the canonical lock bytes **locally**, from its own pin list and its own publish
  receipts;
- refuses to sign if they differ from the server's draft;
- displays a per-package diff — versions, manifest digests, file counts, size deltas — and
  requires explicit confirmation.

### 6.3 Serving a signed document through the API

When a client fetches a channel pointer from the API rather than from storage, the API returns
it as **opaque bytes** — not re-wrapped in a response envelope that reserialises it. The client
verifies the signature over the received bytes *before parsing*, on the identical code path it
uses for the static `channels/live.json`. A byte-equality test between the two paths is a
required test.

## 7. Known-hostile cases and what stops them

### 7.1 CAS pre-poisoning

**Attack:** an attacker with publish access computes SHA-256 of files certain to reappear
unchanged in the next release (engine DLLs, redistributables, unmodified assets — the vast
majority of a 200k-file client), uploads garbage under those hashes, and promotes. The next
legitimate publish's existence probe reports them present, so the real bytes are never uploaded.
Every client then fetches, hashes, mismatches and hard-fails — and dedup guarantees it is
self-perpetuating, while GC marks the poisoned blobs live because a channel references them.

**Stopped by:** the §5 invariant — storage enforcing the SHA-256 key on S3 single PUT, and
`ServerVerified` everywhere else — closes the injection path. But the design also
needs a **repair path**, which no version of it had:

- `POST /blobs/{hash}/quarantine` — operator-only, audited. Moves the object to `_trash/`,
  clears `BlobPlacement`, and re-permits a grant for that digest.
- `verify-repo --deep --rehash-cas` — walks or samples the CAS and re-hashes.
- `blobs/query` reports `present` **only** for placements with a non-null `verifiedAt`.
- Promotion must never report success from a path that did not observe the destination: on an
  `AlreadyPresent` result, HEAD the destination and compare stored size; on mismatch fail loudly
  with a distinct diagnostic. Silently accepting a pre-existing object at the one moment the
  correct bytes were in hand is structurally the same bug as the reference's `CleanupBlobsAsync`
  reporting success while sweeping the wrong prefix.

### 7.2 Replaying an old signed channel pointer

A leaked FTP credential (or any write access to `channels/`) can `STOR` an archived, genuinely
signed pointer back after a security fix ships. Existing installs refuse it via the monotonic
`releaseSequence` ledger rule — but **fresh installs have no ledger floor** and pin themselves to
the old, known-vulnerable release. Signatures structurally cannot stop this; it is a freshness
problem, not an authenticity one.

Mitigations, none complete on its own: the ACL split in §5.2; an absolute staleness bound below
which a fresh install refuses a pointer outright rather than warning; and surfacing
`releaseSequence` and `updatedAt` at first install.

### 7.3 Staging must live outside the served root

**The first draft of [05](05-repository-format.md) contradicted itself**: §1 placed
`_staging/{sessionId}/` inside `{root}` while invariant I-4 in §1.1 said staging never happens
inside a served prefix. With a public bucket or a CDN fronting the root, that makes any publisher
credential into arbitrary file hosting on the game's own trusted domain, and leaks unreleased
builds to anyone who can compute an expected file's digest.

Corrected: staging is a **separate bucket, a separate FTP root, or a prefix explicitly denied by
bucket policy and by the CDN origin rule**. `verify-repo` asserts it with a **live GET against a
known staging key, requiring 403/404** — the same way `PKG011` asserts cache headers.

### 7.4 `Content-Encoding` cannot be forbidden on a presigned PUT

[05](05-repository-format.md) §2.3 rule 3 said "publishing must never set `Content-Encoding`".
That is **unenforceable** — a presigned URL cannot forbid a header the client chooses to send,
and a single poisoned blob breaks every client on every release referencing it.

Forbidding is impossible; **overwriting is trivial**. Promotion sets the destination headers
explicitly — S3 `CopyObject` with `MetadataDirective=REPLACE`, Azure headers set on the copy
destination — so whatever the publisher sent is discarded. Sign `content-encoding` with an empty
value as an early reject, and keep the live `verify-repo` HEAD as the backstop.

### 7.5 Resource exhaustion under a valid grant

A correctly scoped, single-use grant still permits an unbounded upload. Sign `content-length`
(or use a POST policy with `content-length-range`), enforce `maxObjects` and `maxTotalBytes` per
session at grant-mint time, and cap total heartbeat-extended session lifetime independently of
the per-heartbeat extension — otherwise a session can suppress staging GC indefinitely.

## 8. Abandoned sessions

The reference's `POST /UploadSession/{id}/complete` was marked `[Obsolete]` and **had no caller
anywhere in the solution**, so `Completed` was always false and the flag was dead.

Here: sessions expire on a TTL, and expired-unsealed sessions are swept by a staging GC that is
independent of the blob GC ([08](08-publishing-and-validation.md) §5.2). Because staging now
lives outside the served root, a swept-late session is a storage-cost problem and nothing worse.
