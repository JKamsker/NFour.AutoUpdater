# 07 — Client Engine

Install, update, switch, repair and rollback are **one operation**: compose a target file set,
diff it against the ledger, apply the diff. There is no separate "update" code path.

## 1. Types

```csharp
namespace FourSaas.AutoUpdater.Client;

public sealed record InstalledFile(
    VirtualPath Path, ContentHash Content, long Size, PackageId Owner,
    FileInstallPolicy Policy, long ObservedSize, long ObservedMtimeUnix);

public abstract record FileOperation(VirtualPath Path)
{
    public sealed record Write(VirtualPath Path, ContentHash Content, long Size,
                               PackageId Owner, FileInstallPolicy Policy)
        : FileOperation(Path);
    public sealed record Delete(VirtualPath Path, PackageId PreviousOwner) : FileOperation(Path);
    public sealed record Keep  (VirtualPath Path)                          : FileOperation(Path);
    /// Was ours, is no longer in the target, but policy forbids deleting it. Kept + logged.
    public sealed record Orphan(VirtualPath Path, PackageId PreviousOwner, string Reason)
        : FileOperation(Path);
}

public sealed record InstallPlan
{
    public required ImmutableArray<FileOperation> Operations { get; init; }
    public required ImmutableArray<BlobLocator> BlobsToFetch { get; init; }   // deduped by hash
    public required long BytesToDownload { get; init; }
    public required long BytesToWrite { get; init; }

    /// What the disk must actually accommodate at peak (staging + not-yet-deleted old
    /// files), bucketed by volume root — NOT the net delta. Surfacing only the net delta
    /// is how a switch that installs 4 GB and deletes 4 GB runs the volume dry mid-apply.
    public required ImmutableDictionary<string, long> PeakFreeSpaceRequiredByVolume { get; init; }
    public required long NetInstallDelta { get; init; }
}

/// What the scanner observed on disk. Explicit input, so planning is reproducible.
public sealed record ObservedEntry(
    VirtualPath Path,
    bool Exists,
    ObservedKind Kind,             // File | Directory | Reparse | Other
    long Size,
    long MtimeUnixSeconds,
    FileIdentity? Identity,        // volume+file id where the OS provides one
    ContentHash? Hash);            // present only if the scanner was asked to hash

public sealed record ObservedTreeSnapshot(
    string InstallRoot,
    ImmutableDictionary<VirtualPath, ObservedEntry> Entries);

public interface ITreeScanner
{
    /// Observes exactly the union of ledger paths and target paths — never a full walk.
    ValueTask<ObservedTreeSnapshot> ScanAsync(
        string installRoot,
        IEnumerable<VirtualPath> ledgerPaths,
        IEnumerable<VirtualPath> targetPaths,
        HashPolicy hashPolicy,
        CancellationToken ct = default);
}

public interface IInstallPlanner
{
    /// current == null (fresh install OR lost ledger) => NEVER emits Delete.
    InstallPlan Plan(
        ComposedFileSet target,
        IReadOnlyDictionary<VirtualPath, InstalledFile>? current,
        ObservedTreeSnapshot observed);
}
```

`Plan` **is** a pure function — but only because the filesystem observations are now an explicit
parameter. The first draft declared the same purity claim while the algorithm called
`File.Exists`, `Size` and `mtime` directly, so identical inputs produced different plans depending
on disk state and the claim was unimplementable (review finding **C7**).

Splitting scan from plan also makes the delete-set logic — the part that can destroy player data —
testable with literals, which was the point of the original claim.

## 2. The planning rules

Reproduced from [04](04-variant-model.md) §5 step 12 because this is the safety-critical part:

```
foreach (path, want) in target.Files:
    obs = observed[path]
    if !current.TryGetValue(path, out have):
        if want.Policy == Preserve && obs.Exists -> Adopt   (unmanaged content, see below)
        else                                     -> Write
    elif have.Content != want.Content:
        if want.Policy == Preserve               -> Keep
        else                                     -> Write
    else                                         -> Keep
         // re-hash only when (obs.Size, obs.Mtime, obs.Identity) drift from the ledger;
         // the hash is always the authority, the stat triple is only a cache key (18 §8.3)

foreach (path, had) in current:
    if target.Files.ContainsKey(path): continue
    if had.Policy == Preserve                    -> Orphan
    else                                         -> Delete
```

