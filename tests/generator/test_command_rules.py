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

SPEC = {"components": {"schemas": {
    "QueryRequest": {"properties": {
        "kind": {"type": "string"},
        "returnedFields": {"type": "array", "items": {"type": "string"}},
        "excludedFields": {"type": "array", "items": {"type": "string"}},
    }},
    # What Search's cursor endpoint takes: the query's fields but `offset`, and a cursor.
    "CursorQueryRequest": {"properties": {
        "kind": {"type": "string"}, "limit": {"type": "integer"}, "cursor": {"type": "string"},
    }},
}}}


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


class TestCommaSeparated:
    """`comma-separated` lets a list option take `-f a,b`, which System.CommandLine does not."""

    @staticmethod
    def entry(field, spec_field=None):
        return {"command": "record search", "op": {"method": "post", "path": "/query"},
                "body": {"fields": {"returnedFields": {"flag": "-f", **field}}},
                "output": "raw"}

    def test_it_is_carried_onto_the_field(self):
        # `record search -f data.Source,data.Equinor.WellboreName` was sent as one field with
        # a comma in its name, though the help said values could be comma-separated.
        field = build(self.entry({"type": "string[]", "comma-separated": True}),
                      BODY_OPERATION).body.fields[0]
        assert field.comma_separated

    def test_it_is_off_unless_asked_for(self):
        field = build(self.entry({"type": "string[]"}), BODY_OPERATION).body.fields[0]
        assert not field.comma_separated

    @pytest.mark.parametrize("type_", [None, "string", "double[]"])
    def test_it_needs_a_list_of_strings(self, type_):
        field = {"comma-separated": True} | ({"type": type_} if type_ else {})
        with pytest.raises(ManifestError, match="needs `type: string\\[\\]`"):
            build(self.entry(field), BODY_OPERATION)

    def test_it_cannot_be_combined_with_parts(self):
        with pytest.raises(ManifestError, match="`parts` already splits"):
            build(self.entry({"type": "string[]", "comma-separated": True, "parts": ["a", "b"]}),
                  BODY_OPERATION)

    def test_it_cannot_be_combined_with_allowed_values(self):
        # The enum's own parser would replace the splitting one, and the splitting would
        # silently stop.
        spec = {"components": {"schemas": {"QueryRequest": {"properties": {
            "returnedFields": {"type": "array", "items": {"type": "string", "enum": ["a", "b"]}},
        }}}}}
        with pytest.raises(ManifestError, match="allowed values"):
            build(self.entry({"type": "string[]", "comma-separated": True}), BODY_OPERATION, spec)

    def test_it_is_true_or_false(self):
        with pytest.raises(ManifestError, match="true or false"):
            build(self.entry({"type": "string[]", "comma-separated": "yes"}), BODY_OPERATION)


