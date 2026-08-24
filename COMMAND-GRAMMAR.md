# Command grammar

A concrete command tree for the core OSDU services, mapping every non-deprecated operation
to a command name or to a reason for not having one.

**Status: the resource-first grammar (R1–R6) is adopted.** `cli-manifest/storage.yaml` and
the hand-written `osdu status` implement it; the group is `record`, not `storage`. The
per-service trees in §2 are still proposals — names are cheap now and expensive after
`entitlements` and `workflow` set precedent — and §3 lists what is still undecided.

Scope: the 110 core operations, excluding Wellbore DDMS (84, a 2025 bolt-on), health/info
probes, and Unit v2 (deprecated upstream, now auto-excluded by the generator).

---

## 1. The rules

**R1 — `osdu <noun> <verb>`.** Nouns are the things OSDU manages; verbs are what you do to
them. `list`, `get`, `add`, `update`, `delete` mean the same thing everywhere.

**R2 — noun + verb resolves to exactly one operation.** This is the invariant the generator
already enforces (one command, one `op:`). It is weaker than "one noun, one service" — CRS
legitimately spans two services behind one noun — but it is the property that keeps the tree
unambiguous.

**R3 — nouns are domain things, not services.** `record`, not `storage`. Six of the ten core
services already satisfy this (`workflow`, `schema`, `legal`→`legaltag`, `unit`, `file`,
`dataset`), so R3 only changes `storage`, `entitlements` and `search`. Note this is a naming
decision, not a structural one: the manifest still keys off `service:` and `spec:`, and the
generated class is still `StorageCommands`. Renaming `storage` to `record` was a five-line
manifest change with byte-identical machinery underneath.

**R4 — generic and typed nouns coexist.** `record` is Storage's any-kind view and the
universal fallback that works for any id. `welllog`, `wellbore` are DDMS's typed views with
bulk data. Fetching one id both ways is legitimate and useful when debugging ingestion.

**R5 — service identity moves to `osdu status`.** The ten identical `<service> info`
commands collapse into `osdu status [service]`. Implemented in
`src/OsduCli/Commands/StatusCommand.cs`: no argument probes every service, one argument
narrows to one, and an unreachable service is reported as a row rather than aborting the
run — that is the answer the user asked for. A service's `/info` is accounted for in its
manifest's `handwritten:` block, pointing here.

**R6 — destructive operations are exposed with a guard, not withheld.** `--yes` to skip the
interactive confirm. Withholding capability to prevent accidents is a UX failure, not a
safety feature.

---

## 2. The tree

> **Status: built.** Every tree in this section is implemented — 79 generated commands
> across 12 services plus the hand-written `osdu status`. Where the delivered shape differs
> from the sketch below, the delivered shape is authoritative; run `osdu <noun> --help` to
> see it. Two changes were made during implementation:
>
> - **`workflow run`, not `run`.** A bare `run` noun would have been meaningless at the top
>   level — a run only exists relative to a workflow. It became a nested group.
> - **`unit conversion` and `unit catalog`, not `transformation`.** Conversions and the
>   catalog are unit-scoped, so they nest under `unit` rather than competing with it for a
>   top-level name. `crs transform` remained top-level under `crs`, because a coordinate
>   transformation *is* a first-class catalogued OSDU entity, unlike a unit conversion which
>   is computed on demand.

### record — Storage (18 ops)

```
osdu record list      --kind K [--limit] [--cursor]   GET  /query/records
osdu record get       --id ID [--attributes]          GET  /records/{id}
osdu record add       --file F                        PUT  /records            (handwritten)
osdu record patch     --id ID --file F                PATCH /records/{id}
osdu record delete    --id ID                         POST /records/{id}:delete
osdu record purge     --id ID --yes                   DELETE /records/{id}
osdu record version list  --id ID                     GET  /records/versions/{id}
osdu record version get   --id ID --record-version V  GET  /records/{id}/{version}
osdu record version purge --id ID --yes               DELETE /records/{id}/versions
```

