# osducs

A command line for [OSDU](https://osduforum.org) data platforms. Search and read records, and
manage schemas, legal tags, entitlements, workflows, units, CRS and Wellbore DDMS data, from a
single self-contained binary for Windows, macOS and Linux.

```bash
osducs record search --kind "osdu:wks:master-data--Well:*" --query 'data.FacilityName:GB*' -f id -f data.FacilityName
osducs record aggregate --kind "osdu:wks:*:*" --by kind
osducs legaltag list
osducs group member add --group <group-email> --member name@equinor.com --role MEMBER
```

## Why use it

- **Nothing to set up if you already use the Python CLI.** osducs reads the
  [`osducli`](https://community.opengroup.org/osdu/platform/data-flow/data-loading/osdu-cli)
  profiles in `~/.osducli/` and follows the environment it has selected, without changing them.
  The two run side by side, and moving a profile over is one command.
- **The whole core OSDU surface.** 131 commands across 12 services — Storage, Search, Schema,
  Legal, Entitlements, File, Dataset, CRS, Unit, Workflow and Wellbore DDMS — generated from the
  services' own OpenAPI specs, so a new endpoint upstream is noticed rather than missed.
- **Signs in the way your environment needs:** through a browser; as an application with a
  client secret; or with `az login`, a managed identity or a pipeline identity. With more than
  one account signed in, it lists them and waits to be told which, rather than guessing.
- **Readable by default, scriptable when needed.** Tables for people, `-o json` for scripts,
  and `-f` to pick the columns a search returns.
- **Help that answers the question.** `--help` at every level. A refused request names the role
  the endpoint needs, where its spec says. A mistyped command gets a suggestion, so
  `osducs member add group` points to `osducs group member add`.
- **One file, no runtime to install.** About 30 MB to download, with .NET inside, and tab completion for
  bash, zsh, fish and PowerShell.

## Install

Download from the [latest release][releases]; no account is needed.

```bash
# macOS (Apple silicon); for Linux use osducs-linux-x64.tar.gz
curl -LO https://github.com/equinor/osdu-csharp-cli/releases/latest/download/osducs-osx-arm64.tar.gz
tar -xzf osducs-osx-arm64.tar.gz && chmod +x osducs
```

```powershell
# Windows
curl.exe -LO https://github.com/equinor/osdu-csharp-cli/releases/latest/download/osducs-win-x64.zip
Expand-Archive osducs-win-x64.zip -DestinationPath .
.\osducs --version
```

The binaries are not signed yet, so a browser download needs unblocking first. That, putting
osducs on your `PATH`, and checking a download against the release are in
[docs/INSTALL.md](docs/INSTALL.md).

[releases]: https://github.com/equinor/osdu-csharp-cli/releases/latest

## Get started

**If you use the Python CLI, try it straight away:**

```bash
osducs status
```

```
https://<instance>.energy.azure.com  partition dev
Service         Status  Version          Build
--------------  ------  ---------------  ------------------------
crs-catalog     ok      0.29.2-SNAPSHOT  2026-08-05T09:49:35.371Z
crs-conversion  ok      0.29.2-SNAPSHOT  2026-08-05T09:49:28.770Z
storage         ok      0.29.4-SNAPSHOT  2026-08-05T19:04:12.267Z
...
```

**Otherwise, create a profile.** In a terminal this asks for each setting:

```bash
osducs config add dev
```

**To move a Python profile over**, so osducs keeps its own copy:

```bash
osducs config add dev --from dev
```

[docs/USAGE.md](docs/USAGE.md) covers profiles and switching between environments, the
sign-in options, output, and finding records. [docs/COMMANDS.md](docs/COMMANDS.md) lists every
command and option.

## What it covers

| Noun | Service |
| --- | --- |
| `record` | Storage, Search |
| `schema` | Schema |
| `legaltag` | Legal |
| `group`, `member` | Entitlements |
| `file` | File |
| `dataset` | Dataset |
| `crs` | CRS Catalog, CRS Conversion |
| `unit`, `measurement`, `unit-system` | Unit v3 |
| `workflow` | Workflow |
| `well`, `wellbore`, `welllog`, `trajectory`, `markerset`, `intervalset`, `logacquisition`, `ppfg`, `pressuretest` | Wellbore DDMS: every record type, and reading bulk data |

Plus `status`, `config`, `account` and `completion`. Not ported from the Python CLI:
`dataload` and the `wbdutil` commands.

## Status

Released and in use against Azure Data Manager for Energy instances. Reading is well
exercised; most write commands have seen little use yet.
[docs/TROUBLESHOOTING.md](docs/TROUBLESHOOTING.md) lists the failures met so far, and which of
them are not the CLI's.

The binaries are unsigned: macOS quarantines a browser download, and Windows SmartScreen warns
until the file is unblocked.

Found a problem, or something missing? [Open an issue](https://github.com/equinor/osdu-csharp-cli/issues).
Testing it for the first time? [docs/PEER-TEST.md](docs/PEER-TEST.md) is a 20-minute round.

## Documentation

| | |
|---|---|
| [docs/INSTALL.md](docs/INSTALL.md) | installing, unblocking, `PATH` and checking a download |
| [docs/USAGE.md](docs/USAGE.md) | configuration, signing in, output, finding records — the everyday guide |
| [docs/COMMANDS.md](docs/COMMANDS.md) | every command and option, generated from the manifests |
| [docs/TROUBLESHOOTING.md](docs/TROUBLESHOOTING.md) | failures seen against a live instance, and which are not the CLI's fault |
| [docs/PEER-TEST.md](docs/PEER-TEST.md) | **hand this to a tester**: install, what to try, what not to report |
| [docs/DESIGN.md](docs/DESIGN.md) | why commands are generated from the specs, and what the manifests decide |
| [docs/DEVELOPMENT.md](docs/DEVELOPMENT.md) | for maintainers: how the code fits together, upgrading the client, releasing |
| [docs/MANIFEST.md](docs/MANIFEST.md) | every key a manifest can use, for adding or changing commands |
| [COMMAND-GRAMMAR.md](COMMAND-GRAMMAR.md) | why commands are named as they are |

## How it is built

Most of osducs is generated. Each OSDU service's OpenAPI spec says what it can do, and a
hand-written manifest says which operations become commands, what they are called and what
their output shows. A build check fails when a new upstream endpoint is neither mapped nor
deliberately excluded. Commands call
[`Equinor.OsduCsharpClient`](https://github.com/equinor/osdu-csharp-client), which is generated
from the same specs. [docs/DESIGN.md](docs/DESIGN.md) explains the approach, and
[CONTRIBUTING.md](CONTRIBUTING.md) how to change it.

Security issues: see [SECURITY.md](SECURITY.md). Licensed under the [Apache License 2.0](LICENSE).
