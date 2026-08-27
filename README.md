# osdu-csharp-cli (proof of concept)

A C# twin of [osdu-cli](https://community.opengroup.org/osdu/platform/data-flow/data-loading/osdu-cli),
built to answer one question: **can the CLI's command surface be generated from the OpenAPI
specs instead of hand-written?**

The answer, on this evidence, is yes for the bulk of it — with an editorial layer that has to
stay hand-written, and which this PoC makes explicit rather than implicit.

## Install

The repository is **internal**, so release assets need an authenticated download — a plain
`curl` of the asset URL returns 404. Either use the GitHub CLI:

```bash
gh release download --repo equinor/osdu-csharp-cli --pattern "osducs-osx-arm64.tar.gz"
tar -xzf osducs-osx-arm64.tar.gz && chmod +x osducs

# macOS quarantines downloads until the binary is signed and notarized
xattr -d com.apple.quarantine ./osducs
```

…or download from the [releases page][releases] in a browser you are signed in with.

`osducs-linux-x64.tar.gz` and `osducs-win-x64.zip` are the other two. Around 30 MB
compressed; nothing else is needed, since the .NET runtime is inside the binary.

The command is `osducs`, not `osdu`, so it sits alongside the Python
[`osducli`](https://community.opengroup.org/osdu/platform/data-flow/data-loading/osdu-cli)
rather than replacing it.

[releases]: https://github.com/equinor/osdu-csharp-cli/releases/latest

## Documentation

| | |
|---|---|
| [docs/PEER-TEST.md](docs/PEER-TEST.md) | **hand this to a tester** — install, what to try, what not to report |
| [docs/USAGE.md](docs/USAGE.md) | configuration, output, finding records — the everyday guide |
| [docs/COMMANDS.md](docs/COMMANDS.md) | every command and flag, generated from the manifests |
| [docs/TROUBLESHOOTING.md](docs/TROUBLESHOOTING.md) | failure modes seen against a live instance, and which are not the CLI's fault |
| [COMMAND-GRAMMAR.md](COMMAND-GRAMMAR.md) | why commands are named as they are, and the manifest reference |

## First run

**If you already use the Python CLI, there is nothing to configure.** `osducs` reads the
same profiles from `~/.osducli/`:

```bash
osducs status -c dev
```

```
Service         Status  Version          Build
--------------  ------  ---------------  ------------------------
crs-catalog     ok      0.29.2-SNAPSHOT  2026-08-05T09:49:35.371Z
crs-conversion  ok      0.29.2-SNAPSHOT  2026-08-05T09:49:28.770Z
storage         ok      0.29.4-SNAPSHOT  2026-08-05T19:04:12.267Z
wellbore-ddms   ok      0.29
...
```

Otherwise create `~/.osdu/config.json` with an `Osdu` section, or set `OSDU_SERVER`,
`OSDU_DATA_PARTITION_ID`, `OSDU_AUTHORITY`, `OSDU_CLIENT_ID` and `OSDU_SCOPES`.

## Why

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
openapi_specs/<service>/openapi.yaml     what the service can do        (osdu-csharp-client)
cli-manifest/<service>.yaml              what the CLI should expose     (hand-written, reviewed)
        │
        ├── tools/generate_cli.py
        ▼
src/OsduCli/Commands/Generated/*.g.cs    System.CommandLine tree        (committed, never edited)
src/OsduCli/Commands/Handwritten/        the Customize() partial hook   (hand-written)
```

Generated commands call [`Equinor.OsduCsharpClient`](https://github.com/equinor/osdu-csharp-client),
which is itself Kiota-generated from the same specs. The CLI adds no HTTP code of its own.

Since client 2.0.0 the core package is authentication-agnostic — it bundles no identity
library and `OsduClient` takes an `ITokenProvider` rather than defaulting to one. The CLI
therefore also references `Equinor.OsduCsharpClient.Msal` and selects
`MsalInteractiveTokenProvider`, which is the right answer for a tool driven by a person at a
terminal. Sign-in is cached, OS-encrypted, under `~/.osdu`.

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
[COMMAND-GRAMMAR.md](COMMAND-GRAMMAR.md).

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

## Help

`--help` (also `-h`, `-?`) works at every level of the tree, without configuration. Options
are split into command options and **Common Options** — the global `--output`, `--config`,
`--debug` and `--help` — so a command's own options are not buried among them, and every
alias is listed rather than one form per option. Both match what the Python CLI does today.

`Runtime/CliHelp.cs` renders this by hand: System.CommandLine 2.0.11 keeps `HelpBuilder`,
`HelpContext` and `TwoColumnHelpRow` internal, and seals `HelpAction`, so the layout cannot
be customised through the library. Positional arguments get their own section and the usage
line brackets the optional ones, which the default formatter does not distinguish.

Tab completion needs no code: System.CommandLine's `[suggest]` directive completes
subcommands and options at every level, and `osducs status` adds value completion for its
service argument. Shell registration scripts are not written yet. Two rules for any future
value completion: never authenticate on TAB, and never block on the network.

`--debug` prints the full exception instead of the one-line summary — for diagnosing an auth
or transport failure rather than a user error.

## Smoke-testing the examples

Help examples are untested documentation and rot silently — `data.Country:"Norway"` sat in
`record search --help` matching nothing, because that field exists on no OSDU kind. CI cannot
catch it: it needs a live service and a token.

```bash
python3 tools/smoke_test.py            # every example, default profile
python3 tools/smoke_test.py -c dev     # a named profile
python3 tools/smoke_test.py record     # only `record …` commands
```

The examples live in the manifests beside the command they document, so the string shown in
help and the string executed here are the same string. An example that cannot run anywhere —
a record id is scoped to a data partition — carries a `skip:` reason and is reported as
skipped rather than failed.

An empty result counts as a failure: a command that renders nothing has "worked" and told the
user nothing, which is exactly how the stale examples went unnoticed. Non-zero exit if
anything fails, so it can gate a release.

## Running it

```bash
python3 -m pytest                        # test the generator
UPDATE_GOLDEN=1 python3 -m pytest        # re-record emission goldens after a deliberate change
python3 tools/generate_cli.py           # generate
python3 tools/generate_cli.py --check   # CI gate: validate, write nothing
cd src/OsduCli && dotnet build
```

Expects `osdu-csharp-client` checked out as a sibling directory.

## Status

Proof of concept, covering every core service the Python CLI covers, plus the record surface
of Wellbore DDMS: **131 generated commands across 12 services**, plus the hand-written
`osducs status`.

| Noun | Fed by |
| --- | --- |
| `record` | storage, search |
| `schema` | schema |
| `legaltag` | legal |
| `group`, `member` | entitlements |
| `file` | file |
| `dataset` | dataset |
| `crs` | crs_catalog, crs_conversion |
| `unit`, `measurement`, `unit-system` | unit v3 |
| `workflow` | workflow |
| `well`, `wellbore`, `welllog`, `trajectory`, `markerset`, `intervalset`, `logacquisition`, `ppfg`, `pressuretest` | wellbore_ddms (55 of 84 operations — every record type, plus bulk reads) |

Two nouns are assembled from more than one service (`record`, `crs`) and one service supplies
several nouns (entitlements, unit). That mapping is the whole point of the resource-first
grammar, and it is why the command tree lives in `CommandTree` rather than in any one
manifest.

Unit **v2 is deprecated upstream and deliberately excluded**; only v3 is exposed. The
generator refuses to bind a command to any operation the spec marks deprecated.

Every excluded operation carries a written reason, and the generator fails if an exclusion
goes stale. The largest single category is the 12 GET/POST twins — endpoints exposed twice,
once with query parameters and once with a body — where the GET is kept and the POST
excluded; see `COMMAND-GRAMMAR.md` §3.1.

Deliberately not covered: `wbdutil` (LAS/parquet — the only source of native dependencies in
the Python CLI, and out of scope by decision), `dataload` (orchestration), and any real
integration test — nothing here has been run against a live OSDU instance.

One deliberate divergence from the Python CLI: its `storage get` accepts either `--kind` or
`--id` and calls a different endpoint for each, which duplicates `storage list`. Here
`record get` is id-only. See the comment in `cli-manifest/storage.yaml`.

`osducs status` is the other divergence. The Python CLI gives every service its own `info`
command, ten of which call one shared helper; here one command probes every configured
service and reports them together, so an unreachable service shows as a row rather than
aborting the run. It exits non-zero if any service fails to answer.

## Shell completion

```bash
osducs completion bash > /usr/local/etc/bash_completion.d/osducs
```

`zsh`, `fish` and `powershell` are also supported; each script carries its own install line
as a comment. Completion needs no configuration, no network and no token — Tab never
authenticates.
