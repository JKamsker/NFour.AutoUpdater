# 03 — Architecture

## 1. Shape of the system

```
   AUTHORING          ┌───────────────────────────────────────────────┐
   (CI, authenticated)│  slice ▸ hash ▸ probe ▸ grants ▸ upload ▸ seal │
                      └────┬──────────────────────────────┬───────────┘
                           │ metadata only                │ bytes, DIRECT
                           ▼                              │
   MANAGEMENT   ┌──────────────────────────┐              │
   API          │ DB: products · packages  │              │
                │ releases · axes · pins   │              │
                │ channels · grants        │              │
                │ blob placement ledger    │              │
                └────┬──────────────┬──────┘              │
        verify+copy  │              │ publishes           │
        (server-side)│              │ static projection   │
                     ▼              ▼                     ▼
   STORAGE   ┌──────────────────────────────────────────────────────┐
   (bytes)   │ repo.json · blobs/ (CAS) · packages/ · products/     │
             └────────────────────────┬─────────────────────────────┘
                  ▲ mirror            │  anonymous GET (computed keys)
                  │                   ▼   [optionally via content gateway]
                  │            ┌──────────────────────────────┐
                  └────────────│ client: resolve ▸ compose    │
                               │ ▸ plan ▸ fetch ▸ apply       │
                               │ ▸ commit ledger              │
                               └──────────────────────────────┘
                                  no credentials, ever
```

Four rules define the whole architecture:

1. **The read path is anonymous and needs no server code.** Everything a player needs is a
   static file at a key the client computed itself. The management API is required for
   authoring and operations, never for a player to install.
2. **The composed file set is the only truth.** Install, update, switch, repair and rollback
   are all the same operation: compose a target, diff it against the ledger, apply the diff.
   There is no second code path for "update" versus "install".
3. **No client-facing API request or response carries payload bytes.** The API brokers *access*
   to storage — presigned PUT, SAS — and moves bytes with server-side copy. Publishers upload
   directly to storage. See [16](16-publish-protocol.md) §2.
4. **The API never authors or signs a release lock or a channel pointer.** Those are authored
   outside it, signed by a gated identity, and submitted as opaque bytes for verify-and-place.
   Otherwise an API compromise yields arbitrary signed releases. See [10](10-security.md) §5.1.

## 2. Solution layout

```
src/
  FourSaas.AutoUpdater.Core/            # identity, axes, manifests, resolution, composition
  FourSaas.AutoUpdater.Storage/         # IObjectStore ports + RepositoryLayout + capabilities
  FourSaas.AutoUpdater.Storage.Brokering/ # UploadGrant, IStagedObjectVerifier, promotion
  FourSaas.AutoUpdater.Storage.Local/
  FourSaas.AutoUpdater.Storage.S3/
  FourSaas.AutoUpdater.Storage.Ftp/
  FourSaas.AutoUpdater.Storage.Http/    # read-only
  FourSaas.AutoUpdater.Storage.Memory/  # in-memory; ships in the main solution, used by tests
  FourSaas.AutoUpdater.Repository/      # IPackageRepository over IObjectStore; GC, prune, mirror
  FourSaas.AutoUpdater.Publishing/      # slicing, hashing, packing, validation, release building
  FourSaas.AutoUpdater.Client/          # planner, applier, install ledger, verify/repair
  FourSaas.AutoUpdater.Client.Windows/  # service tickets, Restart Manager, SCM integration
  FourSaas.AutoUpdater.Cli/             # `4sup`
  FourSaas.AutoUpdater.Server/          # management API (ASP.NET Core + EF Core/Npgsql)
  FourSaas.AutoUpdater.Gateway/         # OPTIONAL read-path content gateway; separate deployable
tests/
  FourSaas.AutoUpdater.Core.Tests/            # pure; no I/O, no temp directories
  FourSaas.AutoUpdater.Storage.Tests/         # ONE conformance suite, run against ALL backends
  FourSaas.AutoUpdater.Repository.Tests/
  FourSaas.AutoUpdater.Publishing.Tests/
  FourSaas.AutoUpdater.Client.Tests/
  FourSaas.AutoUpdater.GoldenRepo.Tests/      # checked-in v1 repository tree; forward-compat gate
  FourSaas.AutoUpdater.Server.Tests/
  FourSaas.AutoUpdater.Architecture.Tests/    # NetArchTest; enforces §3
```

`Core` has **zero project references and does no I/O**. This is the direct antidote to the
reference's `Apro.AutoUpdater.Lib` god library ([02](02-reference-review.md) §2.18), and it is
what makes the resolution algorithm — the part most likely to have subtle bugs — testable as
pure functions over literals.

## 3. Layering rules

Dependencies flow left-to-right only. Enforced by `NetArchTest.Rules` in
`FourSaas.AutoUpdater.Architecture.Tests`, not by convention.

```
Core ◀── Storage ◀── Storage.{Local,S3,Ftp,Http,Memory}
  ▲         ▲
  │         └──────── Repository ◀── Publishing
  │                        ▲            ▲
  │                        └── Client ──┘
  │                              ▲
  └──────────────────────────────┴── Cli, Server
```

