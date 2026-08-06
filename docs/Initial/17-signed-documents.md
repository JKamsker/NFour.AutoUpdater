# 17 — Signed Documents and Control Lifecycle (normative)

> Added in the redline responding to review findings **C2, C3, C4, H13**. This document is
> normative: where it disagrees with prose elsewhere, this wins.

## 1. Why this document exists

The first draft permitted each implementation to choose between RFC 8785 canonical JSON and
"sign the exact bytes", embedded signatures inside the objects they signed, claimed verification
happened "before parsing", and described a bundle as containing a "byte-identical lock section"
while depicting it as an ordinary nested JSON object. Those are four different ways of not
having a signature format.

It also defined rollback as *restoring the previous channel pointer* while requiring clients to
reject any pointer whose sequence went backwards — so rollback would have silently failed on
every client that had already seen the newer pointer.

## 2. The envelope

**One format. No implementation choice.** Every signed document is a *detached envelope*: the
payload is carried as opaque bytes, and the signature covers exactly those bytes.

```json
{
  "envelope": 1,
  "type": "channel-pointer",
  "payload": "eyJzY2hlbWFWZXJzaW9uIjoxLCJwcm9kdWN0SWQiOiJmb3Vyc3RvcnkuY2xpZW50Ii…",
  "signatures": [
    { "keyId": "4s-2026", "algorithm": "ed25519", "signature": "n1QkV0h…" }
  ]
}
```

| Field | Rule |
|---|---|
| `envelope` | Envelope format version. `1`. An unknown value is a hard failure. |
| `type` | Payload discriminator: `channel-pointer`, `release-lock`, `key-manifest`, `revocation`. Bound into the signing input (§2.2) so a payload cannot be replayed as a different type. |
| `payload` | **base64url, unpadded** (RFC 4648 §5) encoding of the exact payload bytes. |
| `signatures` | One or more. Verification succeeds if **at least one** signature verifies against a currently trusted key. Multiple entries exist to permit key rotation overlap (§5). |

### 2.1 Why detached rather than canonical-JSON-in-place

- A signature embedded in the object it signs is recursive unless the format defines an exclusion
  rule, and exclusion rules are a recurring source of interop bugs.
- "Verify before parsing" is literally implementable here: the verifier parses only the envelope
  (four fields, fixed shape), then verifies, then parses the payload. With an embedded signature
  the verifier must parse the untrusted payload to find the signature — which is the thing it was
  trying to avoid.
- Re-encoding is impossible by construction. A nested JSON object cannot preserve arbitrary
  original bytes across parse/serialise; base64url can.

The payload itself is ordinary JSON and **need not be canonical**, because it is never
re-serialised — it is transported, stored and hashed as bytes. Producers SHOULD emit RFC 8785
canonical form for diffability; consumers MUST NOT depend on it.

### 2.2 Signing input

```
signing_input = "4sup-v1" 0x00 type 0x00 payload_bytes
```

The domain-separation prefix and the `type` field prevent a signed payload of one kind being
presented as another. Ed25519 signs `signing_input` directly (no pre-hash).

### 2.3 Storage and transport

- At rest, a signed document is stored **as the envelope**, byte-for-byte.
- The API's `PUT` accepts the envelope as opaque bytes, verifies, and stores unchanged
  ([16](16-publish-protocol.md) §6).
- The API's `GET` returns the envelope byte-for-byte. Never re-wrapped, never re-serialised.
- A **byte-equality test between the static-file path and the API path is required**
  ([12](12-testing.md)).

### 2.4 `release.bundle.json`

The bundle is **unsigned and derived**. It carries the release-lock *envelope* verbatim as a
base64url string plus parsed copies of the package manifests:

```json
{
  "schemaVersion": 1,
  "lockEnvelope": "eyJlbnZlbG9wZSI6MSwidHlwZSI6InJlbGVhc2UtbG9jayIs…",
  "inline": { "fourstory.client.core": { "…PackageManifest…" } }
}
```

