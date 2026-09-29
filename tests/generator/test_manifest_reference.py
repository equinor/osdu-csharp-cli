"""docs/MANIFEST.md, the manifest reference, stays in step with the generator.

The reference is the only place a manifest author can learn the format without reading the
generator. It fell behind before it existed: `fixed` arrived with a section in
COMMAND-GRAMMAR.md, while `cursor-from`, `forbidden-hint` and five other keys were documented
nowhere. These tests hold the page to what the generator accepts, in both directions, and keep
its example a manifest that actually builds.
"""

import re
from pathlib import Path

import pytest
import yaml

import generate_cli
from generate_cli import MANIFEST_KEYS, ManifestError, build_service

ROOT = Path(__file__).resolve().parents[2]
REFERENCE = ROOT / "docs" / "MANIFEST.md"

# The heading each level of MANIFEST_KEYS is documented under.
SECTIONS = {
    "top": "## Top level",
    "command": "## Command entries",
    "op": "### `op`",
    "param": "### `params`",
    "body": "### `body`",
    "body field": "### Body `fields`",
    "output": "### `output`",
    "example": "### `examples`",
    "handwritten": "## `handwritten`",
    "exclude": "## `exclude`",
}

ROW = re.compile(r"^\| `([^`]+)` \|", re.M)


def documented(level: str) -> set[str]:
    """The keys in the tables under the level's heading, up to the next heading."""
    lines = REFERENCE.read_text(encoding="utf-8").splitlines()
    start = lines.index(SECTIONS[level])
    end = next((i for i in range(start + 1, len(lines)) if lines[i].startswith("#")),
               len(lines))
    return set(ROW.findall("\n".join(lines[start + 1:end])))


def test_every_level_has_a_section():
    assert set(SECTIONS) == set(MANIFEST_KEYS)


@pytest.mark.parametrize("level", sorted(MANIFEST_KEYS))
def test_every_accepted_key_is_documented(level):
    missing = MANIFEST_KEYS[level] - documented(level)
    assert not missing, f"{SECTIONS[level]} in docs/MANIFEST.md is missing {sorted(missing)}"


@pytest.mark.parametrize("level", sorted(MANIFEST_KEYS))
def test_every_documented_key_is_accepted(level):
    # The reverse direction: a key removed from the generator must leave the page too, or it
    # goes on telling people to write something that is now an error.
    stale = documented(level) - MANIFEST_KEYS[level]
    assert not stale, f"{SECTIONS[level]} in docs/MANIFEST.md documents {sorted(stale)}"


def example_manifest() -> str:
    text = REFERENCE.read_text(encoding="utf-8")
    return re.search(r"## A small example\n\n```yaml\n(.*?)```", text, re.S).group(1)


@pytest.mark.skipif(not (ROOT / "openapi_specs" / "legal").is_dir(),
                    reason="specs not fetched; run tools/fetch_specs.py")
def test_the_example_builds_against_the_real_spec(tmp_path):
    # Coverage is checked by main(), not build_service, so a partial manifest is fine here;
    # what this catches is an example naming keys, operations or params that do not exist.
    path = tmp_path / "legal.yaml"
    path.write_text(example_manifest(), encoding="utf-8")

    service = build_service(path)

    assert [" ".join(c.path) for c in service.commands] == ["legaltag get"]


# ---- scope ------------------------------------------------------------------------------

SPEC = """
openapi: 3.0.1
info: { title: t, version: "1" }
paths:
  /ddms/v3/wellbores:
    get: { responses: { "200": { description: ok } } }
  /about:
    get: { responses: { "200": { description: ok } } }
"""


def manifest_with_scope(tmp_path, monkeypatch, scope) -> Path:
    (tmp_path / "specs" / "tiny").mkdir(parents=True)
    (tmp_path / "specs" / "tiny" / "openapi.yaml").write_text(SPEC, encoding="utf-8")
    monkeypatch.setattr(generate_cli, "SPECS_DIR", tmp_path / "specs")
    path = tmp_path / "tiny.yaml"
    path.write_text(yaml.safe_dump({"service": "tiny", "spec": "tiny", "client": "Tiny",
                                    "scope": scope}), encoding="utf-8")
    return path


def test_a_scope_list_is_accepted(tmp_path, monkeypatch):
    service = build_service(manifest_with_scope(tmp_path, monkeypatch, ["/ddms/v3/*"]))

    assert service.coverage.in_scope == 1


def test_a_scope_written_as_a_string_is_refused(tmp_path, monkeypatch):
    # Iterated character by character, where `*` matched every path: the scope silently
    # covered the whole spec.
    with pytest.raises(ManifestError, match="must be a list"):
        build_service(manifest_with_scope(tmp_path, monkeypatch, "/ddms/v3/*"))
