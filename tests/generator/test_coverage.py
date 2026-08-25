"""The coverage gate: every in-scope operation must be accounted for.

This is the property that makes a new upstream endpoint a named build failure rather than a
command that silently never appears. It caught `POST /query/records/headers` unprompted when
the specs were refreshed.
"""

from generate_cli import compute_coverage

OPERATIONS = {
    ("get", "/records/{id}"): {},
    ("put", "/records"): {},
    ("post", "/replay"): {},
    ("get", "/ddms/v3/wellbores"): {},
}


def coverage(claimed, scope=None):
    return compute_coverage(OPERATIONS, set(claimed), scope, 0, 0, 0)


def test_an_unclaimed_operation_is_reported_missing():
    result = coverage([("get", "/records/{id}")])
    assert ("put", "/records") in result.missing
    assert ("post", "/replay") in result.missing


def test_claiming_everything_leaves_nothing_missing():
    assert coverage(OPERATIONS).missing == []


def test_scope_limits_what_must_be_accounted_for():
    # Wellbore DDMS has 84 operations; triaging all of them to add three commands is not
    # useful, so a manifest may declare the prefix it is responsible for.
    result = coverage([("get", "/ddms/v3/wellbores")], scope=["/ddms/v3/wellbores*"])
    assert result.missing == []
    assert result.in_scope == 1


def test_operations_outside_scope_are_reported_separately():
    result = coverage([("get", "/ddms/v3/wellbores")], scope=["/ddms/v3/wellbores*"])
    assert ("put", "/records") in result.untriaged
    assert ("put", "/records") not in result.missing


def test_a_scope_prefix_does_not_overmatch():
    # `/ddms/v3/wellbores*` must not swallow `/ddms/v3/wellboremarkersets`.
    operations = dict(OPERATIONS)
    operations[("get", "/ddms/v3/wellboremarkersets")] = {}
    result = compute_coverage(operations, {("get", "/ddms/v3/wellbores")},
                              ["/ddms/v3/wellbores*"], 0, 0, 0)
    assert ("get", "/ddms/v3/wellboremarkersets") in result.untriaged
