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
https://<instance>.energy.azure.com  partition dev
Service         Status  Version          Build
--------------  ------  ---------------  ------------------------
crs-catalog     ok      0.29.2-SNAPSHOT  2026-08-05T09:49:35.371Z
crs-conversion  ok      0.29.2-SNAPSHOT  2026-08-05T09:49:28.770Z
storage         ok      0.29.4-SNAPSHOT  2026-08-05T19:04:12.267Z
wellbore-ddms   ok      0.29
...            (12 services in all)
```

That first line is deliberate: with a default profile in play, which environment you are
talking to is no longer visible on the command line.

### Choosing the environment

```bash
osducs config add test      # create a profile, asking for any setting not given as a flag
osducs config list          # every profile, with its source, server and partition
osducs config use test      # make one the default for every later command
osducs config show          # what is in effect now, and which files were read
```

osducs keeps its own files in `~/.osdu/`: each profile as `<name>.json`, and the selection in
`state.json`. It reads the Python CLI's profiles in `~/.osducli/` but **never writes there** —
migration runs from that tool to this one, and neither should quietly change the other.

Until you run `osducs config use`, osducs follows whatever profile the Python CLI has
selected, so an existing user starts in the environment they already work in. After that the
two are independent: `config use` records osducs's choice only, and says so.

A profile is validated before it is selected — a config that does not parse is refused at the
moment you choose it, rather than breaking the next command you run.

### Creating and migrating profiles

`osducs config add <profile>` writes `~/.osdu/<profile>.json`. In a terminal it asks for each
required setting that was not given; in a script, a missing one is an error naming its flag.

| Flag | Setting |
|---|---|
| `--server` | OSDU base URL |
| `--partition` | data partition ID |
| `--authority` | Entra ID authority, `https://login.microsoftonline.com/<tenant-id>` |
| `--client-id` | the app registration to sign in through — for a browser sign-in it must allow public-client sign-in with redirect URI `http://localhost` |
| `--scopes` | OAuth scopes, space-separated |
| `--authentication-mode` | `msal_interactive`, as you through a browser (the default), or `msal_non_interactive`, as the application with a client secret — see [Signing in as an application](#signing-in-as-an-application) |
| `--user` | optional default account |
| `--from <profile>` | copy every setting from an existing profile; flags override what is copied |
| `--force` | replace a profile of the same name |

`--from` is also the migration: `osducs config add dev --from dev` copies the Python profile
`dev` into `~/.osdu/dev.json`, which then wins wherever `dev` is read — including when it is
the profile the Python CLI has selected. The Python profile is left in place, and
`config list` shows it as overridden. Keys osducs does not use, such as the `*_url` entries
and the ACL defaults, are named rather than copied. A profile's `authentication_mode` and
`client_secret` are copied; one with a mode osducs does not have, such as `refresh_token`, is
refused until `--authentication-mode` chooses one it does.

A profile created on a machine with no other configuration — no profile osducs would read by
default, no selection that still exists, and no `OSDU_*` variables — is selected. That makes it
osducs's own selection, so if the only selection was a Python CLI one naming a deleted profile,
osducs stops following that tool from then on. Otherwise `config add` never
changes which environment you are on.

A JSON profile looks like this:

```json
{
  "Osdu": {
    "Server": "https://<instance>.energy.azure.com",
    "DataPartitionId": "dev",
    "Authority": "https://login.microsoftonline.com/<tenant-id>",
    "ClientId": "<app-id>",
    "Scopes": "https://energy.azure.com/.default openid",
    "AuthenticationMode": "msal_interactive",
    "User": "name@equinor.com"
  }
}
```

`User` is optional, and so is `AuthenticationMode`: a profile without one signs in through a
browser. A profile that signs in as an application also has a `ClientSecret`. osducs writes
every profile readable only by you. It has no export back to the Python format: a profile
osducs writes holds only the settings osducs uses, and the Python CLI needs more than those.

`-c <profile>` still overrides the default for a single command.

### Where settings come from

Later sources win:

| Source | Notes |
|---|---|
| `~/.osducli/config` | the Python CLI's default profile |
| `~/.osdu/config.json` | this CLI's own default file, if you have one |
| the selected profile | from `~/.osdu/state.json` once `osducs config use` has been run, otherwise the profile the Python CLI's `~/.osducli/state` names |
| `OSDU_*` environment variables | `OSDU_SERVER`, `OSDU_DATA_PARTITION_ID`, `OSDU_AUTHORITY`, `OSDU_CLIENT_ID`, `OSDU_SCOPES`, `OSDU_USER`, `OSDU_AUTHENTICATION_MODE`, `OSDU_CLIENT_SECRET` |
| `--config` | an explicit profile name or file path |

A profile name is looked up in both directories, `~/.osducli/<name>` and then
`~/.osdu/<name>.json`, so an osducs profile wins over a Python profile of the same name.

```bash
osducs status -c prod          # a profile from ~/.osducli/
osducs status -c ./my.json     # a path; format detected from content, not extension
```

`OSDU_CONFIG_DIR` relocates osducs's own directory. `OSDUCLI_CONFIG_DIR` relocates the Python
CLI's, the same variable that tool reads.

The authentication mode and client secret are the exception to layering: they come from the
profile in effect, or the environment, and never from a file beneath it. A default `config`
file set to `msal_non_interactive` would otherwise turn a profile without a mode into an
application sign-in with that file's secret.

Only these values are used: server, data partition, authority, client id, scopes, user,
authentication mode and client secret. The per-service `*_url` entries in a profile are
**ignored** — this CLI derives each base path from the service's own OpenAPI spec, and
honouring the profile would double the version segment. See
[COMMAND-GRAMMAR.md](../COMMAND-GRAMMAR.md).

## Signing in as a particular account

If you have more than one account — a normal one and a separate privileged one is the usual
reason — say which you mean:

```bash
osducs record search -k "osdu:wks:master-data--Well:*" --user azure@equinor.com
```

To avoid typing it every time, put it in the config profile beside the environment it belongs
to — `"User"` in a JSON profile, or `user` in a Python one:

```json
{ "Osdu": { "User": "azure@equinor.com" } }
```

`osducs config add <profile> --user azure@equinor.com` writes it for you.

`--user` overrides it, and `OSDU_USER` works for a shell session.

The setting has to be in the profile that is **selected** — `osducs config show` lists which
files were actually read. A default written into a profile you are not using has no effect and
gives no sign of it.

To see what you are signed in as:

```bash
osducs account list
```

```
Account            In use
-----------------  ------
normal@equinor.com
azure@equinor.com  yes
```

Advisories from `account list` — nothing signed in, or a selected account that is not —
go to stderr, so `--output json` stays parseable while the remark still reaches you.

**With more than one account signed in and no choice made, commands stop and list them
rather than pick one.** That is deliberate. The accounts differ in what they can see and
change, and the difference is invisible in the output — a command that quietly ran as the
wrong identity looks exactly like one that ran as the right one.

The account you name does not have to be signed in yet. The browser will open on it, and if
you sign in as somebody else the command fails rather than use them.

## Signing in as an application

Some environments are reached as an application rather than a person — an admin app
registration, or a pipeline. The Python CLI calls this `authentication_mode =
msal_non_interactive`, and osducs reads its profiles that say so: the client secret in the
profile signs in, with no browser and no account.

To make an osducs profile of one, copy it or create it:

```bash
osducs config add test_admin --from test_admin
osducs config add ci --from dev --authentication-mode msal_non_interactive
```

The secret is copied with `--from`, or asked for without being shown. It is never an option,
which would leave it in your shell history. Leave the prompt blank, or run without a terminal,
and the profile is written without one; the secret then comes from `OSDU_CLIENT_SECRET` when
the profile is used, which is the way for a pipeline:

```bash
OSDU_CLIENT_SECRET="$SECRET_FROM_THE_VAULT" osducs -c ci record search --kind "…"
```

An application signs in with a single scope, its resource's `/.default`. Copying a browser
profile removes `openid` and the other scopes that ask about a person, and says so; scopes
naming more than one resource, or none, are refused until `--scopes` gives the one meant.
A default account copied from a browser profile is left behind for the same reason.

`osducs config show` says whether a secret is set, never what it is, and on Linux and macOS
points out a profile holding one that other users on the machine can read. osducs writes its
own profiles readable only by you — a file mode of 600, or on Windows an access list naming
only you — and the Python CLI restricts only the profiles it wrote.

An application has no accounts to choose between, so `--user` is refused with such a profile,
and `account list` says so rather than listing the browser sign-ins.

The Python CLI's other modes — `refresh_token`, and those named after a cloud provider — are
not supported. A profile using one is refused with that said, rather than signed in through a
browser instead.

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
