# 15 — Risk Register

Ranked by damage-if-ignored. Each risk names its mitigation and where that mitigation lives, so
a reviewer can check the design actually contains it rather than merely acknowledging it.

> **R-0 through R-0e were added after an adversarial review of the API-authority architecture.**
> They rank above everything below because three of them are unbounded and one falsified a claim
> the plan previously made in writing.

---

## R-0a — Signing attests *who assembled*, never *what is inside*

**Severity: critical. Likelihood: low per event, unbounded impact.**

A compromised CI agent publishes a package containing a trojaned binary. Every hash is internally
consistent — the agent computed them over its own bytes. `release check` inspects path
collisions, layer derivation and coverage drift; `PKG001`–`PKG014` contains **nothing that
inspects content**, so a same-size binary swap produces zero diagnostics. The signer then pins
that manifest digest and every client verification passes perfectly, from the pinned trust root
down.

The previous draft claimed a compromised agent "can publish packages and unsigned draft releases
— which no client will install". **That was false and is retracted.** Publishing the package *is*
the attack.

**Mitigation** — [10](10-security.md) §5.3: reviewed signing with a locally reconstructed input
catches an *unexpected package* but not a trojan in an expected one. The real controls are
build-time attestation by a second identity, or reproducible builds verified before signing.
Until one exists, the honest statement is that **the CI agent is inside the trust boundary for
package content**, and the plan says so.

---

## R-0b — Storage validating something that is not the CAS key — RESOLVED by re-keying

**Severity: was critical. Now largely structural.**

The original design keyed the CAS by **SHA-512** while binding `x-amz-checksum-sha256` into the
presigned PUT. Storage then validated a digest that was *not the key*: a publisher could declare
a truthful SHA-256 for content `X` and an arbitrary SHA-512 `H`, pass storage validation, and
have the API write `X` at the key asserting `sha512 = H` — using the API's own `blobs/**`
authority. A textbook confused deputy, in the design's *strongest* integrity mode, on the backend
the plan calls the safest.

Re-keying to SHA-256 was **necessary but not sufficient**, and an external review caught the gap:
S3 checksums the *uploaded body*, so with compressed blobs it still validated `sha256(compressed)`
against a key asserting `sha256(uncompressed)`. The same defect in a new costume.

**Resolved** by two changes together — SHA-256 keying ([16](16-publish-protocol.md) §5.1) **and**
**identity-only storage** ([18](18-normative-contract.md) §5). With stored bytes ≡ content bytes,
the checksum S3 enforces genuinely *is* the CAS key. Side benefits: no verification egress on S3
single PUT, hex digests halve, SHA-256 is hardware-accelerated on every current x64/ARMv8 CPU, and
the compressed-resume problem disappears with it.

**Residual risk**, and it is real: storage enforcement does **not** cover S3 multipart above
5 GiB (SHA-256 there is `COMPOSITE` — a checksum of part checksums, not of the object), Azure
(only collision-broken MD5), B2, or local. Those remain **`ServerVerified`** — the API reads the
staged bytes from storage and hashes before promoting. The invariant is therefore conditional,
not absolute: *the API may write a client-derived key only when storage cryptographically
enforced that exact key, or when the API has read the bytes.* `4sup publish --explain` prints
which branch was taken per object, so the weaker path is visible rather than assumed.

---

## R-0c — An API that authors signed documents is a signing oracle

**Severity: critical. Likelihood: low, but it inverts the whole trust model.**

If the API assembles `release.lock.json` from database rows and asks a signer to sign it, an API
compromise yields arbitrary signed releases — RCE fleet-wide — and the API becomes **strictly
more valuable than the signing key**, because it controls what the key signs. Moving the key
offline bounds custody but not control.

**Mitigation** — [16](16-publish-protocol.md) §6, [10](10-security.md) §5.1: `release.lock.json`
and `channels/*.json` are authored outside the API, signed by a gated identity, submitted as
opaque bytes, and stored byte-for-byte without reserialisation. Enforced structurally by an
architecture test: **`Server` contains no signing primitive at all.**

---

## R-0d — CAS pre-poisoning is permanent and self-perpetuating

**Severity: high. Likelihood: moderate absent verification.**

An attacker computes SHA-256 of files certain to reappear unchanged in the next release — in a
200k-file client, the vast majority — uploads garbage under those hashes, and promotes. The next
legitimate publish's existence probe reports them present, so the real bytes are **never**
uploaded. Every client fetches, hashes, mismatches and hard-fails. GC marks the poison live
because a channel references it.

