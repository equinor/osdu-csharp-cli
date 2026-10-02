# Maintainer guide

For anyone changing, releasing or looking after `osducs`. [CONTRIBUTING.md](../CONTRIBUTING.md)
covers the everyday loop: setting up, the checks to run before pushing, and how commit messages
drive releases. This page covers how the pieces fit together, and the tasks that come up less
often.

## How the code fits together

Most commands are generated. Two inputs go in, and committed C# comes out:

```
spec-source.yaml ──► tools/fetch_specs.py ──► openapi_specs/     what each service can do
cli-manifest/*.yaml                                              what the CLI exposes, and how
                          │
                          ├── tools/generate_cli.py  ──► src/OsduCli/Commands/Generated/*.g.cs
                          └── tools/generate_docs.py ──► docs/COMMANDS.md
```

- **Specs.** Read from the client library's release, pinned in
  [`spec-source.yaml`](../spec-source.yaml) to the version the CLI builds against, so the
  coverage check sees the same operations the client has methods for. Fetched into a gitignored
  `openapi_specs/`. See [Where the specs come from](../README.md#where-the-specs-come-from).
- **Manifests.** One per service. Every key is listed in [MANIFEST.md](MANIFEST.md), and the
  reasoning behind the rules is in [COMMAND-GRAMMAR.md](../COMMAND-GRAMMAR.md).
- **Generated code.** Committed, so building needs no Python. CI regenerates it on every pull
  request and fails if the result differs from what is committed.

### The client library

Every generated command calls [`Equinor.OsduCsharpClient`](https://github.com/equinor/osdu-csharp-client),
which is generated from the same specs with Kiota. Its `OsduClient` has one property per service,
which is what a manifest's `client:` names. Sign-in comes from the separate
`Equinor.OsduCsharpClient.Msal` package. The CLI sends no HTTP requests of its own, with one
exception: `status` probes CRS Conversion's `/info` directly, because that service's spec does
not declare it.

Both packages come from GitHub Packages, which needs a token even though they are public. See
[CONTRIBUTING.md](../CONTRIBUTING.md#prerequisites).

### `src/OsduCli`

| File | Role |
|---|---|
| `Program.cs` | Builds the command tree, adds the hand-written commands and runs the parse. |
| `Commands/Generated/` | One class per manifest, plus `GeneratedCommands.g.cs`, which assembles them into one tree. Never edited by hand. |
| `Commands/StatusCommand.cs` | `osducs status`: probes every service. |
| `Commands/AccountCommand.cs` | `osducs account list`: the accounts signed in on this machine. |
| `Commands/ConfigCommand.cs` | `osducs config`: create, list, select and show profiles. |
| `Commands/CompletionCommand.cs` | `osducs completion`: shell completion scripts, and the hidden command they call. |
| `Runtime/CommandTree.cs` | Joins commands from several manifests under one noun, such as `crs`. |
| `Runtime/CliRunner.cs` | Runs every command: builds the context, and turns failures into one-line errors and exit codes. |
| `Runtime/CliContext.cs` | Per-command state: the loaded configuration, the signed-in `OsduClient` and the output writer. |
| `Runtime/CliConfig.cs` | Finds and loads the configuration: osducs's JSON profiles, the Python CLI's profiles, the selection and environment variables. |
| `Runtime/OsduCliIniConfiguration.cs` | Reads a Python CLI profile. |
| `Runtime/SignIn.cs` | How a profile signs in: through a browser, or as an application with a client secret. |
| `Runtime/AccountScopedTokenProvider.cs` | Refuses to guess when more than one account is signed in and none was chosen. |
| `Runtime/TextBodyParseNodeFactory.cs` | Reads an HTML or plain-text error page, so it becomes a one-line error rather than a crash. |
| `Runtime/OutputWriter.cs`, `OutputSpec.cs`, `OsduJson.cs` | Turn a response into a table or JSON. |
| `Runtime/CliHelp.cs` | Renders `--help`; the library's own help cannot be customised. |
| `Runtime/GlobalOptions.cs` | `--output`, `--config`, `--user`, `--debug`: the options every command has. |
| `Runtime/EnumOptions.cs` | Accepts enum values in any casing and sends the spec's spelling. |
| `Runtime/CommandSuggestions.cs` | Explains a command that does not exist, and suggests the one meant: the same words in another order, or a typo away. |

Behaviour a manifest cannot express can also go in a partial class that extends a generated
service through its `Customize` hook, in `Commands/Handwritten/`. That folder does not exist yet,
because no service has needed one.

### What happens when a command runs

1. `Program.cs` parses the command line. A parse error, a missing required option or a wrong enum
   value stops here, before any configuration is read or any network call is made.
2. `CliRunner` builds a `CliContext`. `CliConfig` loads the configuration, and the profile's
   authentication mode picks the MSAL token provider: the interactive one, with the sign-in
   cache at `~/.osdu/msal_cache.bin` or `OSDU_MSAL_CACHE_PATH`, or for `msal_non_interactive`
   the client-credentials one, with the profile's client secret.
3. The generated code builds the request from the options and calls the client. The first call
   signs in: silently from the cache, by opening a browser, or with the client secret.
4. `OsduJson` turns the response into JSON, and `OutputWriter` prints it as the manifest's
   `output:` says.
5. `CliRunner` catches what can go wrong: a service error becomes one line naming the status and
   the service's message, and a 403 also names the roles the spec says the operation needs. An
   error page in HTML or plain text gives its title as the message, and a sign-in Entra ID
   refuses gives its `AADSTS` code.
   `--debug` shows each request and response, with the `Authorization` header redacted.

## Tests

| Where | What | How to run |
|---|---|---|
| `tests/generator/` | The generator: every rule it enforces, the C# it emits (against recorded goldens in `golden/`), coverage, the spec pin, and that the manifest reference matches the generator. | `python3 -m pytest` |
| `tests/OsduCli.Tests/` | The CLI: configuration, help, output, completion, errors, and the real generated command tree. | `dotnet test` |
| `tools/smoke_test.py` | Every manifest example, run against a live environment. Not in CI, since it needs a sign-in. | `python3 tools/smoke_test.py -c dev` |

Two things to know about the C# tests:

- **Configuration tests share process-wide state.** Environment variables and the default
  directories are global, so tests that touch them belong to `EnvironmentCollection`, which does
  not run in parallel. Inherit from `ConfigTestDirectories` to get private config directories
  and a clean environment; otherwise a test reads the profiles of whoever runs it.
- **Some tests skip themselves** where the behaviour depends on the platform. The test for a
  case-insensitive file system runs only on macOS and Windows, so CI, which runs on Linux, skips
  it; the test using Unix file permissions skips on Windows.

## Common tasks

### Changing or adding a command

Edit the manifest, regenerate and commit the result; see
[CONTRIBUTING.md](../CONTRIBUTING.md#changing-a-command) and [MANIFEST.md](MANIFEST.md).
Behaviour a manifest cannot express goes in a hand-written command, or in a `Customize` partial
class as described [above](#srcosducli).

### Upgrading the client library

1. Bump both `Equinor.OsduCsharpClient` and `Equinor.OsduCsharpClient.Msal` in
   `src/OsduCli/OsduCli.csproj`. A test fails if they differ.
2. Set `ref` in `spec-source.yaml` to the matching client tag, such as `v2.3.0`. A test fails
   if it does not match the package version.
3. Run `python3 tools/fetch_specs.py`. The generator refuses to run against specs fetched for a
   different version, so this is not optional.
4. Run `python3 tools/generate_cli.py --check`. Endpoints added, removed or renamed upstream
   show up here by name. Map each new one to a command, list it as hand-written, or exclude it
   with a reason.
5. Regenerate, then build and test as [CONTRIBUTING.md](../CONTRIBUTING.md#build-and-test)
   describes.

### Adding a service

1. The client library must support it first: the spec is added and the client released there.
   Then upgrade to that release as above.
2. Add `cli-manifest/<service>.yaml`. For a large service, a `scope:` lets you adopt part of it
   and widen it later.
3. Add a probe for it to the `Services` table in `Commands/StatusCommand.cs`. A test fails if a
   manifest's service has no probe, so `status` cannot quietly stop covering a service.
4. If its nouns belong under their own heading in `osducs --help`, give the manifest a
   `section:`.

### When a spec is wrong

Fix it in the client library rather than working around it here. Its generator can patch a spec
before generating, and the fix can also be sent upstream to OSDU. The CLI reads the specs a
client release was generated from, so a workaround in a manifest would disagree with the client
it calls. `schema add` sending no schema was fixed this way, in client 2.2.1.

A spec can also run ahead of the deployed service. A command then fails with a 404 against an
environment that has not caught up, while the spec is right. See
[TROUBLESHOOTING.md](TROUBLESHOOTING.md).

### Trying a change against a live environment

- `dotnet run --project src/OsduCli -- <command>` runs the working copy, and `-c <profile>` picks
  the environment. `--debug` shows exactly what was sent.
- **On macOS, allow the Keychain prompt.** The sign-in cache is protected by the Keychain, and a
  locally built binary is a different program each time it is rebuilt, so macOS asks whether it
  may read the cache. Until someone answers, the command waits.
- **A pull request whose checks pass gets binaries** for all three platforms, kept for 14
  days. They are built only when both Check manifests and Build and test succeed, so a pull
  request that fails either has none.
  [PEER-TEST.md](PEER-TEST.md#testing-a-change-that-is-not-released-yet) shows how to download
  one, so a reviewer can run the change without building it.

## Releasing

[release-please](https://github.com/googleapis/release-please) does the work:

1. Merging a `fix:`, `feat:`, `deps:` or `revert:` change makes it open, or update, a pull request titled
   `chore: release <version>`, with the changelog and the version bump in
   `src/OsduCli/OsduCli.csproj`.
2. Merging that pull request tags the release and creates it on GitHub. The tests run again, and
   one job per platform builds a self-contained binary and attaches it, with a `.sha256` beside
   each archive.
3. Check the result:

   ```bash
   curl -LO https://github.com/equinor/osdu-csharp-cli/releases/latest/download/osducs-osx-arm64.tar.gz
   curl -LO https://github.com/equinor/osdu-csharp-cli/releases/latest/download/osducs-osx-arm64.tar.gz.sha256
   tar -xzf osducs-osx-arm64.tar.gz
   shasum -a 256 -c osducs-osx-arm64.tar.gz.sha256    # checks the archive and the binary
   ./osducs --version
   ```

   On Linux, use `sha256sum -c` and the `linux-x64` archive.

If a platform's build fails after the release was created, running the Release workflow by hand
builds and attaches the binaries to the latest release again.

Changes with any other commit type, such as `docs:`, `ci:` or `chore:`, do not lead to a
release.

## CI and repository settings

| Workflow | Runs on | What it does |
|---|---|---|
| Run Tests | pull requests, and called by Release | **Check manifests**: fetches the specs, runs the generator's tests and checks, and fails if the generated code or `docs/COMMANDS.md` is out of date. **Build and test**: builds and runs the C# tests in Release. **Preview**, on pull requests only and once both have passed: builds the three binaries for reviewers. |
| Release | pushes to `main`, and by hand | On a push, runs release-please, and when that creates a release, runs Run Tests and publishes the binaries. Run by hand, it skips release-please and runs the tests and publishing for the latest release. |
| Code scanning | pull requests, pushes to `main`, weekly | Checks the workflow files for security problems with zizmor. GitHub's CodeQL default setup runs alongside it. |
| Lint Pull Request | pull requests | Checks the title against [`.commitlintrc.yml`](../.commitlintrc.yml). |

- **Dependabot** updates the GitHub Actions weekly. It does not update NuGet packages: the
  project's package sources include GitHub Packages, which needs a token Dependabot does not
  have. Update them by hand, the client library as described above.
- **Pull requests are squash-merged only**, so the title becomes the commit on `main` and the
  release notes.
- **`main` is protected** by a ruleset matching the client library's: one approving review, from
  a code owner (`@equinor/well-logs`), and no force pushes. Repository admins can bypass it when
  merging a pull request, and GitHub records each bypass.
- **Actions from other repositories are pinned to commit hashes**, with the version in a
  comment. Keep them pinned when editing a workflow.

## Known limitations

- **The binaries are not signed.** macOS quarantines a browser download, Windows SmartScreen
  warns, and whether managed Windows laptops block the binary outright is not yet known.
  Equinor's internal code-signing service is the likely route for Windows; macOS needs Apple
  notarisation.
- **NativeAOT does not work yet.** It would make the binary about 8 MB instead of about 80 MB, but
  it is blocked by the client library's public API; the reasons are in
  `.github/workflows/release.yml`.
- **Not ported from the Python CLI:** `dataload` and the `wbdutil` commands. Wellbore DDMS bulk
  writes are deliberately not exposed.
- **Most write commands have seen little use** against a live environment.
- **Pull requests from forks are untested.** CI restores the client library from GitHub
  Packages, which works for branches in this repository but has not been tried from a fork.
