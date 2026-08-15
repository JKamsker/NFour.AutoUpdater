# 02 — Reference Review: `Apro.AutoUpdater_1`

Reference: the legacy updater repository (read-only; unmodified).

The reference is a working, production-deployed .NET updater for Apro cashbox software. It
solves several hard problems correctly and is worth studying carefully. It also has a set of
defects that are instructive precisely because they are the defects a reasonable team arrives
at. This document records both, with citations, so the new design can be judged against
something concrete rather than against intuition.

**Decision made:** ideas only, clean design. No code is ported verbatim. Where a mechanism is
kept, it is re-derived — but the *test tables* (glob rules, version-string normalisation) are
worth porting literally because they encode real production edge cases.

---

## 1. What the reference gets right

### 1.1 The flat content-addressed blob store

One `blobs/` directory shared by all products and all versions, keyed by SHA-512 of the
uncompressed content, with compression expressed as a `.gz` sibling rather than a different
identity. Writes are naturally idempotent — `TemporaryLocalPatchFileRepository.MoveToStoreAsync`
probes for `{sha}` and `{sha}.gz` and skips the transfer entirely if either exists.

This is the single most important idea to carry over, and it is *already* the answer to the
variants requirement: five language builds sharing 95% of their files cost 95% nothing,
because only the manifest differs. **Variants are different manifests over one blob store.**

### 1.2 "Every resource has a deterministic, computable filename"

Nothing on the read path lists a directory, uses a query string, or requires server-side
computation. That single invariant is what makes one repository tree work identically over a
local folder, an S3 bucket, an FTP mirror and a plain nginx root — and it is the reason the
new design can support HTTP-read-only at all. Adopted as a hard rule.

### 1.3 Static paged manifests for dumb hosts

`manifest.json` (head, newest N inline) plus `manifest.{n}.json` spill pages, with a
`pagination { pageFormat, pageSize, pageCount }` descriptor
(`LocalFilePatch/Models/ProductManifest/ProductManifestDto.cs`). The hot question — "what is
the newest version?" — costs exactly one request. Kept, with the ordering made explicit in the
document rather than reconstructed by walking pages backwards.

### 1.4 Two hashes, separated by role

`FileMetaDataDto` documents SHA-512 as the *identity* ("used to store on the server") and MD5
as the *checksum* ("used to determine if the file has changed"), computed in a single pass by
`Hasher` over one pooled buffer with N `IncrementalHash` instances. Kept, with the algorithm
made explicit instead of inferred (see §2.1).

### 1.5 The two-tier staleness check

`FileIndexItem.MetadataEquals(fileInfo)` (`Indexing/FileIndex.cs:263`) gates an expensive
content hash behind a cheap `(Size, LastWriteTimeUtc, CreationTimeUtc)` comparison. Four
lines, and it carries most of the performance win on a multi-GB tree — the difference between
a two-second and a five-minute `patch create`. Kept on both the publish and the install side.

### 1.6 The `RuleAction` vocabulary

`KeepExisting | Replace | Upsert | Ignore | Delete` (`IO/Globbing/RuleAction.cs`) is genuinely
the right set for real installs where user config, savegames and per-deployment `.ini` files
must survive a patch. The 36-line header comment on that file is the best specification in the
repository. The **vocabulary** is kept; the **resolution mechanism** is not (see §2.5).

### 1.7 The `IFileReference` seam and `OpenReadRawAsync`

One file contract (`Path`, `Size`, `Sha512`, `Md5`, `IlIdentity`, `IsCompressed`,
`OpenReadRawAsync`) with `IPatchFileRepository : IFileReference` as an empty marker meaning
"already in a repository". This makes "a file in repo A" structurally acceptable as "a file to
add to repo B", which collapses cross-backend replication to one loop instead of N² adapters.
Critically, decompression is **not** on the interface — it is a 12-line extension method, so
every backend only has to know how to hand back bytes. Kept, with `IsCompressed` generalised
from a bool to a `ContentEncoding` value.

### 1.8 `Func<long, Task<Stream>>` as the resumable-stream seam

`ResilientStream`'s "give me a stream starting at byte N" maps cleanly onto HTTP
`Range: bytes=n-`, S3 `GetObject` with Range, FTP `REST n` + `RETR`, and `FileStream.Seek`.
The **signature** is kept verbatim; the implementation is not (see §2.6).

### 1.9 Three-phase write with state in the manifest