Asserted rules:

| Rule | Rationale |
|---|---|
| `Core` references no other project in the solution | Keeps resolution pure and fast to test |
| `Core` uses no `System.IO` type except `Stream` | I/O in the domain is how god libraries start |
| No project outside `Storage.*` references an SDK client (`AWSSDK.*`, `FluentFTP`) | Backend leakage is the reference's `AzureBlobManager` failure |
| Only `Storage` and `Repository` construct object keys | The `Products`/`products` split brain ([02](02-reference-review.md) §2.3) |
| `Client.Windows` is referenced only by `Cli` and consumers, never by `Client` | NFR-6, cross-platform |
| No type in `Storage.Brokering` accepts or returns a payload `Stream` on a client-facing path | Rule 3; the byte rule enforced by the type system, not by review |
| `Gateway` references no project that references `Server` | The gateway must never become a route on the API |
| `Server` contains no signing primitive and no reference to a signing library | Rule 4; an API that cannot sign cannot be a signing oracle |
| No `public` type has a `DateTime` property | Forces `DateTimeOffset`; the reference mixed them |

## 4. Core domain types

Full definitions live with the format spec in [05-repository-format.md](05-repository-format.md)
and the variant model in [04-variant-model.md](04-variant-model.md). This section gives the
shape and the reasoning.

### 4.1 Identity

```csharp
namespace FourSaas.AutoUpdater.Core;

public enum HashAlgorithmId { Sha256 = 1, Sha512 = 2, Md5 = 3, Blake3 = 4 }

// Sha256 is the CAS key: it is the only strong digest S3 can enforce at PUT,
// which is what makes storage-enforced integrity sound (16 §5.1).

/// Algorithm-tagged digest. Replaces the reference's BinaryValue, which inferred
/// the algorithm from byte length and reported Md5 for 32 bytes (BinaryValue.cs:66-84).
/// Equals/GetHashCode are HAND-WRITTEN and structural over Value.Span — the
/// compiler-generated versions on a record struct compare the ReadOnlyMemory
/// SEGMENT, which silently breaks every Dictionary and HashSet lookup.
public readonly record struct ContentHash(HashAlgorithmId Algorithm, ReadOnlyMemory<byte> Value)
{
    public static ContentHash Parse(string s);                  // "sha256:9f2c…", lowercase hex
    public static bool TryParse(string? s, out ContentHash h);
    public override string ToString();
    public bool Equals(ContentHash other);                      // structural over bytes
    public override int GetHashCode();                          // structural over bytes
}

/// Transport/at-rest encoding. Generalises the reference's bool IsCompressed + ".gz".
public enum ContentEncoding { Identity = 0, Gzip = 1, Zstd = 2 }

/// A blob is addressed by the hash of its UNCOMPRESSED bytes; the encoding is a
/// property of the STORED OBJECT, not of a manifest entry. In the reference, two
/// versions referencing one sha512 could disagree on FileMetaDataDto.IsCompressed
/// and one of them would 404.
public readonly record struct BlobLocator(ContentHash Content, ContentEncoding Encoding)
{
    public string Suffix { get; }                               // "" | ".gz" | ".zst"
}

public readonly record struct PackageId          // ^[a-z0-9]([a-z0-9.-]*[a-z0-9])?$
{
    public string Value { get; }
    public static bool TryCreate(string? v, out PackageId id);
}

/// Opaque display label plus a publisher-supplied monotonic sequence. Ordering NEVER
/// parses the label, so "2026-w32-hotfix" and "christmas-event" are legal identifiers.
/// Removes the reference's hard SemVer gate (PathParser.cs:181-184).
public readonly record struct PackageVersion(string Label, long Sequence)
    : IComparable<PackageVersion>;

public readonly record struct PackageRef(PackageId Id, PackageVersion Version);
```

### 4.2 Virtual paths

The single most under-defended type in the reference. `VirtualPath` is the boundary where a
Linux build agent is prevented from publishing something no Windows client can install.

```csharp
/// Normalised: '/' separators, Unicode NFC, no leading '/', no "..", no drive letter,
/// no UNC, no ':'. Compared ORDINAL everywhere — the reference compared ordinal in
/// PatchApplier.cs:166 and OrdinalIgnoreCase in FileIndex.cs:61.
public readonly record struct VirtualPath : IComparable<VirtualPath>
{
    public string Value { get; }
    public string FoldedKey { get; }        // invariant-lower; COLLISION DETECTION ONLY
    public static VirtualPath Create(string raw);
    public static bool TryCreate(string? raw, out VirtualPath p, out string? error);
    public static VirtualPath FromHostPath(DirectoryInfo root, FileInfo file);
}
```

`TryCreate` additionally rejects, and the publish gate reports as `PKG012 NonPortablePath`:

- Windows reserved device names — `con`, `nul`, `aux`, `prn`, `com1`–`com9`, `lpt1`–`lpt9` —
  case-insensitive, with or without an extension. A legal Linux path `data/aux.dat` bricks
  every Windows client.
