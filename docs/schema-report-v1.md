# Report Schema v1 — Normative Definition

| Field | Value |
|---|---|
| Product | Cloud Sync Conflict Doctor |
| Card | t_957718d4 (T03 — Deterministic report schema v1) |
| Date | 2026-08-22 |
| Revision | 1.0 |
| Owner | forg3 — backend/architect |
| Status | Accepted |
| Extends | ADR-0003 (Report schema v1 and determinism rule) |
| Normative for | Doctor.Core (emission), Doctor.Cli (`--json`), Doctor.Gui (consumption), Doctor.Tests (byte-by-byte fixtures) |

This document closes the v1 report schema. Where this document and ADR-0003 diverge in detail, this document prevails; no decision here reverses ADR-0003, it only makes it operational.

---

## 1. Fundamental Conventions

### 1.1 Encoding

- The document is UTF-8, **without BOM**.
- Line breaks: only `LF` (`0x0A`).
- File ends with exactly one `\n`.

### 1.2 Paths

- Every path within the report is **relative to `generated_from.root_path`**, never absolute.
- Separator always `/`, on all platforms. Never `\`.
- No `./` prefix, no empty segment, no `..`.
- File name is reproduced **exactly as it exists on the filesystem**: no Unicode normalization (NFC/NFD) is applied to names. Comparison and sorting use original bytes.
- `generated_from.root_path` is the literal echo of the argument received by CLI, without canonicalization. Two different spellings of the same tree produce different reports — this is invocation variation, not determinism violation. The determinism test uses the same invocation string.

### 1.3 Canonical Ordering (normative)

Raw UTF-8 byte order (`memcmp` semantics; equivalent to Unicode code point order). Using collation, locale, `String.Compare` cultural, or normalization before sorting is prohibited.

Since UTF-8 preserves code point order, comparing bytes is the same as comparing code points — the rule is platform-independent and locale-independent by construction.

Rules per structure:

| Structure | Sort Key |
|---|---|
| `groups` | `(`normalized_base_name` in bytes, then `size_bytes` ascending`)` |
| `groups[].members` | `path` in bytes |
| `identical_duplicates` | smallest `path` of the set, in bytes; tie impossible (see §1.4) |
| `identical_duplicates[].files` | `path` in bytes |
| `real_conflicts` | smallest `path` of the set, in bytes |
| `real_conflicts[].files` | `path` in bytes |
| `placeholders` | `path` in bytes |
| `placeholders[].kinds` | position in declared canonical order: `reparse_point` → `recall_on_data_access` → `recall_on_open` → `offline` |

Within any list, order is always by path when the entry has a path. No list is ordered by discovery, thread, size alone, or hash.

### 1.4 Totality of Orders

Paths are unique within a scan (a path identifies exactly one entry), so path ordering is total: **no additional tie-break is needed or defined**. The `mtime → size → path` rule from the specification (§17) governs **resolution decisions** executed on ordered sets, not report ordering. Do not confuse the two roles.

### 1.5 Time Fields

- The only wall time fields in the document are `generated_from.scan_started_utc` and `generated_from.scan_finished_utc`.
- **No list contains a time field.** In particular, file `mtime` does **not appear** in the v1 report: mtime varies with tree operations and its presence in lists would break the byte-by-byte test for stable tree state.
- Format of both fields: RFC 3339, UTC, fixed-width `YYYY-MM-DDTHH:MM:SS.mmmZ`, with uppercase `Z` and exactly 3 millisecond digits.

### 1.6 Mask for the Determinism Test (SPEC §20)

The two §1.5 fields are the only non-deterministic fields in the document. The canonical determinism test proceeds as follows:

1. Generate reports from the N scans on the same tree;
2. In each report, replace the value of `scan_started_utc` and `scan_finished_utc` with the literal string `MASKED-FOR-DETERMINISM-TEST`;
3. Require **byte-by-byte** equality of the masked documents.

Without the mask, no pair of real scans can be byte-identical; the mask is part of the test's done definition, not a concession. All other structure — including all telemetry counters, which are counts not times — must match without the mask.

---

## 2. Exact Serialization Format

The format is fixed here and **part of the schema**: changing any item in this section requires bumping `report_schema_version`.

| Aspect | Exact Value |
|---|---|
| Encoding | UTF-8 without BOM |
| Line ending | `LF` (`\n`), including the last |
| Indentation | 2 spaces per level; no tabs |
| Separator | `": "` after key (colon + one space); `,` without trailing space |
| Empty keys | Empty object prints `{}`; empty array prints `[]`, both inline |
| Key order | **Declared** — each object's sequence is from this document's tables, and reordering is prohibited |
| String escaping | Minimum RFC 8259: `\"`, `\\`, `\b`, `\f`, `\n`, `\r`, `\t` and `\u00XX` for remaining control characters `< 0x20`; forward slash `/` is **not** escaped; non-ASCII characters emitted **literally** in UTF-8 (no `\uXXXX`) |
| Numbers | Integers only; simple decimal notation; no `+` sign, leading zeros, exponents, or decimal places |
| Strict | Strict JSON RFC 8259: no comments, no trailing commas, no `NaN` |

Reference implementation in C#: `System.Text.Json.JsonSerializer` with `WriteIndented = true`, `JavaScriptEncoder.UnsafeRelaxedJsonEscaping` (produces exactly the above escaping) and POCOs whose property declaration order replicates the tables below — declaration order is emission order. Manually append final `\n`.

---

## 3. Root Structure

All fields of all objects are **mandatory and always present**. v1 has no optional fields; lists may be empty, objects never. Consumers can index directly without defensive navigation.

Root keys, in declared order:

| # | Key | Type | Description |
|---|---|---|---|
| 1 | `report_schema_version` | integer ≥ 1 | Constant `1` in this format. Bump policy: §7. |
| 2 | `algorithm` | string | Constant `"BLAKE3"`. Full hash algorithm name. |
| 3 | `hash_version` | integer ≥ 1 | Hash definition version (algorithm + recipe). Constant `1`. |
| 4 | `normalization_rules_version` | integer ≥ 1 | Version of the fixed, ordered list of conflict name normalization patterns applied in this scan. Constant `1`. |
| 5 | `generated_from` | object | Execution identification. See §4. |
| 6 | `telemetry` | object | Pipeline counters. See §5. |
| 7 | `groups` | array | Level 1 candidate groups. See §6. |
| 8 | `identical_duplicates` | array | Identical duplicate classes (final verdict). See §6.1. |
| 9 | `real_conflicts` | array | Sets with real content divergence (final verdict). See §6.2. |
| 10 | `placeholders` | array | Entries detected as placeholder/online-only and never opened. See §6.3. |

`normalization_rules_version` was added relative to the ADR-0003 example because grouping (and thus `groups` content) depends on this version; it is a third independent versioning axis (§7).

## 4. `generated_from`

Declared order: `root_path`, `scan_started_utc`, `scan_finished_utc`.

| Key | Type | Description |
|---|---|---|
| `root_path` | string | Literal echo of the CLI argument (§1.2). |
| `scan_started_utc` | timestamp | Scan start, §1.5 format. |
| `scan_finished_utc` | timestamp | Scan end, §1.5 format. Always ≥ `scan_started_utc`. |

## 5. `telemetry`

Integer counters ≥ 0, 64-bit width. Declared order per table.

| Key | Precise Definition |
|---|---|
| `files_enumerated` | Total file entries seen at Level 0 (directories never count). Includes placeholders. |
| `files_placeholder` | Entries classified as placeholder (offline/recall attributes or reparse point) and excluded from all content access. Subset of `files_enumerated`. |
| `files_skipped` | Non-placeholder entries that had **zero** bytes read: members of singleton groups (outside `groups`) and nothing else. |
| `files_partial_hashed` | Non-placeholder entries that received partial hash at Level 2 (first 64 KiB + last 64 KiB window). |
| `files_full_hashed` | Entries that survived Level 2 and received full BLAKE3 at Level 3. Subset of `files_partial_hashed`. |
| `bytes_read` | `bytes_read_partial + bytes_read_full`. |
| `bytes_read_partial` | Bytes read during the partial hash pass. Files ≤ 128 KiB have the entire file covered by both windows; the read counts entirely here. |
| `bytes_read_full` | Bytes read during the full hash pass. |
| `placeholder_bytes_read` | Bytes read from placeholders. **Absolute invariant: always `0`.** Value ≠ 0 is a security failure, not data. |

Verifiable invariants (mandatory in tests):

```text
files_enumerated == files_placeholder + files_skipped + files_partial_hashed
files_full_hashed <= files_partial_hashed
bytes_read       == bytes_read_partial + bytes_read_full
placeholder_bytes_read == 0
```

## 6. Result Lists

### 6.0 `groups` — Candidate Groups (Level 1)

Complete inventory of groups formed by `normalized_base_name + size` with **2 or more members**. A group appears here regardless of the subsequent verdict: the consumer identifies the verdict by presence/absence in the final lists — a group present in `groups` and absent from both final lists was eliminated at Level 2 (content probably different). This is the pipeline's audit trail.

Declared order of each element:

| Key | Type | Description |
|---|---|---|
| `normalized_base_name` | string | Base name after conflict normalization, extension preserved. Produced by version `normalization_rules_version`. |
| `size_bytes` | integer ≥ 0 | Size shared by all members (grouping key). |
| `members` | array of object | Group members, sorted by path (§1.3). |

`members` is the only entry form in the document without hash, deliberately: at grouping time only path exists. `members` element, declared order:

| Key | Type |
|---|---|
| `path` | string |

### 6.1 `identical_duplicates`

One element per equivalence class by full content (equal BLAKE3), with 2 or more files. Only forms from groups whose full hashes are **all equal**.

Declared order of each element:

| Key | Type | Description |
|---|---|---|
| `hash` | string | Full BLAKE3, 64 **lowercase** hex characters. Equal for all members by definition. |
| `size_bytes` | integer ≥ 0 | Common size. |
| `files` | array of string | Relative paths, sorted by bytes (§1.3). |

### 6.2 `real_conflicts`

One element per candidate group whose full hashes show **at least two distinct values** after surviving Level 2. The element covers **all** group members, including internally identical subsets: per-file hashes allow the consumer to reconstruct sub-groupings. A group never generates simultaneous entries in `identical_duplicates` and `real_conflicts` — group classification is mutually exclusive.

Declared order of each element:

| Key | Type | Description |
|---|---|---|
| `normalized_base_name` | string | As in §6.0. |
| `size_bytes` | integer > 0 | Common group size. |
| `files` | array of object | All group members, sorted by path. |

`files` element, declared order:

| Key | Type | Description |
|---|---|---|
| `path` | string | Relative path. |
| `hash` | string | Individual full BLAKE3, 64 lowercase hex. |

### 6.3 `placeholders`

Entries detected as placeholder/online-only before any content access. Never have hash — hash absence is proof of safe behavior.

Declared order of each element:

| Key | Type | Description |
|---|---|---|
| `path` | string | Relative path, global sort by bytes. |
| `kinds` | array of string | Detected labels, no duplicates, in canonical order: `reparse_point`, `recall_on_data_access`, `recall_on_open`, `offline`. Allowed values: only these four. |
| `size_bytes` | integer ≥ 0 | Size obtained from enumeration metadata (Level 0), without opening the file. |

---

## 7. Versioning and Bump Policy

Three independent axes. One cause produces a bump of **one** axis, never two.

### 7.1 `report_schema_version`

Mandatory bump (+1) when changing anything that alters the **possible bytes** of the document:

- adding, removing, renaming, or nesting a field;
- changing type, optionality, enumeration, or value domain;
- changing sort rule, including the canonical order of `kinds`;
- changing any item in §2 (indentation, escaping, key order, line ending);
- changing the relative path rule (§1.2) or the determinism mask (§1.6).

Deliberately conservative policy: **there is also a bump for additive changes** (new field). Consumers are strict and consume exactly one version; backward compatibility via tolerance for unknown fields is not a v1 goal. Changing a constant value (e.g. `report_schema_version` example in docs) is not a bump.

Consumer receiving an unknown version must refuse the document with an explicit error, never try to approximate interpretation.

### 7.2 `hash_version`

Mandatory bump when changing the **exposed hash definition**:

- algorithm swap (`algorithm` changes along — the two fields are coherent by definition);
- change in published hash length/truncation;
- change in partial hash recipe (windows, composition) — even if full hash does not change, because it invalidates historical comparisons;
- introduction of keyed hash.

Normative side effect: `hash_version` bump **invalidates the incremental cache** — cache rows whose `hash_version` differs cannot be reused (SPEC §12: cache carries `algorithm` and `hash_version`). Does not cause schema bump: fields continue to exist with same name and type.

### 7.3 `normalization_rules_version`

Mandatory bump when changing the fixed, ordered list of name normalization patterns: including, removing, reordering patterns, or altering pattern semantics. Effect: groupings may change (different `groups` for the same tree). Does **not** cause schema bump (structure intact) **nor** hash bump (hash definition intact). Reports generated under different normalization versions are not comparable in `groups`, but are in `identical_duplicates`/`real_conflicts` only when groupings match — consumer must treat reports from different normalization versions as separate series.

### 7.4 Summary Matrix

| Cause | report_schema_version | hash_version | normalization_rules_version | Cache |
|---|---|---|---|---|
| New/removed field/type/ordering/serialization | bump | — | — | — |
| Algorithm or hash recipe changes | — | bump | — | invalidated |
| Normalization pattern changes | — | — | bump | valid (hashes remain correct) |

---

## 8. Derived Formulas for Consumers

Normative for GUI and tests — implementations must arrive at the same values:

```text
total_excess_duplicate_files = Σ (len(files) - 1) over identical_duplicates
recoverable_identical_space   = Σ ((len(files) - 1) * size_bytes) over identical_duplicates
real_conflicts                = len(real_conflicts)
files_in_conflict             = Σ len(files) over real_conflicts
ignored_placeholders          = len(placeholders)
```

These formulas answer the GUI's first screen (SPEC §15). This is why v1 carries no `summary` block: every derivable sum is prohibited in the document, eliminating inconsistency risk between summary and lists.

---

## 9. Out of Scope for v1 (Explicit Non-Doing Decisions)

- **Suggestion of which file to keep** (`recommended_keep`): resolution decision belongs to the resolution engine with strategy chosen by user (SPEC §17); report is evidence, not policy.
- **`summary` block**: derivable per §8; redundancy is inconsistency risk.
- **`mtime` in entries**: prohibited in lists (§1.5).
- **Partial hash in report**: recipe still to be closed by hashing card; exposing it would freeze the recipe prematurely. Byte telemetry already audits Level 2.
- **Correlation between same-base-name groups of different sizes**: v1 grouping is `base + size` (SPEC §7); variants that diverged in size fall into separate groups and are not correlated. Known limitation, recorded; changing here would be schema bump in future version with dedicated card.

---

## 10. Official Fixture `fixtures/report-v1-example.json`

### 10.1 Reference Synthetic Tree

Logical root: `report-v1-tree/` inside `fixtures/` (simulated invocation: `conflictdoctor scan fixtures/report-v1-tree --json`). Contents defined by deterministic formula and regenerable by versioned script `fixtures/generate-report-v1-tree.py` — which resides **outside** the tree, so a scan captures exactly the fixture's files. The two placeholder directories exist empty in the versioned tree (`dead file`, `large files`). Permanent content verification: `sha256sum` over the 8 files listed below (§10.3).

| Relative Path | Content | Size | Role in Fixture |
|---|---|---|---|
| `docs/photo-meeting.jpg` | C1 | 96 B | Identical triple |
| `docs/backup/photo-meeting.jpg` | C1 | 96 B | Identical triple |
| `photos/photo-meeting.jpg` | C1 | 96 B | Identical triple |
| `projects/budget.xlsx` | X1 | 262144 B | Real conflict |
| `projects/budget-DESKTOP-ABC123 (conflicted copy).xlsx` | X2 | 262144 B | Real conflict |
| `notes/meeting.txt` | N1 | 44 B | Group eliminated at Level 2 |
| `notes/dead file/meeting.txt` | N2 | 44 B | Group eliminated at Level 2 |
| `readme.txt` | L | 32 B | Unique file (`files_skipped`) |
| `large files/video-lesson.mp4` | — (not versioned) | illustrative | Simulated placeholder |
| `dead file/old report.docx` | — (not versioned) | illustrative | Simulated placeholder |

Content definitions (reproducible by script):

```text
C1[i]           = (i * 7 + 3) mod 256,                i in [0, 96)
X1[i] = X2[i]   = i mod 251,                          i in [0, 262144)
X1[i]           = 0xAA for i in [131072, 131088)
X2[i]           = 0xBB for i in [131072, 131088)
N1              = "meeting planning notes - version A\n"   (44 bytes)
N2              = "meeting planning notes - version B\n"   (44 bytes)
L               = "Unique file - no duplicates.\n"         (32 bytes)
```

X1 and X2 differ only in the 16-byte range `[131072, 131088)`, which is outside the partial hash windows (first 64 KiB = `[0, 65536)`; last 64 KiB = `[196608, 262144)`): equal partial hash, different full hash — this is what characterizes the real conflict in the pipeline.

The two placeholders simulate metadata that only exists on Windows (`FILE_ATTRIBUTE_*`, reparse points) and therefore have no versioned file: their `size_bytes` are illustrative and not verifiable against a Linux tree. All other fixture fields are verifiable against versioned files.

### 10.2 Expected Values

- `groups`: 3 groups — `photo-meeting.jpg` (3 members), `budget.xlsx` (2 members), `meeting.txt` (2 members), in this order by `(base, size)` in bytes: `photo-meeting.jpg` < `budget.xlsx` < `meeting.txt`.
- `identical_duplicates`: 1 entry (triple of `photo-meeting.jpg`, smallest path `docs/backup/photo-meeting.jpg`).
- `real_conflicts`: 1 entry (`budget` pair).
- Group `meeting.txt` present in `groups` and absent from both final lists: eliminated at Level 2.
- `placeholders`: 2 entries, `dead file/old report.docx` before `large files/video-lesson.mp4` (byte order: `dead` < `large`, because `0x20` precedes `l`).
- Telemetry: `files_enumerated=10`, `files_placeholder=2`, `files_skipped=1`, `files_partial_hashed=7`, `files_full_hashed=5`, `bytes_read_partial=262632` (288 + 2×131072 + 88), `bytes_read_full=524864` (288 + 2×262144), `bytes_read=787496`, `placeholder_bytes_read=0`.
- Fixture BLAKE3 hashes were calculated with the official library (`Blake3`, NuGet) over exactly the §10.1 contents; they are not illustrative.

### 10.3 Reproduction and Verification

Verification artifacts versioned in this repository:

- `fixtures/generate-report-v1-tree.py` — regenerates the exact synthetic tree from §10.1;
- `tools/report-v1-fixturegen/` — C#/.NET 8 project (official `Blake3` package, NuGet) that emits the fixture JSON in the exact format from §2;
- `scripts/validate_report_v1.py` — structural validator for v1 report (declared key order, canonical UTF-8 byte sorting, telemetry invariants, hash format, `kinds` canonical order); exit code 0 = compliant, 1 = violations listed.

```text
python3 fixtures/generate-report-v1-tree.py
dotnet run --project tools/report-v1-fixturegen -- <repo-root>
python3 scripts/validate_report_v1.py fixtures/report-v1-example.json
cd fixtures/report-v1-tree
sha256sum "docs/backup/photo-meeting.jpg" "docs/photo-meeting.jpg" "photos/photo-meeting.jpg" \
          "readme.txt" "notes/dead file/meeting.txt" "notes/meeting.txt" \
          "projects/budget-DESKTOP-ABC123 (conflicted copy).xlsx" "projects/budget.xlsx"
```

Reference sha256 (recorded at document issuance):

```text
c9f1a5f79d7bea01a54f4edb41673722f627ee2e82dda324946b63cf4b9b16af  docs/backup/photo-meeting.jpg
c9f1a5f79d7bea01a54f4edb41673722f627ee2e82dda324946b63cf4b9b16af  docs/photo-meeting.jpg
c9f1a5f79d7bea01a54f4edb41673722f627ee2e82dda324946b63cf4b9b16af  photos/photo-meeting.jpg
8fc2d4ad5de8aad5d7c58bba230f634bbf35a6cda881b19b6d76a35b25f8f414  readme.txt
f1aa8dbb2cc2b3fff8c8842ac17c2d2b5c1ae1feac32eb957bf3886f5358355b  notes/dead file/meeting.txt
1197d0e5fa85136614d806540720970c786340c6b4ad6fab3afe360259309ba4  notes/meeting.txt
6384a8ea4e63a28461f4a83328e9ab1c1b13bf3c59b7fc4f7a1267c83a8b4c0d  projects/budget-DESKTOP-ABC123 (conflicted copy).xlsx
1d73b17db393481a4bc4d5294de368995b2c5e4f2512b542433bbd90789cc56e  projects/budget.xlsx
```

Any future change to these contents invalidates the fixture and requires full regeneration (contents, hashes, and telemetry together), maintaining the self-consistency required by this document.