| Not exposed | Reason |
|---|---|
| `GET /records` | "Fetch All records" — superseded by kind-scoped `record list` |
| `POST /query/records` | Body-based multi-id fetch; batch variant of `record get` (see §3.1) |
| `POST /query/records/headers` | Same family, headers only |
| `POST /query/records:batch` | Frame-of-reference batch fetch; specialised |
| `PATCH /records` | Bulk metadata patch; `record patch` covers the single case |
| `PUT /records/copy` | Namespace copy — an admin operation |
| `POST /records/delete` | Bulk soft-delete; too easy to fire by accident |
| `POST /replay` + `GET /replay/status/{id}` | Operator tooling; triggers a full reindex |

**Change from today's PoC:** the two hard-purge endpoints move from excluded to exposed
behind `--yes` (R6), and `delete`/`purge` now name the soft/hard distinction that
`POST /records/{id}:delete` vs `DELETE /records/{id}` hides.

**Built today:** `list`, `get`, `delete`, `version list`, `version get`. `add` is accounted
for as hand-written but not yet implemented; `patch`, `purge` and `version purge` are still
excluded in the manifest pending the `--yes` guard (R6), which the generator cannot yet
express.

### group, member — Entitlements (12 ops)

```
osdu group list                                    GET    /groups          (caller's groups)
osdu group list --all                              GET    /groups/all
osdu group create --name N --description D         POST   /groups
osdu group update --group E                        PATCH  /groups/{group_email}
osdu group delete --group E --yes                  DELETE /groups/{group_email}
osdu group member list  --group E                  GET    /groups/{group_email}/members
osdu group member count --group E                  GET    /groups/{group_email}/membersCount
osdu group member add    --group E --member M --role R   POST /groups/{group_email}/members
osdu group member remove --group E --member M      DELETE /groups/{group_email}/members/{member_email}
osdu member group list --member M                  GET    /members/{member_email}/groups
osdu member delete     --member M --yes            DELETE /members/{member_email}
```

| Not exposed | Reason |
|---|---|
| `POST /tenant-provisioning` | Tenant bootstrap; run once by an operator, not from a CLI |

This is where resource-first pays for itself. `GET /groups/{e}/members` and
`GET /members/{m}/groups` are the same relation read from either end, and the grammar says
so. Today that second one is `entitlements mygroups`.

**Caveat:** `group list --all` is a *different endpoint* from `group list`, which R2 forbids.
See §3.2.

### workflow, run — Workflow (11 ops)

```
osdu workflow list                                 GET    /v1/workflow
osdu workflow get    --name N                      GET    /v1/workflow/{workflow_name}
osdu workflow create --file F                      POST   /v1/workflow
osdu workflow delete --name N --yes                DELETE /v1/workflow/{workflow_name}
osdu workflow run list    --name N                 GET    /v1/workflow/{name}/workflowRun
osdu workflow run get     --name N --run-id R      GET    /v1/workflow/{name}/workflowRun/{runId}
osdu workflow run trigger --name N [--file F]      POST   /v1/workflow/{name}/workflowRun
osdu workflow run update  --name N --run-id R      PUT    /v1/workflow/{name}/workflowRun/{runId}
osdu workflow run latest  --name N --run-id R      GET    /v1/workflow/{name}/workflowRun/{runId}/latestInfo
```

| Not exposed | Reason |
|---|---|
| `POST /v1/workflow/system` | System workflows are platform-internal |
| `DELETE /v1/workflow/system/{name}` | Same |

Service name and noun coincide, so this tree is identical under either grammar.

### legaltag — Legal (10 ops)

```
osdu legaltag list [--valid-only]                  GET  /legaltags
osdu legaltag get      --name N                    GET  /legaltags/{name}
osdu legaltag create   --file F                    POST /legaltags
osdu legaltag update   --file F                    PUT  /legaltags
osdu legaltag delete   --name N --yes              DELETE /legaltags/{name}
osdu legaltag validate --names N...                POST /legaltags:validate
osdu legaltag query    --file F                    POST /legaltags:query
osdu legaltag properties                           GET  /legaltags:properties
osdu legaltag batch-get --names N...               POST /legaltags:batchRetrieve
```

| Not exposed | Reason |
|---|---|
| `GET /jobs/updateLegalTagStatus` | Compliance cron job status; operator concern |

