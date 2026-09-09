"""The spec source is pinned, and the pin agrees with the client the CLI builds against.

The CLI calls a specific client version's generated methods, so the specs its coverage gate
validates against must be the specs that version was generated from. Before this was pinned,
CI checked out the client's default branch while the build resolved a fixed package version:
two independent snapshots, and nothing that noticed when they disagreed. These tests are that
notice.
"""

from __future__ import annotations

import io
import tarfile
import xml.etree.ElementTree as ElementTree
from pathlib import Path

import pytest
import yaml

from fetch_specs import FetchError, extract

ROOT = Path(__file__).resolve().parents[2]
CONFIG = ROOT / "spec-source.yaml"
CSPROJ = ROOT / "src" / "OsduCli" / "OsduCli.csproj"

SOURCE = yaml.safe_load(CONFIG.read_text(encoding="utf-8"))["source"]


def client_package_versions() -> dict[str, str]:
    tree = ElementTree.parse(CSPROJ)
    return {
        reference.get("Include"): reference.get("Version")
        for reference in tree.iter("PackageReference")
        if (reference.get("Include") or "").startswith("Equinor.OsduCsharpClient")
    }


def test_ref_matches_the_pinned_client_version():
    versions = client_package_versions()
    assert versions, f"no Equinor.OsduCsharpClient PackageReference found in {CSPROJ.name}"
    expected = {f"v{version}" for version in versions.values()}
    assert SOURCE["ref"] in expected, (
        f"spec-source.yaml pins {SOURCE['ref']} but {CSPROJ.name} references "
        f"{sorted(versions.values())}. The specs must be the ones the client was generated "
        f"from; bump both together."
    )


def test_both_client_packages_are_pinned_together():
    versions = client_package_versions()
    assert len(set(versions.values())) == 1, (
        f"Equinor.OsduCsharpClient packages disagree: {versions}"
    )


def test_archive_is_a_ref_template():
    # The ref appears once, in `ref`. A URL with the version baked in would drift silently.
    assert "{ref}" in SOURCE["archive"]
    formatted = SOURCE["archive"].format(ref=SOURCE["ref"])
    assert formatted.startswith("https://")
    assert SOURCE["ref"] in formatted


def make_archive(names: list[str]) -> bytes:
    buffer = io.BytesIO()
    with tarfile.open(fileobj=buffer, mode="w:gz") as tar:
        for name in names:
            payload = b"openapi: 3.0.0\n"
            info = tarfile.TarInfo(name)
            info.size = len(payload)
            tar.addfile(info, io.BytesIO(payload))
    return buffer.getvalue()


def test_extract_strips_the_wrapper_directory(tmp_path):
    archive = make_archive([
        "osdu-csharp-client-2.2.1/README.md",
        "osdu-csharp-client-2.2.1/openapi_specs/storage/openapi.yaml",
        "osdu-csharp-client-2.2.1/openapi_specs/unit/v3/openapi.yaml",
    ])
    written = extract(archive, "openapi_specs", tmp_path)
    assert sorted(str(path) for path in written) == [
        "storage/openapi.yaml", "unit/v3/openapi.yaml",
    ]
    assert (tmp_path / "unit" / "v3" / "openapi.yaml").is_file()


def test_extract_ignores_a_nested_directory_of_the_same_name(tmp_path):
    # GitLab's `?path=` archives wrap the tree in `<project>-<ref>-<sha>-<path>/`, so the
    # wrapper is matched rather than assumed — but only ever one level deep.
    archive = make_archive([
        "client-2.2.1/openapi_specs/storage/openapi.yaml",
        "client-2.2.1/tests/fixtures/openapi_specs/fake/openapi.yaml",
    ])
    written = extract(archive, "openapi_specs", tmp_path)
    assert [str(path) for path in written] == ["storage/openapi.yaml"]


def test_extract_refuses_to_escape_the_target(tmp_path):
    archive = make_archive(["client/openapi_specs/../../../etc/passwd"])
    with pytest.raises(FetchError, match="escapes the target"):
        extract(archive, "openapi_specs", tmp_path)


@pytest.mark.skipif(not (ROOT / "openapi_specs").is_dir(),
                    reason="specs not fetched; run tools/fetch_specs.py")
def test_expect_specs_matches_the_fetched_tree():
    found = [path for path in (ROOT / "openapi_specs").rglob("openapi.*")
             if path.suffix in {".yaml", ".yml", ".json"}]
    assert len(found) == SOURCE["expect_specs"], (
        f"spec-source.yaml expects {SOURCE['expect_specs']} specs, tree has {len(found)}"
    )
