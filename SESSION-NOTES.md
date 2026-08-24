# Session notes — 2026-08-24

Evaluating whether a rewrite of `osdu-cli` on a runtime other than Python would be easier to
package for enterprise Windows 11 and macOS distribution, and building a proof of concept
for generating its command surface from OpenAPI specs.

The PoC now covers **every core service the Python CLI covers except Wellbore DDMS** — 80
commands across 12 services — under a redesigned, resource-first command grammar.

This file records what was investigated, what was decided and why, what was built, and what
is still open. It is a record of one session, not a design document — see [README.md](README.md)
for how the PoC actually works, and [COMMAND-GRAMMAR.md](COMMAND-GRAMMAR.md) for the command
grammar and the open questions on the command surface.

---

## 1. The question

> The osdu-cli tool has been attempted to package for enterprise distribution in a Windows 11
> environment. The packaging has met with issues regarding security and other things like
> which Python runtime to bundle. Would a C# twin be easier to package and distribute?

It evolved over the session into five decisions:

1. Is a rewrite worth it? → **Probably yes**, for reasons that are mostly not about the
   language — Python's packaging problem is architectural, not a matter of tuning.
2. Can the CLI be generated from the specs? → **Yes for ~80% of the command surface.**
3. Does it need to run on macOS too? → **Yes, and that strengthens the case.**
4. C# or Java? → **Narrow lean to C#**, and the tiebreaker is a team question, not a
   technical one. See §7.
5. Should it copy the Python CLI's command grammar? → **No.** A resource-first grammar
   (`osdu <noun> <verb>`) was adopted instead, and the PoC was expanded to cover every core
   service. See §6.

The PoC in this repo is C#. §7 records what it would take in Java and what carries over —
roughly 90% of the generator, and the manifests unchanged.

---

## 2. What the investigation found

All figures below were measured from the checkouts in `~/dev/equinor`, not estimated.

### The Python packaging problem is really a native-dependency problem

`osdu-cli`'s lock file pulls `lasio`, `numpy`, `pandas` and `pyarrow` — >150 MB of native
wheels. Every one of them arrives via `wbdutil==0.1.1` (from `wellbore-ddms-data-loader`):

```
grep -rnE "^(import|from) (pandas|numpy|pyarrow|lasio)" src/osducli --include="*.py" \
  | grep -v "/wbdutil/"
→ (empty)
```

`dataload`, the other heavyweight group, imports only `click`, `requests`, `json`, `os`,
`re`, `urllib`, `dataclasses`.

**Consequence:** dropping `wbdutil` removes every native dependency except `cryptography`.
That also makes the *Python* meaningfully easier to package — an honest counterweight to the
rewrite argument, and it is recorded here so nobody later mistakes the case for stronger than
it is.

### The packaging wall is the same on both target platforms

PyInstaller's extract-to-temp-and-exec pattern fails Windows WDAC/AppLocker (the outer .exe
is signed; the `.pyd`/`.dll` files extracted to `%TEMP%` at runtime are not) *and* macOS
hardened runtime, which notarization requires. One architectural trait, two code-integrity
systems, two separate bureaucracies. A signed .NET binary has nothing to extract.

### The rewrite is much cheaper than it looks, because the SDK already exists

`osdu-csharp-client` is `Equinor.OsduCsharpClient` 1.1.4 — Kiota-generated from 19 OSDU
OpenAPI specs, `net10.0`, 1,130 generated files behind 13 hand-written facade files. It
already covers every service the CLI touches, and `Facade/Auth/` already has interactive,
device-flow, client-credentials and static-token providers.

The SDK layer is normally 60–70% of a CLI rewrite. It was already done.

### What counts as "the CLI" — corrected

The raw total is 149 commands, but that number is misleading and it shaped some early
framing in this session that has since been corrected.

**`wellbore_ddms` (83 commands) was a late addition in 2025**, and `wbdutil` (10) wraps a
separate tool. The command set the CLI actually exists for is the remaining **56**:

| Group | Cmds | | Group | Cmds |
|---|---|---|---|---|
| `entitlements` | 9 | | `schema` | 4 |
| `storage` | 7 | | `search` | 4 |
| `workflow` | 7 | | `file` | 3 |
| `crs` | 5 | | `unit` | 2 |
| `config` | 4 | | `list` | 1 |
| `dataload` | 4 | | `status` | 1 |
| `legal` | 4 | | `version` | 1 |

