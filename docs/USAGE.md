# Using osducs

A guide to the parts you meet every day. The full command list is in
[COMMANDS.md](COMMANDS.md); `osducs <noun> --help` is the authority.

## Configuration

`osducs` reads the **Python CLI's profiles**, so if you already use `osducli` there is
nothing to set up:

```bash
osducs status
```

```
https://equinorswedev.energy.azure.com  partition dev
Service       Status  Version          Build
------------  ------  ---------------  ------------------------
crs-catalog   ok      0.29.2-SNAPSHOT  2026-08-05T09:49:35.371Z
storage       ok      0.29.4-SNAPSHOT  2026-08-05T19:04:12.267Z
```

That first line is deliberate: with a default profile in play, which environment you are
talking to is no longer visible on the command line.

### Where settings come from

Later sources win:

| Source | Notes |
|---|---|
| `~/.osducli/config` | the Python CLI's default profile |
| `~/.osdu/config.json` | this CLI's own file, if you have one |
| `~/.osducli/state` → `default_config` | the profile `osdu config update` selected |
| `OSDU_*` environment variables | `OSDU_SERVER`, `OSDU_DATA_PARTITION_ID`, `OSDU_AUTHORITY`, `OSDU_CLIENT_ID`, `OSDU_SCOPES` |
| `--config` | an explicit profile name or file path |

Selecting a profile in the Python CLI moves both tools together — `osducs` reads that
selection rather than keeping a second one that could disagree.

```bash
osducs status -c prod          # a profile from ~/.osducli/
osducs status -c ./my.json     # a path; format detected from content, not extension
```

`OSDUCLI_CONFIG_DIR` relocates the profile directory, the same variable the Python CLI reads.

Only five values are used: server, data partition, authority, client id and scopes. The
per-service `*_url` entries in a profile are **ignored** — this CLI derives each base path
from the service's own OpenAPI spec, and honouring the profile would double the version
segment. See [COMMAND-GRAMMAR.md](../COMMAND-GRAMMAR.md).

## Output

Table by default, JSON on request:

```bash
osducs record search --kind "osdu:wks:master-data--Well:*" --limit 5
osducs record search --kind "osdu:wks:master-data--Well:*" --limit 5 -o json | jq '.[].id'
```

Which columns appear is chosen per command, and for search you choose them yourself:

```bash
osducs record search --kind "osdu:wks:master-data--Well:*" -f id -f data.FacilityName
```

```
Id                                                      FacilityName
------------------------------------------------------  ----------------
dev:master-data--Well:86cade7a137a4a68b334145844d7eed0  FR SOULTZ (4616)
```

`--returned-fields` is server-side, so it also cuts what crosses the wire. Column headers are
the last dotted segment; values that are not scalars print as compact JSON.

## Finding things

The Search service is the way in. Storage's kind-scoped query needs entitlements many users
do not have.

```bash
# what kinds exist, and how many of each
osducs record aggregate --kind "osdu:wks:*:*" --by kind

# wells whose name starts GB
osducs record search --kind "osdu:wks:master-data--Well:*" --query 'data.FacilityName:GB*'

# an exact phrase
osducs record search --kind "osdu:wks:master-data--Well:*" --query 'data.FacilityName:"GB 211/23-A8"'

# newest first
osducs record search --kind "osdu:wks:master-data--Well:*" --sort-by id --sort-order DESC

# how many really match — without this the count stops at 10000
osducs record search --kind "osdu:wks:master-data--Well:*" --limit 1 --track-total-count
```

Wildcards work per segment, and `…--Well:*` is better than pinning a version: a kind usually
has records against several schema versions, and pinning one silently misses the rest.

### Counting

`record aggregate` answers "how many of each", which `search` cannot:

```bash
osducs record aggregate --kind "osdu:wks:master-data--*:*" --by legal.legaltags
osducs record aggregate --kind "osdu:wks:master-data--Well:*" --by acl.owners
```

Only keyword-indexed fields can be aggregated. On ADME dev that means envelope fields —
`kind`, `type`, `namespace`, `authority`, `source`, `legal.legaltags`, `acl.owners`,
`acl.viewers`, `createUser` — and **not** `data.*`.

### By location

```bash
osducs record search --kind "osdu:wks:master-data--Well:*" \
    --spatial-field data.SpatialLocation.Wgs84Coordinates \
    --near 48.935251,7.865344 --within 50000
```

`--bbox TOPLAT,TOPLON,BOTTOMLAT,BOTTOMLON` is the rectangle form. Note top-left first, so the
latitudes descend.

## Reading one record

```bash
osducs record get --id "dev:master-data--Well:86cade7a137a4a68b334145844d7eed0"
osducs record version list --id "…"
osducs record headers --id "…" -a kind -a acl      # headers only, no data payload
```

Record ids are scoped to a data partition, so an id from one environment is meaningless in
another. Take them from `record search`.

## Shell completion

```bash
echo 'eval "$(osducs completion zsh)"' >> ~/.zshrc
```

`bash`, `fish` and `powershell` are also supported; each script carries its own install line.
Completion covers nouns, verbs and enum values, and needs no network or token — Tab never
authenticates.

## When something looks wrong

`--debug` prints the request and response, which is usually enough to tell a CLI problem from
a service one:

```bash
osducs record get --id "…" --debug
```

See [TROUBLESHOOTING.md](TROUBLESHOOTING.md) for the failure modes we have actually hit.
