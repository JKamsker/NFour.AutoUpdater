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
    public sealed record Write(VirtualPath Path, ContentHash Content, long Size, long StoredSize,
                               ContentEncoding Encoding, PackageId Owner, FileInstallPolicy Policy)
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

public interface IInstallPlanner
{
    /// current == null (fresh install OR lost ledger) => NEVER emits Delete.
    InstallPlan Plan(ComposedFileSet target, IReadOnlyDictionary<VirtualPath, InstalledFile>? current);
}
```

`Plan` is a **pure function**. It takes no repository, no filesystem and no clock, which means
the entire delete-set logic — the part that can destroy player data — is testable with literals.

## 2. The planning rules

Reproduced from [04](04-variant-model.md) §5 step 12 because this is the safety-critical part:

```
foreach (path, want) in target.Files:
    if !current.TryGetValue(path, out have):
        if want.Policy == Preserve && File.Exists(path) -> Keep      (adopt into ledger)
        else                                            -> Write
    elif have.Content != want.Content:
        if want.Policy == Preserve                      -> Keep
        else                                            -> Write
    else                                                -> Keep      (re-hash only if (size, mtime) drifted)

foreach (path, had) in current:
    if target.Files.ContainsKey(path): continue
    if had.Policy == Preserve                           -> Orphan
    else                                                -> Delete
```

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
4. Acquire process/service     tickets — stop what must be stopped, record what was running
5. Fetch                       all blobs, deduped by hash, into .4sup/staging/{hash},
                               VERIFYING sha256 as bytes land
6. ── barrier ──               nothing is written into the install tree until every blob is
                               present and verified
7. Materialise                 hardlink-or-copy staged blobs into place; escalate on lock
8. Delete                      the delete set; prune emptied directories
9. COMMIT                      atomic + durable rewrite of .4sup/state.jsonl
10. Restore tickets            restart what was running, restore service start types
11. Remove .4sup/plan.json     the operation is over
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

### 3.3 Resume — with a validator

The reference resumed by setting a `Range` header with no `If-Range` and reopening at
`_position` with no validator, so a blob replaced mid-download **spliced two different objects
together**.

Here:

1. Capture the `ObjectValidator` at first open.
2. Pass it as `ifMatch` on every reopen.
3. A `412` means *the object changed* → restart from zero, do not splice.
4. `ReadResult.ActualStartOffset` reports what the backend actually gave us, so the caller can
   refuse to drain 3.9 GB forward to resume at 3.9 GB.
5. Drain-forward is an explicit policy value — `never | under N bytes | always` — not a silent
   behaviour.
6. On FTP the validator is `(MDTM, SIZE)` and is **weak**: the backend cannot detect
   replacement. The caller is told this. Whole-content SHA-256 verification is what makes it
   safe, and here it is load-bearing rather than belt-and-braces.

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

## 4. Locked files, processes and services

On Windows, patching a game whose launcher holds a handle to `bin/game.exe` simply fails:
`File.Move` throws. The new plan carries the reference's **ticket pattern** forward — it is the
one part of the reference that is unambiguously production-hardened — and fixes its escalation.

```csharp
public enum LockedFilePolicy { Fail, RenameAside, PendingReboot, AskUser }
public enum RestartBehavior  { Never, WhenStarted, Always }
```

A ticket captures pre-mutation state (`OriginalStartType`, `OriginalStatus`, `WasRunning`,
`SessionId`) and exposes `Restore*` verbs, so "leave the machine as you found it" is the
default. Two details carried over verbatim:

- **Set a Windows service's `StartType` to `Manual` before stopping it**, so the SCM cannot
  auto-restart it mid-patch; restore afterwards. Three lines; prevents a real class of
  corruption.
- **Rename-aside** to `{path}.old-{n}` when a move fails, so the running process keeps its
  handle to the old inode and the new file lands.

What is **not** carried over: the reference's unconditional
`Process.Kill(entireProcessTree: true)` of whoever holds the handle, which will happily kill
`explorer.exe` or an antivirus scanner. Escalation is now an explicit per-file policy, defaulting
to `RenameAside` for `Replace` files and `Fail` for anything else.

`Restart Manager` (`RstrtMgr.dll`) is used to *identify* holders for the error message; it is
not used to terminate them.

## 5. The local content cache (CAS)

A first-class subsystem, not an assumption. Without it every variant switch and every rollback
is a full re-download, which contradicts the design's own claim that a switch is nearly offline.

| Property | Decision |
|---|---|
| Location | `%LOCALAPPDATA%\4Story\4sup\cas` (Windows), `$XDG_CACHE_HOME/4sup/cas` (POSIX); overridable |
| Layout | identical to the repository CAS — `{alg}/{aa}/{bb}/{hash}{enc}` |
| Size cap | configurable, default 20 GB |
| Eviction | LRU by last-access, never evicting a blob referenced by the current `state.jsonl` |
| GC | on-demand and on-cap; separate from `.4sup/staging` |
| Relationship to staging | `.4sup/staging` is per-operation and per-install; the CAS is per-machine and shared across installs |

**Materialisation is hardlink-first:**

1. Same volume + `Replace`/`Executable` policy → `CreateHardLink` / `link(2)`. Zero copy, zero
   extra space.
2. Same volume + filesystem supports reflink → `FSCTL_DUPLICATE_EXTENTS_TO_FILE` / `FICLONE` /
   `clonefile` behind a P/Invoke fast path.
3. `Preserve` policy, or different volume, or hardlink unsupported → copy.

Rule 3's `Preserve` exclusion is not an optimisation detail: the game **mutates** a preserved
config in place, and a hardlink there would corrupt the shared CAS entry for every other install
on the machine.

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
- For the service host: rewrite the SCM `BinaryPath` and `Environment.Exit(1)` into the
  configured failure action, with a `WaitTimeGenerator` backoff (15 s → 10 min over 20 actions).
- Version-collision suffixes (`{ver}`, `{ver}-1`, `{ver}-2`) for republished builds.

`minimumClientVersion` on both `repo.json` and the channel pointer lets a repository refuse an
old client with an actionable message rather than letting it misinterpret the format.

## 8. IPC surface for a launcher GUI

Not a GUI, but the seam one needs. Carried over from the reference, which got this right:

- `[Route]`-attributed controllers over a named pipe, with `IAsyncEnumerable<T>` as a
  first-class response shape and `CancellationToken` injected from the request context — so
  progress streams incrementally instead of buffering.
- **One protocol in both directions**: the service pushes progress by opening a normal request
  to a route the *client* hosts. No second channel, no second serializer.
- A `UseInProcess(...)` transport swap so the entire IPC stack runs inside one process for
  design-time and debug builds. This is how GUI work gets done without a service installed, and
  it means the RPC layer is covered by ordinary tests.