### 2.1 `Adopt` is not `Keep`

A `Preserve` file that already exists when we first see it has **unknown content** — the updater
did not write it and has not read it. The first draft recorded it in the ledger with the
*desired* hash, which makes the ledger assert something false, and every later `verify` would
compare against a hash that was never observed.

`Adopt` therefore writes a distinct ledger state:

```jsonl
{"k":"f","p":"config/user.ini","st":"adopted","h":null,"oh":"sha256:1a2b…","o":"…","pol":"preserve","mt":…,"os":1391}
```

- `st: "adopted"` — content is unmanaged.
- `h: null` — no desired-content claim is made.
- `oh` — the *observed* hash, populated only if the scanner was asked to hash it.

`verify` reports adopted files as `unmanaged`, not as `drifted`. `repair` leaves them alone. They
are never deletion candidates. If a later release changes the file's policy from `Preserve` to
`Replace`, the transition is an ordinary `Write` and the state clears.

Three properties that follow, and that the reference did not have:

1. **Files the updater never wrote are never deletion candidates.** Savegames, crash dumps,
   mods and user configs are structurally safe — not safe by virtue of someone remembering to
   add a glob rule.
2. **Ledger loss degrades to never-delete**, never to delete-something-we-did-not-place.
3. **Un-shadowing is automatic.** Ownership is recomputed from the manifests every time, not
   maintained as a mutable stack, so a lower layer re-emerging when a higher package stops
   shipping a path produces a `Write` for free. This is the case `dpkg` diversions get wrong.

### 2.1 Install policy replaces glob rules

The reference matched `RuleAction` globs on the **client at apply time**, which made resolution
order-dependent and untestable. Here the policy is bound to the file entry at **publish time**
by `slice.yaml`, so:

- The publisher — who knows which files are config and which are content — decides.
- The decision is recorded in the signed manifest and is auditable.
- The client applies a value, it does not evaluate a rule set.

The glob engine still exists, but only on the publishing side.

## 3. The apply protocol

### 3.1 Ordering

```
1. Verify preconditions        free space per volume; staging on the same volume as the root;
                               minimumInstalledRelease satisfied; minimumClientVersion satisfied
2. Acquire the install lock    named mutex; reports WHO holds it, not just "blocked"
3. Write .4sup/plan.json       makes the operation resumable from this point on
4. Fetch                       all blobs, deduped by hash, into .4sup/staging/{hash},
                               VERIFYING sha256 as bytes land
5. ── barrier ──               nothing is written into the install tree until every blob is
                               present and verified
6. Materialise                 hardlink-or-copy staged blobs into place; escalate on lock
7. Delete                      the delete set; prune emptied directories
8. COMMIT                      atomic + durable rewrite of .4sup/state.jsonl
9. Remove .4sup/plan.json      the operation is over
```

**Step 6 is the single most important line in this document.** "All blobs present and verified
locally" is a hard precondition of touching the install tree. A network failure can therefore
never leave a half-installed product — it leaves a fully-staged or partially-staged download
that resumes. The reference had the right instinct (separate `Caching`/`Patching` job states)
and this makes it an invariant.

### 3.2 Download and verification

- Blobs are deduped by `ContentHash`: three virtual paths mapping to one blob fetch it once.
- SHA-256 is computed **as bytes land**; a blob whose content does not match its key is
  discarded and refetched from the next mirror. The reference performed **no content
  verification at all** on the download path — its only post-copy check was
  `tempFileInfo.Length != patchFile.Size` (`PatchApplier.cs:137-140`), so a corrupted transfer
  or a proxy returning a wrong same-size object installed cleanly.
- Concurrency is `min(configured, store.RecommendedParallelism)`.
- Mirror failover is sticky and ordered, and **the first mirror serving byte-correct content
  wins** — a mirror that serves a hash mismatch is demoted for the session. This is also the
  only way to detect a poisoned CDN edge.

### 3.3 Resume