`:action` paths need explicit `builder:` overrides — the mechanism `storage delete` already
proved.

### schema — Schema (5 ops)

```
osdu schema list [--kind ...] [--status ...]       GET /schema     (searches SchemaInfo)
osdu schema get    --id ID                         GET /schema/{id}
osdu schema add    --file F                        POST /schema
osdu schema update --file F                        PUT  /schema    (development status only)
```

| Not exposed | Reason |
|---|---|
| `PUT /schemas/system` | System schemas are platform-internal |

### search (3 ops)

```
osdu search --kind K --query Q [--limit] [--cursor]   POST /query
                                                      POST /query_with_cursor
osdu search close-cursor --cursor C                   DELETE /query_with_cursor/{cursor}
```

Search is a verb, not a noun — it spans kinds and returns records from anywhere. Forcing it
to `osdu record search` would be tidier grammatically and wrong semantically. See §3.3.

`/query` and `/query_with_cursor` are the same search with and without pagination state, so
one command with `--cursor` covers both — but that is again two endpoints behind one
command (§3.2).

### file — File (6 ops)

```
osdu file upload   --file F                        GET  /v2/files/uploadURL  (+ PUT to signed URL)
osdu file download --id ID [--output-file O]       GET  /v2/files/{id}/downloadURL
osdu file metadata get    --id ID                  GET  /v2/files/{id}/metadata
osdu file metadata create --file F                 POST /v2/files/metadata
osdu file metadata delete --id ID --yes            DELETE /v2/files/{id}/metadata
osdu file revoke-url --file F                      POST /v2/files/revokeURL
```

`file upload` and `file download` are handwritten: the endpoints return signed URLs, and the
actual byte transfer is a second request the CLI must make. That is orchestration, like
`record add`.

### dataset — Dataset (9 ops)

```
osdu dataset list     --id ID...                   GET  /getDatasetRegistry
osdu dataset register --file F                     PUT  /registerDataset
osdu dataset delete   --id ID --yes                POST /metadataRecord/{id}/softDelete
osdu dataset undelete --id ID                      POST /metadataRecord/{id}/undelete
osdu dataset download-url --id ID [--expiry E]     GET  /retrievalInstructions
osdu dataset upload-url   --kind K                 POST /storageInstructions
osdu dataset revoke-url --file F                   POST /revokeURL
```

Two batch twins fold in per §3.1 (`POST /getDatasetRegistry`, `POST /retrievalInstructions`).

### crs, transformation — CRS Catalog + Conversion (8 ops)

```
osdu crs list [--record-id R] [--data-id D]        GET  /v3/coordinate-reference-system
osdu crs transformation list [--record-id R]       GET  /v3/coordinate-transformation
osdu crs points-in-aou --file F                    POST /v3/points-in-aou
osdu crs convert            --file F               POST /v4/convert
osdu crs convert-geojson    --file F               POST /v4/convertGeoJson
osdu crs convert-trajectory --file F               POST /v4/convertTrajectory
```

Two services, one noun, no ambiguity — `list` comes from the catalog, `convert` from the
conversion service. This is the case that killed "one noun, one service" and produced R2.

Two batch twins fold in per §3.1.

### unit, measurement, unitsystem — Unit v3 (28 ops)

The largest core service, and the sharpest test of curation: Python exposes one command
(`unit list`) from these 28.

```
osdu unit list [--offset] [--limit]                GET /v3/unit
osdu unit get    --symbol S                        GET /v3/unit/symbol
osdu unit list   --symbol S                        GET /v3/unit/symbols     (all namespaces)
osdu unit search --query Q                         POST /v3/unit/search
osdu unit maps                                     GET /v3/unit/maps
osdu unit list --measurement A                     GET /v3/unit/measurement
osdu unit list --measurement A --preferred         GET /v3/unit/measurement/preferred
osdu unit get  --system S --measurement A          GET /v3/unit/unitsystem
osdu unit convert --from F --to T [--scale]        GET /v3/conversion/abcd, /v3/conversion/scale
osdu measurement list                              GET /v3/measurement/list
osdu measurement get    --ancestry A               GET /v3/measurement
osdu measurement search --query Q                  POST /v3/measurement/search
osdu measurement maps                              GET /v3/measurement/maps
osdu unitsystem list                               GET /v3/unitsystem/list
osdu unitsystem get --name N                       GET /v3/unitsystem
osdu unit catalog get                              GET /v3/catalog
osdu unit catalog search --query Q                 POST /v3/catalog/search
osdu unit catalog mapstates                        GET /v3/catalog/mapstates
osdu unit catalog modified                         GET /v3/catalog/lastmodified
```

