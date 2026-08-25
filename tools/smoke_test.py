#!/usr/bin/env python3
"""Run every documented example against a live OSDU instance.

Help examples are untested documentation and rot silently: `data.Country:"Norway"` sat in
`record search --help` for a while and matched nothing, because the field does not exist on
any OSDU kind. Nothing in CI can catch that — it needs a real service and a token — so this
is a pre-release check somebody runs by hand.

The examples live in the manifests, next to the command they belong to, so the string shown
in help and the string executed here are the same string.

Usage::

    python3 tools/smoke_test.py                  # every example, default profile
    python3 tools/smoke_test.py -c dev           # against a named profile
    python3 tools/smoke_test.py record           # only commands starting "record"
    python3 tools/smoke_test.py --binary ./osducs

Exit code is non-zero if any example fails, so it can gate a release.
"""

from __future__ import annotations

import argparse
import pathlib
import shlex
import subprocess
import sys
import time

import yaml

ROOT = pathlib.Path(__file__).resolve().parent.parent
MANIFEST_DIR = ROOT / "cli-manifest"


def examples() -> list[tuple[str, str, str | None]]:
    """Every (command, arguments, skip reason) triple declared across the manifests.

    An example is either a bare string, or a mapping with a ``skip:`` reason. Skipping is
    for examples that cannot run anywhere — a record id is scoped to a data partition, so
    any literal id is wrong on someone else's instance — and for endpoints an instance does
    not have. They still appear in help; they are simply not assertions about this run.
    """
    found = []
    for path in sorted(MANIFEST_DIR.glob("*.yaml")):
        manifest = yaml.safe_load(path.read_text(encoding="utf-8"))
        for command in manifest.get("commands") or []:
            for example in command.get("examples") or []:
                if isinstance(example, dict):
                    found.append((command["command"], str(example["args"]).strip(),
                                  example.get("skip")))
                else:
                    found.append((command["command"], str(example).strip(), None))
    return found


def run(binary: str, command: str, arguments: str, profile: str | None,
        timeout: float) -> tuple[bool, str, float]:
    """Run one example. Returns (ok, first meaningful line, seconds)."""
    argv = [binary, *command.split(), *shlex.split(arguments)]
    if profile:
        argv += ["--config", profile]

    started = time.monotonic()
    try:
        result = subprocess.run(argv, capture_output=True, text=True, timeout=timeout)
    except subprocess.TimeoutExpired:
        return False, f"timed out after {timeout:g}s", timeout
    elapsed = time.monotonic() - started

    output = (result.stdout or result.stderr).strip()
    first = next((line for line in output.splitlines() if line.strip()), "(no output)")

    # A zero exit code is necessary but not sufficient: a command that renders an empty
    # table has "worked" and told the user nothing. That is exactly the failure the stale
    # examples produced, so it counts as a failure here.
    ok = result.returncode == 0 and "(no results)" not in output and output != ""
    return ok, first, elapsed


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__,
                                     formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("filter", nargs="?", help="only run commands starting with this")
    parser.add_argument("-c", "--config", help="profile passed through to the CLI")
    parser.add_argument("--binary", default="osducs", help="CLI to run (default: osducs)")
    parser.add_argument("--timeout", type=float, default=60.0, help="per-example seconds")
    args = parser.parse_args()

    selected = [entry for entry in examples()
                if not args.filter or entry[0].startswith(args.filter)]
    if not selected:
        print("No examples matched.", file=sys.stderr)
        return 1

    print(f"{len(selected)} example(s) against "
          f"{args.config or 'the default profile'}\n")

    failures, skipped = [], []
    for command, arguments, skip in selected:
        shown = f"{command} {arguments}"
        if skip:
            print(f"  skip        {shown}")
            print(f"              {skip}")
            skipped.append(shown)
            continue

        ok, first, elapsed = run(args.binary, command, arguments, args.config, args.timeout)
        print(f"  {'ok  ' if ok else 'FAIL'} {elapsed:5.1f}s  {shown}")
        if not ok:
            print(f"         {first[:120]}")
            failures.append(shown)

    print()
    if failures:
        print(f"{len(failures)} of {len(selected)} failed:", file=sys.stderr)
        for failure in failures:
            print(f"  {failure}", file=sys.stderr)
        return 1

    ran = len(selected) - len(skipped)
    print(f"All {ran} runnable example(s) returned data"
          + (f"; {len(skipped)} skipped." if skipped else "."))
    return 0


if __name__ == "__main__":
    sys.exit(main())