Blobs are stored **identity-encoded** ([18](18-normative-contract.md) §5), so a range offset is
an offset into the content itself. Resume is therefore a byte-append into
`.4sup/staging/{sha256}` with a streaming hash carried across the resume boundary — there is no
decode stage, no second representation, and the staging filename is unambiguous. That is the
whole reason at-rest compression was dropped.

The reference resumed by setting `Range` with no validator and reopening at `_position`, so a
blob replaced mid-download **spliced two different objects together**.

Here, and using the correct HTTP semantics (review finding **H10** — the first draft conflated
`If-Range` with `If-Match` and expected `412` from a validator mismatch, which is not what
`If-Range` does):

1. Capture the `ObjectValidator` at first open.
2. Resume with `Range: bytes=n-` **plus `If-Range: <strong validator>`**.
3. Interpret the response:

| Status | Meaning | Action |
|---|---|---|
| `206` | Range honoured, validator matched | Append from `n` |
| `200` | Validator mismatched **or** server ignored `Range` | Discard the partial, restart from 0 |
| `416` | Offset past end — object shrank | Discard, restart from 0 |
| `412` | Only from `If-Match`, which is used on **writes**, not this path | Treat as a protocol error |

4. **A weak ETag (`W/"…"`) is not usable with `If-Range`.** If the only validator is weak, resume
   is disabled for that object and it restarts from 0. Silently resuming on a weak validator is
   how mirror nodes splice.
5. `ReadResult.ActualStartOffset` reports what the backend actually delivered, so the caller can
   refuse to drain forward. Drain-forward is an explicit policy — `never | under N bytes |
   always` — never silent.
6. On FTP the validator is `(MDTM, SIZE)` and is **weak by definition**, so resume over FTP is
   restart-only. This costs nothing in the recommended topology, where FTP is a write transport
   and reads go over the paired HTTP endpoint.
7. Whatever the path, the streaming SHA-256 over the assembled content is the authority. A
   mismatch discards the staged file and refetches from the next mirror.

Retry uses the reference's `Func<long, Task<Stream>>` seam — which maps cleanly onto HTTP
`Range`, S3 Range, FTP `REST`+`RETR` and `FileStream.Seek` — with a correct implementation:
bounded total attempts (not reset per reopen), exponential backoff in **seconds not
milliseconds**, and `_position` advanced on every path.

### 3.4 The commit point

**One file, one rename, and it must be durable.**

```
write   .4sup/state.jsonl.tmp   with FileOptions.WriteThrough
flush   Flush(flushToDisk: true)
rename  → .4sup/state.jsonl     (atomic within the volume)
fsync   the containing directory (P/Invoke on POSIX)
```

Two separate files (`install.lock.json` + `state.jsonl`) would mean two renames and therefore a
window where the lock describes release B while the ledger describes release A. Hence the
single file with the lock as its head record ([05](05-repository-format.md) §10).

Rename gives **atomicity, not durability**. Without the flush and the directory fsync, power
loss on ext4/XFS or a device with a volatile write cache loses the ledger. .NET has no
directory-fsync API; this needs P/Invoke on POSIX. If we choose not to do it, that must be a
recorded accepted risk, not an oversight.

### 3.5 Crash and cancellation semantics

| Crash point | State on disk | Recovery |
|---|---|---|
| Before step 3 | untouched | nothing to do |
| During fetch | `plan.json` + partial staging | re-run: staged blobs verified by hash and reused |
| During materialise (7) | `plan.json`, tree partially new, old ledger | re-run: every op is idempotent and content-addressed; converges |
| During delete (8) | as above | re-run: `Delete` of a missing file is a no-op |
| During commit (9) | old ledger *or* new ledger, never half | re-run from the surviving ledger |
| After commit | new ledger, `plan.json` still present | re-run: plan is a no-op; `plan.json` removed |

Cancellation mid-apply leaves exactly the resumable state above. That is correct, but it must be
**specified and tested** rather than emergent. Pause is distinct from cancel: pause holds the
tickets and the lock; cancel releases them.

The reference accepted a `CancellationToken` in `PatchApplier.ApplyAsync` and never checked it
in the loop (`:83`).

### 3.6 Progress