Eight batch twins fold in per §3.1, accounting for the remaining ops.

**This is 19 commands for what is really a reference catalog.** A defensible narrower cut is
`unit list|get|convert|search` plus `measurement list|get` — six commands — deferring
`unitsystem`, `catalog` and `maps` until someone asks. Flagged as an open decision (§3.5).

---

## 3. Open decisions

### 3.1 The GET/POST batch twin — affects 24 operations

Twelve paths expose the same logical operation twice: `GET` with query parameters for one
item, `POST` with a request body for many. The specs make this explicit through the naming:

```
GET  /v3/coordinate-reference-system   getCoordinateReferenceSystem
POST /v3/coordinate-reference-system   getCoordinateReferenceSystems     <- plural
GET  /getDatasetRegistry               getDatasetRegistry
POST /getDatasetRegistry               getDatasetRegistry_1              <- _1 suffix
GET  /retrievalInstructions            retrievalInstructions
POST /retrievalInstructions            retrievalInstructions_1
```

Eight of the twelve are in Unit v3, two in CRS catalog, two in Dataset. That is **24 of the
110 core operations, 22%.**

Three options:

- **(a) One command, `--file` switches to the batch endpoint.** `osdu crs list --data-id X`
  vs `osdu crs list --file ids.json`. Best UX, but it breaks R2 and the generator cannot
  express it — one command maps to one `op:`. Needs a `variants:` mechanism in the manifest.
- **(b) Two commands.** `osdu crs list` and `osdu crs list-batch`. Honest, ugly, and inflates
  the surface by 12 commands.
- **(c) Expose only the GET.** Smallest surface. Loses genuine batch capability, which
  matters most exactly where it is most common — Unit lookups in a loop.

I lean **(a)**, because 22% is too much of the surface to name badly, and because a
`variants:` mechanism is a contained change to the generator that pays off across three
services. But it is a real cost and it should be a deliberate choice.

> **Resolved: (c), for now.** All 12 twins expose the GET and exclude the POST with a written
> reason pointing here. Rationale: (a) remains the better end state, but it needs a
> `variants:` mechanism that does not exist, and shipping (c) does not foreclose it — moving
> a twin from `exclude:` to a variant later is a manifest edit, not a redesign. Nothing is
> named badly in the meantime, because the GET keeps the natural name.
>
> The cost is real and specific: batch lookups are unavailable, which bites hardest in Unit,
> where converting a list of symbols now means one call per symbol. Revisit if that shows up
> in practice.

### 3.2 Flag-based endpoint dispatch

Independently of §3.1, three cases want one command to reach two endpoints:

```
osdu group list  /  group list --all       GET /groups        vs GET /groups/all
osdu search --cursor                       POST /query        vs POST /query_with_cursor
osdu workflow create --system              POST /v1/workflow  vs POST /v1/workflow/system
```

The third is resolved by not exposing system workflows. The first two are not. Note the PoC
already rejected exactly this pattern once, deliberately: `storage get` is id-only because
the Python CLI's `--kind`/`--id` dispatch "cannot be expressed as one operation".

Same underlying gap as §3.1, and one `variants:` mechanism would close both.

### 3.3 Where search lives

`osdu search` as a top-level verb, or `osdu record search`? Search returns records, which
argues for the noun; but it spans kinds and is the primary discovery entry point, which
argues for top level. I lean top-level. Either way, `search kind` / `search id` /
`search query` collapse into one command with options.