Of those 56, roughly **45 are thin service CRUD that generates cleanly** — storage, schema,
search, entitlements, workflow, crs, legal, file, unit. The other **11 are genuinely
hand-written**: `dataload` (4), `config` (4), `status`, `version`, `list`.

So the generated:hand-written ratio is ~80:20 either way. **The codegen case survives the
rescoping unchanged** — which is the point that matters.

### The Python CLI is two different kinds of code in one tree

| Group | LOC | Commands | LOC/cmd |
|---|---|---|---|
| `wellbore_ddms` | 3,607 | 83 | 43 |
| `dataload` | 1,870 | **4** | 468 |
| `config` | 617 | 4 | 154 |
| `wbdutil` | 494 | 10 | 49 |
| `storage` | 430 | 7 | 61 |

**116 of `wellbore_ddms`'s files are under 50 lines of near-identical boilerplate** — machine
output that happened to be produced by copy-paste. `dataload` is the opposite: 4 commands of
real orchestration.

That split is the entire design of the PoC. Read it as a shape, not as a priority ranking:
the boilerplate is concentrated in `wellbore_ddms` because of *how* it was added, not
because it is the important part of the tool.

### Codegen has to be editorial, not mechanical

435 operations across the 19 specs; 149 commands in the CLI (56 in the core set). The CLI
is a curated subset either way. Naive one-command-per-endpoint generation produces a
435-command tool nobody wants, named after `operationId`s.

---

## 3. Decisions taken

| Decision | Rationale |
|---|---|
| Generate from spec **+ a hand-written manifest** | The spec cannot express which endpoints deserve commands, what to name them, or what a human wants in the output. |
| Key manifest entries on **`method` + `path`**, not `operationId` | OpenAPI guarantees uniqueness for the former; OSDU specs do not always give clean values for the latter (Storage ships `getAllRecords` *and* `getAllRecords_1`). |
| Output projection as **data, not JMESPath** | The Python CLI uses `"results \|\| {Id:id,Version:version}"`. Data is validatable by the generator, readable without knowing JMESPath, and needs no expression evaluator — which keeps NativeAOT trivial. |
| **Commit** generated code, don't use a Roslyn source generator | Reviewable diffs, debuggable, NativeAOT-safe, and it matches the `Generated/` + `Facade/` pattern `osdu-csharp-client` already uses. |
| Enforce **total coverage**, with a `scope:` escape hatch | Every in-scope op must be in `commands`, `handwritten` or `exclude` (with a reason). `scope:` lets a service be adopted a path prefix at a time instead of triaging all 84 Wellbore DDMS ops at once. |
| **Derive** the Kiota accessor chain; refuse to guess | Derivation is mechanical for ordinary paths. Where Kiota renames non-obviously (`:action` suffixes), the generator errors and asks for an explicit `builder:` rather than emitting something subtly wrong. |
| Drop `wbdutil` **and** `dataload` from the PoC | `wbdutil` was scoped out by decision; `dataload` is 1,870 lines / 4 commands — the worst effort-to-coverage ratio in the tree, and it proves nothing about the generator. |

---

## 4. What was built

New sibling directory `~/dev/equinor/osdu-csharp-cli`, named to match `osdu-csharp-client`
and `osdu-csharp-samples`. Self-contained; **not `git init`'d — CI has therefore never
actually run.**

```
tools/generate_cli.py               1,101   the generator
cli-manifest/*.yaml                 1,331   12 manifests, 12 services
src/OsduCli/Commands/Generated/     3,289   emitted — 79 commands (never edited by hand)
src/OsduCli/Commands/StatusCommand.cs 163   the one hand-written command
src/OsduCli/Runtime/                  754   hand-written, shared by every command
src/OsduCli/Program.cs                 20
README.md, COMMAND-GRAMMAR.md         600
.github/workflows/generate.yml         52   manifest gate + staleness check + 3-platform build
```

Toolchain: .NET 10.0.400, `System.CommandLine` 2.0.11, PyYAML. The CLI project references
`osdu-csharp-client` by `ProjectReference` for the PoC; swap for the NuGet package later.

**The generator is the fixed cost, and it is paid.** It grew from 758 to 1,101 lines over the
session, almost entirely to support the expansion in §6 — after which each additional service
was a manifest-writing exercise of 40–275 lines with no generator work at all.

### Command surface

**80 commands: 79 generated across 12 services, plus the hand-written `osdu status`.**

