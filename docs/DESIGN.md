# How osducs is built

osducs began as a C# twin of the Python
[osdu-cli](https://community.opengroup.org/osdu/platform/data-flow/data-loading/osdu-cli), built
to answer one question: **can the CLI's command surface be generated from the OpenAPI specs
instead of hand-written?**

The answer, on this evidence, is yes for the bulk of it — with an editorial layer that has to
stay hand-written, and which this repo makes explicit rather than implicit. The question is
settled enough that the tool is now the point rather than the experiment: it is released, it
is used against live instances, and the generator runs in CI.

This page explains the approach. [DEVELOPMENT.md](DEVELOPMENT.md) covers working on it, and
[COMMAND-GRAMMAR.md](../COMMAND-GRAMMAR.md) the rules commands follow.

## Why generate

The Python CLI is 149 commands / ~11,900 lines. Of those, 83 are Wellbore DDMS and 10 wrap
`wbdutil` — both later additions. **The core command set is the remaining 56**: storage,
search, schema, entitlements, workflow, crs, legal, unit, file, plus `config`, `status`,
`version`, `list` and `dataload`.

Roughly 45 of those 56 are thin service CRUD that generates cleanly. The other 11 are
genuinely hand-written — `dataload` alone is 4 commands and 1,870 lines of orchestration no
generator should touch. (Wellbore DDMS shows the same split more starkly: 116 of its files
are under 50 lines of near-identical boilerplate, machine output that happened to be
produced by copy-paste.)

This repo draws that line as a build-enforced boundary.

## How it fits together

```
openapi_specs/<service>/openapi.{yaml,json}  what the service can do       (fetched, pinned)
cli-manifest/<service>.yaml                  what the CLI should expose    (hand-written, reviewed)
        │
        ├── tools/generate_cli.py
        ▼
src/OsduCli/Commands/Generated/*.g.cs        System.CommandLine tree       (committed, never edited)
src/OsduCli/Commands/Handwritten/            the Customize() partial hook  (hand-written)
```

Generated commands call [`Equinor.OsduCsharpClient`](https://github.com/equinor/osdu-csharp-client),
which is itself Kiota-generated from the same specs. The CLI adds no HTTP code of its own.

Since client 2.0.0 the core package is authentication-agnostic — it bundles no identity
library and `OsduClient` takes an `ITokenProvider` rather than defaulting to one. The CLI
therefore also references `Equinor.OsduCsharpClient.Msal` and selects
`MsalInteractiveTokenProvider`, which is the right answer for a tool driven by a person at a
terminal. Sign-in is cached, OS-encrypted, under `~/.osdu`. A profile set to sign in as an
application gets `MsalClientCredentialsTokenProvider` instead, and one signing in through Azure
a small provider over Azure.Identity's `DefaultAzureCredential`, which the client library does
not offer.

## The manifest is the point

Three things cannot be derived from a spec, and all three live in the manifest:

**Which operations deserve to be commands.** Storage has 20 operations; the CLI exposes 6.
Every operation must appear in exactly one of `commands`, `handwritten` or `exclude` — and
`exclude` requires a `reason`. `tools/generate_cli.py --check` fails otherwise, so a new
upstream endpoint surfaces as a named build failure rather than silently not existing.

Operations the spec marks `deprecated: true` are the one exception: they are excluded
automatically, and mapping one to a command is an error. OSDU retires endpoints in bulk —
all 28 Unit v2 operations at once — and that many exclusions all reading "deprecated" would
bury the editorial ones.

**Naming and hierarchy.** `osducs record version get` does not follow from
`GET /records/{id}/{version}`. Commands are named for the resource, not the OSDU service
that hosts it — the Storage manifest builds `osducs record`, not `osducs storage`. See
[COMMAND-GRAMMAR.md](../COMMAND-GRAMMAR.md).

**What a human wants to see.** The Python CLI encodes this as JMESPath
(`"results || {Id:id,Version:version,Kind:kind}"`). Here it is data:

```yaml
output:
  root: results
  columns: { Id: id, Version: version, Kind: kind }
```

Data rather than an expression language because a generator can emit and validate it, a
reviewer can read it without knowing JMESPath, and it needs no expression evaluator at
runtime — which keeps NativeAOT trivial.

Every key a manifest can use is listed in [MANIFEST.md](MANIFEST.md).

## What the generator derives, and what it refuses to guess

Derived from the spec: the Kiota request-builder accessor chain, the HTTP method, C# option
types (honouring `format: int32` vs `int64`, because Kiota does), whether the operation
returns a body, and the request-body model from its `$ref`.

It refuses to guess where Kiota's naming is not mechanical. `POST /records/{id}:delete`
becomes a *method* (`Records.WithIdDelete(id)`), not an indexer, so the manifest states it:

```yaml
builder: Records.WithIdDelete({id})
```

A wrong guess would be a compile error, not a runtime one — but a clear manifest field beats
a confusing compiler message.

## Scope, for incremental adoption

Wellbore DDMS has 84 operations. Triaging all 84 to add three commands is not useful, so a
manifest may declare a `scope`:

```yaml
scope: ["/ddms/v3/wellbores*"]
```

Coverage is enforced inside the scope and reported outside it. Widening the scope one prefix
at a time is how the rest of a service gets adopted.

## Coverage, and what is left out

Two nouns are assembled from more than one service (`record`, `crs`) and one service supplies
several nouns (entitlements, unit). That mapping is the whole point of the resource-first
grammar, and it is why the command tree lives in `CommandTree` rather than in any one
manifest.

Unit **v2 is deprecated upstream and deliberately excluded**; only v3 is exposed. The
generator refuses to bind a command to any operation the spec marks deprecated.

Every excluded operation carries a written reason, and the generator fails if an exclusion
goes stale. The largest single category is the 12 GET/POST twins — endpoints exposed twice,
once with query parameters and once with a body — where the GET is kept and the POST
excluded; see [COMMAND-GRAMMAR.md](../COMMAND-GRAMMAR.md) §3.1.

Deliberately not covered: `wbdutil` (LAS/parquet — the only source of native dependencies in
the Python CLI, and out of scope by decision) and `dataload` (orchestration).

## Where it differs from the Python CLI

- **`record get` is id-only.** The Python CLI's `storage get` accepts either `--kind` or `--id`
  and calls a different endpoint for each, which duplicates `storage list`. See the comment in
  `cli-manifest/storage.yaml`.
- **One `status` for every service.** The Python CLI gives every service its own `info`
  command, ten of which call one shared helper; here one command probes every configured
  service and reports them together, so an unreachable service shows as a row rather than
  aborting the run. It exits non-zero if any service fails to answer.
- **It does not write the Python CLI's files.** It reads that tool's profiles, but its own live
  in `~/.osdu/`, so migrating one way never changes the other; see
  [USAGE.md](USAGE.md#configuration).
