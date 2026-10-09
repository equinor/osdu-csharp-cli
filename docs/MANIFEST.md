# Manifest reference

Each file in [`cli-manifest/`](../cli-manifest/) describes one OSDU service: which of its
operations become commands, what they are called, which options they take and how their output
is shown. `tools/generate_cli.py` joins a manifest with the service's OpenAPI spec and emits the
C# under `src/OsduCli/Commands/Generated/`.

This page lists every key, level by level. For why the format is the way it is, see
[DESIGN.md](DESIGN.md#the-manifest-is-the-point); for the rules commands follow, see
[COMMAND-GRAMMAR.md](../COMMAND-GRAMMAR.md).

Every level is checked. A key the generator does not know is an error that names the keys that
are allowed, so a typo cannot silently switch a rule off. A test fails if a key the generator
accepts is missing from this page.

## A small example

```yaml
service: legal
spec: legal
client: Legal
description: Legal service — legal tags and their properties.

groups:
  legaltag: Work with legal tags, which govern who may hold and see data.

commands:
  - command: legaltag get
    summary: Get a legal tag by name.
    op: { method: get, path: "/legaltags/{name}" }
    params:
      name: { flag: --name, short: -n, required: true, help: Legal tag name. }
    examples:
      - --name "opendes-public-usa-dataset-1"
    output: raw

exclude:
  - op: { method: post, path: /legaltags:batchRetrieve }
    reason: Batch read of named tags; `legaltag get` covers the single case.
```

## Top level

| Key | Required | What it does |
|---|---|---|
| `service` | yes | Names the service in reports and errors, and names the generated class: `storage` becomes `StorageCommands`. A test requires `osducs status` to probe every service named here. |
| `spec` | yes | The spec to read: `openapi_specs/<spec>/openapi.yaml` or `.json`, for example `storage` or `unit/v3`. |
| `client` | yes | The `OsduClient` property the commands call, for example `Storage` or `UnitV3`. |
| `models` | no | The namespace under `Equinor.OsduCsharpClient` that holds the service's request models and enums. Defaults to `client`. Set it where the client library renamed the namespace, as `File` did to `FileNamespace` to avoid `System.IO.File`. |
| `description` | no | Doc comment on the generated class. Not shown to users. |
| `groups` | see text | Help text for each group a command sits under, keyed by its path: `record`, `record version`. Every group needs exactly one description, which may come from any manifest that puts commands under it, so a manifest adding commands to a group another manifest already describes does not repeat it. Two manifests describing the same group differently is an error, and so is describing a group no command sits under. |
| `scope` | no | Path patterns (`fnmatch` style, such as `/ddms/v3/*`) limiting which operations the manifest must account for. Operations outside the scope are reported, not enforced; `generate_cli.py --strict-scope` fails on them. See [Scope](DESIGN.md#scope-for-incremental-adoption). |
| `section` | no | Heading under which this manifest's top-level nouns appear in `osducs --help`. Without it they are listed under "Core resources". See [`section`](../COMMAND-GRAMMAR.md#section). |
| `commands` | no | The operations that become generated commands. See [Command entries](#command-entries). |
| `handwritten` | no | Operations that deserve a command the manifest cannot express. See [`handwritten`](#handwritten). |
| `exclude` | no | Operations deliberately left out, each with a reason. See [`exclude`](#exclude). |

**Coverage.** Every operation in the spec that is inside the scope and not marked `deprecated`
must appear in `commands`, `handwritten` or `exclude`, and `generate_cli.py --check` fails
otherwise. An operation may back more than one command: `record search` and `record aggregate`
both call `POST /query`. Deprecated operations are excluded automatically, and naming one
anywhere in the manifest is an error.

## Command entries

Each item under `commands:`.

| Key | Required | What it does |
|---|---|---|
| `command` | yes | The command's path, space-separated: `record version get`. Every word before the last is a group, which needs a `groups:` description. A path must be unique across all manifests, and cannot be both a command and a group. |
| `summary` | no | One line shown in `--help` and in [the command reference](COMMANDS.md). A command without one has no description. |
| `op` | yes | The operation the command calls. See [`op`](#op). |
| `builder` | no | The client call path, when it cannot be derived from the operation's path. Derivation turns `/records/{id}/{version}` into `Records[{id}][{version}]`. A path segment with characters other than letters, digits and `_` stops it, as in `/records/{id}:delete`, and the generator asks for `builder:` rather than guess, for example `builder: Records.WithIdDelete({id})`. `{name}` placeholders take the value of the path parameter of that name. |
| `params` | no | Options mapped to the operation's path and query parameters. See [`params`](#params). |
| `body` | no | The request body, read from a file or assembled from options. See [`body`](#body). |
| `output` | no | How the response is shown. See [`output`](#output). |
| `examples` | no | Example invocations. See [`examples`](#examples). |
| `require-one-of` | no | Two or more param names of which at least one must be given, checked before anything is sent. See [`require-one-of`](../COMMAND-GRAMMAR.md#require-one-of). |
| `mutually-exclusive` | no | Params or body fields that cannot be combined: one group as `[a, b]`, or several as `[[a, b], [c, d]]`. See [`mutually-exclusive`](../COMMAND-GRAMMAR.md#mutually-exclusive). |
| `forbidden-hint` | no | An extra line printed under a 403, for example pointing to a command most users can run instead. The roles the operation needs are added without this key: the generator reads them from the spec's "Allowed roles" or "Required roles" text. |
| `paging` | no | A cursor endpoint to send the same request to when one page of results is not enough. Adds `--all`. See [`paging`](#paging). |

### `op`

| Key | Required | What it does |
|---|---|---|
| `method` | yes | HTTP method: `get`, `put`, `post`, `delete` or `patch`. |
| `path` | yes | The operation's path exactly as the spec writes it, such as `/records/{id}`. It must exist in the spec; if it no longer does, the endpoint was renamed or removed upstream. |

### `params`

A map from the spec's parameter name to the option it becomes. Only path and query parameters
can be mapped. Headers such as `data-partition-id` come from the configuration.

| Key | Required | What it does |
|---|---|---|
| `flag` | yes | The long option, such as `--id`. It cannot reuse a global option: `--output`, `-o`, `--config`, `-c`, `--user`, `-u`, `--debug` or any form of `--help`. |
| `short` | no | A short alias, such as `-i`, checked against the same list. |
| `required` | no | Whether the option must be given. It must be `true` when the spec requires the parameter, since the command reference is generated from the manifest alone. It may be `true` when the spec does not, to make the CLI stricter. |
| `help` | no | Help text. Defaults to the spec's description of the parameter. |

The option's type always comes from the spec: an integer is `int`, or `long` for `format: int64`,
and a boolean becomes a flag that takes no value. A boolean the spec defaults to `true` takes
`true` or `false` instead, since leaving it out has to mean something different from `false`.
A parameter the spec limits to an `enum` accepts only those values, in any casing, and sends the
spec's own spelling. See [Enum casing](../COMMAND-GRAMMAR.md#enum-casing).

### `body`

A request body takes one of two forms.

**Read from a file.** The user passes the path to a JSON file:

| Key | Required | What it does |
|---|---|---|
| `flag` | yes, for this form | The option that takes the file path, such as `--file`. Checked against the global options, as for params. |
| `short` | no | A short alias for it. |
| `required` | no | Whether the file must be given. Defaults to `true`. |
| `help` | no | Help text. Defaults to "JSON file containing the request body." |
| `wrap-single` | no | Accept a single JSON object where the endpoint takes a list, and wrap it in an array. Several create endpoints take a list, but users usually have one record. |
| `collection` | no | Whether the body is a JSON array of `model`. Derived from the spec, including when `model` is given, so set it only to override that. |

**Assembled from options.** Each body property the command exposes gets its own option:

| Key | Required | What it does |
|---|---|---|
| `fields` | yes, for this form | Body properties and their options. See [Body `fields`](#body-fields). |
| `fixed` | no | Properties sent with a fixed value on every call, which no option can change. See [`fixed` body values](../COMMAND-GRAMMAR.md#fixed-body-values). |

A body has either `flag` or `fields`, never both. The file-form keys `short`, `required`, `help`,
`wrap-single` and `collection` are refused alongside `fields`. Options build a single JSON
object, so an operation whose body is an array has to read it from a file.

Both forms take this:

| Key | Required | What it does |
|---|---|---|
| `model` | no | The client library's class for the body. Derived from the spec's `$ref`, and required when the spec declares the body inline. A class outside the service's `models` namespace needs its full name. |

### Body `fields`

A map from body property to option. A dotted name reaches into a nested object: `sort.order` sets
`order` inside `sort`. See [Nested body fields](../COMMAND-GRAMMAR.md#nested-body-fields).

| Key | Required | What it does |
|---|---|---|
| `flag` | yes | The long option. Checked against the global options, as for params. |
| `short` | no | A short alias, checked the same way. |
| `required` | no | Whether the option must be given. Defaults to `false`; unlike params, it is not taken from the spec. |
| `help` | no | Help text. There is no default. |
| `type` | no | The option's C# type. Defaults to `string`; the manifests also use `string[]`, `int`, `bool`, `double` and `double[]`. An array type accepts several values after one flag. |
| `parts` | no | Paths to spread one option's comma-separated numbers across, such as a bounding box's four coordinates. Needs `type: double[]`, and exactly one value per path. See [`parts`](../COMMAND-GRAMMAR.md#parts). |
| `comma-separated` | no | `true` splits each value on commas as well as accepting the flag repeated, so `-f a,b -f c` is three values. Needs `type: string[]`, and cannot be combined with `parts` or with allowed values from the spec. Without it a comma is part of the value, which is right for values that may contain one. |

A field inherits the allowed values of an `enum` in the body schema, including one on an array's
items. An optional field the user did not give is left out of the body rather than sent as `null`
or as an empty array.

### `output`

When the operation returns a body, `output: raw`, or leaving `output` out, prints it as indented
JSON. When it returns none, the command prints `message`, or "Done.". Otherwise `output` is a
mapping:

| Key | Required | What it does |
|---|---|---|
| `root` | no | A top-level property to unwrap first, such as `results`. Applies to `--output json` too, which prints the unwrapped value. Without `columns`, table mode also prints it as JSON. |
| `columns` | no | Table columns, as a map from header to a dotted path within each row: `{ Id: id, Kind: kind }`. The path `.` means the row itself, for a response that is an array of plain values; property paths are refused there because they would print blank rows. A value that is not a plain value is printed as compact JSON. `--output json` ignores `columns`. |
| `columns-from` | no | A body field whose values become the columns when the user gives it, so fields asked for with `record search --returned-fields` are shown. Must name one of the command's body fields. See [`columns-from`](../COMMAND-GRAMMAR.md#columns-from). |
| `total-from` | no | A top-level number printed above the table as "N matching records", in table mode only. A count of exactly 10,000 is shown as "10,000+" with a pointer to `--track-total-count`, Search's cap on counts. See [`total-from`](../COMMAND-GRAMMAR.md#total-from). |
| `cursor-from` | no | A top-level string holding the next page's cursor. When it is present, a note says `More results: repeat with --cursor <value>`, in both output modes, so the command should have a `--cursor` option. |
| `message` | no | The line printed on success by a command whose operation returns no body. Defaults to "Done.". Refused on an operation that returns a body, where it would never be shown, even when empty. |

### `examples`

A list of example invocations, each either the arguments after the command as a string, or a
mapping:

| Key | Required | What it does |
|---|---|---|
| `args` | yes | The arguments after the command, such as `--kind "osdu:wks:master-data--Well:*"`. |
| `skip` | no | Why the example cannot run anywhere, for instance because it names a record id that exists in one data partition only. |

Examples are shown in [the command reference](COMMANDS.md), not in `--help`.
`tools/smoke_test.py` runs every example that is not skipped against a live environment, and
counts an empty result as a failure. See [Smoke-testing the examples](DEVELOPMENT.md#smoke-testing-the-examples).

### `paging`

For an endpoint that answers one page at a time, beside a cursor endpoint that takes the same
request plus a cursor — Search's `POST /query` and `POST /query_with_cursor`. The command's
options build the request once. A `limit` above `page-size`, or `--all`, sends it to the cursor
endpoint instead, page after page, and joins the results under `output.root`; anything else
goes to the command's own endpoint as before. The endpoints named here count as accounted for,
so they are not excluded as well.

| Key | Required | What it does |
|---|---|---|
| `op` | yes | The cursor endpoint, as `{ method, path }`: a `POST` without path parameters, whose request body is one object, not an array. |
| `release` | no | A `DELETE` with the cursor as its one path parameter, called when the command stops before the cursor's end — at its limit, or on an error or an interruption. |
| `limit` | yes | The top-level `int` body field, without `parts`, setting the number of results. Above `page-size` the command pages, and each page asks for at most `page-size`. |
| `page-size` | yes | The most one page may ask for: the service's own per-request maximum. |
| `cursor` | yes | The top-level request and response property holding the cursor. It cannot be a property a body field or fixed value writes to, including the paths a field's `parts` spread across. |
| `not-with` | no | Body fields the cursor endpoint does not take, such as `offset`, refused alongside paging. |

Paging needs a body built from `fields` and an `output.root`. The cursor endpoint's request
must take the `limit` field, the `cursor`, and every property the other body fields and fixed
values write to; the generator refuses one that does not, unless the field is listed in
`not-with` by its own name. The added `--all` cannot be combined with the `limit` field's
option.

## `handwritten`

Operations that deserve a command the manifest cannot express, such as one that takes a
directory of files and uploads them in batches.

| Key | Required | What it does |
|---|---|---|
| `op` | yes | The operation, as for [`op`](#op). |
| `command` | no | The command that covers it, or will. |
| `reason` | no | Why it cannot be generated. |

An entry only accounts for the operation in the coverage check; the generator does not check
that the command exists. Each service's `/info` operation is listed here because `osducs status`
calls it, while `storage add` is listed because the batch-upload command has not been written
yet. Hand-written commands live in `src/OsduCli/Commands/`, or extend a generated service
through the `Customize` hook in a partial class under `src/OsduCli/Commands/Handwritten/`.

## `exclude`

Operations deliberately left out of the CLI.

| Key | Required | What it does |
|---|---|---|
| `op` | yes | The operation, as for [`op`](#op). |
| `reason` | yes | Why it is left out. An exclusion without one is refused. |

An exclusion whose operation is no longer in the spec is refused as stale, and so is one of an
operation the spec marks `deprecated`, since those are excluded already.