```
osdu record      list get search delete version{list,get}      storage + search
osdu schema      list get add update                           schema
osdu legaltag    list get add update validate delete properties  legal
osdu group       list add delete member{list,add,delete,count} entitlements
osdu member      delete group{list}                            entitlements
osdu file        get add delete upload-url download-url revoke-url    file
osdu dataset     get add delete undelete upload/download/revoke-url   dataset
osdu crs         get transform area-of-use convert{,-geojson,-trajectory}  crs_catalog + crs_conversion
osdu unit        list get search by-{measurement,symbol,system} maps preferred
                 catalog{get,search,map-states,last-modified} conversion{scale,abcd}   unit v3
osdu measurement list get search maps                          unit v3
osdu unit-system list get                                      unit v3
osdu workflow    list get add delete run{list,get,trigger,update,latest}   workflow
osdu wellbore    get add delete version{list,get}              wellbore_ddms (scoped)
osdu status      [service]                                     hand-written
```

Coverage is enforced per service — every in-scope operation is in `commands`, `handwritten`
or `exclude` with a written reason, and stale exclusions fail the build.

| Service | gen | hand | excl | in scope |
|---|---|---|---|---|
| unit v3 | 20 | 1 | 10 | 31 |
| entitlements | 9 | 1 | 5 | 15 |
| workflow | 9 | 1 | 4 | 14 |
| dataset | 7 | 1 | 3 | 11 |
| legal | 7 | 1 | 5 | 13 |
| file | 6 | 1 | 2 | 9 |
| storage | 5 | 2 | 13 | 20 |
| wellbore_ddms | 5 | 0 | 0 | 5 (+79 out of scope) |
| schema | 4 | 1 | 2 | 7 |
| crs_catalog | 3 | 1 | 2 | 6 |
| crs_conversion | 3 | 0 | 0 | 3 |
| search | 1 | 1 | 4 | 6 |

Unit **v2 is deprecated upstream and excluded entirely**; only v3 is exposed. The generator
refuses to bind a command to any operation a spec marks deprecated.

Deliberately never generated, by design: `config`, `version`, `list`, `dataload`, `wbdutil`.

---

## 5. What was verified

Everything below was actually run. Nothing has touched a live OSDU instance.

- Builds clean — 0 errors, 0 warnings.
- `--help` renders the full nested tree, including two-level groups (`storage version get`).
- Both option aliases parse (`--id` and `-id`), despite help displaying only one.
- Output rendering: table projection, `--output json`, unwrap-only, missing-root fallback,
  and the void-endpoint message path.
- Error paths print one line and exit 1 — missing config, missing body file, malformed JSON.
- Regeneration is byte-identical, which the CI staleness check depends on.
- Drift detection fires on all three failure modes: unmapped new endpoint, renamed
  parameter, removed endpoint.

### Two bugs found the way the design intends

**The compiler caught a type-derivation error.** Kiota honours `format: int32` vs `int64`;
`/records/{id}/{version}` has an `int64` version so its indexer takes `long`, and the
spec→C# mapping produced `int?`. Fixed in the generator, not the output. This is the case
for generating a compiled language: a wrong derivation is a build failure, not a runtime 500.

**Testing caught unhandled `JsonException`** on a malformed `--file` payload. Added to
`Runtime/CliRunner.cs`.

### The coverage gate caught real drift, unprompted

Mid-session, `osdu-csharp-client` changed underneath the work. Branch
`chore/spec-subfolders-and-prune` restructures `openapi_specs/` from flat `Storage.yaml`
into `openapi_specs/storage/openapi.yaml`, and refreshes the specs.

The check went red on its own:

```
UNMAPPED POST /query/records/headers
```