`PatchVersionState { Editing, Published, Deleted }` persisted *inside* the version manifest
means the state travels with the artifact through any copy mechanism — robocopy, `s3 sync`,
an FTP mirror. `AddFileAsync` hard-fails outside `Editing`. Kept, with the transition made an
atomic whole-manifest replace instead of an in-place field flip.

### 1.10 The service/process "ticket" pattern (out of scope)

The reference captured and restored service or process state around a patch. 4sup does not carry
this capability: it neither controls Windows services nor stops, restarts, or inspects processes.

### 1.11 Self-update via versioned directories

Install to `Bin\{version}\`, repoint a `Bin\latest` symlink, then exit into the configured
host-process failure action. The updater never writes
over the binary it is executing, and the same mechanism is a ready-made rollback primitive.
The shape is kept; the fragility is not (see §2.9).

### 1.12 Free-space precomputation bucketed by volume

`SpaceRequirementCalculator` groups requirements by
`new DirectoryInfo(ResolveSymLinksTarget(path)).Root.FullName` rather than summing globally,
behind a mockable `ISpaceRequirementIO` seam. It is also the one component in this area with
real unit tests. Carried over essentially verbatim.

### 1.13 Descriptor-in-the-payload

The patch carries its own `.autoupdate.yaml` declaring applications to stop or restart, file
rules and post-install scripts, validated at publish time by an `IFileValidator`.
The server needs no per-product knowledge. The principle — *policy travels with the artifact* —
is kept and is where install policy lives in the new design.

### 1.14 The best test in the repository

`HttpResiliencyTest.FailingMemoryStream` — a stream that throws `IOException` every Nth read
and records the position it was recreated at. A multi-backend resumable downloader lives or
dies on resume-after-failure correctness, and this tests it deterministically with no network
and no flakiness. Promoted to a first-class test utility in [12-testing.md](12-testing.md).

### 1.15 Other keepers, briefly

- `IFileProvider` / `VirtualFileProvider` — a three-method seam that let config tests exercise
  two-level precedence and write-back with zero disk I/O. Generalised into the storage port's
  in-memory backend.
- Single-token CLI path grammar `([alias]/)[product]/[version]` with `VersionPathParserHints`
  so one parser serves verbs at different arities.
- Layered config with `[JsonExtensionData]` so a file written by a newer version round-trips
  through an older binary, and synthesised `local`/`remote` aliases.
- Batched existence-check before upload (one round trip per 100 blobs, not one HEAD per blob).
- Two-phase publish: upload into a session-scoped staging prefix, then promote. Gives
  atomicity, a natural GC target, and credential scoping.
- Compress-only-if-worth-it: attempt only above 10 MiB, discard if ratio > 0.75.
- `AsyncLock` keyed on the **content hash** (not the destination path) to collapse concurrent
  writes of identical content.
- Separating "cache/download" from "install" as distinct job states so a network failure never
  leaves a half-installed product.
- Golden-file schema testing (`MigrationTests` with a checked-in `litedb_V0.db`).
- Harvesting real-world garbage version strings into an `[InlineData]` table.

---

## 2. What the reference gets wrong

These are the failure modes the new design is built to avoid. Each is cited.

### 2.1 `BinaryValue` infers the hash algorithm from byte length

`Core/BinaryValue.cs:66-84` reports **MD5 for a 32-byte value** — MD5 is 16 bytes. The type
cannot represent sha256 and md5 unambiguously and the path shape hardcodes sha512, so the
store cannot evolve.
→ **Fix:** `ContentHash(HashAlgorithmId, ReadOnlyMemory<byte>)`, algorithm tagged in the value
*and* in the blob key.

### 2.2 A single flat `blobs/` directory of 128-char UPPERCASE hex

Does not survive hundreds of thousands of entries on NTFS (worse with 8.3 name generation), is
hostile to `aws s3 sync`, and makes an FTP `LIST` unusable. Uppercase hex additionally invites
case-sensitivity bugs the moment the store moves off Windows.
→ **Fix:** `blobs/{alg}/{aa}/{bb}/{lowercase-hex}{enc}`.

### 2.3 `Products` vs `products` — a case split brain

`LocalRepositoryPathResolver` uses capital `P` (`LocalRepositoryFactory.cs:38`) while
`LocalPatchRepository.cs:57`, `LocalProductRepository.cs:100`/`:160` and
`LocalPatchVersionRepository.Writable.cs:213` all hardcode lowercase. This "works" only because
Windows is case-insensitive; on Linux, S3 or FTP it produces two disjoint trees.
→ **Fix:** one `RepositoryLayout` type owns every key template, all lowercase, and *nothing*
bypasses it. Enforced by an architecture test.

### 2.4 Garbage collection that silently did nothing, for years

`LocalPatchRepository.CleanupBlobsAsync` (`:114-115`) builds the keep-set correctly and then
sweeps `_pathResolver.ProductsPath` — while blobs live under `BlobsPath`. It matched nothing,
deleted nothing, and reported success to three call sites that all believed they were
reclaiming space.
→ **Fix:** explicit `EnumerateBlobsAsync`, a real orphan in the golden-repo fixture, and an
assertion that it is collected. Plus quarantine-then-delete with an auditable manifest.

### 2.5 `deleteFilesNotInPatch: true` deleted nothing

`PatchApplier.cs:163-193`: the orphan branch falls through for every `RuleAction` except an
explicit `Delete` glob, so the flag is a no-op in normal configuration. The deeper problem is
that "what to delete" is inferred from *glob rules evaluated at apply time* rather than from a
record of what the updater installed — which is both wrong and dangerous.
→ **Fix:** an owner-attributed ledger. `delete = state − compose(target)`. Files the updater
never wrote are structurally undeletable.

### 2.6 `ResilientStream` has three separate defects

- `:161` passes `MaxRetries` to `Task.Delay` **as milliseconds**.
- `:140` resets the attempt counter on every reopen, giving unbounded retries.
- `:94-100` never advances `_position` in the small-seek path, so a short forward seek drains
  the entire stream.

→ **Fix:** keep the `Func<long, Task<Stream>>` signature, rewrite the body, and add the
validator the reference lacks entirely (see §2.7).

### 2.7 Resume with no validator, and no content verification

`AproPatchFileRepository.OpenReadAtPositionAsync` (`:56-110`) sets a `Range` header with no
`If-Range` and `ResilientStream` reopens at `_position` with no validator, so a blob replaced
mid-download splices two different objects together. The only post-transfer check anywhere is
`tempFileInfo.Length != patchFile.Size` (`PatchApplier.cs:137-140`) — a corrupted transfer, or
a proxy returning a wrong object of the same size, installs cleanly.
→ **Fix:** capture a validator at first open, pass it on every reopen, treat 412 as
restart-from-zero, and verify SHA-512 as bytes land.

### 2.8 Declared indirection that is never implemented

`PatchVersionMetaData.DefaultCdn` and per-file `FileMetaDataDto.Cdn` are documented at length
(the doc comment on `DefaultCdn` is nine lines) and then **ignored by both readers**:
`AproPatchFileRepository.OpenReadRawAsync:41-43` and `HttpPatchRepository.cs:212` each hardcode
a single base URL. Any blob not in the default store 404s.

The same pattern appears in the storage layer: `BlobManagerFactory` presents itself as
multi-backend while `LocalFileSystemManager` (`AzureBlobManager.cs:222-228`) is an empty class
and every call site does `CreateManager<AzureBlobManager>()`.
→ **Fix:** if the plan declares N backends, the conformance suite runs against N backends
before any of them ships. See [12-testing.md](12-testing.md).

### 2.9 Site-specific network hacks baked into library code

`CapabilitiesResolver.cs:50-102` retries on port 9090 when connection is refused on 80, and
appends `/Patch` on a 404. `PatchRepositoryFactory.FixupPathInfo` (`:289-316`) calls
`Dns.GetHostEntryAsync` to decide whether a bare token is a domain — making URI parsing
network-dependent and non-deterministic offline.
→ **Fix:** backend selection from the URI scheme or an explicit alias table only. One hard
failure on a missing descriptor, no probing.

### 2.10 `CapabilityType` breaks hash-based collections

`Models/Http/Capabilities.cs:34-56` overrides `Equals(CapabilityType)` and `operator ==` but
neither `object.Equals` nor `GetHashCode`, so every `Dictionary`/`HashSet` lookup silently
falls back to reference equality. It is also a closed two-value type where a real capability
*set* is needed.
→ **Fix:** a `[Flags]` capability enum cross-checked against capability *interfaces*, plus an
enforced test that every hash-valued type has structural `Equals`/`GetHashCode`. Note this
trap recurs: `readonly record struct ContentHash(…, ReadOnlyMemory<byte>)` gets compiler
equality over the memory *segment*, not the bytes.

### 2.11 Path comparison is inconsistent

`PatchApplier.cs:166` compares paths ordinal while `FileIndex.cs:61` looks them up
`OrdinalIgnoreCase`. Two manifest paths differing only in case are one file on Windows and two
on Linux.
→ **Fix:** ordinal everywhere, plus a publish-time rejection of case-only collisions.

### 2.12 A hard SemVer gate on version identifiers

`PathParser.cs:181-184` throws `Invalid version: {version}`. A game platform wants
`2026-w32-hotfix` and `christmas-event`.
→ **Fix:** opaque label + an explicit publisher-supplied `Sequence` for ordering. Ordering
never parses the label.

### 2.13 Cancellation accepted and ignored; progress that skips

`PatchApplier.ApplyAsync` takes a `CancellationToken` and never checks it in the loop
(`:83`). Progress is emitted as `if (progress % 10 == 0)` log lines, which skip whenever a
step crosses more than one boundary.
→ **Fix:** a real `IProgress<T>` contract, cancellation checked per operation, and defined
resumable-cancel semantics.

### 2.14 Post-install scripts that cannot fail

`UpdateExecutor.RunScriptsAsync` swallows every script exception and never checks the process
exit code, so a failed hook leaves the update marked successful. Combined with the complete
absence of signing — while the system downloads and executes binaries — this is the most
serious defect in the reference.
→ **Fix:** signing first ([10-security.md](10-security.md)), then hooks with explicit failure
semantics and script identity as a content hash inside the signed manifest.

### 2.15 Locked-file handling escalates to killing processes

The ladder (move → identify holders → rename-aside) is the right set of strategies, but the
escalation includes an unconditional `Process.Kill(entireProcessTree: true)` of whoever holds
the handle — which will happily kill `explorer.exe` or an antivirus scanner.
→ **Fix:** an explicit per-file policy `Fail | RenameAside | PendingReboot | AskUser`.

### 2.16 Default-interface-method gymnastics on the repository contracts

`IPatchRepository.cs` uses DIMs to share algorithms, producing a hiding `FindProductAsync`
with no `new` keyword (CS0108) that hard-casts the base result, and a `new GetVersionAsync`
shadowing a method that does not exist (the read API is `FindVersionAsync`). Writability is
then probed with runtime type tests (`is not IWritablePatchRepository`).
→ **Fix:** abstract base classes or explicit services for shared algorithms; capability
interfaces instead of type tests.

### 2.17 Enumeration that is "best-effort"

`IPatchRepository.cs:8` carries the comment *"ListProducts (Might not work, depending on the
implementation)"*, and `HttpPatchRepository.ListProductsAsync:30` throws — **mid-enumeration**,
after the caller has already started iterating an `IAsyncEnumerable` and cannot recover
cleanly. A fat interface plus runtime `NotSupportedException` is the anti-pattern the new
storage port is explicitly shaped to avoid.

### 2.18 The god library and the test inversion

`Apro.AutoUpdater.Lib` mixes repositories, HTTP clients, hashing, indexing, globbing, config,
process control and text formatting; every other project depends on it, and the test project
must reference six projects at once. Meanwhile 230 test methods exist and
`Repositories/Repositories/` — 47 source files, the entire product — has **none**.
→ **Fix:** `Core` has zero project references and does no I/O; layering enforced by
`NetArchTest`. See [03-architecture.md](03-architecture.md).

### 2.19 Repository hygiene

`NuGet.Config` contains a plaintext `ClearTextPassword` for the private `packages.apro.at`
feed. Committed build output (`ScriptDemo.exe`/`.dll`) is used as a test fixture. Two of three
test projects contain no tests. Three different manifest shapes exist in `SampleData/` and no
test reads any of them.
→ Not defects of the design, but a reminder: the new repository gets a secrets policy, no
committed binaries except deliberate golden fixtures, and no decorative projects.

---

## 3. The single most important structural lesson

The reference has no concept of variants at any level — not in the manifests, not in the
repository interfaces, not in the server schema. Its product identity is a bare string and its
version identity is a SemVer string. Retrofitting five axes onto that model is what produces
`product-de-classic-x64` naming schemes and a combinatorial manifest explosion.

The new design therefore treats the variant model as the **primary** design axis, and
everything else — storage, format, client, server — is shaped to serve it. That is the subject
of [04-variant-model.md](04-variant-model.md).
