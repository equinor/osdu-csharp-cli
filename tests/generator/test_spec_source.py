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

import fetch_specs
import generate_cli
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


class TestStaleFetchedSpecs:
    """A fetched tree is only useful while it still matches the pin.

    Selecting it because it exists put the drift back one pull later: fetch once, pull a pin
    bump, and generation reads the old tree without saying so. CI never sees this — it
    fetches every run — so the check exists for the working copy alone.
    """

    def prepare(self, monkeypatch, tmp_path, origin, stamped):
        if stamped is not None:
            (tmp_path / ".spec-ref").write_text(f"{stamped}\n", encoding="utf-8")
        monkeypatch.setattr(generate_cli, "SPECS_DIR", tmp_path)
        monkeypatch.setattr(generate_cli, "SPECS_ORIGIN", origin)

    def test_a_stamp_matching_the_pin_passes(self, monkeypatch, tmp_path):
        self.prepare(monkeypatch, tmp_path, "fetched", SOURCE["ref"])
        assert generate_cli.stale_specs() is None

    def test_a_stamp_from_another_ref_is_reported(self, monkeypatch, tmp_path):
        self.prepare(monkeypatch, tmp_path, "fetched", "v0.0.1")
        message = generate_cli.stale_specs()
        assert message and "v0.0.1" in message and SOURCE["ref"] in message
        assert "fetch_specs.py" in message

    def test_an_unstamped_tree_is_reported(self, monkeypatch, tmp_path):
        self.prepare(monkeypatch, tmp_path, "fetched", None)
        assert "unstamped" in (generate_cli.stale_specs() or "")

    @pytest.mark.parametrize("origin", ["override", "sibling"])
    def test_only_the_fetched_copy_is_held_to_the_pin(self, monkeypatch, tmp_path, origin):
        # Both are the caller saying which specs to use; neither carries a stamp, and
        # failing them would break the setups this change promises to keep working.
        self.prepare(monkeypatch, tmp_path, origin, None)
        assert generate_cli.stale_specs() is None


class TestMissingSpecRemedy:
    """The way out of "no spec found" depends on how the directory was chosen."""

    def test_an_override_is_told_to_fix_the_override(self, monkeypatch, tmp_path):
        # Fetching cannot help here: OSDU_SPECS_DIR would keep winning afterwards.
        monkeypatch.setattr(generate_cli, "SPECS_DIR", tmp_path)
        monkeypatch.setattr(generate_cli, "SPECS_ORIGIN", "override")
        with pytest.raises(generate_cli.ManifestError, match="OSDU_SPECS_DIR"):
            generate_cli.resolve_spec("storage")

    def test_otherwise_the_remedy_is_to_fetch(self, monkeypatch, tmp_path):
        monkeypatch.setattr(generate_cli, "SPECS_DIR", tmp_path)
        monkeypatch.setattr(generate_cli, "SPECS_ORIGIN", "sibling")
        with pytest.raises(generate_cli.ManifestError, match="fetch_specs.py"):
            generate_cli.resolve_spec("storage")


def write_config(path, ref, expect=2):
    path.write_text(yaml.safe_dump({"version": 1, "source": {
        "name": "client", "ref": ref,
        "archive": "https://example.invalid/{ref}.tar.gz",
        "specs_path": "openapi_specs", "expect_specs": expect}}), encoding="utf-8")