**Mitigation** — [16](16-publish-protocol.md) §7.1: mandatory verification closes the injection
path; `blobs/query` reports present only for `verifiedAt` placements; and a **repair path** that
no draft previously had — `POST /blobs/{hash}/quarantine`, `verify-repo --deep --rehash-cas`, and
a promotion that HEADs the destination on `AlreadyPresent` rather than reporting success from a
path that never observed it.

---

## R-0e — Staging inside the served root

**Severity: high. Likelihood: certain — it was in the first draft.**

`05-repository-format.md` §1 placed `_staging/{sessionId}/` inside `{root}` while invariant I-4
in the same document said staging never happens inside a served prefix. **The spec contradicted
itself.** On a public bucket that makes any publisher credential into arbitrary file hosting on
the game's own trusted CDN domain, and a hash-derived staging key lets anyone who can compute an
expected file's digest read unreleased builds.

**Mitigation** — [16](16-publish-protocol.md) §7.3: staging moved to a separate root entirely;
the key is an opaque server-generated `grantId`, not the content hash; asserted by a live `GET`
requiring 403/404.

---

## R-0f — Unauthenticated telemetry cannot gate a safety control

**Severity: high. Likelihood: certain if automatic gating ships.**

With anonymous clients, anyone can report any outcome. Reporting failures halts every rollout so
security patches never ship; reporting success drowns the genuine canary signal so a release that
bricks installs advances to 100%. The second converts the safety gate into a **false assurance**,
which is worse than having no gate.

**Mitigation** — [09](09-control-plane-server.md) §2.1: rollout advancement is operator-driven
and telemetry is advisory. Channel-scoped canaries rather than per-device assignment also avoid
wedging the canary population — the group most likely to be on a broken build — behind the
monotonic-sequence rule.

---

## R-1 — `Content-Encoding` collides with the blob-name encoding scheme

**Severity: catastrophic. Likelihood: high. This is the most likely single cause of a day-one
total failure on the HTTP and S3 backends.**

Blobs are stored and served as raw content bytes. If the origin applies transport compression — nginx `gzip on` / `gzip_static on`, CloudFront or Cloudflare
auto-compression, or an uploader setting `Content-Encoding` on the S3 object — then a .NET
client with `AutomaticDecompression` enabled receives **decoded** bytes and the SHA-256 check
fails **100% of the time**; and `Range` offsets refer to **encoded** octets, so every resume is
wrong. (At-rest compression is gone, but this risk is about the **HTTP header**, which an origin
can add to any response, and it remains live.)

The reference never configured decompression on its patch clients at all (the only
`AutomaticDecompression` in the tree is `Apro.AutoUpdater.PlayGround/Program.cs:19`) and
survived purely because Azure Blob does not set the header. That luck is not portable.

**Mitigation** — [05](05-repository-format.md) §2.3, [12](12-testing.md) §2:
`AutomaticDecompression = None` on every blob client; any `Content-Encoding` on a blob response
is a hard error naming the key; a publish rule forbidding it; a `verify-repo` live-`HEAD`
assertion; **and an nginx conformance variant configured with `gzip on` specifically to prove
the client rejects it.**

---

## R-2 — Multi-backend claims unvalidated by any working code

**Severity: high. Likelihood: high without countermeasures.**

There is **no FTP or S3 code anywhere in the reference** — grepping `ftp`, `amazon`, `s3client`,
`minio` across every `.cs` and `.csproj` returns one unrelated EF visitor file. Every claim about
those backends is inference from specifications.

The reference's own multi-backend abstraction was theatre: `BlobManagerFactory` presented itself
as pluggable while `LocalFileSystemManager` (`AzureBlobManager.cs:222-228`) was an **empty
class** and every call site did `CreateManager<AzureBlobManager>()`.

**Mitigation** — [12](12-testing.md) §2: the conformance suite is written in Phase 2, **before**
the backends that will have to pass it, and runs against MinIO, vsftpd and nginx containers. A
backend that does not pass does not ship.

---

## R-3 — The small-file problem makes FTP unusable and S3 expensive

**Severity: high. Likelihood: certain at game-client scale.**

50k changed files = 50k GETs: minutes of latency on HTTP/2, **hours** on FTPS behind a
4-connection cap with a TCP+TLS handshake per transfer, and ~5×10⁹ requests ≈ **$2,000 per
patch** in S3 request charges at 100k players — before any egress.

The design elegantly solves the *metadata* small-file problem via shard hashing and then leaves
the *content* one untouched.

