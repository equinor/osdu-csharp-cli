#!/usr/bin/env python3
"""Fetch the OpenAPI specs the generator reads, pinned to a released client version.

The specs are not this repository's to own — each is a copy of a file its OSDU service
publishes, vendored by osdu-csharp-client. This downloads the snapshot belonging to the
client version the CLI builds against, so the manifest coverage gate and the compiled code
see one tree rather than two that drift apart. Where it comes from is declared in
``spec-source.yaml``, not spelled out here, so moving the client is a config edit.

The specs land in a gitignored ``openapi_specs/`` at the repo root. A sibling
``osdu-csharp-client`` checkout still works and still takes precedence over nothing --
see ``generate_cli.py``'s resolution order — but is no longer required.

Usage::

    python3 tools/fetch_specs.py            # fetch (no-op if the pinned ref is already there)
    python3 tools/fetch_specs.py --force    # re-fetch, replacing what is there
"""

from __future__ import annotations

import argparse
import io
import shutil
import ssl
import sys
import tarfile
import urllib.error
import urllib.request
from pathlib import Path

import yaml

ROOT = Path(__file__).resolve().parent.parent
CONFIG = ROOT / "spec-source.yaml"
TARGET = ROOT / "openapi_specs"
# Records which ref the tree came from, so a checkout left over from an earlier pin is
# re-fetched instead of silently validated against. generate_cli.py reads it back; a test
# holds the two spellings together.
STAMP_NAME = ".spec-ref"

SPEC_SUFFIXES = {".yaml", ".yml", ".json"}


class FetchError(Exception):
    """The specs could not be obtained, or arrived looking wrong. Always fatal."""


def load_source() -> dict:
    if not CONFIG.is_file():
        raise FetchError(f"{CONFIG.name} is missing; it declares where the specs come from")
    document = yaml.safe_load(CONFIG.read_text(encoding="utf-8")) or {}
    source = document.get("source")
    if not isinstance(source, dict):
        raise FetchError(f"{CONFIG.name}: expected a `source:` mapping")
    for key in ("ref", "archive", "specs_path", "expect_specs"):
        if key not in source:
            raise FetchError(f"{CONFIG.name}: `source` is missing `{key}`")
    return source


def download(url: str) -> bytes:
    try:
        with urllib.request.urlopen(url, timeout=60) as response:  # noqa: S310 - https, from config
            return response.read()
    except urllib.error.HTTPError as error:
        raise FetchError(
            f"{url} returned HTTP {error.code}. If the client repository moved or the tag was "
            f"removed, update `archive`/`ref` in {CONFIG.name}."
        ) from error
    except urllib.error.URLError as error:
        if isinstance(error.reason, ssl.SSLCertVerificationError):
            # The python.org macOS installer ships without a trust store until its
            # `Install Certificates.command` is run, so this is a setup problem on the
            # machine rather than anything about the URL. Say so, because the raw message
            # reads like the server is at fault.
            raise FetchError(
                f"TLS certificate verification failed for {url}.\n"
                "  This Python has no CA bundle. On macOS run\n"
                "    /Applications/Python\\ 3.x/Install\\ Certificates.command\n"
                "  or point it at one:\n"
                "    SSL_CERT_FILE=$(python3 -m certifi) python3 tools/fetch_specs.py"
            ) from error
        raise FetchError(f"could not reach {url}: {error.reason}") from error


def extract(archive: bytes, specs_path: str, target: Path) -> list[Path]:
    """Unpack just ``specs_path`` out of a tag archive, dropping its top-level directory.

    Both forges wrap the tree in a directory whose name embeds the ref — and GitLab's
    ``?path=`` archives embed the commit sha too — so the leading component is matched
    rather than assumed.
    """
    written: list[Path] = []
    marker = f"/{specs_path.strip('/')}/"
    try:
        tar = tarfile.open(fileobj=io.BytesIO(archive), mode="r:gz")
    except tarfile.TarError as error:
        # A forge can answer 200 with an error page, a redirect stub or a truncated body.
        # Without this the traceback replaces the fetcher's own diagnosis.
        raise FetchError(
            f"the download did not parse as a gzipped tar ({error}). The URL may no longer "
            f"point at a tag archive."
        ) from error
    with tar:
        for member in tar.getmembers():
            if not member.isfile():
                continue
            head, separator, rest = member.name.partition(marker)
            # `head` must be the archive's single wrapper directory, so a nested directory of
            # the same name deeper in the tree cannot be mistaken for the spec root.
            if not separator or "/" in head:
                continue
            relative = Path(rest)
            # A tag archive is not hostile input, but it is remote input: refuse anything
            # that would land outside the target rather than trusting the names in it.
            if relative.is_absolute() or ".." in relative.parts:
                raise FetchError(f"archive member escapes the target directory: {member.name}")
            handle = tar.extractfile(member)
            if handle is None:
                continue
            destination = target / relative
            destination.parent.mkdir(parents=True, exist_ok=True)
            destination.write_bytes(handle.read())
            written.append(relative)
    return written


def fetch(force: bool = False) -> int:
    source = load_source()
    ref = str(source["ref"])
    url = str(source["archive"]).format(ref=ref)
    expected = int(source["expect_specs"])

    stamp = TARGET / STAMP_NAME
    if not force and stamp.is_file() and stamp.read_text(encoding="utf-8").strip() == ref:
        print(f"openapi_specs/ is already at {ref}; nothing to do (--force to re-fetch)")
        return 0

    print(f"Fetching specs for {source.get('name', 'the client')} {ref}\n  {url}")
    archive = download(url)

    staging = TARGET.with_name(TARGET.name + ".incoming")
    shutil.rmtree(staging, ignore_errors=True)
    staging.mkdir(parents=True)
    try:
        written = extract(archive, str(source["specs_path"]), staging)
        specs = [path for path in written if path.suffix.lower() in SPEC_SUFFIXES]
        # The archive endpoint answering 200 says nothing about what is in the tree. A
        # client that stopped vendoring specs would land here, and must stop here rather
        # than in the generator as eighteen separate "no spec found" errors.
        if len(specs) != expected:
            raise FetchError(
                f"expected {expected} spec files under {source['specs_path']}/ at {ref}, "
                f"found {len(specs)}. If the client's spec layout changed deliberately, "
                f"update `specs_path`/`expect_specs` in {CONFIG.name}."
            )
        (staging / STAMP_NAME).write_text(f"{ref}\n", encoding="utf-8")
        shutil.rmtree(TARGET, ignore_errors=True)
        staging.replace(TARGET)
    finally:
        shutil.rmtree(staging, ignore_errors=True)

    # Relative when it sits under the repo, which is every real run; absolute otherwise,
    # rather than raising on the success path.
    try:
        where = TARGET.relative_to(ROOT)
    except ValueError:
        where = TARGET
    print(f"Wrote {len(specs)} spec(s) to {where}/")
    return 0


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__,
                                     formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--force", action="store_true",
                        help="re-fetch even when the pinned ref is already present")
    args = parser.parse_args()
    try:
        return fetch(force=args.force)
    except FetchError as error:
        print(f"error: {error}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main())
