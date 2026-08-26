"""`osducs status` must probe every service the CLI can issue a command against.

The status table is hand-written C#, while the command surface comes from `cli-manifest/`.
Nothing tied the two together, and they drifted: manifests for CRS Conversion and Wellbore
DDMS gained commands, no probes were added, and `status` went on reporting a healthy estate
while two reachable services were never checked. Silence is the worst failure mode for a
command whose entire job is to tell you what is up.

This is the same bargain the manifest coverage gate strikes: a service is either covered or
explicitly exempted with a reason, and there is no third option.
"""

import pathlib
import re

import pytest
import yaml

ROOT = pathlib.Path(__file__).resolve().parents[2]
STATUS_SOURCE = ROOT / "src" / "OsduCli" / "Commands" / "StatusCommand.cs"

# Services deliberately absent from the status table, each with the reason. Empty today;
# a service belongs here only when it genuinely cannot be probed, not when probing it is
# merely inconvenient.
EXEMPT: dict[str, str] = {}


def probed_services() -> set[str]:
    """The service names in StatusCommand's Services table."""
    source = STATUS_SOURCE.read_text(encoding="utf-8")
    table = re.search(r"Services\s*=\s*\[(.*?)\n    \];", source, re.S)
    assert table, "could not find the Services table in StatusCommand.cs"
    return set(re.findall(r'^\s*\("([a-z0-9-]+)",', table.group(1), re.M))


def manifest_services() -> set[str]:
    """Manifest service keys, normalised to the names `status` displays."""
    names = set()
    for path in sorted((ROOT / "cli-manifest").glob("*.yaml")):
        service = yaml.safe_load(path.read_text(encoding="utf-8"))["service"]
        # The manifests carry the client's attribute names; status shows the OSDU service
        # name, which drops the API version suffix and spells words with hyphens.
        names.add(re.sub(r"_(v\d+|service)$", "", service).replace("_", "-"))
    return names


def test_every_manifest_service_is_probed_or_exempt():
    missing = manifest_services() - probed_services() - set(EXEMPT)
    assert not missing, (
        "these services have CLI commands but `osducs status` never probes them: "
        + ", ".join(sorted(missing))
        + ". Add a probe to StatusCommand.Services, or add the service to EXEMPT with "
        "a reason."
    )


def test_no_probe_exists_for_a_service_with_no_commands():
    # The reverse drift: a probe left behind after its manifest is removed reports on a
    # service the user has no way to call.
    stray = probed_services() - manifest_services()
    assert not stray, (
        "`osducs status` probes services the CLI has no commands for: "
        + ", ".join(sorted(stray))
    )


@pytest.mark.parametrize("service", sorted(EXEMPT))
def test_exemptions_carry_a_reason(service):
    assert EXEMPT[service].strip(), f"{service} is exempt without a reason"
