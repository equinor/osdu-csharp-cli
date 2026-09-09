"""Manifest rules that constrain how a command may be declared.

Each of these guards a mistake that would otherwise reach a user: a rule that silently does
nothing, or generated C# that does not compile.
"""

import pytest

from generate_cli import ManifestError, build_command

OPERATION = {
    "parameters": [
        {"name": "recordId", "in": "query", "schema": {"type": "string"}},
        {"name": "dataId", "in": "query", "schema": {"type": "string"}},
    ],
    "responses": {"200": {"content": {"application/json": {"schema": {"type": "object"}}}}},
}

BODY_OPERATION = {
    "requestBody": {"content": {"application/json": {"schema": {
        "$ref": "#/components/schemas/QueryRequest"}}}},
    "responses": {"200": {"content": {"application/json": {"schema": {"type": "object"}}}}},
}

SPEC = {"components": {"schemas": {"QueryRequest": {"properties": {
    "kind": {"type": "string"},
    "returnedFields": {"type": "array", "items": {"type": "string"}},
    "excludedFields": {"type": "array", "items": {"type": "string"}},
}}}}}


def build(entry, operation=OPERATION, spec=None):
    return build_command(entry, operation, "here", "Models", spec or SPEC)


def base(**extra):
    entry = {"command": "crs get", "op": {"method": "get", "path": "/v3/thing"},
             "params": {"recordId": {"flag": "--record-id"},
                        "dataId": {"flag": "--data-id"}},
             "output": "raw"}
    entry.update(extra)
    return entry


class TestRequireOneOf:
    def test_a_valid_rule_is_carried(self):
        command = build(base(**{"require-one-of": ["recordId", "dataId"]}))
        assert command.require_one_of == ["recordId", "dataId"]

    def test_naming_a_param_the_command_lacks_is_rejected(self):
        with pytest.raises(ManifestError, match="nonsense"):
            build(base(**{"require-one-of": ["recordId", "nonsense"]}))

    def test_a_single_name_is_rejected(self):
        # One mandatory param is `required: true`; a one-element rule means someone
        # misunderstood, and it would never fire.
        with pytest.raises(ManifestError, match="at least two"):
            build(base(**{"require-one-of": ["recordId"]}))

    def test_no_rule_is_fine(self):
        assert build(base()).require_one_of == []


class TestMutuallyExclusive:
    def test_a_flat_pair_becomes_one_group(self):
        command = build(base(**{"mutually-exclusive": ["recordId", "dataId"]}))
        assert command.mutually_exclusive == [["recordId", "dataId"]]

    def test_several_groups_are_kept_separate(self):
        # A command can have more than one contradictory pair. Honouring only the first
        # would silently drop a rule — which is how `mutually-exclusive-2` came about.
        command = build(base(params={"recordId": {"flag": "-a"}, "dataId": {"flag": "-b"}},
                             **{"mutually-exclusive": [["recordId", "dataId"]]}))
        assert command.mutually_exclusive == [["recordId", "dataId"]]

    def test_an_unknown_name_is_rejected(self):
        with pytest.raises(ManifestError, match="neither a param nor a body field"):
            build(base(**{"mutually-exclusive": ["recordId", "nope"]}))

    def test_a_one_element_group_is_rejected(self):
        with pytest.raises(ManifestError, match="at least two"):
            build(base(**{"mutually-exclusive": ["recordId"]}))

    def test_a_body_field_may_be_named(self):
        entry = {"command": "record search", "op": {"method": "post", "path": "/query"},
                 "body": {"fields": {
                     "returnedFields": {"flag": "-f", "type": "string[]"},
                     "excludedFields": {"flag": "-x", "type": "string[]"}}},
                 "mutually-exclusive": ["returnedFields", "excludedFields"],
                 "output": "raw"}
        command = build(entry, BODY_OPERATION)
        assert command.mutually_exclusive == [["returnedFields", "excludedFields"]]


class TestColumnsFrom:
    def test_it_must_name_a_body_field(self):
        entry = {"command": "record search", "op": {"method": "post", "path": "/query"},
                 "body": {"fields": {"kind": {"flag": "-k"}}},
                 "output": {"root": "results", "columns-from": "nonsense"}}
        with pytest.raises(ManifestError, match="columns-from"):
            build(entry, BODY_OPERATION)


class TestParts:
    def test_parts_are_carried_onto_the_field(self):
        entry = {"command": "record search", "op": {"method": "post", "path": "/query"},
                 "body": {"fields": {"kind": {
                     "flag": "--bbox", "type": "double[]",
                     "parts": ["a.latitude", "a.longitude"]}}},
                 "output": "raw"}
        field = build(entry, BODY_OPERATION).body.fields[0]
        assert field.parts == ["a.latitude", "a.longitude"]
        assert field.is_collection


class TestParamValidation:
    def test_a_param_absent_from_the_spec_is_rejected(self):
        # Catches an upstream rename: the manifest names a parameter the operation no
        # longer has, and the mapping would silently do nothing.
        entry = base(params={"kindName": {"flag": "--kind"}})
        with pytest.raises(ManifestError, match="kindName"):
            build(entry)

    def test_a_spec_required_param_must_say_so_in_the_manifest(self):
        # docs/COMMANDS.md is rendered from the manifest alone, so inheriting required-ness
        # from the spec made the reference advertise a mandatory option as optional while
        # the CLI refused the command without it. `member group list --type` was that.
        operation = {"parameters": [{"name": "type", "in": "query", "required": True,
                                     "schema": {"type": "string"}}],
                     "responses": {"200": {"content": {"application/json": {
                         "schema": {"type": "object"}}}}}}
        entry = {"command": "member group list", "op": {"method": "get", "path": "/groups"},
                 "params": {"type": {"flag": "--type"}}, "output": "raw"}
        with pytest.raises(ManifestError, match="required: true"):
            build(entry, operation)

        entry["params"]["type"]["required"] = True
        assert build(entry, operation).params[0].required

    def test_the_manifest_may_require_what_the_spec_leaves_optional(self):
        # The CLI is allowed to be the stricter of the two; only the reverse is a lie.
        entry = base(params={"recordId": {"flag": "--record-id", "required": True}})
        assert build(entry).params[0].required
