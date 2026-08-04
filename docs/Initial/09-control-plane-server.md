# 09 — Management API

> **Revised.** The first draft treated the server as an optional control plane bolted on in a
> late phase. The architecture is now: **the API is the management authority**, storage backends
> are dumb byte stores, **reads are anonymous**, and **auth exists only on the write path**.

## 1. What the API is

The API owns the database and every authoring decision: products, packages, releases, axes,
requirements, pins, channels, and the blob placement ledger. It brokers write access to storage
([16](16-publish-protocol.md)) and it publishes a **static projection** of its state so that
players can resolve and install without ever talking to it.

| The API owns | The API must never |
|---|---|
| The database — the authoring surface | Carry payload bytes on a client-facing path |
| Grant minting and the placement ledger | Hold the release signing key |
| Publishing the static projection | **Author or sign** `release.lock.json` or `channels/*.json` |
| Operator actions: promote, roll back, yank, GC | Be required for a player to install |

That third prohibition is the one that matters most. If the API assembled the release lock and
asked a signer to sign it, an API compromise would yield arbitrary signed releases — RCE on
every player machine — and the API would become strictly more valuable than the signing key.
Signed documents are authored outside the API and submitted as opaque bytes for
**verify-and-place**. See [16](16-publish-protocol.md) §6.

## 2. Anonymous reads

**Players hold no credential of any kind.** The read path is `GET` against a static key the
client computed itself, exactly as specified in [05](05-repository-format.md). Consequences,
each of which is a design input rather than a regret:

- No device enrolment, no device tokens, no token theft, and no path by which a read credential
  could be escalated into a write grant — because there is no read credential.
- A dumb HTTP mirror is a **first-class deployment**, not a degraded one.
- **Everything in the repository root is public.** Internal and PTR channels, draft releases and
  unreleased content are readable by anyone who guesses a URL, and
  `products/{p}/channels/ptr.json` is not a hard guess.

  → Non-public channels belong in a **separate repository root** with its own credentials, or
  behind the content gateway ([06](06-storage-backends.md) §6), which is the one component that
  can gate reads while keeping `live` anonymous.

### 2.1 What anonymity costs: telemetry is not a control input

An earlier draft gated staged rollout on observed telemetry failure rate. **With anonymous
clients that is not trustworthy.** Anyone can report any outcome: reporting failures halts every
rollout so security patches never ship, and reporting success drowns the genuine signal so a
release that bricks installs is promoted anyway. The second is the serious one — it converts a
safety gate into a false assurance, which is worse than having no gate.

**Telemetry is therefore diagnostic only.** It is useful in aggregate for spotting that something
is wrong; it never advances or halts anything automatically. Promotion is a human decision
informed by telemetry, support volume and crash reporting together.

### 2.2 Rollout is channel promotion

```
products/fourstory.client/channels/live.json   ← everyone
products/fourstory.client/channels/ptr.json    ← opt-in testers
```

A client follows one channel. Promotion is a single write; rollback is a single write using
`previousReleaseId`. Each channel has its own monotonic sequence, so `ptr` running ahead of
`live` never causes a client on either to refuse an update.

Channel membership is client configuration, so a player can opt into `ptr` by editing a file.
That is a content-disclosure nuisance, not a security failure — and non-public builds belong in a
separate repository root anyway.

## 2.3 There is no device targeting

**Every device gets the latest version of the channel it follows.** There is no per-device
version control, no device registry, no device identity, and no device control plane.

This is not a simplification forced by anonymous reads — it is what the product actually wants.
A game client should be current; a device held deliberately behind is a support burden, not a
feature. The reference carried an entire targeting subsystem
(`ProductVersionAssignments`, `CashboxReleaseChannels`, per-device update queues) because it
patched point-of-sale terminals, where holding a specific till on a specific build is a real
requirement. That requirement does not exist here.

What remains is **channels**, and they carry the whole load:

| Need | Mechanism |
|---|---|
| Everyone on the current build | `channels/live.json`; the launcher follows it |
| Pre-release testing | `channels/ptr.json`; testers opt in by configuration |
| Staged rollout | Promote `ptr` → `live` when satisfied. The gate is a human, not a percentage |
| Emergency rollback | One write to `live.json` using `previousReleaseId` |
| Internal builds | A separate repository root ([10](10-security.md) §7) |

Consequences worth stating plainly:

- **Rollout is coarse.** There is no "5% of devices". A release is either on `ptr` or on `live`.
  If finer rollout is ever needed, the honest way to get it is more channels, not device
  identity.
- **Telemetry becomes purely diagnostic** — useful for spotting a bad release in aggregate,
  never a control input. That was already true once reads went anonymous
  ([§2.1](#21-what-anonymity-costs-rollout-gating)); now nothing depends on it at all.
- **The API's scope narrows to authoring.** Packages, releases, channels, grants, GC. It is a
  publishing service, not a fleet manager.

### 2.4 Server-scoped content ("special servers")

The one case that genuinely varies per player is **which game server they join** — a realm may
ship custom UI, custom maps, or modified data. That is a *composition* problem, not a targeting
problem, and the variant model already solves composition.

Concretely: a special server is a higher-layer package (or set of packages) composed over the
base game. Joining realm X means resolving a file set that includes `realm.x`; leaving it means
recomposing without it. Both are ordinary diffs, and the local CAS makes the second and
subsequent switches nearly offline.

Three ways to express it, in increasing order of capability and cost:

| Model | How | Fits when |
|---|---|---|
| **Server as a channel** | `channels/realm-phoenix.json` pins base packages + `realm.phoenix` | Realms are few and you publish for all of them |
| **Server as an axis** | `server` axis; one release covers every realm | Realms are few, centrally known, and change rarely |
| **Federated overlay** | Base repo + the realm's *own* repository, composed client-side | Realms are operated by third parties |

The design currently specifies the first two — both fall out of
[04](04-variant-model.md) with no new machinery. **The third is a real feature, not a
configuration**, and it carries a security cliff: a federated overlay lets a third-party server
operator write files into a player's install. Preconditions before it could ship:

- A **separate trust root per server**, with a visible consent step when a player first joins.
- Overlay packages **confined to data paths** — never `bin/**`, never anything executable — or
  the design has handed arbitrary code execution to every realm operator.
- A path-prefix allowlist enforced at compose time, not merely at publish time, since the
  publisher is no longer trusted.

Which model applies depends on who operates special servers. See
[14](14-open-questions.md) Q14.

## 3. Domain model

Derived from the reference's schema — which got the shape right — with the dimension it entirely
lacks: **variants**.

```
Product ──┬── Package ── PackageVersion ── PackageVersionFileTable (header only, ~16 rows)
          ├── AxisCatalog ── AxisValue ── AxisRetirement          ◀── no equivalent in reference
          ├── ReleaseDraft ─┬── ReleaseAxis / ReleaseAxisValue
          │                 ├── ReleaseRequirement (whenJson, rankAs, overrides[])
          │                 └── ReleasePin
          ├── Release (published index row: lockDigest, coverageDigest, signedBy, projectionState)
          └── Channel (current, previous, sequence, pointerDigest, projectionState)

BlobRef (hash, algorithm, size, storedSize, encoding, firstSeenAt)
  └── BlobPlacement (blobRefId, backendId, key, verifiedAt)        ◀── verifiedAt is load-bearing

PublishSession ── UploadGrant (grantId, stagingKey, expectedDigest, expiresAt, consumedAt)
PublisherIdentity · OperatorIdentity · AuditEvent
```

### 3.1 Three decisions worth calling out

**File-table rows are not in the database.** The reference's `ProductFiles`
(`Id, Path, ProductVersionId, ReferenceId`) is tens of millions of rows of immutable,
content-addressed data that is never queried transactionally — and it is why republishing a
184k-file core package is a write storm. Here, paths live in JSONL shard blobs in the CAS; the
database holds only the ~16-row shard header. This is the single biggest departure from the
reference and it is what makes package republication cheap.

**`BlobPlacement.verifiedAt` is a security field, not bookkeeping.** `blobs/query` reports a
hash as present **only** when placement is verified. Without that, an attacker who pre-uploads
garbage under a hash that will legitimately appear later makes the real bytes permanently
unpublishable — dedup guarantees it. See [16](16-publish-protocol.md) §7.1.

**Published artifacts outrank the database.** For anything digest-referenced from a signed lock
— package manifests, the lock itself — the *artifact* wins on conflict. The database row is a
query index. A digest mismatch quarantines the version; it never rewrites the object. A database
cannot outrank an Ed25519 signature.

## 4. Ownership: database versus published artifact

| Concern | Database | Artifact | Authoritative |
|---|---|---|---|
| Product registry, package identity | ✔ | `product.json` (cosmetic) | DB |
| Package version, published | index row | `packages/{id}/{v}/package.json` | **artifact** |
| File table rows | ✘ **not owned** | shard blobs (JSONL) | **artifact** |
| File table header | ✔ (~16 rows) | inside `package.json` | artifact |
| Blob catalogue + placement | ✔ | the blob itself | DB for placement, storage for bytes |
| Release draft, axes, requirements, pins | ✔ | — | DB |
| Release, published | index row | `release.lock.json` (**signed**) | **artifact** |
| Coverage | summary + per-point rows for diffing | `coverage.json` | artifact for the digest |
| Channel pointer | intent | `channels/{c}.json` (**signed**) | **DB for intent, artifact for effect** |
| Repo descriptor, indexes, bundle | derived | `repo.json`, `index.json`, `release.bundle.json` | DB (regenerated) |
| Trust roots | public key metadata only | `trustedKeys[]` (advisory) | **neither — the client's pinned key wins** |

The channel row deserves its wording: the database says where the channel *should* point; the
object says where clients *are actually being sent*. A reconciler drives storage toward the
database and **alerts on divergence** rather than silently correcting, because divergence means
either a failed projection or an unauthorised direct write.

## 5. Degradation — what works with the API down

This is the acceptance test for the whole boundary.

| Operation | API down |
|---|---|
| Cold install from a mirror | ✔ works |
| Update | ✔ works |
| Variant switch | ✔ works |
| Rollback | ✔ works |
| Verify / repair | ✔ works |
| Joining a special server | ✔ works (it is a composition, not a lookup) |
| Publish, promote, GC | ✘ requires the API |
| Telemetry | ✘ buffered and dropped |

Every player-facing operation resolves from the static projection. The API is required only for
authoring and operations. **This property survives the revision** and remains a required test.

## 6. API surface

Versioned under `/api/v1`. Read endpoints are anonymous; every write endpoint is authenticated.

### 6.1 Anonymous (read)

| Verb | Route | Purpose |
|---|---|---|
| `GET` | `/.well-known/4sup` | Discovery: API root, versions, repository base URL, `minimumClientVersion` |
| `GET` | `/repositories/{repo}/products` | Browse |
| `GET` | `/products/{p}/channels/{c}` | Channel pointer as **opaque signed bytes** (§6.4) |
| `GET` | `/products/{p}/releases/{r}` | Release lock as opaque signed bytes |
| `POST` | `/telemetry` | Anonymous, rate-limited, advisory only |

### 6.2 Publisher (authenticated write)

| Verb | Route | Purpose |
|---|---|---|
| `POST` | `/repositories/{repo}/blobs/query` | Batched existence probe — **repo-scoped** |
| `POST` | `/repositories/{repo}/publish/sessions` | Open a staging session |
| `POST` | `/publish/sessions/{id}/grants` | Mint per-object upload grants |
| `POST` | `/publish/sessions/{id}/seal` | Close; triggers verify + promote |
| `POST` | `/packages/{id}/versions` | Create a draft version |
| `POST` | `/packages/{id}/versions/{v}/files` | Register verified blobs to paths |
| `POST` | `/packages/{id}/versions/{v}/publish` | Freeze immutable |
| `POST` | `/products/{p}/releases/drafts` | Create/edit a release draft, axes, requirements, pins |
| `POST` | `/products/{p}/releases/drafts/{d}/check` | Run the publish gate; returns diagnostics |

`blobs/query` **must** be repository-scoped. An unscoped variant is a global hash-existence
oracle over every product in the installation.

### 6.3 Operator (authenticated, audited)

| Verb | Route | Purpose |
|---|---|---|
| `PUT` | `/products/{p}/releases/{r}/lock` | **Verify-and-place** a signed lock (opaque bytes) |
| `PUT` | `/products/{p}/channels/{c}` | **Verify-and-place** a signed pointer (opaque bytes) |
| `POST` | `/products/{p}/channels/{c}/rollback` | Place the previous signed pointer |
| `POST` | `/products/{p}/releases/{r}/yank` | Mark yanked |
| `POST` | `/blobs/{hash}/quarantine` | **CAS repair path** — see [16](16-publish-protocol.md) §7.1 |
| `POST` | `/repositories/{repo}/gc` | Mark-and-sweep with quarantine |
| `POST` | `/repositories/{repo}/reconcile` | Drive storage toward DB; report divergence |

Note that `PUT .../lock` and `PUT .../channels/{c}` are *placement* operations. The API verifies
the signature against the trusted key list and stores the bytes unchanged. It does not construct
either document.

### 6.4 Signed documents through the API

A signed document returned by the API is returned as **opaque bytes** — never re-wrapped in a
response envelope that reserialises it. The client verifies the signature over the received bytes
*before parsing*, using the identical code path as for the static file. A byte-equality test
between the API path and the static path is required; without it, the pinned-key chain silently
breaks for every client that prefers the API.

## 7. Authentication and authorization

The reference has **effectively none**: `Program.cs:133` calls `app.UseAuthorization()` with no
`UseAuthentication()`, there is not one `[Authorize]` attribute in the project, identity comes
from a plaintext `x-api-key` header with no expiry and no request binding, and
`ClientIdentifierMiddleware` swallows every exception. `DebugController.cs:32-46` will resolve an
**arbitrary api key supplied as a URL path segment** and return its full privilege set — a free
validity-and-privilege oracle that also writes secrets into proxy logs.

None of that is carried forward.

### 7.1 Principals

| Principal | Credential | Lifetime | May |
|---|---|---|---|
| **Anonymous** | none | — | Read everything in a public repository root |
| **Publisher / CI** | OIDC federation from the CI provider, exchanged for a short-lived token | ≤ 15 min | Upload, register, draft releases, run the gate |
| **Operator** | Interactive login, MFA, step-up for destructive ops | session | Place signed documents, promote, roll back, quarantine, GC |
| **Signer** | Offline or HSM/KMS identity | per-operation | Sign locks and pointers. **Not an API principal at all** |

The signer is deliberately outside the API's principal model. It never receives a token and
never has an endpoint.

### 7.2 Rules

- **Audience is a property of the credential type, never of the request.** A token endpoint that
  echoes a requested `audience`, `resource` or `scope` is how a low-privilege credential mints a
  high-privilege token. Requests carrying those parameters beyond the principal's registration
  are rejected, and an integration test asserts it.
- **Deny by default, verified at startup.** A hosted service enumerates every endpoint and fails
  startup if any lacks an explicit authorization policy or an explicit `[AllowAnonymous]`. This
  single check would have prevented all six of the reference's unauthenticated endpoints.
- **Separation of duties.** Publishing and signing are different identities; the publisher
  credential can never produce an installable release on its own.
- **Break-glass** exists for promote and rollback, is heavily audited, and must not be gated on
  a step-up factor a machine credential cannot satisfy — otherwise it cannot do the one thing it
  exists for.
- **Rollback and halt are single-operator operations.** Requiring two people to stop a bad
  rollout is how outages get long.
- Publisher secrets: short-lived OIDC federation, not long-lived keys.

### 7.3 The limit of this model — stated plainly

A compromised CI agent controls the *bytes inside a package*. It publishes a package normally;
every hash is internally consistent because the agent computed them over its own bytes; the
publish gate checks path collisions, layer derivation and coverage drift and has **nothing that
inspects content**; a same-size trojaned binary produces zero diagnostics; the signer then pins
that manifest digest and every client verification passes perfectly.

**Signing attests who assembled a release, never what is inside it.** The separation of publisher
from signer bounds *who can ship*, not *what a compromised builder can put in a package*.
[10](10-security.md) §5 states this honestly and lists the controls that would actually bound it.

## 8. Persistence

PostgreSQL via EF Core + Npgsql, matching the sibling repo's cutover.

- `whenJson` and `VariantSelection` stored as `jsonb` with GIN indexes, queried by containment,
  never joined.
- Unique index on `BlobRef(hash, algorithm)`.
- Telemetry in a separate partitioned table with a retention policy, never in operational tables.
- Golden-file migration tests: a checked-in older database the current code must still open.

## 9. Deployment

Docker-by-default per `rules/docker.md` — this is a new containerisable component, so the native
host exception that covers TClient does not apply. Ships alongside PostgreSQL in the sibling
repo's compose topology.

The API and the optional **content gateway** ([06](06-storage-backends.md) §6) are separate
deployables with different scaling profiles and different failure domains. The gateway must never
be a route on the API.
