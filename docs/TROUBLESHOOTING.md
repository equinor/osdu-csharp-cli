# Troubleshooting

Failure modes actually encountered against a live ADME instance, and how to tell which are
the CLI's fault. Most are not.

Start with `--debug`, which prints the request and the response body:

```bash
osducs record get --id "…" --debug
```

## The command runs but prints nothing

Fixed in the client from **1.1.9**. Below that, `record get` and `record version get` return
`200` with a full record and display an empty line.

Storage documents those responses as `application/json` with `schema: {type: string}` —
Spring's `ResponseEntity<String>` leaking into the spec. Kiota generates `Task<string?>`,
cannot turn a JSON object into a string, and returns `null`. A successful call, real data on
the wire, no output.

Check with `--debug`: a `← 200` and a populated `body=` with nothing printed is this.

## `403 The user is not authorized to perform this action`

Entitlements, not the CLI. Common on `record list`, which uses Storage's kind-scoped query.

**Use `record search` instead** — same question, answered through the Search service, which
most users can reach.

## `401 Not authorized`

Also entitlements. `group member list` and `group member count` need rights on the
Entitlements service that a normal user account does not have.

## `404 No static resource …`

The endpoint is not deployed on your instance. That is Spring's message for "no controller
handles this path", and it means the CLI is ahead of the platform, not broken.

`record headers` is the current example: it exists upstream and **arrives with M27**.

The specs this CLI generates from track OSDU upstream; ADME lags. Check what is actually
running before assuming a bug:

```bash
osducs status
```

## `500 … not implemented`

Same family. `GET /records` answers `IRecordsMetadataRepository.getRecords not implemented`
on ADME — present in the spec, absent from the deployment.

## `400 Aggregations are not supported for one or more of the specified fields`

Only keyword-indexed fields can be aggregated. On dev that means envelope fields — `kind`,
`type`, `namespace`, `authority`, `source`, `legal.legaltags`, `acl.owners`, `acl.viewers`,
`createUser` — and no `data.*` field.

## `400 The record '…' does not belong to account '…'`

Record ids carry their data partition. An id copied from another environment, or from
documentation using `opendes`, will not resolve against `dev`. Take ids from
`record search`.

## A search returns 0 and you expected results

Three usual causes:

1. **The field does not exist.** `data.Country` looks plausible and is on no OSDU kind.
   Check with `osducs record search --kind … --limit 1 -o json` and read the real field names.
2. **The kind version is pinned.** `…--Well:1.0.0` may hold 21 records where `…--Well:*`
   holds 141,286. Prefer the wildcard.
3. **Quoting.** `data.FacilityName:GB*` is a prefix; `data.FacilityName:"GB 211/23-A8"` is an
   exact phrase. Shell quoting matters — use single quotes around the whole query.

## The count says exactly 10,000

That is the cap, not the answer. `osducs` prints it as `10,000+ …`; add
`--track-total-count` for the true figure.

## macOS refuses to run the binary

The download is quarantined and the binary is not yet notarized:

```bash
xattr -d com.apple.quarantine ./osducs
```

## Windows blocks or warns

The binary is unsigned, so Windows has no reputation for it. Two different things can happen,
and they are worth telling apart.

**SmartScreen** shows *"Windows protected your PC"* with only a **Don't run** button. Click
**More info**, then **Run anyway**. Or clear the downloaded-from-internet mark first and avoid
the dialog:

```powershell
Unblock-File .\osducs.exe
```

**WDAC** — application control on a managed laptop — blocks with no way through, and no
"Run anyway" appears. That is a policy decision the CLI cannot work around; raise it with your
Windows team, and tell us, because it changes how the tool has to be distributed.

Both go away with a signed binary, which is the intended fix rather than asking every user to
click through a warning.

## `osdu` runs the wrong tool

It does not: this CLI is `osducs`. The name was chosen so both can sit on `PATH` while the
Python `osducli` is still in use.
