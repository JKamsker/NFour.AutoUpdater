# 06 — Storage Backends

> **Revised.** Backends are now **dumb byte stores** under a management API
> ([09](09-control-plane-server.md)). A repository has **two endpoints, not one** — an anonymous
> read URL and an authenticated write transport, which need not be the same protocol.

## 1. The read/write endpoint split

```
readBaseUrl   https://cdn.example.com/patches/    ← anonymous; the ONLY thing players touch
writeTarget   s3://bucket/prefix/                 ← presigned PUT brokered by the API
              ftp://host/www/patches/             ← admin creds, configured locally
              file:///srv/patches/
```

This is the reference's `ServerBaseUrl` / `CdnUrl` split done properly rather than
half-declared — the reference documented `defaultCdn` and per-file `cdn` at length and then
**ignored both in every reader** (`AproPatchFileRepository.cs:41-43`,
`HttpPatchRepository.cs:212`), so any blob outside the default store 404s.

Splitting the endpoints collapses several problems at once:

- **FTP's read-path weaknesses stop mattering.** No conditional GET, no ETag, no resume
  validator — irrelevant, because reads go over the paired HTTP endpoint where `Range` and
  `If-Range` exist. FTP's weak `(MDTM, SIZE)` validator was only ever a download problem.
- **Writes and reads scale independently.** A CDN fronts reads; writes happen a few times a week
  from one machine.
- **The client only ever speaks HTTP** (or reads a local path). It does not know or care what
  the write transport was.

### 1.1 The pairing must be verified

If the FTP path and the HTTP URL do not resolve to the same document root — a `public_html`
prefix, a symlink, a stale vhost — you publish into a void and every client 404s while the
publish reports success.

`verify-repo` uploads a probe object over the write transport, fetches it over the read URL,
compares bytes, and deletes it. Five seconds; catches an entire class of silent
misconfiguration.

## 2. Capability matrix

### 2.1 S3 and S3-compatible (AWS, MinIO, R2, B2)

**Can:** `ListObjectsV2` with prefix/delimiter; `HeadObject` (size, ETag, encoding in one round
trip); conditional GET; **conditional writes** — `If-None-Match: *` create-if-absent (GA Aug
2024) and `If-Match` compare-and-swap (GA Nov 2024); Range GET with `If-Range`; **server-side
copy** — `CopyObject` ≤ 5 GiB, `UploadPartCopy` above; multipart upload (5 MiB min part, 10k
parts, 5 TiB max) with `ListParts` for resumable upload; batch delete (1000); **presigned
GET/PUT**; strong read-after-write and LIST consistency; lifecycle rules including
`AbortIncompleteMultipartUpload`.