The client verifies `lockEnvelope` exactly as it would the standalone file, then verifies each
inline manifest against its `manifestDigest` from the verified lock. The bundle is an
optimisation and is never a trust shortcut — a bundle whose inline manifests disagree with the
lock is rejected.

## 3. Mutability classification

Replaces the earlier claim that "the channel pointer is the only mutable object", which
contradicted indexes, descriptors and yanking.

| Class | Members | Mutable | Signed | Trusted for install decisions |
|---|---|---|---|---|
| **A — Immutable signed** | `release.lock.json` (envelope) | no | yes | yes |
| **B — Immutable unsigned** | `package.json`, file-table shards, content blobs | no | no — covered by digest chain | yes, via digest from class A |
| **C — Mutable signed control** | `channels/*.json`, `keys.json`, `revocations.json` | yes, monotonically | yes | yes |
| **D — Mutable unsigned derived** | `repo.json`, `*/index.json`, `product.json`, `release.bundle.json`, `coverage.json` | yes | no | **no** |

Class D is the important one: **nothing in class D may influence an install decision.** It exists
for discovery and tooling. `repo.json` may supply layout templates and mirror URLs, but those are
constrained by [10](10-security.md) §7 and a client must behave correctly if class D is hostile.

`coverage.json` is class D and its digest is carried inside the signed lock; it is a publish-time
report, not a client input.

## 4. Sequences and control documents

### 4.1 Two independent sequences

The review's C3 finding: a single sequence cannot express both "which release is newer" and
"which channel state is newer".

| Sequence | Scope | Meaning |
|---|---|---|
| `releaseSequence` | per product | Ordering of releases. Monotonic. Assigned at release creation. |
| `channelSequence` | per (product, channel) | Ordering of channel *states*. Monotonic. **Strictly increments on every channel write, including rollback.** |

A client tracks `channelSequence` for anti-replay and **must not** infer freshness from
`releaseSequence`.

### 4.2 Channel pointer payload

```json
{
  "schemaVersion": 1,
  "productId": "fourstory.client",
  "channel": "live",
  "channelSequence": 419,
  "supersedesChannelSequence": 418,
  "releaseId": "2026.02.14-a",
  "releaseSequence": 417,
  "reason": "rollback",
  "minimumClientVersion": "1.0.0",
  "updatedAt": "2026-02-15T18:22:04Z"
}
```

Note `channelSequence` 419 pointing at `releaseSequence` 417. That is a rollback, and it is a
**new signed document**, not a restored old one.

### 4.3 Client acceptance rule

```
accept iff  signature verifies against a currently trusted key
       and  payload.type == "channel-pointer"
       and  productId and channel match what was requested
       and  channelSequence > ledger.lastChannelSequence      (strict)
       and  updatedAt is within the staleness bound (10 §9)
```

`releaseSequence` going **down** is legal and expected on rollback. A client warns and proceeds;
it does not refuse. This is the change that makes operational rollback work.

Local user-initiated rollback is a **different operation**: it targets a specific installed
release from local history and does not consult the channel at all. The two must not share code.

### 4.4 Rollback is a signing operation

Because the API is not a signer, `4sup channel rollback` **requires a signer**:

```bash
4sup channel rollback fourstory.client live --sign 4s-2026
```

It reads the current pointer, constructs a new payload with `channelSequence + 1` and the prior
`releaseId`, signs it locally, and submits the envelope for verify-and-place. The "one write"
property survives; the "restore the old pointer" description does not.

### 4.5 Yank is a control document, not a mutation

An immutable signed release cannot have its `state` field edited — that changes its digest and
invalidates its signature, and the API cannot author a replacement.