class TestPaging:
    """`paging:` sends the same request to a cursor endpoint when one page is not enough."""

    CURSOR = {"requestBody": {"content": {"application/json": {"schema": {
        "$ref": "#/components/schemas/CursorQueryRequest"}}}},
        "responses": BODY_OPERATION["responses"]}
    RELEASE = {"responses": BODY_OPERATION["responses"]}

    @staticmethod
    def entry(paging=None, fields=None, output=None):
        spec_fields = {"kind": {"flag": "-k"}, "limit": {"flag": "--limit", "type": "int"},
                       "offset": {"flag": "--offset", "type": "int"}}
        paging_cfg = {"op": {"method": "post", "path": "/query_with_cursor"},
                      "release": {"method": "delete", "path": "/query_with_cursor/{cursor}"},
                      "limit": "limit", "page-size": 1000, "cursor": "cursor",
                      "not-with": ["offset"]}
        paging_cfg.update(paging or {})
        return {"command": "record search", "op": {"method": "post", "path": "/query"},
                "body": {"fields": fields or spec_fields},
                "paging": {k: v for k, v in paging_cfg.items() if v is not None},
                "output": output if output is not None else {"root": "results"}}

    def build(self, entry, operations="default"):
        ops = (self.CURSOR, self.RELEASE) if operations == "default" else operations
        return build_command(entry, BODY_OPERATION, "here", "Models", SPEC, ops)

    def test_it_is_carried_onto_the_command(self):
        paging = self.build(self.entry()).paging
        assert (paging.builder, paging.model, paging.release_builder) == (
            "Query_with_cursor", "CursorQueryRequest", "Query_with_cursor[{cursor}]")
        assert (paging.limit.name, paging.page_size, paging.cursor) == ("limit", 1000, "cursor")
        assert [field.name for field in paging.not_with] == ["offset"]

    def test_release_is_optional(self):
        assert self.build(self.entry({"release": None}), (self.CURSOR, None)).paging.release_builder is None

    def test_a_body_read_from_a_file_cannot_page(self):
        # Each page resizes the request, which a file's body cannot be.
        entry = self.entry()
        entry["body"] = {"flag": "--file"}
        with pytest.raises(ManifestError, match="body built from `fields:`"):
            self.build(entry)

    def test_the_results_need_a_root_to_join(self):
        with pytest.raises(ManifestError, match="output.root"):
            self.build(self.entry(output={}))

    @pytest.mark.parametrize("limit", ["kind", "nonsense"])
    def test_the_limit_must_be_a_number_field(self, limit):
        with pytest.raises(ManifestError, match="must name an `int` body field"):
            self.build(self.entry({"limit": limit}))

    # 2**31 is one past a C# int, and generated code that did not compile.
    @pytest.mark.parametrize("size", [0, -5, "1000", True, 2**31])
    def test_the_page_size_is_a_positive_whole_number(self, size):
        with pytest.raises(ManifestError, match="page-size"):
            self.build(self.entry({"page-size": size}))

    def test_not_with_names_fields_of_the_command(self):
        with pytest.raises(ManifestError, match="not-with"):
            self.build(self.entry({"not-with": ["sort"]}))

    def test_release_is_a_delete_with_the_cursor_in_its_path(self):
        with pytest.raises(ManifestError, match="release"):
            self.build(self.entry({"release": {"method": "post", "path": "/query_with_cursor/{cursor}"}}))
        with pytest.raises(ManifestError, match="release"):
            self.build(self.entry({"release": {"method": "delete", "path": "/query_with_cursor"}}))

    def test_the_op_must_be_a_post(self):
        # The emitted code calls PostAsync whatever the manifest said.
        with pytest.raises(ManifestError, match="must be a POST"):
            self.build(self.entry({"op": {"method": "put", "path": "/query_with_cursor"}}))

    # Any placeholder, not only one of word characters, as derive_builder treats them all.
    @pytest.mark.parametrize("path", ["/items/{id}/cursor", "/items/{item.id}/cursor"])
    def test_the_op_cannot_have_path_parameters(self, path):
        # Nothing would supply them, and the generated code would not compile.
        with pytest.raises(ManifestError, match="cannot have path parameters"):
            self.build(self.entry({"op": {"method": "post", "path": path}}))

    def test_a_release_placeholder_of_any_name_is_given_the_cursor(self):
        paging = self.build(self.entry({"release": {"method": "delete", "path": "/query_with_cursor/{cursor.id}"}})).paging
        assert paging.release_builder.count("{") == 1

    def test_the_limit_and_cursor_are_top_level(self):
        # Set on the request itself: a dotted name became a literal property beside the
        # nested one it meant.
        fields = {"kind": {"flag": "-k"}, "page.limit": {"flag": "--limit", "type": "int"}}
        with pytest.raises(ManifestError, match="top-level body field"):
            self.build(self.entry({"limit": "page.limit", "not-with": None}, fields=fields))
        with pytest.raises(ManifestError, match="top-level cursor property"):
            self.build(self.entry({"cursor": "page.cursor"}))

    def test_the_cursor_cannot_share_a_name_with_a_body_field(self):
        # Written onto each page after the limit: `cursor: limit` replaced the page size.
        with pytest.raises(ManifestError, match="is also a body field"):
            self.build(self.entry({"cursor": "limit"}))

    def test_the_cursor_endpoint_must_take_the_limit_and_the_cursor(self):
        with pytest.raises(ManifestError, match="must take both"):
            self.build(self.entry({"cursor": "nextPage"}))

    def test_a_field_the_cursor_endpoint_does_not_take_must_be_listed_in_not_with(self):
        # `offset` is not in the cursor endpoint's request; left out of not-with, a page
        # would have been a different request from the one the options described.
        with pytest.raises(ManifestError, match=r"does not take \['offset'\]"):
            self.build(self.entry({"not-with": None}))

    def test_unknown_paging_keys_are_refused(self):
        entry = self.entry()
        entry["paging"]["pages"] = 3
        with pytest.raises(ManifestError, match="unknown paging key"):
            self.build(entry)


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