class TestFetch:
    """`fetch()` itself — the no-op, the re-fetch, and what a bad tree does to a good one."""

    def prepare(self, monkeypatch, tmp_path, ref="v9.9.9", expect=2, payload=None):
        config = tmp_path / "spec-source.yaml"
        write_config(config, ref, expect)
        target = tmp_path / "openapi_specs"
        monkeypatch.setattr(fetch_specs, "CONFIG", config)
        monkeypatch.setattr(fetch_specs, "TARGET", target)
        calls = []

        def download(url):
            calls.append(url)
            if payload is not None:
                return payload
            # Build from the ref in the URL, so a re-pin produces a matching tree.
            fetched = url.rsplit("/", 1)[-1].removesuffix(".tar.gz")
            return make_archive([f"c-{fetched}/openapi_specs/storage/openapi.yaml",
                                 f"c-{fetched}/openapi_specs/legal/openapi.yaml"])

        monkeypatch.setattr(fetch_specs, "download", download)
        return calls, config, target

    def test_a_first_fetch_writes_the_tree_and_stamps_it(self, monkeypatch, tmp_path):
        calls, _, target = self.prepare(monkeypatch, tmp_path)
        assert fetch_specs.fetch() == 0
        assert len(calls) == 1
        assert (target / "storage" / "openapi.yaml").is_file()
        assert (target / fetch_specs.STAMP_NAME).read_text(encoding="utf-8").strip() == "v9.9.9"

    def test_a_matching_stamp_is_a_no_op(self, monkeypatch, tmp_path):
        calls, _, _ = self.prepare(monkeypatch, tmp_path)
        fetch_specs.fetch()
        fetch_specs.fetch()
        assert len(calls) == 1, "a tree already at the pinned ref should not be downloaded again"

    def test_force_refetches(self, monkeypatch, tmp_path):
        calls, _, _ = self.prepare(monkeypatch, tmp_path)
        fetch_specs.fetch()
        fetch_specs.fetch(force=True)
        assert len(calls) == 2

    def test_a_changed_pin_refetches(self, monkeypatch, tmp_path):
        # The reason the stamp exists: bumping the pin must not leave the old tree in place.
        calls, config, target = self.prepare(monkeypatch, tmp_path, ref="v1.0.0")
        fetch_specs.fetch()
        write_config(config, "v2.0.0")
        fetch_specs.fetch()
        assert len(calls) == 2
        assert (target / fetch_specs.STAMP_NAME).read_text(encoding="utf-8").strip() == "v2.0.0"

    def test_a_wrong_spec_count_leaves_the_previous_tree_intact(self, monkeypatch, tmp_path):
        # Unpacking into a staging directory means a client that stopped shipping specs
        # fails without also destroying the working tree it was replacing.
        _, config, target = self.prepare(monkeypatch, tmp_path, ref="v1.0.0")
        fetch_specs.fetch()
        write_config(config, "v2.0.0", expect=99)
        with pytest.raises(FetchError, match="expected 99 spec files"):
            fetch_specs.fetch()
        assert (target / "storage" / "openapi.yaml").is_file()
        assert (target / fetch_specs.STAMP_NAME).read_text(encoding="utf-8").strip() == "v1.0.0"

    def test_a_response_that_is_not_a_tar_is_reported_cleanly(self, monkeypatch, tmp_path):
        # A forge can answer 200 with an error page; that must not surface as a traceback.
        self.prepare(monkeypatch, tmp_path, payload=b"<html>not found</html>")
        with pytest.raises(FetchError, match="did not parse as a gzipped tar"):
            fetch_specs.fetch()


def test_the_stamp_filename_agrees_across_the_two_tools():
    # fetch_specs writes it, generate_cli reads it, and they spell it separately so the
    # generator stays free of the fetcher's network imports.
    assert fetch_specs.STAMP_NAME == generate_cli.SPEC_STAMP


def test_an_unreadable_pin_fails_rather_than_passes(monkeypatch, tmp_path):
    # Fail closed: with no readable ref there is nothing to check a fetched tree against.
    monkeypatch.setattr(generate_cli, "SPECS_DIR", tmp_path)
    monkeypatch.setattr(generate_cli, "SPECS_ORIGIN", "fetched")
    monkeypatch.setattr(generate_cli, "SPEC_SOURCE", tmp_path / "absent.yaml")
    assert "no readable" in (generate_cli.stale_specs() or "")