```csharp
public sealed record ApplyProgress
{
    public required ApplyPhase Phase { get; init; }        // Planning|Fetching|Materialising|Deleting|Committing
    public required long DownloadedBytes { get; init; }
    public required long TotalDownloadBytes { get; init; }
    public required long WrittenBytes { get; init; }
    public required long TotalWriteBytes { get; init; }
    public required int  FilesDone { get; init; }
    public required int  FilesTotal { get; init; }
    public VirtualPath? Current { get; init; }
    public string? Mirror { get; init; }
}
```

Download bytes and write bytes are **separate**, because they differ by the compression ratio
and a launcher needs both. The reference emitted `if (progress % 10 == 0)` log lines that skip
percentages whenever a step crosses more than one boundary.

## 3.7 Secure traversal — lexical validation is not a boundary

`VirtualPath` validation is **syntactic**. It proves the manifest did not *ask* to escape the
install root. It does not prove the write *lands* inside it, because an existing directory
symlink, junction, mount point or reparse point below the root redirects the write after
validation succeeds — and a TOCTOU swap between validation and mutation produces the same result.
For an updater running elevated, `Path.Combine(root, validatedRelativePath)` is an arbitrary-write
primitive (review finding **C10**).

Normative rules for every mutation — write, replace, delete, and directory creation:

1. **Open parents by handle, component by component**, from a handle to the install root. Never
   resolve a full path string in one call.
2. **No-follow at every component.** `O_NOFOLLOW` / `FILE_FLAG_OPEN_REPARSE_POINT`. Encountering
   a reparse point or symlink on the path to a managed file is a hard error, not a traversal.
3. **Verify parent identity immediately before mutation** — the parent handle's file id must match
   the one observed during the scan. A mismatch aborts the operation.
4. **Mutate relative to the parent handle**: `openat`/`unlinkat`/`renameat` on POSIX,
   `NtCreateFile` with a root directory handle on Windows. Not by absolute path.
5. **Root preconditions**: the install root must not itself be a reparse point, must be on the
   same volume as `.4sup/staging`, and — when running elevated — must not be writable by
   unprivileged users. A world-writable root under an elevated updater is refused with exit
   code 4, because it lets an unprivileged user plant a junction between scan and apply.
6. **Directories the updater creates are created no-follow**; a pre-existing directory that is a
   reparse point is never entered.

The adversarial cases — junction swap, symlink race, mount point, case collision, parent
replacement mid-apply — are explicit tests ([12](12-testing.md) §7), not review items.

## 4. Locked files and processes

On Windows, patching a game whose launcher holds a handle to `bin/game.exe` simply fails:
`File.Move` throws. The new plan carries the reference's **ticket pattern** forward — it is the
one part of the reference that is unambiguously production-hardened — and fixes its escalation.

```csharp
public enum LockedFilePolicy { Fail, RenameAside, PendingReboot, AskUser }
```

The applier does not stop, restart, or otherwise control services or processes. **Rename-aside**
to `{path}.old-{n}` when a move fails allows the existing handle to remain valid while the new
file lands.

What is **not** carried over: the reference's unconditional
`Process.Kill(entireProcessTree: true)` of whoever holds the handle, which will happily kill
`explorer.exe` or an antivirus scanner. Escalation is now an explicit per-file policy, defaulting
to `RenameAside` for `Replace` files and `Fail` for anything else.

Service and process control, including holder inspection, is outside 4sup's scope.

## 5. The local content cache (CAS)

A first-class subsystem, not an assumption. Without it every variant switch and every rollback
is a full re-download, which contradicts the design's own claim that a switch is nearly offline.

| Property | Decision |
|---|---|
| Location | `%LOCALAPPDATA%\4Story\4sup\cas` (Windows), `$XDG_CACHE_HOME/4sup/cas` (POSIX); overridable |
| Layout | identical to the repository CAS — `{alg}/{aa}/{bb}/{hash}` |
| Size cap | configurable, default 20 GB |
| Eviction | LRU by last-access, never evicting a blob referenced by the current `state.jsonl` |
| GC | on-demand and on-cap; separate from `.4sup/staging` |
| Relationship to staging | `.4sup/staging` is per-operation and per-install; the CAS is per-machine and shared across installs |

