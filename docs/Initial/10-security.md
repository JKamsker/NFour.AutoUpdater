# 10 — Security

> **Revised** after an adversarial review of the API-authority architecture. §5 previously
> claimed that separating the signing identity from the publishing identity bounds a compromised
> build agent. **That claim was false and is retracted** — see §5.3.

## 1. Threat model

The system downloads files from a network location and writes them into a directory from which
executables are run. It is a code-delivery channel and must be designed as one.

| # | Threat | Exposure |
|---|---|---|
| T-1 | Malicious repository content — a blob or manifest replaced | HTTP and FTP mirrors are trivially MITM-able; both in scope |
| T-2 | Trust-root substitution | Fatal if the key list is fetched over the channel it authenticates |
| T-3 | **Rollback / freeze** — an old *validly signed* release served forever | Signatures structurally cannot stop this |
| T-4 | **Compromised build agent** | Controls package *content*. See §5.3 — this is not bounded |
| T-5 | Compromised signing key | Blast radius, revocation path |
| T-6 | Path traversal via manifest (Zip-Slip) | Arbitrary write outside the install root |
| T-7 | Corrupted transfer / wrong-object proxy | The reference verified only the byte *count* |
| T-8 | **Compromised management API** | Holds storage credentials for every backend |
| T-9 | **CAS poisoning** — wrong content at a correct key | Permanent, self-perpetuating denial |
| T-10 | Local privilege escalation via the updater | Runs elevated to write into Program Files |

The reference addresses none of T-1 through T-8. It has no signing whatsoever, no content
verification on the download path, and it executes downloaded scripts whose failures it swallows.

## 2. Layered defence

| Layer | Mechanism | Stops |
|---|---|---|
| Content integrity | SHA-256 verified as bytes land, before staging | T-7, partially T-1 |
| Document integrity | Every immutable document referenced by digest from the one above | T-1 |
| Authenticity | Ed25519 on the channel pointer and release lock | T-1, T-8 |
| Trust anchoring | Public key pinned **outside** the repository | T-2 |
| Freshness | Monotonic `sequence`; absolute staleness floor for fresh installs | T-3 (partially) |
| **Server-side CAS verification** | The API re-hashes from storage before promoting | T-9 |
| Path safety | `VirtualPath` + `PKG012` at publish | T-6 |
| Privilege | Elevation only for the write phase | T-10 |

### 2.1 The digest chain

```
pinned public key
   └─signs─▶ channels/live.json
                 └─releaseDigest─▶ release.lock.json  (independently signed)
                       └─manifestDigest─▶ package.json
                             └─fileTable.shards[].digest─▶ shard blob
                                   └─entry.h─▶ content blob
```

Every arrow is a hash the client verifies. The only trust input is the pinned key at the top.

**This chain is the design's real strength.** It is what bounds almost every storage-layer
compromise — a poisoned mirror, a leaked FTP credential, a hostile CDN edge — to *denial of
service* rather than code execution. It is why FTP-as-origin is an acceptable tradeoff
([16](16-publish-protocol.md) §5.2), and it must not be relaxed because some other layer looks
trustworthy.

Inlined manifests in `release.bundle.json` are verified against `LockedPackage.manifestDigest`,
**not** trusted because the lock is signed. Otherwise a bundle with a valid signed lock and
tampered inline manifests passes, and the optimisation becomes the hole.

## 3. Trust root — pinned, not fetched

`repo.json` carries `trustedKeys`. **It is advisory only.** A key list fetched over the same
channel as everything else authenticates nothing: on FTP or plain HTTP an attacker replaces
`repo.json` with their own key, signs a malicious release, and every check passes.

1. The key is **compiled into the client binary** as the primary anchor.
2. **TOFU-pinned into the ledger** at first install, per repository URL.
3. A key change thereafter requires explicit user-visible confirmation.
4. `repo.json`'s list only *selects among* pinned keys. It never adds one.

### 3.1 Revocation must not become a bricking primitive

The revocation list is signed **only by the offline signer** — never assembled or countersigned
by the API. Clients must refuse to apply a revocation that would empty their trusted-key set, and
refuse to revoke a key not superseded by a valid replacement in the same list. Otherwise the
monotonicity that protects against revocation-replay is exactly what makes a bad list an
irreversible fleet-wide brick with no remote recovery.

## 4. Signing mechanics

- **Ed25519.** Small keys and signatures, no parameter choices to get wrong.
- **One envelope format, no implementation choice** — a detached envelope carrying the payload as
  base64url bytes, defined normatively in [17](17-signed-documents.md) §2. The earlier text
  permitted *either* RFC 8785 *or* exact-bytes signing, which is two incompatible protocols; it
  also embedded the signature inside the bytes it signed, which is recursive. Both are fixed there.
- **What is signed:** `ChannelPointer` and `ReleaseLock`. Everything else is covered transitively.
- **Rotation:** overlapping validity windows; clients accept any pinned key valid at the
  document's `createdAt`.
- **Serving through the API:** returned as opaque bytes, verified before parsing, byte-identical
  to the static file ([09](09-control-plane-server.md) §6.4).

