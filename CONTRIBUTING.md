# Contributing

Most of `osducs` is generated. The OpenAPI specs say what each OSDU service can do, the
manifests in [`cli-manifest/`](cli-manifest/) say what the CLI exposes and how, and
`tools/generate_cli.py` joins the two into C#. The [README](README.md) explains why it is built
this way, and [COMMAND-GRAMMAR.md](COMMAND-GRAMMAR.md) holds the rules every command follows.

## Prerequisites

- **.NET 10 SDK.**
- **Python 3** with the generator's dependencies: `pip install -r requirements-dev.txt`.
- **Read access to GitHub Packages.** The CLI is built on
  [`Equinor.OsduCsharpClient`](https://github.com/equinor/osdu-csharp-client), which is
  published there. GitHub Packages asks for a token even for public packages, so restore fails
  without one. Put the credentials in your user-level NuGet config, under the same source name
  as this repo's [`nuget.config`](nuget.config), so no token ever ends up in the repository:

  ```bash
  gh auth refresh --scopes read:packages
  dotnet nuget add source https://nuget.pkg.github.com/equinor/index.json \
    --name equinor-github \
    --username "$(gh api user --jq .login)" \
    --password "$(gh auth token)" \
    --store-password-in-clear-text \
    --configfile ~/.nuget/NuGet/NuGet.Config
  ```

  On Windows the user-level file is `%AppData%\NuGet\NuGet.Config`. A classic personal access
  token with `read:packages` works in place of `gh auth token`.

## Build and test

Before pushing, with your changes committed, run the same checks CI runs on a pull request, in
the same configuration:

```bash
python3 tools/fetch_specs.py              # the specs, pinned to the client version in use
python3 -m pytest                         # the generator's own tests
python3 tools/generate_cli.py --check     # every in-scope endpoint is mapped or excluded
python3 tools/generate_docs.py --check    # docs/COMMANDS.md is up to date
python3 tools/generate_cli.py && git diff --exit-code -- src/OsduCli/Commands/Generated
dotnet build OsduCli.slnx --configuration Release --no-incremental
dotnet test tests/OsduCli.Tests/OsduCli.Tests.csproj --configuration Release --no-build
```

The fifth line fails if regenerating changes the committed code, which is how CI catches a
manifest change committed without its generated output. It compares against what is committed,
so run it after committing; if it fails, the regenerated files are what belongs in the next
commit.

CI builds and tests in Release, so do the same: a check that passes only in Debug has not passed.
It also builds a preview binary for each platform and checks the pull request title, neither of
which needs a local step.

Keep the build free of warnings, though CI does not enforce it. CI starts from a clean checkout,
so it sees every warning. Locally, `--no-incremental` gets the same result, because an
incremental build does not repeat warnings from files it did not recompile.

## Changing a command

- **Edit the manifest, then regenerate.** Run `python3 tools/generate_cli.py` and
  `python3 tools/generate_docs.py`, and commit the regenerated `src/OsduCli/Commands/Generated/`
  and `docs/COMMANDS.md` with the manifest change. CI fails if they are out of date.
- **Never edit the generated `.g.cs` files.** Behaviour the manifest cannot express belongs in
  a partial class under `src/OsduCli/Commands/Handwritten/`, through the `Customize` hook each
  generated class declares.
- **Every endpoint in a manifest's scope must be accounted for**: mapped to a command, listed as
  hand-written, or excluded with a reason. A new upstream endpoint fails the manifest check
  until it is.
- **A deliberate change to generated output may change a golden file** in
  `tests/generator/golden/`. Re-record them with `UPDATE_GOLDEN=1 python3 -m pytest`, and
  review the diff before committing.

## Commits and releases

Pull request titles follow [Conventional Commits](https://www.conventionalcommits.org/), and a
check fails any that do not, using the same rules as the other OSDU libraries
([`.commitlintrc.yml`](.commitlintrc.yml)). Pull requests are squash-merged, so the title becomes
the commit on `main`. [release-please](https://github.com/googleapis/release-please) reads those
commits to choose the next version and write the changelog:

- `fix:` for a bug fix, released as a patch version.
- `feat:` for new behaviour, released as a minor version.
- `deps:` for dependency updates, released as a patch version. Dependabot uses it.
- `!` (as in `feat!:`) only when users of `osducs` must change something to keep working. A
  change that is breaking somewhere else, such as in a dependency, is not a breaking change here.
- `revert:` to undo an earlier change. It appears in the changelog and triggers a release.
- `docs:`, `chore:`, `refactor:`, `style:` and `ci:` for everything else. These are left out of
  the changelog and do not trigger a release.

Bumping `Equinor.OsduCsharpClient` also means bumping `ref` in
[`spec-source.yaml`](spec-source.yaml) to the matching client tag, in the same pull request. A
test fails if the two disagree, since the coverage check has to see the specs that client was
generated from.

## Security

To report a vulnerability, follow [SECURITY.md](SECURITY.md). Which channel to use depends on
how serious it is.
