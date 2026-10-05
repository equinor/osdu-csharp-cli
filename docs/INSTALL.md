# Installing osducs

Download the archive for your platform from the [latest release][releases]. No account is
needed. Each is around 30 MB compressed, and nothing else has to be installed, since the .NET
runtime is inside the binary.

[releases]: https://github.com/equinor/osdu-csharp-cli/releases/latest

## macOS and Linux

macOS (Apple silicon):

```bash
curl -LO https://github.com/equinor/osdu-csharp-cli/releases/latest/download/osducs-osx-arm64.tar.gz
tar -xzf osducs-osx-arm64.tar.gz && chmod +x osducs
```

Linux: the same, with `osducs-linux-x64.tar.gz`.

To run it as `osducs` from anywhere, move it somewhere already on `PATH`, such as
`~/.local/bin/osducs`.

Downloaded through a browser instead? macOS marks the file as quarantined, and since the binary
is not yet signed and notarized, it will not run until the mark is cleared:

```bash
xattr -d com.apple.quarantine ./osducs
```

`curl` does not set that flag, so after the commands above `xattr` would only report
`No such xattr`.

## Windows

```powershell
curl.exe -LO https://github.com/equinor/osdu-csharp-cli/releases/latest/download/osducs-win-x64.zip
Expand-Archive osducs-win-x64.zip -DestinationPath .
.\osducs --version           # note the .\ — see below
```

A browser download carries a `Zone.Identifier` stream — the Mark of the Web — which is what
SmartScreen reacts to. Clearing it needs no administrator:

```powershell
Unblock-File .\osducs.exe    # or tick Unblock in the file's Properties dialog
```

Unblocking the `.zip` before extracting saves doing it per file. The mark belongs to the
downloaded file, so a new version downloaded through a browser needs unblocking again.

The leading `.\` is not optional. PowerShell does not run programs from the current directory,
so a bare `osducs` reports `CommandNotFoundException` even though the file is right there. That
is PowerShell's rule about the current directory, not anything about this binary or about it
being unsigned.

To use it as `osducs` from anywhere, put its directory on your user PATH — no administrator,
and it applies to shells opened afterwards:

```powershell
$dir  = "C:\Appl\osducs-win-x64"
$user = [Environment]::GetEnvironmentVariable("Path", "User")
# Whole entries, not a substring match — "*$dir*" would also match ...\osducs-win-x64-old
$have = @("$user" -split ';' | ForEach-Object { $_.TrimEnd('\') })
if ($have -notcontains $dir.TrimEnd('\')) {
    [Environment]::SetEnvironmentVariable("Path", "$user;$dir".Trim(';'), "User")
}
```

## Checking a download against the release

Every asset ships a `.sha256` beside it, at the same URL with `.sha256` added, holding two
lines: the archive's hash and the hash of the binary inside it. The second is the one Windows
tooling reports, and the one to compare after extracting:

```powershell
(Get-FileHash .\osducs.exe -Algorithm SHA256).Hash
```

```bash
shasum -a 256 ./osducs        # macOS
sha256sum ./osducs            # Linux
```

That catches a truncated download, a corrupted extract, or the wrong version — the file not
matching the release entry. It is not proof of origin: the checksum is published beside the
asset, so whoever could replace one could replace the other. Establishing origin needs a
signature, and the binaries are not signed yet.

## Alongside the Python CLI

The command is `osducs`, not `osdu`, so it sits alongside the Python
[`osducli`](https://community.opengroup.org/osdu/platform/data-flow/data-loading/osdu-cli)
rather than replacing it, and reads that tool's profiles without changing them. See
[USAGE.md](USAGE.md#configuration).

## Shell completion

```bash
echo 'eval "$(osducs completion zsh)"' >> ~/.zshrc
```

`bash`, `fish` and `powershell` are also supported; each script carries its own install line.
See [USAGE.md](USAGE.md#shell-completion).