## 5. Key custody, and its limits

### 5.1 The API must not be a signing oracle

If the API assembled `release.lock.json` from database rows and asked a signer to sign it, an API
compromise would yield arbitrary signed releases — RCE on every player machine — and the API
would become **strictly more valuable than the signing key**, because it controls what the key
signs. Custody without control of the input is decorative.

Therefore: signed documents are authored **outside** the API, signed by a gated identity, and
submitted as opaque bytes for verify-and-place. See [16](16-publish-protocol.md) §6.

### 5.2 The signer must not sign what it was handed

`release publish --sign` reconstructs the canonical lock bytes **locally** from its own pin list
and publish receipts, refuses to sign if they differ from the server's draft, and displays a
per-package diff — versions, manifest digests, file counts, size deltas — requiring explicit
confirmation.

### 5.3 What this does *not* bound — retraction

The previous draft stated that a compromised build agent "can publish packages and unsigned draft
releases — which no client will install". **That is false.** Publishing the package *is* the
attack:

1. The agent uploads a trojaned `bin/4story.exe` of similar size to the legitimate one.
2. Every hash is internally consistent — the agent computed them over its own bytes — so every
   integrity check in the pipeline passes.
3. `release check --strict --against <prev>` checks path collisions, layer derivation, `when`
   predicates and coverage drift. `PKG001`–`PKG014` contains **nothing that inspects content**. A
   same-count, similar-size binary swap produces zero diagnostics.
4. The signer signs. The lock pins the attacker's manifest digest, which pins the trojan's blob
   hash.
5. Every client verifies perfectly, from the pinned trust root down.

**Signing attests who assembled a release, never what is inside it.** T-4 is inside the trust
boundary for content and the design must say so rather than imply otherwise.

Controls that would actually bound it, in increasing order of cost:

- **Reviewed signing** (§5.2) catches an *unexpected package* but not a trojan inside an expected
  one. Necessary, insufficient.
- **Build-time attestation** — a second identity signs package manifests at build time, so the
  release signer verifies a chain that does not originate on the publishing agent.
- **Reproducible builds** verified independently before the release is signed. The real answer.
- At minimum: `metadata.vcsRef` / `buildId` cross-checked against an out-of-band build record,
  and refusal to sign a package version that does not match one.

Until one of these exists, the honest statement is: **the CI agent is inside the trust boundary
for package content.**

## 6. CAS integrity

> **The API never writes an object at a key derived from client-supplied data without having
> read the bytes that key names.**

The CAS is keyed by **SHA-256** precisely so that storage can enforce the key: S3's additional
checksums include SHA-256, so a presigned PUT carrying `x-amz-checksum-sha256` in its signed
headers makes S3 reject a mismatching body. That closes the confused deputy by construction on
the primary backend rather than patching it with a compensating read.

Where storage cannot enforce the key — S3 multipart above 5 GiB (`COMPOSITE` checksums), Azure
(only collision-broken MD5), B2, local — **server-side re-hash before promotion is mandatory**.
Full matrix and the FTP exception: [16](16-publish-protocol.md) §5.

CAS poisoning also needs a **repair path**, because dedup makes it self-perpetuating: an
attacker who uploads garbage under a hash that will legitimately appear later makes the real
bytes permanently unpublishable. `POST /blobs/{hash}/quarantine`, `verify-repo --deep
--rehash-cas`, and `blobs/query` reporting present only for `verifiedAt` placements
([16](16-publish-protocol.md) §7.1).

## 6.1 `repo.json` is unsigned, so its templates are constrained

`repo.json` is class D — mutable, unsigned, and **never trusted for an install decision**
([17](17-signed-documents.md) §3). But it supplies layout templates and mirror URLs, so a
compromised origin could use it to redirect fetches. Hash checking stops content substitution; it
does not stop denial of service, tracking, credential leakage, or SSRF when the fetcher runs
server-side (review **H14**).

Therefore:

- **Templates resolve to relative object keys only.** An allowlist of placeholders
  (`{alg} {h0:2} {h2:4} {hash} {productId} {packageId} {version} {releaseId} {channel} {page}`);
  anything else is rejected. A template that produces an absolute URL, a scheme, an authority,
  `..`, a percent-encoded traversal, or a leading `/` is rejected.
- **`blobBaseUrls` entries must be same-origin with the configured repository URL, or appear in a
  client-side allowlist.** A repository cannot introduce a new origin to a client that did not
  already trust it.
- **Schemes are limited to `https` (and `http` only when the configured repository is already
  `http`).** Never `file:`, never `ftp:` on the read path.
- **Server-side fetchers additionally refuse** loopback, link-local, and RFC 1918 destinations,
  because there the redirect is an SSRF primitive rather than a nuisance.

## 6.2 Two distribution profiles

The blanket claim that "players hold no credential" is true of the public profile and not of the
protected one; conflating them hides a real difference (review **H18**).