- Trailing dots or spaces on any component. Win32 silently strips them, so two distinct
  manifest paths collapse to one file on disk.
- The characters `< > " | ? *` and control characters 1–31.
- Component and total length that would overflow `MAX_PATH` under a plausible install root
  plus `.4sup/staging/`.
- Case-only collisions within a package, or between two co-selectable packages
  (`PKG009 CaseOnlyPathCollision`) — on Windows the two paths are one file and the composed
  set silently loses one, while the S3 mirror carries two distinct objects.

### 4.3 The variant vocabulary

`AxisDefinition`, `AxisValue`, `AxisCardinality`, `VariantSelection`, `AxisPredicate` —
defined and explained in [04-variant-model.md](04-variant-model.md) §3.

### 4.4 Packages, releases, channels

`PackageFileEntry`, `FileTableRef`, `PackageManifest`, `PackageRequirement`, `LockedPackage`,
`ReleaseLock`, `ReleaseBundle`, `ChannelPointer` — defined in
[04-variant-model.md](04-variant-model.md) §4 with their JSON encodings in
[05-repository-format.md](05-repository-format.md).

### 4.5 Resolution and composition

```csharp
public interface IVariantResolver
{
    /// Pure, synchronous, offline, allocation-bounded. Needs ONLY the ReleaseLock.
    ResolutionResult Resolve(ReleaseLock release, VariantSelection requested);
}

public interface IFileSetComposer
{
    ValueTask<ComposedFileSet> ComposeAsync(
        IPackageRepository repository, ResolutionResult resolution, CancellationToken ct = default);
}
```

`IVariantResolver` being pure and synchronous is a deliberate constraint, not an accident: it
means the entire variant model can be tested with literals and no fixtures, and it means a
launcher can render "what would change if I picked German?" without touching the network.

## 5. Cross-cutting decisions

### 5.1 Diagnostics, not exceptions, for validation

Resolution and validation return `ImmutableArray<Diagnostic>` with stable codes
(`SEL001`, `PKG002`, `RES004`, …). Exceptions are for programming errors and I/O failure only.

Rationale: the reference threw `new Exception("Could not get Capabilities.json")` and
`Invalid version: {version}` — unactionable strings with no code to search for, no severity,
and no way for a caller to render a list. Every code in this design has a table entry
([08](08-publishing-and-validation.md) §3, [04](04-variant-model.md) §6) and a test.

### 5.2 Capability interfaces, not a fat interface with `NotSupportedException`

The reference's `HttpPatchRepository.ListProductsAsync` throws **mid-enumeration**. The new
storage port splits into a small mandatory read interface plus optional capability interfaces,
with a `[Flags]` enum used as a cross-check rather than as the gate.
See [06-storage-backends.md](06-storage-backends.md) §3.

### 5.3 One layout authority

`RepositoryLayout` owns every key template. Nothing else constructs a key. All templates are
lowercase. Asserted by an architecture test.

### 5.4 Streaming by default

File tables are `IAsyncEnumerable<PackageFileEntry>` over JSONL shards and are never fully
materialised. Manifest writes stream through `System.IO.Pipelines`. A 200k-file package must
be publishable and installable in bounded memory (NFR-5).

### 5.5 Time

`DateTimeOffset` only, always UTC, always injected via `TimeProvider` so tests are
deterministic. No `DateTime` on any public surface (asserted).

### 5.6 Serialization

`System.Text.Json` with source-generated contexts for hot paths. Every signed document is
canonicalised per RFC 8785 (JCS) *or* signed over the exact received bytes and never
reserialised — see [10-security.md](10-security.md) §4. Unknown fields are preserved via
`[JsonExtensionData]` on documents a client may rewrite (config), and rejected on documents a
client only reads (manifests) unless the `schemaVersion` is one it knows.

### 5.7 Configuration

An ordered array of named, typed, individually-enableable backends, binding through
`IOptions<>`, in the shape the reference already used in `docker-compose.yml`:

```
Storages__0__Name=live-s3      Storages__0__Type=S3     Storages__0__Priority=10
Storages__1__Name=cdn-mirror   Storages__1__Type=Http   Storages__1__ReadOnly=true
```

Layered config files with a per-layer save hook (`./4sup.json` →
`%APPDATA%\4Story\4sup\config.json` → `%PROGRAMDATA%\4Story\4sup\config.json`, first-wins),
`[JsonExtensionData]` for forward compatibility, and synthesised `local`/`remote` aliases —
all carried over from the reference, which got this genuinely right.

## 6. Why not a database on the client

The client's state is a JSONL ledger and a lock document, not SQLite or LiteDB. Reasons:

- A crash must leave a recoverable state; append-only JSONL plus one atomic rename is easier
  to reason about, and easier to repair by hand at a support desk, than a corrupted b-tree.
- The ledger must survive the user copying the install folder to another machine.
- It removes a native dependency from the client (`SQLitePCLRaw` ships a per-RID binary).

The **server** does use a real database (PostgreSQL via EF Core) — different problem, different
answer. See [09-control-plane-server.md](09-control-plane-server.md).