class TestFixedBodyValues:
    """`body: fixed:` — values sent on every call that no option can change."""

    FIXED_SPEC = {"components": {"schemas": {
        "QueryRequest": {"properties": {
            "kind": {"type": "string"},
            "limit": {"type": "integer"},
            "trackTotalCount": {"type": "boolean"},
            "sort": {"$ref": "#/components/schemas/SortQuery"},
            "score": {"type": "number"},
            "offset": {"anyOf": [{"type": "integer"}, {"type": "null"}]},
            "version": {"type": "integer", "format": "int64"},
            "order": {"type": "string", "enum": ["ASC", "DESC"]},
        }},
        "SortQuery": {"properties": {"field": {"type": "string"}}},
    }}}

    def entry(self, fixed, fields=None, **body):
        return {"command": "record aggregate", "op": {"method": "post", "path": "/query"},
                "body": {**body, "fixed": fixed,
                         **({"fields": fields} if fields is not None else {})},
                "output": "raw"}

    def build(self, entry):
        return build_command(entry, BODY_OPERATION, "here", "Models", self.FIXED_SPEC)

    KIND = {"kind": {"flag": "--kind", "required": True}}

    def test_a_valid_value_is_carried_onto_the_body(self):
        body = self.build(self.entry({"limit": 1}, self.KIND)).body
        assert body.fixed == [("limit", 1)]

    def test_a_dotted_name_reaches_the_nested_schema(self):
        body = self.build(self.entry({"sort.field": "id"}, self.KIND)).body
        assert body.fixed == [("sort.field", "id")]

    def test_a_name_the_body_does_not_have_is_rejected(self):
        # The case worth the check: a typo would otherwise ship as a property the service
        # ignores, and the command would quietly not do what the manifest says.
        with pytest.raises(ManifestError, match="not a property"):
            self.build(self.entry({"limt": 1}, self.KIND))

    def test_a_value_cannot_also_be_an_option(self):
        with pytest.raises(ManifestError, match="collides with 'kind'"):
            self.build(self.entry({"kind": "x"}, self.KIND))

    def test_a_value_under_an_option_is_rejected(self):
        # The emitter writes fixed values first, so an option on the parent replaces the
        # whole object and the fixed child is never sent.
        fields = {**self.KIND, "sort": {"flag": "--sort"}}
        with pytest.raises(ManifestError, match="collides with 'sort'"):
            self.build(self.entry({"sort.field": "id"}, fields))

    def test_a_value_on_a_path_an_option_spreads_to_is_rejected(self):
        # `parts` writes to paths other than the option's own name, so those count too.
        fields = {**self.KIND, "box": {"flag": "--box", "type": "double[]",
                                       "parts": ["sort.field"]}}
        with pytest.raises(ManifestError, match="collides with 'sort.field'"):
            self.build(self.entry({"sort.field": "id"}, fields))

    @pytest.mark.parametrize("declared", [None, [], False, "", {}])
    def test_a_declared_fixed_must_hold_something(self, declared):
        # Read as absence, each of these would pass review looking like a deliberate choice.
        with pytest.raises(ManifestError, match="at least one field name"):
            self.build(self.entry(declared, self.KIND))

    @pytest.mark.parametrize("value", [float("nan"), float("inf"), float("-inf")])
    def test_a_number_must_be_finite(self, value):
        # YAML reads `.nan` and `.inf` as floats; neither has a C# literal to emit.
        with pytest.raises(ManifestError, match="not a finite number"):
            self.build(self.entry({"score": value}, self.KIND))

    def test_a_nullable_wrapper_is_seen_through(self):
        with pytest.raises(ManifestError, match="`integer` in the spec"):
            self.build(self.entry({"offset": "1"}, self.KIND))
        assert self.build(self.entry({"offset": 1}, self.KIND)).body.fixed == [("offset", 1)]

    def test_an_object_field_cannot_be_fixed(self):
        with pytest.raises(ManifestError, match="string, integer, number and boolean"):
            self.build(self.entry({"sort": "x"}, self.KIND))

    def test_an_enum_value_must_be_one_the_spec_allows(self):
        with pytest.raises(ManifestError, match="must be one of"):
            self.build(self.entry({"order": "UP"}, self.KIND))
        assert self.build(self.entry({"order": "ASC"}, self.KIND)).body.fixed == [("order", "ASC")]

    def test_a_body_read_from_a_file_cannot_have_fixed_values(self):
        with pytest.raises(ManifestError, match="needs `fields:`"):
            self.build(self.entry({"limit": 1}, flag="--file"))

    @pytest.mark.parametrize("value", ["1", True, 1.5])
    def test_the_value_must_match_the_spec_type(self, value):
        # True is the subtle one: Python treats it as an int, the spec does not.
        with pytest.raises(ManifestError, match="`integer` in the spec"):
            self.build(self.entry({"limit": value}, self.KIND))

    def test_a_boolean_field_takes_a_boolean(self):
        body = self.build(self.entry({"trackTotalCount": False}, self.KIND)).body
        assert body.fixed == [("trackTotalCount", False)]

    @pytest.mark.parametrize("value", [[1], {"a": 1}, None])
    def test_the_value_must_be_a_single_value(self, value):
        with pytest.raises(ManifestError, match="single value"):
            self.build(self.entry({"limit": value}, self.KIND))

    @pytest.mark.parametrize("value", [2**31 - 1, -2**31])
    def test_an_integer_at_the_int32_bounds_is_accepted(self, value):
        assert self.build(self.entry({"limit": value}, self.KIND)).body.fixed == [("limit", value)]

    @pytest.mark.parametrize("value", [2**31, -2**31 - 1, 10**100])
    def test_an_integer_outside_int32_is_rejected(self, value):
        # Kiota types an unformatted integer as `int?`, and the body is deserialised into
        # that model before it is sent — so this fails at run time even where the literal
        # would compile, and 10**100 has no C# literal at all.
        with pytest.raises(ManifestError, match="outside `int`"):
            self.build(self.entry({"limit": value}, self.KIND))

    def test_an_int64_field_takes_the_long_range(self):
        assert self.build(self.entry({"version": 2**40}, self.KIND)).body.fixed == [("version", 2**40)]
        with pytest.raises(ManifestError, match="outside `long`"):
            self.build(self.entry({"version": 2**63}, self.KIND))

    def test_a_number_is_sent_as_a_double(self):
        assert self.build(self.entry({"score": 2}, self.KIND)).body.fixed == [("score", 2.0)]
        # Too large for a double: refused here, not left to a C# literal that cannot parse.
        with pytest.raises(ManifestError, match="not a finite number"):
            self.build(self.entry({"score": 10**400}, self.KIND))