**Mitigation:** bundling, recommended for v1 — [14](14-open-questions.md) Q7. **This is a format
decision.** Retrofitting it invalidates every published manifest, so deferring it past first
publish converts a design choice into a migration.

---

## R-4 — Trust root fetched from the thing it authenticates

**Severity: catastrophic if exploited. Likelihood: low but trivial on the in-scope backends.**

`repo.json` carries `trustedKeys` and is itself unsigned, fetched over the same channel as
everything else. On FTP and plain HTTP — both explicitly in scope, both trivially MITM-able — an
attacker replaces `repo.json` with their own key, signs a malicious release, and every signature
check passes. Hashes give integrity; signatures give authenticity **only if the verification key
comes from somewhere the attacker does not control.**

**Mitigation** — [10](10-security.md) §3: key compiled into the client, TOFU-pinned into the
ledger at first install, `repo.json`'s list used only to *select* among pinned keys, never to
add one. Plus rotation windows and a monotonically versioned revocation list, designed in v1
even if one key is ever issued.

---

## R-5 — Signing with no canonicalisation rule

**Severity: high. Likelihood: certain over time.**

Without RFC 8785 (or a sign-the-exact-bytes rule), any change in property ordering, whitespace,
number formatting, or a `System.Text.Json` version bump invalidates **every previously published
signature with no migration path**. Costs nothing to fix now; unfixable later.

Related: `release.bundle.json`'s inlined manifests must be verified against
`LockedPackage.manifestDigest`, not trusted because the lock is signed — otherwise a bundle with
a valid signed lock and tampered inline manifests passes.

**Mitigation** — [10](10-security.md) §4, [12](12-testing.md) §4: canonicalisation specified,
golden-vectored against the RFC's own test vectors.

---

## R-6 — Concurrent promote silently corrupts the rollback lever

**Severity: high. Likelihood: moderate.**

`channels/{c}.json` is read-decide-write. Two concurrent `4sup channel promote` runs lose an
update and the channel silently regresses — **breaking rollback exactly when it is
needed.** S3/MinIO/R2 have compare-and-swap; local can use `FileMode.CreateNew`; B2's S3
endpoint cannot reliably; **FTP has nothing at all** — `RNTO` overwrite semantics are
server-dependent.

The design must not present four symmetric write targets, because they are not.

**Mitigation** — [08](08-publishing-and-validation.md) §4.1: `promote` gated on
`IConditionalWriteStore`; FTP and B2 require `--force-unsafe-promote` and warn; FTP repositories
declared single-publisher **in writing**.

---

## R-7 — A wildcard `immutable` cache rule disables rollback during an incident

**Severity: high. Likelihood: moderate — it is the natural thing for an ops team to configure.**

A blanket `Cache-Control: immutable` across the bucket is the obvious optimisation for a
content-addressed store, and it freezes the channel pointer — removing the rollback lever at the
one moment it matters.

**Mitigation** — [05](05-repository-format.md) §1.2, `PKG011`: `verify-repo` issues a **live HTTP
request against the origin** and inspects the response headers. A config-file lint would not
catch a CDN-level rule.

---

## R-8 — Commit is atomic but not durable

**Severity: high. Likelihood: low per event, certain across a population.**

Rename gives atomicity, not durability. Without `fsync` of the temp file before rename and of
the containing directory after, power loss on ext4/XFS or a device with a volatile write cache
loses the ledger — and a lost ledger means no deletions ever again on that install.

.NET has **no directory-fsync API**; this needs `FileOptions.WriteThrough` +
`Flush(flushToDisk: true)` plus P/Invoke on POSIX.

**Mitigation** — [07](07-client-engine.md) §3.4: one file, one rename, explicit flush and
directory fsync. If we choose not to P/Invoke, that must be a recorded accepted risk.

---

## R-9 — Windows-hostile paths published from a Linux build agent

**Severity: high — bricks installs. Likelihood: moderate.**

A Linux agent can legally publish `data/aux.dat` (reserved device name), `data/thing.` (trailing
dot, silently stripped by Win32 so two paths collapse to one file), or a path that overflows
`MAX_PATH` under a real install root. Every Windows client then fails.

**Mitigation** — [03](03-architecture.md) §4.2, `PKG012` + `PKG009`: the rules are enforced
**identically** in `VirtualPath.TryCreate` and in the publish gate, and both cases are in the
golden-repository fixture.

---

## R-10 — Resume without a validator splices two different objects

**Severity: moderate — caught, but expensively. Likelihood: moderate.**