**Cannot:** no atomic rename (`CopyObject` + `Delete`, non-atomic); no directory semantics; **no
content-encoding negotiation** — it stores and echoes the header verbatim (§5); **no SHA-512** —
additional checksums are CRC32/CRC32C/CRC64NVME/SHA-1/**SHA-256**, which is exactly why the CAS
is keyed by SHA-256: storage can then enforce the key itself
([16](16-publish-protocol.md) §5.1). Note multipart SHA-256 is `COMPOSITE`, so objects above
5 GiB still need server-side verification. **"S3-compatible" is not one capability set:** B2
has no reliable conditional writes and weaker LIST consistency; R2 has no object tagging.

**Cost:** at ~$0.0004/1000 GET, 50k GETs × 100k players ≈ 5×10⁹ requests ≈ **$2,000 in request
charges per patch**, before egress.

### 2.2 FTP / FTPS

**Can:** `MLSD`/`MLST` (the only machine-parseable listing, `FEAT`-gated); `SIZE`, `MDTM`;
**`REST` + `RETR`** resumable download; **`RNFR`/`RNTO`** rename — on a POSIX-backed server this
is `rename(2)` and atomic in practice, and it is the *only* atomic publish primitive FTP offers;
`DELE`/`MKD`/`RMD`; real hierarchical directories; FTPS via explicit `AUTH TLS`.

**Cannot:** no conditional requests at all; **no server-side copy**; **no compare-and-swap and no
create-if-absent**; no multipart with retryable parts; **no per-object credentials** — a
credential is tree-wide and all-or-nothing within its chroot; no presigned URLs; no atomic
visibility during upload (a file being `STOR`ed is visible at partial length, so publish requires
upload-to-temp then `RNTO`); stateful and connection-expensive — a **new data connection per
transfer**, commonly capped at 4–10 per IP.

### 2.3 HTTP read-only

**Can:** `GET`, `HEAD`, conditional requests, usually `Range` with `If-Range`,
`Cache-Control: immutable`, HTTP/2-3 multiplexing.

**Cannot:** **no listing** — `autoindex` is HTML, usually disabled, absent on every CDN; no
writes of any kind; **`Range` is not universal** — nginx `gzip on` disables byte ranges entirely
because the response is generated, and some CDNs strip `Accept-Ranges`; **ETag semantics are
unreliable across mirror nodes** — Apache may include the inode, so two nodes serving identical
bytes emit different ETags, and any gzip transform downgrades a strong ETag to weak `W/"…"`,
which is illegal with `If-Range`; **no consistency across CDN edges** — a channel pointer can be
stale at one edge and fresh at another for the TTL.

### 2.4 Local file system

**Can:** full directory semantics; **atomic rename within a volume** (the commit primitive);
**atomic create-if-absent** (`FileMode.CreateNew` / `O_EXCL`) — strictly better than the
reference's racy `File.Exists` probe (`LocalPatchFileRepository.cs:133-145`); hardlinks;
reflink/CoW clone (ReFS/btrfs/XFS/APFS, needs P/Invoke); Restart Manager.

**Cannot:** cross-volume rename is not atomic; **rename gives atomicity, not durability** —
without `fsync` of the file and of the containing directory, power loss can lose it, and .NET has
**no directory-fsync API**; directory-entry scaling degrades past ~10⁵ entries; `MAX_PATH`;
the Windows illegal-name minefield ([03](03-architecture.md) §4.2); case-insensitivity; locked
files.

## 3. The read port

Capability *interfaces* are the gate; the flags enum is a cross-check. The reference's
`HttpPatchRepository.ListProductsAsync:30` throws **mid-enumeration**, after the caller has
started iterating and cannot recover — a fat interface plus runtime `NotSupportedException` is
exactly what this shape exists to prevent.

```csharp
public interface IReadableObjectStore : IAsyncDisposable
{
    StorageCapabilities Capabilities { get; }

    /// FTP servers commonly cap at 4-10 connections per IP; a fetcher that opens
    /// 32 streams will simply be refused.
    int RecommendedParallelism { get; }

    /// ActualStartOffset reports what the backend really gave us — some origins ignore
    /// Range and answer 200 — so the caller can refuse to drain 3.9 GB forward.
    /// Validator is the resume guard (ETag / Last-Modified / MDTM+SIZE / none).
    ValueTask<ReadResult?> OpenAsync(string key, long offset = 0,
                                     ObjectValidator? ifMatch = null,
                                     CancellationToken ct = default);

    /// (exists, size, validator, encoding) in ONE round trip. Replaces ExistsAsync,
    /// which throws away the size and forces a second call.
    ValueTask<ObjectHead?> HeadAsync(string key, CancellationToken ct = default);
}

// optional, each a separate interface:
//   IListableObjectStore · IWritableObjectStore · IConditionalWriteStore
//   IServerSideCopyStore · IPresigningStore · IMultipartUploadStore
```

A conformance test asserts declared capabilities match implemented interfaces **in both
directions**, so the two cannot drift.

### 3.1 Two capability sources, arbitrated

| Source | Means |
|---|---|
| `RepositoryDescriptor.Capabilities` (`repo.json`) | **format-level**: what this repository *is* |
| `IReadableObjectStore.Capabilities` | **transport-level**: what this connection *can do* |

The effective set is the **intersection**, computed once and never recomputed. A bucket whose
`repo.json` says `[read, write]` accessed over HTTP is read-only in fact. Leaving this
unarbitrated is how the reference's `CapabilityType` became a discovery document that was really
an implementation selector.

## 4. Write-side asymmetry

Writes are brokered by the API ([16](16-publish-protocol.md)). The backends are **not**
symmetric write targets and the design must say so rather than imply otherwise.

| Operation | Local | S3 | FTP | HTTP |
|---|---|---|---|---|
| Per-object expiring grant | n/a (ACL) | ✔ presigned PUT | ✘ **none** | ✘ |
| Server-side promotion (**same store only**) | ✔ rename | ✔ `CopyObject` | ✔ `RNTO` | ✘ |
| Server-side verification before promote | ✔ | ✔ | ⚠ needs an API-held read credential | ✘ |
| Race-free channel placement | ✔ lock file | ✔ `If-Match` CAS | ✘ **single-publisher only** | ✘ |
| GC (needs List) | ✔ | ✔ | ⚠ 65,536 `MLSD` round trips | ✘ |

**FTP is single-publisher, in writing.** No CAS means two concurrent placements lose an update
and the channel silently regresses or forks — breaking rollback exactly when it is needed.
B2's S3 endpoint falls in the same bucket despite being "S3-compatible".

**GC over FTP is effectively unsupported** — 65,536 leaf directories, no resumability, a
per-IP connection cap. The authoritative writable repository is S3 or local; that is where GC
runs; mirrors are re-synced with a **delete-propagating** sync afterwards. A mirror that is not
delete-propagating accumulates orphans forever.

**FTP-as-origin is a reduced-guarantee mode.** The publisher's credential is tree-wide, so no
server-side control can bound a malicious publisher. See [16](16-publish-protocol.md) §5.2 and
[10](10-security.md) §8.2 for what still holds and why the tradeoff is acceptable.

## 5. The `Content-Encoding` hazard

Blobs are stored and served as raw content bytes with no encoding. If the origin applies
transport compression — nginx `gzip on`/`gzip_static on`, CloudFront/Cloudflare
auto-compression, Apache `mod_deflate` — then a .NET client with `AutomaticDecompression` enabled
receives **decoded** bytes and the SHA-256 check fails **100% of the time**, and `Range` offsets
refer to **encoded** octets so every resume is wrong.

The reference never configured decompression on its patch clients at all and survived only
because Azure Blob does not set the header. That is not portable — and it gets *worse* on
FTP-style shared hosting, where the paired HTTP endpoint is typically Apache with `mod_deflate`
enabled by default and not under your control.

Required:

1. `AutomaticDecompression = DecompressionMethods.None` on every blob client.
2. Any `Content-Encoding` on a blob response is a **hard error** naming the key.
3. **Promotion overwrites the header.** Forbidding it on a presigned PUT is impossible — a
   presigned URL cannot stop a client sending a header — so `CopyObject` uses
   `MetadataDirective=REPLACE` with an explicit header set. Signing `content-encoding` with an
   empty value is a cheap early reject, not the mechanism.
4. `verify-repo` issues a **live HEAD** and fails on `Content-Encoding` or a missing
   `Accept-Ranges`.

## 6. The content gateway (optional)

A dedicated read-path service that fronts any backend and presents the repository layout over
HTTPS.

**It does not violate the byte rule.** The rule constrains the *management API* — the component
holding the database and minting grants. A gateway is a **separate deployable** with no database,
a different scaling profile, and a different failure domain. It is a private CDN origin, not a
route on the API ([09](09-control-plane-server.md) §9).

**It costs nothing on the client.** A gateway serving the repository layout is byte-for-byte
indistinguishable from a static HTTP mirror; the `Storage.Http` backend already handles it. It is
purely a server-side deployment choice and can be added later with no format change and no client
release.

**Where it earns its keep:**

1. **FTP connection pooling.** FTP's real cost is a fresh data connection per transfer behind a
   4–10 connection cap — the reason 50k small files takes hours. A gateway amortises that across
   every client: HTTP/2 multiplexing to the gateway, warm FTP connections to the origin. This is
   what makes FTP viable at scale.
2. **Caching that is trivially correct.** Blobs are immutable and content-addressed, so there is
   no invalidation logic and `ETag` is just the content hash.
3. **It is the only place `Content-Encoding` can be *guaranteed* off** (§5). On shared hosting
   you are at the mercy of `mod_deflate`.
4. **Selective read auth.** It can gate `internal`/`ptr` channels while keeping `live` anonymous
   — the only component that can, without a second bucket.

**Constraints:**

- **Read-only by default.** A write-through gateway needs write credentials for every backend,
  recreating on an internet-facing service exactly the credential-vault blast radius that
  admin-held FTP creds avoid. If wanted later: a separate deployment, separate credentials, no
  read traffic.
- **Not a trust anchor.** TLS to the gateway is transport security, not authenticity. The client
  still verifies SHA-256 on every blob and the signature chain against its pinned key
  ([10](10-security.md) §11).
- Must pass `Range` through, never transform content, never buffer a 20 GB blob, and cache
  channel pointers only for their short TTL.

## 7. Deployment modes

| Mode | Read | Write | Use when |
|---|---|---|---|
| Direct public storage | S3 + CDN | presigned PUT via API | Cheapest at scale |
| FTP + paired HTTP | hoster's HTTP | FTP, admin creds | Cheap shared hosting |
| **Content gateway** | gateway HTTPS | direct to backend | No public HTTP on the backend; or you want TLS, caching, or read auth you own |
| Local | filesystem | filesystem | Dev, LAN, air-gapped |

## 8. Other backend consequences

**Hardlink, do not copy.** Materialising a 40 GB install by copying from the local CAS doubles
disk and write time. Hardlink on the same volume, reflink where supported, **copy fallback for
`Preserve` files** — the game mutates those in place and a hardlink would corrupt the shared CAS
entry ([07](07-client-engine.md) §5).

**Peak space is per-volume.** Bucket `PeakFreeSpaceRequired` by resolved volume root, as the
reference's `SpaceRequirementCalculator` correctly did. Assert that `.4sup/staging` is on the
install root's volume — that is what makes `File.Move` atomic.

**Large files.** A 20 GB `.pak` needs multipart PUT and `UploadPartCopy` for promotion — a
separate code path that must exist, not be discovered in production.

## 9. Dependencies

| Backend | Library |
|---|---|
| Local | BCL only |
| HTTP | `System.Net.Http`, `AutomaticDecompression = None` |
| S3 | `AWSSDK.S3` (covers MinIO/R2/B2 via `ServiceURL` + `ForcePathStyle`) |
| FTP | `FluentFTP` — `FEAT`-gated detection; BCL `FtpWebRequest` is obsolete |

Per `rules/csharp.md` §5: pinned, nothing under 3 days old, via `Directory.Packages.props`.

> **No FTP or S3 code exists anywhere in the reference** — grepping `ftp`, `amazon`, `s3client`,
> `minio` across every `.cs` and `.csproj` returns one unrelated EF visitor file. Its own
> multi-backend abstraction was theatre: `LocalFileSystemManager` is an **empty class**
> (`AzureBlobManager.cs:222-228`) and every caller hardcodes `CreateManager<AzureBlobManager>()`.
> The conformance suite ([12](12-testing.md) §2) is not optional.