class TestKeysThatWereAcceptedAndIgnored:
    """Keys the manifest reference used to have to describe as "accepted, no effect".

    Each was valid YAML, passed the unknown-key check, and did nothing, which is the failure
    the unknown-key check exists to prevent.
    """

    NO_BODY = {"responses": {"204": {"description": "done"}}}

    def test_a_param_type_is_rejected_because_the_spec_decides_it(self):
        with pytest.raises(ManifestError, match="'type'"):
            build(base(params={"recordId": {"flag": "--record-id", "type": "int"}}))

    def test_a_message_on_an_operation_that_returns_a_body_is_rejected(self):
        with pytest.raises(ManifestError, match="never be shown"):
            build(base(output={"message": "Restored"}))

    def test_a_message_on_an_operation_with_no_body_is_accepted(self):
        command = build(base(output={"message": "Deleted"}), {**OPERATION, **self.NO_BODY})
        assert command.message == "Deleted"

    @pytest.mark.parametrize("body", [
        {"flag": "--file", "fields": {}},
        {"fields": {}},
    ])
    def test_an_empty_fields_map_is_rejected(self, body):
        # Beside `flag:` it was accepted and did nothing.
        entry = {"command": "record search", "op": {"method": "post", "path": "/query"},
                 "body": body, "output": "raw"}
        with pytest.raises(ManifestError, match="is empty"):
            build(entry, BODY_OPERATION)

    def test_an_empty_message_on_an_operation_that_returns_a_body_is_rejected(self):
        # The key, not its value: `message: ""` is just as ineffective.
        with pytest.raises(ManifestError, match="never be shown"):
            build(base(output={"message": ""}))

    @pytest.mark.parametrize("key, value", [
        ("short", "-b"), ("required", False), ("help", "x"), ("wrap-single", True),
        ("collection", True), ("collection", False)])
    def test_file_only_body_keys_are_rejected_with_fields(self, key, value):
        entry = {"command": "record search", "op": {"method": "post", "path": "/query"},
                 "body": {key: value, "fields": {"kind": {"flag": "--kind"}}},
                 "output": "raw"}
        with pytest.raises(ManifestError, match=key):
            build(entry, BODY_OPERATION)