```json
{
  "schemaVersion": 1,
  "productId": "fourstory.client",
  "revocationSequence": 7,
  "updatedAt": "2026-02-15T20:00:00Z",
  "entries": [
    { "releaseId": "2026.02.15-a", "action": "yank",
      "effect": "block-install", "reason": "regression in ui.modern",
      "at": "2026-02-15T20:00:00Z" }
  ]
}
```

`effect` is one of:

| Effect | Client behaviour |
|---|---|
| `block-install` | Refuse a *new* install or update **to** this release. Existing installs untouched. |
| `block-repair` | The above, plus refuse `verify --repair` against it. Forces a move. |
| `force-move` | The above, plus prompt to move to the current channel head at next launch. |

Never "force uninstall" — the updater does not delete a player's game because a release was
yanked.

`revocations.json` is class C: monotonic `revocationSequence`, signed, fetched alongside the
channel pointer. It carries an `updatedAt` UTC timestamp so even an empty document is
freshness-bounded. A missing revocation document is treated as empty; a *stale* one is bounded by
the same staleness rule as the channel pointer.

## 5. Key lifecycle

Replaces the earlier hand-wave. `keys.json` is class C.

```json
{
  "schemaVersion": 1,
  "keySequence": 3,
  "keys": [
    { "keyId": "4s-2026", "algorithm": "ed25519", "publicKey": "MCowBQ…",
      "notBefore": "2026-01-01T00:00:00Z", "notAfter": "2027-06-30T00:00:00Z" },
    { "keyId": "4s-2027", "algorithm": "ed25519", "publicKey": "MCowBQ…",
      "notBefore": "2027-01-01T00:00:00Z", "notAfter": "2028-06-30T00:00:00Z" }
  ],
  "revokedKeyIds": []
}
```

Rules:

1. **Rooted in an already-trusted key.** `keys.json` is signed by a key the client already
   trusts — the binary-pinned root, or a key from a previously accepted `keys.json`. A key list
   never establishes its own authenticity.
2. **Monotonic.** `keySequence` must strictly increase. Replay of an older list is refused.
3. **Overlapping windows.** Rotation publishes a list containing both old and new keys with
   overlapping validity, so in-flight documents stay verifiable. A document is verified against
   keys valid at the document's `updatedAt`, with a configurable clock-skew tolerance
   (default ±24 h) to survive client clock drift.
4. **Never empty.** A client refuses a list that would leave its trusted set empty, and refuses
   to revoke a key not superseded by a valid replacement in the same list. Otherwise a bad list
   is an irreversible fleet-wide brick.
5. **Emergency revocation** adds to `revokedKeyIds` and requires a replacement key present and
   valid. Revoking the *pinned root* is not possible remotely — that requires a client update,
   which is deliberate.
6. **Offline recovery.** A client whose trusted set cannot verify the current channel pointer
   fails closed with a distinct exit code (5) and a message naming the expected key ids. It does
   not silently fall back.

### 5.1 Trust profiles

The two profiles have different rules and must not be conflated:

| Profile | Root | Key change |
|---|---|---|
| **First-party pinned** (4Story official) | Compiled into the client binary | Only via `keys.json` chained from the pinned root |
| **TOFU** (self-hosted, third-party repos) | Recorded in the ledger at first install | Requires explicit user-visible confirmation |

## 6. Golden vectors

Every rule above is worthless without cross-implementation test vectors.
`tests/vectors/signed/` ships, and every implementation must reproduce it exactly:

- Envelopes whose payloads contain non-ASCII, escaped characters, large and negative numbers,
  reordered properties, unknown fields, and empty objects.
- An envelope with a **duplicate key** in the payload — must be rejected
  ([18](18-normative-contract.md) §4).
- A valid signature over a payload with unusual but legal whitespace, proving the payload is not
  re-serialised.
- A rollback chain: `channelSequence` 417 → 418 → 419-pointing-at-417, with the expected accept
  and reject at each step.
- A key rotation: sign under `4s-2026`, rotate, verify old documents still validate.
- A revocation that would empty the trusted set — must be refused.
