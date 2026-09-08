# Trying osducs — a 20-minute test round

`osducs` is a proof of concept: an OSDU command line whose commands are **generated from the
OSDU OpenAPI specs**, rather than hand-written like the Python `osducli`. The question it is
meant to answer is whether that approach produces a CLI people actually want to use, and
whether it can be packaged for a managed Windows laptop — which is where the Python one
struggles.

You are being asked to use it for twenty minutes and say what is wrong with it. Rough edges
are expected and useful; **131 of its commands were generated from a spec and most have never
been run by a human.**

It does not replace `osducli`. The binary is called `osducs` precisely so both can sit on your
`PATH` while you decide.

---

## 1. Install (2 minutes)

The repository is internal, so the download needs to be authenticated. A plain `curl` of the
asset URL returns 404.

**macOS (Apple silicon)**

```bash
gh release download --repo equinor/osdu-csharp-cli --pattern "osducs-osx-arm64.tar.gz"
tar -xzf osducs-osx-arm64.tar.gz && chmod +x osducs
xattr -d com.apple.quarantine ./osducs     # not yet notarized
sudo mv osducs /usr/local/bin/
```

**Windows**

```powershell
gh release download --repo equinor/osdu-csharp-cli --pattern "osducs-win-x64.zip"
Expand-Archive osducs-win-x64.zip -DestinationPath .
```

**SmartScreen will stop it the first time**, with *"Windows protected your PC — prevented an
unrecognised app from starting"*. The dialog shows only a **Don't run** button; the way past it
is the **More info** link, which reveals **Run anyway**. That is expected: the binary is
unsigned, so Windows has no reputation for it.

Cleaner alternative, which avoids the dialog by clearing the downloaded-from-internet mark:

```powershell
Unblock-File .\osducs.exe
```

**If Run anyway does not appear, or the app is still blocked after clicking it, stop and tell
us.** That is the difference between SmartScreen — a warning anyone can click through — and
WDAC, an application-control policy that cannot be bypassed. Which of the two your laptop
enforces is the single most valuable thing this test round can establish; see §5.

**Linux** — `osducs-linux-x64.tar.gz`, same shape as macOS without the `xattr` line.