— a genuinely new upstream endpoint (`getRecordsHeaders`, "Fetch multiple records' headers
by ID"). The generator was taught to resolve both layouts (that branch is unmerged) and the
endpoint was triaged into `exclude`.

Better evidence for the design than the three synthetic drift tests.

### CI, once there is a remote

The workflow now generates the client before building: osdu-csharp-client gitignores its
Kiota output, so a fresh CI checkout has none and every build referencing it would have
failed on the first run. It also runs `dotnet test`. None of this has executed yet — there
is still no remote.

### Verified again after the expansion

Re-run at the end of the session against all 12 services:

- Clean `--no-incremental` Release build — **0 errors, 0 warnings**.
- **Every node in the command tree renders `--help` and exits 0**, checked by walking the
  tree programmatically rather than spot-checking.
- Regeneration byte-identical; `--check` gate green.
- Shell completion resolves at every level — nouns, nested groups, verbs, and enum values.
- Enum validation fires **before** config load and authentication, so a typo costs nothing.
- `osdu status nosuch` exits 1 naming all ten probeable services, without authenticating.

### Five more bugs the expansion surfaced

Each was found by adding a service, not by inspection — the argument for expanding the PoC
beyond the two comfortable manifests.

1. **`resolve_spec` could not find most specs.** The client's flat filenames are not a
   mechanical transform of the service name (`crs_catalog`→`CRS_Catalog.yaml`,
   `unit/v3`→`Unit_v3.yaml`). An early "capitalise the first letter" fix was wrong; the
   working fix strips non-alphanumerics and lowercases both sides.
2. **The provenance header embedded the spec's on-disk path**, so generated output differed
   between checkouts and would have failed CI's staleness gate on any machine but mine. Now
   records `spec: storage (2.0.0)` from the manifest name plus `info.version`.
3. **`returns_value` false-positived on 204.** Several OSDU specs attach a JSON schema to a
   204 — Legal's `DELETE /legaltags/{name}` declares an enum of *every* HTTP status name.
   Kiota correctly generates `void`, so the generator emitted `var result = await <void>`.
4. **The `model:` escape hatch was unreachable.** The error message told you to add
   `model:`, but the code raised before ever reading it. Two inline body schemas were
   blocked by this.
5. **Body-field enums were never read from the spec.** Only *query* enums validated, so
   `group member add --role BOGUS` was accepted locally and failed at the server. Body
   schemas are now resolved through `$ref` and get the same treatment.

The generator also caught **an error in the manifest I was writing**: I claimed nine GET/POST
twins for Unit when only eight exist, and the stale-exclusion check rejected it.

**Separate finding worth attention:** that same branch *deletes* the Geospatial and
Seismic_ddms specs and splits Unit into v2/v3. If CLI coverage was planned for either
pruned service, that decision is being made now in the client repo.

---

## 6. Command grammar, and the expansion to all core services

The PoC initially mirrored the Python CLI's grammar — `osdu storage list`, i.e. **service
first**. Questioning that produced the largest design change of the session.

### The decision: resource-first

**`osdu <noun> <verb>`, where the noun is the resource, not the OSDU service that hosts
it.** Six rules (R1–R6) are recorded and marked adopted in
[COMMAND-GRAMMAR.md](COMMAND-GRAMMAR.md). The load-bearing one is **R2: a noun + verb pair
resolves to exactly one operation.**

The argument against service-first is that it makes the user learn OSDU's deployment
topology to find a command. Nothing about a record's *behaviour* explains why fetching one
is `storage` but finding one is `search`; that split is an implementation detail of how OSDU
is deployed. Resource-first also removes the Python CLI's ten near-identical `<service> info`
commands, which collapse into one `osdu status`.

Renaming Storage's group from `storage` to `record` was **a five-line manifest change** with
no generator work — which is itself the evidence that the grammar is an editorial decision
the design already supported.

### What the expansion then forced

Covering all core services (except Wellbore DDMS, by decision) broke the assumption that a
manifest owns a subtree, **in both directions**:

- **One noun, several services.** `record` is fed by storage *and* search; `crs` by
  crs_catalog *and* crs_conversion.
- **One service, several nouns.** entitlements supplies `group` and `member`; unit v3
  supplies `unit`, `measurement` and `unit-system`.

So the command tree cannot belong to any single manifest. Per-service classes now
`Attach(CommandTree)` to a shared tree (`Runtime/CommandTree.cs`) that
`GeneratedCommands.All()` owns. The generator fails the build on duplicate command paths, a
leaf colliding with a group, conflicting or missing group descriptions, and stale `groups:`
entries.

Two other capabilities were needed: **body-from-flags** (`body: { fields: … }`, so
`record search --kind X --query Y` builds its own POST body rather than demanding a JSON
file) and a **`models:` key**, because Kiota's namespace is not always the facade property —
`OsduClient.File` lives in `…FileNamespace`, since `File` would collide with `System.IO.File`.

### Discoverability was treated as a requirement

Carried over verbatim from the Python CLI: **`-h`/`--help` must be self-explanatory at every
level.** Concretely, this session:

- Added an `Arguments` section to the hand-written help renderer, which previously ignored
  arguments entirely and rendered optional ones as required.
- Made the generator emit **`One of: …` from the spec's enums**, so an option that rejects
  everything except six magic strings now says which six. `workflow run update --status`
  turned out to have six valid values where the manifest had hand-written three — the spec
  was right and the prose was wrong, which is exactly why it is now derived.
- Shell completion works for nouns, verbs and enum values, and needs no network or token.
  Registration scripts are still unwritten.

### The main cost accepted

**12 paths expose the same logical operation twice** — GET with query parameters, POST with
a body for batches. That is 24 of the 110 core operations. The GET is exposed and the POST
excluded with a reason (COMMAND-GRAMMAR.md §3.1). The better end state is one command with a
`variants:` mechanism, but that does not exist yet, and moving a twin out of `exclude:` later
is a manifest edit rather than a redesign.

**The concrete loss is batch lookups**, which bites hardest in Unit: converting a list of
symbols is now one call per symbol. Revisit if that shows up in practice.

---

## 7. Java as an alternative to C#

Raised late in the session. Everything below was measured by generating and compiling
against `openapi_specs/storage/openapi.yaml`, not assessed from memory.

### Packaging: Java clears the same bar

`jpackage` + `jlink` produces MSI/EXE and .pkg/.dmg with a trimmed JRE whose DLLs ship **on
disk, signed at build time** — not extracted to `%TEMP%` at launch. That is the PyInstaller
failure mode, and Java does not have it. GraalVM native-image is the NativeAOT analogue.
Possible edge: WDAC governs PE loading, so only the JVM's native libraries are in scope and
jars are data the JVM reads — worth confirming with the Windows team.

**Packaging is not the tiebreaker. Both languages clear the bar Python fails.**

### What Java loses

**There is no Java OSDU client in the estate.** Verified: no `*java*` repo; `admincli` is
Python/poetry; `core-libs` is `os-core-test` / `os-obm` / `osdu-enforcer-rules`. That
removes the single strongest argument for C# — that `Equinor.OsduCsharpClient` 1.1.4 already
exists.

Also lost: **no WAM broker for Java**, so the Windows Entra SSO / Conditional Access
advantage does not transfer. Gained: `msal4j-persistence-extension` gives the same
DPAPI / Keychain / KeyRing encrypted token cache, so Java solves the plaintext-cache problem
identically.

**Do not reuse `os-core-common` as the client.** It has 332 model classes and ~17 service
interfaces in Java, compile scope is only 15 deps and Spring-free — but they are
service-to-service clients: they assume a token is already in `DpsHeaders`, are built for
Spring DI factories, cover only the subset services call each other with, and are
hand-written so they drift from the spec independently. Wiring them in would mix drifting
hand-written clients with generated ones and forfeit the coverage gate. Reference for
naming, not an SDK.

### Generator bake-off (measured)

| | Kiota `--language Java` | OpenAPI Generator 7.24 `-g java --library native` |
|---|---|---|
| Files (Storage) | 60 | 102 (57 compiled) |
| Spec validation | permissive | **rejects by default** — found 2 real defects |
| Compiles out of the box | no | no |
| Fix needed | 4 pom lines | 1-line patch to generated source |
| API shape | fluent path builders (`records/withiddelete`) | flat per-tag classes (`RecordsApi.getLatestRecordVersion(...)`) |
| Free-form `Record.data` | empty `RecordData` class | **`Map<String,Object>`** — correct |
| Jars on classpath | **22** (34 with `authentication-azure`) | **6** |
| HTTP stack | OkHttp + Kotlin stdlib + Gson | **JDK `java.net.http`, no HTTP dependency** |

Kiota's bundle declares its serializers at `runtime` scope while the generated client imports
them at compile time — so Kiota's own documented dependency set does not compile Kiota's own
Java. Adding `microsoft-kiota-serialization-{json,text,form,multipart}` explicitly fixes it.

OpenAPI Generator's validator rejected the spec over two genuine defects Kiota ignored:
`records` appears twice in `tags`, and `PATCH /records` declares `data-partition-id` twice.
With `--skip-validate-spec` it generates but emits non-compiling code for
`MultiRecordHeadersRequest` — a template bug iterating `Set<AttributesEnum>` as `String`.

**Recommendation: Kiota `--language Java`**, because the manifests port unchanged (its Java
tree mirrors its C# tree exactly, so `derive_builder()` and every `builder:` override still
works) and `generate_all.py` becomes a flag change. Caveats: add the four serializer deps,
keep the free-form `data` patch, drop `microsoft-kiota-authentication-azure` in favour of an
MSAL4J provider. **Switch to OpenAPI Generator only if GraalVM native-image is a hard
requirement** — 6 Jackson jars over the JDK's HTTP client beats 22 including OkHttp and the
Kotlin stdlib when it comes to reflection config.

Avoid Kiota's `--language CLI` mode: C#-only, and it emits one command per endpoint with
`operationId` names — the 435-command tool this design exists to prevent.

### picocli

The right CLI framework, verified by building the `osdu storage` group in it. Maps close to
1:1 onto System.CommandLine (`ScopeType.INHERIT` for `Recursive`, `Callable<Integer>` for
`SetAction`), parses `-id` style options, and gives better parse errors for free
(`Did you mean: storage list?`).

**Emit annotated classes, not the programmatic `CommandSpec` API.** Adding `picocli-codegen`
as an annotation processor generated `reflect-config.json`, `resource-config.json` and
`proxy-config.json` at compile time with zero manual work. The programmatic API bypasses
that and leaves reflection config to be hand-maintained.

Two traps for a generator:

- `mixinStandardHelpOptions` does **not** inherit — every generated `@Command` needs it.
- **The mixin's `-V/--version` silently collides with an OSDU record `--version` option.**
  Declaring `-v/--version` on `storage version get` made picocli drop the *entire* mixin —
  no `--help`, no `-h`, no error at build or run time. Renaming to `--record-version`
  restored it. Not a corner case: record versions appear throughout OSDU, and the Python CLI
  already uses `-v/--version` for exactly this. System.CommandLine does not have this
  failure mode.

### Verdict

Packaging is a wash. **Time-to-shippable favours C#** — the SDK is done and the PoC builds.
**Long-term maintenance favours Java** — `core-services` is 20+ Java 17 / Spring Boot 3.5
services and a C# CLI is an island in a Java shop. A narrow lean to C#, but if the honest
read is that the team will maintain Java and quietly will not maintain C#, that beats the
architectural argument.

Cheapest way to settle it: run `generate_all.py --language Java` across all 19 specs and
count how many compile. Storage needed only the pom fix.

---

## 8. Open items

### Not done, in priority order

1. ~~**The MSAL token cache is still plaintext.**~~ **Done** (2026-08-24), in
   osdu-csharp-client on branch `fix/encrypt-msal-token-cache`, not yet pushed or merged.
   `TokenCacheStorage` registers `MsalCacheHelper` — DPAPI on Windows, Keychain on macOS,
   libsecret on Linux — for both the interactive and device-flow providers. Where no secure
   store exists it falls back to **in-memory only**, never to a plaintext file. A cache left
   by the old version is detected (MSAL v3 blobs are JSON, so they start with `{`) and
   deleted, so the fix does not ship while the plaintext token stays readable. 50/50 client
   tests pass, 4 of them new.
2. ~~**Nothing has run against a live OSDU instance.**~~ **First live run, 2026-08-24** —
   see §8.9. Only two read-only calls; the 80-command surface is still almost entirely
   unexercised. Auth, real responses and HTTP error
   mapping are all unproven — and the surface that is unproven is now 80 commands across 12
   services, not 11 across two.
3. ~~**The repo is not `git init`'d.**~~ **Done** (2026-08-24) — 44 files committed as
   `Steinar Hjellvik <steh@equinor.com>`. **CI has still never run**, since there is no
   remote: the manifest gate, staleness check and three-platform build remain hand-executed.
4. ~~**No test project.**~~ **Done** (2026-08-24) — `tests/OsduCli.Tests`, 49 tests,
   xunit.v3 on Microsoft.Testing.Platform to match osdu-csharp-client. Covers `CommandTree`
   assembly and verb ordering, `OutputWriter` projection, the hand-written help renderer,
   config resolution, and a walk of the real generated tree that asserts every node has a
   description, no path is duplicated, every leaf has an action and every node renders help
   — the checks §5 previously performed by hand at the end of a session.
   Added `OsduCli.slnx`, without which `dotnet test` had no project to find.
   **It found a real bug:** `CliConfig`'s flat environment aliases built a dictionary of
   `{envValue: envValue}` rather than `{configKey: envValue}`, so `OSDU_SERVER` and its
   siblings had never worked — including the fallback the "no configuration found" error
   message tells users to reach for. Fixed.
5. **NativeAOT does not build.** Attempted 2026-08-24 on osx-arm64; three blockers, all in
   osdu-csharp-client's facade rather than the CLI. Details in §8.7. CI stays on
   self-contained single-file, which does publish cleanly.
6. ~~**Shell completion has no registration scripts.**~~ **Done** (2026-08-24), and the
   claim it was replacing turned out to be wrong — see §8.8.

### Reusing the Python CLI's profiles, and first contact

Existing users have profiles in `~/.osducli/` — INI files, one per environment, 13 on this
machine. All five values `OsduConfig` needs are in them (`server`, `data_partition_id`,
`authority`, `client_id`, `scopes`), so the CLI now reads them directly. No migration step,
no second copy to keep in sync, both tools usable side by side. `--config` takes either a
path or a bare profile name, matching the Python CLI, and `OSDUCLI_CONFIG_DIR` relocates the
directory as it does there.

**The `*_url` keys are deliberately ignored.** A profile says
`storage_url = /api/storage/v2/` and `unit_url = /api/unit/v3/`, but this CLI derives each
base path from the service's own spec `servers` entry and appends spec paths verbatim.
Honouring the profile would give `/api/unit/v3/v3/unit` — the doubling osdu-python-client
documents in its own registry. The specs are also fresher: profiles here still name
`crs/catalog/v2` while the vendored spec is v3.

**This produced the first live run against a real OSDU instance.** With the `dev` profile,
`osdu status` reported all ten services reachable with versions and build dates, and a
deliberately malformed `record get` returned a real `400` rendered as one line by
`CliRunner`. That validates, against a live service rather than a stub: MSAL authentication,
**the spec-derived base URIs for all ten services** — the part with the most room to be
wrong — output table rendering, and HTTP error mapping.

Two read-only calls is not coverage. But the class of unknown that made "point it at a real
instance" the biggest remaining risk is now much smaller.

### Shell completion: the earlier claim was wrong

§5 and §6 recorded that completion "resolves at every level — nouns, nested groups, verbs,
and enum values". It did not. `ParseResult.GetCompletions()` in System.CommandLine 2.0.11
returns **option names and nothing else**: no subcommands at any depth, no enum values, and
nothing from a hand-written `CompletionSources`. Measured, not inferred —
`osdu complete -- record ""` returned `--config --debug --help …`.

So the candidate list is now built directly from the parsed command: subcommands, then
argument completion sources, then options as a fallback. Options appear only once the user
types `-`, or when nothing better exists at that position — otherwise `osdu <Tab>` leads
with eleven spellings of `--help` and buries the nouns.

`osdu completion bash|zsh|fish|powershell` prints a registration script; the hidden
`osdu complete` is what those scripts call. Deliberately not `dotnet-suggest`: that means a
second global tool installed and this one registered with it, which is a poor ask for an
enterprise rollout and another executable for WDAC to allow.

**A bug the scripts only revealed by being run.** The obvious bash idiom is wrong:

```bash
local IFS=$'\n'
COMPREPLY=( $(osdu complete -- "${COMP_WORDS[@]:1}") )   # broken
```

With `IFS` already set, bash collapses `"${COMP_WORDS[@]:1}"` into a single joined argument,
so the CLI sees `record ` instead of `record` plus an empty word and returns nothing.
Completion worked at the root and nowhere else. Capture first, split second. A test asserts
the ordering so it cannot regress.

Verified end to end by sourcing the emitted script in real bash and zsh and driving
`_osdu_complete` directly — nouns, verbs, partial words, enum values and the `status`
service list all resolve. **Also noticed:** the Python `osdu` is installed on this machine
and shadowed the build during that test. Both tools claim the `osdu` name, which is a
migration question nobody has answered.

### NativeAOT: what actually blocks it

Measured on osx-arm64. The prize is real — **8.3 MB and ~0ms startup, against 82 MB for the
self-contained single-file build that CI produces today** — but it does not currently work,
and every blocker is in `Equinor.OsduCsharpClient`, not in the CLI.

The chain, each one found only after clearing the one before it:

1. **`OsduConfig`'s `required` members break the source-generated config binder.** Under AOT
   the binding generator is switched on automatically, and it emits `new OsduConfig()`
   without an object initializer — five `CS9035` errors, a hard build failure.
2. **With `required` removed, `init`-only setters bind nothing.** The build succeeds and the
   binary runs, but every property comes back empty and MSAL receives a null authority. A
   silent failure, which is worse than the first. Changing them to `set` fixes it.
3. **`OsduClient.Client<T>()` uses `Activator.CreateInstance`** (warned as `IL2087`), so the
   trimmer removes each generated client's constructor:
   `MissingMethodException: No parameterless constructor defined for type StorageClient`.
   Needs `[DynamicallyAccessedMembers]` on the type parameter, or a factory delegate per
   service.

Two more are visible in the warnings but not yet reached at runtime:

- **`UntypedNodeJsonExtensions` uses reflection-based `JsonSerializer`** (`IL2026`/`IL3050`),
  which needs a `JsonSerializerContext`.
- **`Validator.ValidateObject` is not trim-safe** (`IL2026`). Under AOT it appears to
  validate nothing, so a config missing required values passes validation and fails later
  and less clearly. That is a correctness problem for trimmed builds generally, not only for
  AOT.

Fixing 1 and 2 means changing `OsduConfig`'s public shape in a published library. That is a
decision for the client's owners, not something to slip into a CLI branch — which is why this
was measured and reverted rather than fixed here.

### The binary is `osducs`, not `osdu`

Decided 2026-08-25. The Python CLI installs an `osdu` executable and shadowed this build
during completion testing; peers evaluating the new tool will mostly still have `osducli`
installed and need both on PATH at once. `osducs` keeps them separable, and the name is
baked into the completion scripts and install instructions, so it had to be settled before
packaging rather than after.

The config directories are unchanged: `~/.osdu/` is shared with the client's token cache,
and `~/.osducli/` is where the Python profiles live and is read, not written.

### Known divergences from the Python CLI

All three are deliberate and documented in the manifests or COMMAND-GRAMMAR.md.

- **Command names are resource-first**, so most commands are renamed: `storage list` →
  `record list`, `entitlements mygroups` → `group list`, `legal listtags` → `legaltag list`.
  The migration surface is tabulated in COMMAND-GRAMMAR.md §4. If the Python CLI has users,
  this is the cost they pay.
- **`record get` is id-only.** The Python version accepts either `--kind` or `--id` and calls
  a different endpoint for each, which duplicates `record list` and cannot be expressed as
  one operation.
- **Ten `<service> info` commands collapse into one `osdu status`**, which probes every
  service and reports an unreachable one as a row rather than aborting. Exits non-zero if any
  service fails.

### Decisions still needing an owner

- **File vs Dataset overlap (COMMAND-GRAMMAR.md §3.4).** Both are exposed with symmetric
  verbs so the parallel is visible, but `file download-url` and `dataset download-url` remain
  near-synonyms hitting different services. Someone who knows which path Equinor's ingestion
  actually uses should decide whether to drop or de-emphasise one. **This is the only open
  item on the command surface.**
- **Wellbore DDMS remains scoped out** — 5 of 84 operations, by decision. If it is in fact
  used, that triage is the largest single piece of remaining manifest work.

### Strategic cost not yet paid

`osdu-cli` is an upstream OSDU community project. A C# twin is a permanent fork with no
upstream: every new OSDU service, community fix and API change becomes ours to track. The
Kiota + manifest pipeline mitigates the API-drift half of that. It does not mitigate the
feature half.

---

## 9. Suggested next steps

The command-surface question is now answered; what remains is almost entirely about proving
the thing works and can be shipped safely.

1. ~~Swap the token cache to `MsalCacheHelper`~~ — **done**, see §8.1. Needs review and a
   push; the branch exists only locally.
2. ~~`git init` the repo~~ — **done**, see §8.3. Still needs a remote before CI can run.
3. **Exercise the rest of the command surface against a real instance.** First contact is
   made (§8.9) — auth, base URIs and output rendering all work — but only `status` and one
   `record get` have actually run.
   ~~Point the CLI at a real OSDU instance~~ and run the 80 commands. This is now the single
   biggest source of unknowns: auth, real response shapes, HTTP error mapping and the output
   projections in 12 manifests are all unverified against a live service.
4. **Decide the Mac distribution channel before building a notarization pipeline.** If Macs
   are Jamf-managed, MDM-installed binaries skip quarantine entirely and the fiddliest part
   of macOS distribution disappears.
5. ~~Add a test project for the runtime layer~~ — **done**, see §8.4. ~~Try a NativeAOT
   publish~~ — **attempted, and it does not work today**; see §8.7 for the blocker chain.
   CI stays on self-contained single-file until the client library is AOT-clean.
6. ~~Write shell completion registration scripts~~ — **done**, see §8.8. The PowerShell
   execution-policy question is still worth raising with the Windows team alongside WDAC;
   the emitted script says what to do about it, but nobody has tried it on a managed
   machine.

`config`, `version`, `list` and `dataload` stay hand-written by design and never enter a
manifest.
