# 20 — Build-Tree Layout

Answers **Q2** ([14-open-questions.md](14-open-questions.md)). There is no partitioning concept
in the build today, so this defines the one to build toward rather than describing an existing
tree.

Enforced by `BuildTreeLayout` in `NFour.AutoUpdater.Publishing`.

---

## 1. The problem this solves

Slicing has to answer one question for each of ~200,000 files: **which package does this
belong to?**

There are only two ways to answer it.

| Answer by… | Slice rules needed | Outcome |
|---|---|---|
| **Directory** | ~12 | Adding a language is a build change; the slicer never moves |
| Filename patterns, or nothing | one per file | 200k hand-maintained entries |

The second is not a slower version of the first, it is a configuration nobody maintains. It
drifts, and the symptom is a package shipping the wrong files — noticed by players, not by the
build.

So the convention has exactly one rule: **a file's package is determined by its directory, and
by nothing else.** No content inspection, no filename conventions, no exceptions.

---

## 2. The layout

```
<build-output>/
  common/                      axis-independent content — the bulk of the tree
  axis/<axis>/<value>/         content selected by one value of one axis
```

Nothing else may exist at the root. The install path is whatever follows the classifying
directory:

| Build path | Owner | Installs as |
|---|---|---|
| `common/data/world.bin` | — | `data/world.bin` |
| `axis/lang/de/data/strings.bin` | `lang` | `data/strings.bin` |
| `axis/ui/classic/ui/main.bin` | `ui` | `ui/main.bin` |
| `axis/arch/x64/bin/game.exe` | `arch` | `bin/game.exe` |

That single transformation is one `stripPrefix` per rule, so a rule covers a subtree of any
size. Axis names come from the release's own axis declarations (`lang`, `ui`, `hd`, `arch`,
`brand` — see Q4).

### Why the axis is a directory level rather than part of the filename

`data/strings_de.bin` would work for a slicer that matches patterns, but it forces every
future rule to parse filenames, and it cannot express a file whose name legitimately contains
an axis value. A directory level is unambiguous, greppable, and free to check.

---

## 3. Invariants

Checked by `BuildTreeLayout.Validate`. The first three are errors; the fourth is a warning
because it is occasionally intentional.

### LAY001 — every file is classified

A path under neither `common/` nor `axis/<axis>/<value>/` is an error.

This is the check that keeps the convention true over time. Without it, a file added outside
the structure is merely unclassified: it slices into no package, ships in nothing, and the
first symptom is a variant missing content.

### LAY002 — every axis directory is declared

An `axis/<name>/` the release does not declare is an error. A typo (`langauge/`) would
otherwise build a package that nothing ever selects, so the content silently disappears.

### LAY003 — axes are path-disjoint

Two different axes may not claim the same install path. This is the Q4 constraint —
*"every axis marked switchable needs its packages kept path-disjoint from the packages of other
axes"* — enforced rather than trusted.

If `lang` and `ui` both own `data/shared.bin`, switching language rewrites a file `ui` owns, so
a language change drags UI content down with it and the cheap-switch property is lost.

**Values within one axis may overlap, and normally do.** `axis/lang/de/data/strings.bin` and
`axis/lang/en/data/strings.bin` are alternatives — never installed together — so they are
expected to share an install path. Only distinct owners conflict.

### LAY004 — axis values agree on what they provide (warning)

If `lang/de` provides `data/credits.bin` and `lang/en` does not, that is usually an export that
did not run, and it ships a variant silently short a file. A warning rather than an error
because a genuinely language-specific asset is legitimate.

### Build invocation

Run the checks directly from CI or the build with:

```sh
4sup slice lint <build-output> --axis lang --axis ui
```

Repeat `--axis` for every axis declared by the release. Omitting the build-output path checks
the current directory. Errors return exit code 1; LAY004 remains a warning unless the build
also passes `--warnings-as-errors`.

---

## 4. Generated slice rules

`BuildTreeLayout.GenerateRules` derives the rules from a conforming tree:

```yaml
source: out
packages:
  - id: game.base
    stripPrefix: common
    include: ["common/**"]
  - id: lang.de
    stripPrefix: axis/lang/de
    include: ["axis/lang/de/**"]
  - id: lang.en
    stripPrefix: axis/lang/en
    include: ["axis/lang/en/**"]
  - id: ui.classic
    stripPrefix: axis/ui/classic
    include: ["axis/ui/classic/**"]
```

One rule per package regardless of file count. Adding a language means adding
`axis/lang/fr/` to the build output; the slicer needs no change.

**Check the generated file in and review it.** Generating silently at publish time would mean a
package appearing or vanishing with nothing to notice — the diff is the point.

---

## 5. Adopting this

The build produces no such structure today, so this is a migration, and it is worth doing in
the order below because each step is independently verifiable.

1. **Enumerate the current output** and decide an owner for every top-level directory. Most of
   the tree is `common/`.
2. **Move the axis-varying content** into `axis/<axis>/<value>/`. Only localized data, UI
   assets, per-arch binaries and branding should move.
3. **Turn LAY001 on** with `UnmatchedIsError`. It will fail loudly at first; each failure is a
   file whose owner was never decided, which is exactly the information wanted.
4. **Generate the rules** and commit them.
5. **Add the checks to the build**, so a file added outside the convention fails there rather
   than at publish.

### If localized data turns out to be interleaved with shared data

That is the case the original question was really asking about, and the recommendation stands:
**fix it in the build, not in the slicer.** A build that cannot say which files are German is a
build that cannot ship German as a separate download, and moving that complexity into packaging
only hides it somewhere with less tooling.

---

## 6. Ownership

`slice.yaml` is generated, so what needs an owner is the **build-output layout** — whoever adds
a file decides its directory, and the LAY checks enforce that the decision was made. That is the
whole point of answering by directory: correctness is a build-time property, not a packaging
chore.