No browser? The [releases page](https://github.com/equinor/osdu-csharp-cli/releases/latest)
works if you are signed in to GitHub.

### Testing a change that is not released yet

Every pull request builds the same three binaries and attaches them to its CI run, so a
change can be tried before anyone commits to releasing it:

```bash
gh run download --repo equinor/osdu-csharp-cli --name osducs-win-x64 \
  $(gh run list --repo equinor/osdu-csharp-cli --branch <branch> \
      --workflow "Run Tests" --limit 1 --json databaseId --jq '.[0].databaseId')
```

Swap `osducs-win-x64` for `osducs-osx-arm64` or `osducs-linux-x64`. They are kept for 14
days, and are built exactly the way release assets are — same flags, same archive, same
runner per platform — so what you test is what would ship. The GitHub UI works too: open the
PR's checks, click the **Run Tests** run, and the artifacts are at the bottom.

## 2. First run (1 minute)

**If you already use `osducli`, there is nothing to configure.** `osducs` reads the same
profiles from `~/.osducli/` and follows whichever one you have selected.

```bash
osducs status
```

```
https://equinorswedev.energy.azure.com  partition dev
Service         Status  Version          Build
--------------  ------  ---------------  ------------------------
crs-catalog     ok      0.29.2-SNAPSHOT  2026-08-05T09:49:35.371Z
crs-conversion  ok      0.29.2-SNAPSHOT  2026-08-05T09:49:28.770Z
storage         ok      0.29.4-SNAPSHOT  2026-08-05T19:04:12.267Z
wellbore-ddms   ok      0.29
...            (12 services in all)
```

Use `-c <profile>` to point at another, e.g. `osducs status -c test`.

If you do not use `osducli`, see [USAGE.md](USAGE.md#configuration).

## 3. Things to try (15 minutes)

Please stay on a **development partition**. Everything below is read-only.

```bash
# What is in this partition, and how much of it
osducs record aggregate --kind "osdu:wks:*:*" --by kind

# Find something
osducs record search --kind "osdu:wks:master-data--Well:*" --limit 5
osducs record search --kind "osdu:wks:master-data--Well:*" --query 'data.FacilityName:GB*'

# Choose your own columns
osducs record search --kind "osdu:wks:master-data--Well:*" --limit 5 \
    -f id -f data.FacilityName

# The real match count — without this it stops at 10000
osducs record search --kind "osdu:wks:master-data--Well:*" --limit 1 --track-total-count

# Read one record (take an id from a search above)
osducs record get --id "<id from above>"

# Governance
osducs legaltag list
osducs record aggregate --kind "osdu:wks:master-data--*:*" --by legal.legaltags

# Discoverability — does the help tell you what you need?
osducs --help
osducs record --help
osducs record search --help
```

Then **go off-script**. Look for the thing you would actually do in a normal week and see
whether you can work out how to do it from `--help` alone. That is the part we most want
tested.

Tab completion, if you want it:

```bash
echo 'eval "$(osducs completion zsh)"' >> ~/.zshrc     # bash, fish, powershell also work
```

## 4. Please avoid, for now

**Write commands are untested.** Every `add`, `update`, `delete`, `upload` and `trigger` —
32 commands — was generated from a spec and has never been run against anything. They may
work; they may also do something you did not intend. Do not point them at data you care
about. If you want to exercise them, tell us and we will find a scratch partition.

## 5. What to report

Most useful, in order:

1. **A command that ran but told you nothing useful** — empty columns, the wrong fields, a
   table where you wanted the detail. Output columns for 131 commands were chosen by reading
   spec field names, and many have never met a real response. Each is a one-line fix.
2. **Help that did not answer your question.** If you could not work out what a flag wanted,
   that is a defect.
3. **A command you expected to exist and could not find**, or one whose name you would have
   guessed differently. Commands are named for the *resource* (`osducs record search`, not
   `osducs search`), and whether that reads naturally is exactly what is in question.
4. **Windows: whether it runs at all.** WDAC or AppLocker blocking an unsigned executable is
   the finding that decides whether this approach is viable, and none of us can test it from
   a Mac.

Include the command you ran. `--debug` prints the request and response if you want to attach
more:

```bash
osducs record get --id "…" --debug
```

Raise it wherever suits — an issue on
[equinor/osdu-csharp-cli](https://github.com/equinor/osdu-csharp-cli/issues) keeps it with
the code.

## 6. Known, please do not report

| You see | Why |
|---|---|
| `record list` → `403 not authorized` | Storage's kind-scoped query needs entitlements most people lack. **Use `record search`.** |
| `group member list` → `401` | Same, on Entitlements. |
| `record headers` → `404 No static resource` | The endpoint is real but **arrives with M27**. The CLI is ahead of ADME. |
| `GET /records` → `500 not implemented` | In the spec, absent from ADME. |
| `--by data.Something` → `400 Aggregations are not supported` | Only keyword-indexed fields aggregate; on dev that means envelope fields, not `data.*`. |
| Count shows exactly `10,000+` | That is the cap, not the answer. Add `--track-total-count`. |
| `wellbore get` / `trajectory get` → `422` | The **stored record** carries a property its schema rejects (`'WellboreIdentity' was unexpected`). A data problem on the service, not the CLI. |
| `well get` / `markerset get` → `404` while `well version list` works | The DDMS strips the version suffix from the id and looks up the base id. Server-side; the CLI sends the id you gave it. |
| `welllog data get` → `404 bulk for record ... not found` | The record exists but no curves were ingested for it. Most WellLogs on dev have no bulk. Try `--describe` across a few ids to find one that does. |
| macOS quarantine, Windows SmartScreen warning | Not signed yet. Click **More info → Run anyway**, or `Unblock-File`. Being decided. |

More detail in [TROUBLESHOOTING.md](TROUBLESHOOTING.md).

---

**Reference:** [USAGE.md](USAGE.md) for the everyday guide, [COMMANDS.md](COMMANDS.md) for
every command and flag.