Whole-content SHA-256 verification catches the corruption, but only after transferring
gigabytes, and it cannot distinguish "network corruption, retry from here" from "the object
changed, restart from zero". On FTP there is *no* validator at all, so verification is
load-bearing rather than belt-and-braces.

**Mitigation** — [06](06-storage-backends.md) §3, [07](07-client-engine.md) §3.3: validator
captured at first open and passed on reopen; 412 → restart from zero;
`ReadResult.ActualStartOffset` exposes a backend that ignored `Range` so the caller can refuse
to drain forward.

---

## R-11 — GC deletes blobs belonging to an in-flight publish

**Severity: high — unrecoverable. Likelihood: low with the mitigation, moderate without.**

A publish uploads blobs **before** the release lock exists. A concurrent GC with too small a
`minAge` deletes them. The reference's GC swept the wrong directory and reported success from
three call sites for years, so "we'd notice" is not a defence.

**Mitigation** — [08](08-publishing-and-validation.md) §5.2: `minAge` ≥ maximum publish
duration; a publish lease/heartbeat; `_staging/**` immune to the blob sweeper;
**quarantine-then-delete** with an auditable manifest; and a real orphan in the golden repo that
a test asserts is collected.

---

## R-12 — `slice.yaml` maintenance rots

**Severity: moderate. Likelihood: high over time.**

Partitioning a 200k-file tree and keeping the slice exhaustive is a permanent engineering cost —
the largest recurring cost of the chosen model. A forgotten `when`, or a new asset directory
landing in the wrong package, ships thousands of wrong files with **no error at all**.

**Mitigation:** `unmatched: error` in the slicer (`PKG014`), plus
`release check --against <prev>` diffing the coverage matrix and failing on file-count or
install-size drift. This is the *only* mechanical defence against the class of bug, which is
why the coverage matrix is a committed, digested artifact rather than a report.

---

## R-13 — Local CAS doubles disk usage if materialisation copies

**Severity: moderate. Likelihood: certain if not designed.**

The variant-switch story assumes a local CAS ("most modern blobs are still cached"). Copying
from it to materialise a 40 GB install costs 40 GB of extra disk and 40 GB of extra writes.

**Mitigation** — [07](07-client-engine.md) §5: reflink-first with a copy default. Hardlinks are
only available under the explicit immutable-install profile, because a writable hardlink would
corrupt the shared CAS entry for every other install on the machine.

---

## R-14 — Fat interface with runtime `NotSupportedException`

**Severity: moderate. Likelihood: high without discipline.**

The reference's `HttpPatchRepository.ListProductsAsync:30` throws **mid-enumeration**, after the
caller has started iterating an `IAsyncEnumerable` and cannot recover cleanly. A flags enum that
merely describes a fat interface is documentation, not a type-system guarantee.

**Mitigation** — [06](06-storage-backends.md) §3: capability *interfaces* are the gate, the flags
enum is a cross-check, and a conformance test asserts they agree in both directions.

---

## R-15 — Two capability sources with no arbitration

**Severity: moderate. Likelihood: moderate.**

`RepositoryDescriptor.Capabilities` (format-level, from `repo.json`) and
`IReadableObjectStore.Capabilities` (transport-level) can disagree — a bucket whose `repo.json`
says `[read, write]` accessed over the HTTP backend is read-only in fact. Left unresolved this is
the reference's `CapabilityType` mess reborn: a discovery document that is really an
implementation selector.

**Mitigation** — [06](06-storage-backends.md) §3.1: the effective set is the **intersection**,
computed once in `IPackageRepository` and never recomputed.

---

## R-16 — Locked files on Windows with no fallback

**Severity: moderate. Likelihood: high — the launcher usually holds a handle.**

`File.Move` throws when the game or launcher holds `bin/game.exe`. The reference solved this and
the plan must not drop it; equally, the reference's unconditional
`Process.Kill(entireProcessTree: true)` of every handle holder will happily kill `explorer.exe`
or an antivirus scanner.

**Mitigation** — [07](07-client-engine.md) §4: an explicit per-file `LockedFilePolicy` ladder.
Service and process control, including holder inspection, is outside 4sup's scope.

---

## R-17 — No self-update and no client-version floor

**Severity: moderate. Likelihood: certain over time.**

`4sup` cannot update itself and a client running an old binary against a repository at
`schemaVersion: 2` has no upgrade path.