**Materialisation is reflink-first, copy-default, and does not hardlink by default:**

1. Same volume + filesystem supports **copy-on-write clone** → `FSCTL_DUPLICATE_EXTENTS_TO_FILE`
   (ReFS) / `FICLONE` (btrfs, XFS) / `clonefile` (APFS) behind a P/Invoke fast path. Zero copy,
   zero extra space, **and a write to the install file diverges instead of propagating**.
2. Otherwise → **copy**.
3. Hardlink → only under an explicit `immutable-install` profile (below).

The first draft made hardlinks the default for non-`Preserve` files, on the reasoning that only
preserved config is mutated in place. Review finding **C9** is correct that this is wrong: a
hardlink is the *same inode*, so **any** write to an installed file — by the game, a repair tool,
anti-cheat, a mod manager, or an administrator — rewrites the shared CAS object and silently
corrupts it for every other installation linked to it. A content-addressed cache is only safe if
cached bytes cannot be reached through a writable path.

The `immutable-install` profile permits hardlinks only when all of these hold, and it is off by
default:

- the destination is made read-only after linking;
- the running product is known not to write into the install tree;
- the CAS re-verifies an entry's hash before reuse if its link count or file identity has changed
  since it was written;
- the profile is opt-in per install root, never inferred.

## 5.1 Local CAS integrity

Because a CAS entry can be corrupted by anything on the machine, the cache is not trusted blindly:

- Entries are stored read-only.
- An entry is re-hashed before reuse if its `(size, mtime, identity)` triple drifts.
- A hash mismatch **evicts** the entry and refetches; it never fails the install.
- `4sup verify --rehash-cas` walks the whole cache.

## 6. Verify and repair

```
4sup verify <install>                    # hash-scan against the composed set; report drift
4sup verify <install> --repair           # refetch and rewrite anything that does not match
4sup verify <install> --rebuild-state    # reconstruct a lost/corrupt ledger
```

`--rebuild-state` works because `(releaseDigest, selectionId) → fileSetId` is a **function**:
re-resolving the previous lock reconstructs `current` exactly. Failing that, it hash-scans the
tree and matches observed hashes against the composed set, attributing ownership where a hash
matches and leaving anything unmatched alone.

Whether this needs to be wired into the launcher's startup health check rather than left as a
manual command depends on whether the engine globs `ui/**` at startup —
[14](14-open-questions.md) Q1.

## 7. Self-update

`4sup` must be able to update itself, and a client running an old binary against a repository at
`schemaVersion: 2` must have an upgrade path. The reference solved this and the shape is sound:

- Install to `bin/{version}/`; never overwrite the running binary.
- Repoint a `bin/current` pointer — a directory symlink where privileges allow, a `current.txt`
  file where they do not. The reference called `Directory.CreateSymbolicLink` with **no fallback
  and no try/catch**, so on a machine without `SeCreateSymbolicLinkPrivilege` the update
  half-applied.
- Forward state across the update: config and local databases must be copied from the old
  content root, or config silently resets.
- For a host process: exit into its configured failure action with a `WaitTimeGenerator`
  backoff (15 s → 10 min over 20 actions).
- Version-collision suffixes (`{ver}`, `{ver}-1`, `{ver}-2`) for republished builds.

`minimumClientVersion` on both `repo.json` and the channel pointer lets a repository refuse an
old client with an actionable message rather than letting it misinterpret the format.

## 8. IPC surface for a launcher GUI

Not a GUI, but the seam one needs. Carried over from the reference, which got this right:

- `[Route]`-attributed controllers over a named pipe, with `IAsyncEnumerable<T>` as a
  first-class response shape and `CancellationToken` injected from the request context — so
  progress streams incrementally instead of buffering.
- **One protocol in both directions**: the host pushes progress by opening a normal request
  to a route the *client* hosts. No second channel, no second serializer.
- A `UseInProcess(...)` transport swap so the entire IPC stack runs inside one process for
  design-time and debug builds. This is how GUI work gets done without a service installed, and
  it means the RPC layer is covered by ordinary tests.