class TestBodyOptionAliases:
    """Body options were exempt from the global-alias check that params get."""

    @pytest.mark.parametrize("flag", ["--config", "-c", "--output", "--user", "--help"])
    def test_a_body_field_cannot_reuse_a_global_alias(self, flag):
        entry = {"command": "record search", "op": {"method": "post", "path": "/query"},
                 "body": {"fields": {"kind": {"flag": flag}}}, "output": "raw"}
        with pytest.raises(ManifestError, match="global option"):
            build(entry, BODY_OPERATION)

    def test_a_body_field_short_alias_is_checked_too(self):
        entry = {"command": "record search", "op": {"method": "post", "path": "/query"},
                 "body": {"fields": {"kind": {"flag": "--kind", "short": "-o"}}}, "output": "raw"}
        with pytest.raises(ManifestError, match="global option"):
            build(entry, BODY_OPERATION)

    def test_a_body_file_flag_is_checked_too(self):
        entry = {"command": "record add", "op": {"method": "post", "path": "/query"},
                 "body": {"flag": "--config"}, "output": "raw"}
        with pytest.raises(ManifestError, match="global option"):
            build(entry, BODY_OPERATION)


class TestArrayBodies:
    """A body built from `fields:` is always one JSON object, so it cannot serve an operation
    whose body is an array; that has to come from a file."""

    def test_fields_cannot_build_a_body_the_operation_takes_as_an_array(self):
        # `fields:` always builds one JSON object; deserialising it as a list fails at run
        # time, so an array body has to come from a file.
        array_operation = {
            "requestBody": {"content": {"application/json": {"schema": {
                "type": "array", "items": {"$ref": "#/components/schemas/Record"}}}}},
            "responses": {"200": {"content": {"application/json": {"schema": {"type": "object"}}}}},
        }
        entry = {"command": "record put", "op": {"method": "put", "path": "/records"},
                 "body": {"fields": {"kind": {"flag": "--kind"}}}, "output": "raw"}
        with pytest.raises(ManifestError, match="JSON array"):
            build(entry, array_operation)

        entry["body"] = {"flag": "--file", "wrap-single": True}
        assert build(entry, array_operation).body.collection

    INLINE_ARRAY = {
        "requestBody": {"content": {"application/json": {"schema": {
            "type": "array", "items": {"type": "object", "properties": {"kind": {"type": "string"}}}}}}},
        "responses": {"200": {"content": {"application/json": {"schema": {"type": "object"}}}}},
    }

    def test_an_explicit_model_does_not_hide_an_array_body_from_fields(self):
        # An inline array body needs `model:`, and naming the model used to skip looking at the
        # schema, so the array went unnoticed and `fields:` was accepted.
        entry = {"command": "record put", "op": {"method": "put", "path": "/records"},
                 "body": {"model": "RecordsPutRequestBody", "fields": {"kind": {"flag": "--kind"}}},
                 "output": "raw"}
        with pytest.raises(ManifestError, match="JSON array"):
            build(entry, self.INLINE_ARRAY)

    def test_an_explicit_model_on_an_array_body_is_read_as_a_collection(self):
        entry = {"command": "record put", "op": {"method": "put", "path": "/records"},
                 "body": {"model": "RecordsPutRequestBody", "flag": "--file"}, "output": "raw"}
        assert build(entry, self.INLINE_ARRAY).body.collection