> **Resolved: `osdu record search`.** Search is a verb on records, and R1 says nouns come
> first — a top-level `search` would have been the one verb-first command in the CLI. The
> Search service contributes it to the `record` noun that Storage also feeds, which is the
> clearest demonstration that nouns are not service names.

### 3.4 File vs Dataset overlap

Both services issue upload URLs, download URLs and a `revokeURL`, and File is effectively a
specialisation of Dataset. `osdu file download --id X` and `osdu dataset download-url --id X`
are near-synonyms that hit different services and return different shapes.

No naming scheme fixes this — it is a platform-level overlap. The options are to expose both
and document the difference, expose only `file` and treat `dataset` as advanced, or unify
under `dataset` with `file` as a convenience wrapper. This needs a call from someone who
knows which one Equinor's ingestion actually uses.

> **Partially resolved: both exposed, symmetrically named.** `file` and `dataset` each get
> `upload-url` / `download-url` / `revoke-url`, so the parallel is visible rather than
> hidden behind differing verbs. **The underlying question still needs an owner** — if
> Equinor only uses one path, the other should be dropped or de-emphasised.

### 3.5 Coverage depth

110 operations is roughly double the Python CLI's 56 commands. Full coverage is not
automatically the goal — the curation is the product. The specific question is Unit: 19
commands as mapped above, or ~6 for the parts anyone uses.

> **Resolved: 20 commands for Unit, full v3 coverage minus the twins.** Reasoning: the
> marginal cost of a Unit command is a dozen manifest lines, and the catalog endpoints are
> genuinely unguessable — a user cannot know that preferred units live behind a different
> path than units-by-measurement. Splitting into `unit` / `measurement` / `unit-system`
> keeps any single `--help` page short, which was the real objection to breadth. Unit v2 is
> excluded entirely as deprecated.

---

## 4. What this costs

**Commands that change name** (the migration surface, if the Python CLI has users):

| Python | Proposed |
|---|---|
| `storage list|get|delete|add` | `record list|get|delete|add` |
| `storage info` (and 9 more) | `status [service]` |
| `entitlements mygroups` | `group list` |
| `legal listtags` | `legaltag list` |
| `search kind|id|query` | `search` |
| `list` | `record list --count` or dropped |
| `crs areas|summary|transforms` | `crs points-in-aou`, `crs transformation list` |
| `workflow runs` | `workflow run list` |

**Commands that do not change:** everything in `workflow`, `schema`, `unit`, `file`,
`dataset` keeps its group name. Six of ten services are unaffected by the noun decision.

**Unchanged by design:** `config`, `status`, `version` and `dataload` stay hand-written and
never enter a manifest.

---

## 5. Discoverability is a requirement, not a nice-to-have

The Python CLI is self-explanatory: `-h` anywhere gives you what you need. Any restructuring
has to preserve that, and a deeper tree raises the stakes — `osdu group member list` is three
levels, so `osdu group --help` and `osdu group member --help` both have to be useful stops on
the way.

Resource-first helps here rather than hurting. Consistent verbs mean that once you have seen
`osdu legaltag list|get|add|delete`, you can guess `osdu group ...` without reading anything.
Today's `mygroups` / `listtags` / `runs` / `areas` have to be learned one at a time.

Already in place, verified: `--help` at every level and without configuration; `-h`, `-?`
aliases; command options separated from Common Options; every alias listed; "Did you mean"
suggestions on typos; full help reprinted when a required option is missing; `--debug` for
full exception detail.

Two gaps worth closing as the surface grows:

- **`--filter`.** The Python CLI takes a JMESPath expression for ad-hoc field selection. The
  design deliberately replaced JMESPath with data-driven projection in the manifest, which
  is better for the default view but leaves no way to pick a field that the manifest's
  `columns` does not list. `--output json` piped to `jq` covers it; a `--query` option over
  the parsed JSON would cover it without a second tool.
- **Completion for `kind`.** The hardest thing about OSDU from a shell is knowing that the
  string is `osdu:wks:master-data--Wellbore:1.0.0`. System.CommandLine supports dynamic
  completions, so `--kind <TAB>` could be backed by a cached kind list. Nothing in the Python
  CLI does this, and it would be the single largest usability gain available.