**Mitigation** — [07](07-client-engine.md) §7, [05](05-repository-format.md) §11: versioned
install directories plus a pointer with a **non-symlink fallback** (the reference called
`Directory.CreateSymbolicLink` with no try/catch, so on a machine without
`SeCreateSymbolicLinkPrivilege` the update half-applied); `minimumClientVersion` on `repo.json`
and the channel pointer; a specified unknown-`schemaVersion` policy per document type.

---

## R-18 — Structural equality traps on hash-valued types

**Severity: moderate — silent wrong behaviour. Likelihood: high; it is the default.**

`readonly record struct ContentHash(HashAlgorithmId, ReadOnlyMemory<byte>)` gets
compiler-generated equality over the memory **segment**, not the bytes. `ImmutableSortedDictionary<VirtualPath, …>`
and `HashSet<ContentHash>` need explicit comparers. The reference shipped exactly this class of
bug: `CapabilityType` overrode `Equals(CapabilityType)` and `operator ==` but neither
`object.Equals` nor `GetHashCode`, so every dictionary lookup fell back to reference equality
(`Capabilities.cs:34-56`).

**Mitigation:** hand-written structural `Equals`/`GetHashCode`, **enforced by a test**, plus an
architecture rule that every value type used as a dictionary key has both.

---

## R-19 — Encoding fork — CLOSED

**Severity: was moderate. Now structurally impossible.**

The same content stored under two representations by two build agents; GC then collects one and
404s an entire variant for every client.

**Closed** by identity-only storage ([18](18-normative-contract.md) §5): one hash, one object,
no representation axis. `RepositoryDescriptor.canonicalEncoding` and the `PKG013 EncodingFork`
diagnostic were removed along with the hazard they guarded.

---

## R-20 — Very large files hit undocumented backend limits

**Severity: low-moderate. Likelihood: moderate for a game.**

A single 20 GB `.pak`: S3 single `PUT` caps at 5 GiB (multipart required) and `CopyObject` caps
at 5 GiB (`UploadPartCopy` required) — so **server-side promotion of a large blob is a different
code path**. FTP resume depends on the server honouring `REST`. Local staging needs 20 GB of
headroom on the staging volume specifically.

**Mitigation** — [06](06-storage-backends.md) §4.5, [13](13-roadmap.md) Phase 5:
`UploadPartCopy` explicitly in scope; peak space bucketed by resolved volume root; staging
asserted to be on the install root's volume.

---

## R-21 — Mirror selection declared but not designed

**Severity: low-moderate. Likelihood: high — the reference made exactly this mistake.**

The reference declared `defaultCdn` and per-file `cdn` at length and **both readers ignored
them** (`AproPatchFileRepository.cs:41-43`, `HttpPatchRepository.cs:212`), so any blob not in the
default store 404s.

**Mitigation** — [05](05-repository-format.md) §3: `blobBaseUrls` is an ordered **array** with
sticky failover and the rule *the first mirror serving byte-correct content wins*, plus per-mirror
failure rate in telemetry as the only way to detect a poisoned CDN edge. Minimum viable, but
actually implemented and tested.

---

## R-22 — Leaked FTP credential replays an old signed channel pointer

**Severity: high. Likelihood: moderate.**

FTP credentials are tree-wide. An attacker with one can `STOR` back an archived, **genuinely
signed** channel pointer after a security fix ships. Existing installs refuse it via the monotonic
`releaseSequence` rule — but **fresh installs have no ledger floor** and pin themselves to the
old, known-vulnerable release. Signatures structurally cannot stop this; it is freshness, not
authenticity.

**Mitigation** — [10](10-security.md) §9: an absolute staleness bound below which a fresh install
*refuses* rather than warns; plus the ACL split (publisher write on `_staging/`, `blobs/`,
`releases/`, **read-only on `channels/`**) wherever the FTP daemon supports per-directory rules —
that one split downgrades a leaked credential from freeze attack to content DoS.

---

## Cross-cutting note

Ten of these risks (R-0b, R-0d, R-0e, R-1, R-2, R-4, R-6, R-9, R-11, R-21) share one shape:
**a capability or guarantee that is declared but not verified.** The countermeasure is identical
in every case — `verify-repo` and the conformance suite must assert against *reality*, not
against a configuration file: a live HTTP `HEAD` for cache headers and `Content-Encoding`, a live
`GET` for staging exposure, a real orphan blob for GC, a real hash mismatch for verification, and
a probe object round-tripped through the write transport and back out the read URL for endpoint
pairing.

That is the single most important operational habit this design asks for, and it is the one the
reference most conspicuously lacked — its GC swept the wrong prefix and reported success from
three call sites for years, and its multi-backend factory had an empty class behind it.