| | **Public** (default) | **Protected** |
|---|---|---|
| Read auth | none | short-lived scoped token via the content gateway |
| Client holds | nothing | a token, cached in memory, refreshed |
| Use | `live` for players | internal/`ptr` builds, embargoed content |
| Signed-object identity | unaffected | **unaffected** — tokens never enter a digest or a signature |

The last row is the invariant that matters: a protected repository serves *byte-identical*
objects to a public one. Authorisation gates access; it never changes content, because content
that varies by reader cannot be content-addressed.

## 7. Anonymous reads

Reads require no credential. This removes an entire class of threat — no read credential exists
to steal, and none can be escalated into a write grant — at two costs:

1. **The repository root is public.** Non-public channels need a separate root or the content
   gateway ([06](06-storage-backends.md) §6).
2. **Unauthenticated telemetry cannot gate a safety control.** Rollout advancement is
   operator-driven; telemetry is advisory ([09](09-control-plane-server.md) §2.1).

## 8. Storage-layer boundaries

### 8.1 Staging lives outside the served root

With a public bucket or a CDN fronting the root, a staging prefix inside it turns any publisher
credential into arbitrary file hosting on the game's own trusted domain, and leaks unreleased
builds to anyone who can compute an expected file's digest. Separate bucket, separate FTP root,
or an explicit deny in bucket policy **and** CDN origin rules — asserted by a live `GET`
requiring 403/404.

### 8.2 FTP is a reduced-guarantee origin

The publisher holds a tree-wide credential and can write `blobs/**` and `channels/**` directly.
No server-side control can prevent that, because FTP has no scoping to build one from. So:

- `repo.json` marks the repository `"integrityGuarantee": "reduced"`.
- Where the daemon supports per-directory ACLs, publishers get write on `_staging/`, `blobs/`,
  `releases/` and **read-only on `channels/` and `repo.json`**. That one split downgrades a
  *leaked* credential from a freeze attack to content DoS.
- What still holds is the client-side chain: a poisoned FTP origin is DoS, not RCE.

### 8.3 A compromised API (T-8)

The API holds storage credentials for every backend it brokers. A compromise therefore permits
CAS poisoning, staging abuse and pointer placement — but **not** a signed release, because the
API neither holds the signing key nor authors signed documents. The blast radius is bounded to
denial of service by exactly the same chain that bounds a hostile mirror. That bound is the
reason §5.1 is non-negotiable.

## 9. Freshness — the honest limit

Signatures prove *who*, not *when*. An attacker who can serve stale content can pin a population
on a known-vulnerable release.

- `releaseSequence` is monotonic per channel; a client refuses a lower sequence unless the user
  explicitly rolls back. Channel-scoped rather than global, so a canary channel and `live` do not
  interact ([09](09-control-plane-server.md) §2.2).
- **Fresh installs have no ledger floor** — this is the real gap. Mitigation: an absolute
  staleness bound below which a fresh install *refuses* a pointer rather than warning, plus
  surfacing `releaseSequence` and `updatedAt` at first install.
- On a CDN, edges legitimately disagree for the pointer TTL, so the staleness window must exceed
  it. This is a warning signal, not enforcement, and the design should not pretend otherwise.

## 10. Path safety

`VirtualPath` is a security boundary, not a formatting helper — see [03](03-architecture.md) §4.2.
Enforced identically in `TryCreate` (client, compose time) and in the publish gate (`PKG012`), so
a manifest that would escape the install root is rejected at both ends. **Symlinks are rejected
at publish** in v1; if ever supported, the target string becomes an arbitrary-write primitive.

## 11. Transport

- HTTPS with full chain validation; **FTPS with certificate validation** — plain FTP requires
  `--insecure-transport` and warns on every operation.
- Transport security is defence in depth. The design deliberately does **not** depend on it,
  which is what makes plain-HTTP and FTP mirrors acceptable at all.
- A content gateway is **not a trust anchor**. "It came over HTTPS from our own server" must
  never justify skipping verification — a gateway is exactly the component that, once
  compromised, serves poisoned content to everyone at once.
- `AutomaticDecompression = None` on every blob client; any `Content-Encoding` on a blob response
  is a hard error. Note that forbidding the header on a presigned PUT is **impossible** —
  promotion overwrites it instead ([16](16-publish-protocol.md) §7.4).

## 12. Privilege and hygiene

- Download, hash and stage run **unelevated**; only materialise, delete and commit need write
  access to a protected install root.
- `.4sup/` inherits the install root's ACL — a world-writable ledger is a deletion primitive.
- No credentials in the repository. The reference shipped a plaintext `ClearTextPassword` for a
  private NuGet feed in `NuGet.Config`; secret scanning is a blocking CI gate.
- No endpoint that resolves an arbitrary credential supplied as a URL path segment and returns
  its privilege set. The reference's `DebugController.cs:32-46` does exactly this.

## 13. Post-install hooks

Deferred, and gated on signing being live **and** on §5.3 having a real answer. Hooks are a
code-execution primitive; adding them while the CI agent is inside the content trust boundary
widens the blast radius of a build-agent compromise from "ships a trojaned binary" to "runs
arbitrary code as the installer". Preconditions:
[08](08-publishing-and-validation.md) §7.
