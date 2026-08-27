"""What the generator works out from the spec, rather than being told."""

import pytest

from generate_cli import (ManifestError, camel, csharp_type, derive_builder, pascal,
                          resolve_field_schema, returns_value)


class TestBuilderDerivation:
    """Path to Kiota request-builder accessor."""

    @pytest.mark.parametrize("path,expected", [
        ("/info", "Info"),
        ("/query/records", "Query.Records"),
        ("/records/{id}", "Records[{id}]"),
        ("/records/{id}/{version}", "Records[{id}][{version}]"),
        ("/records/versions/{id}", "Records.Versions[{id}]"),
        ("/ddms/v3/wellbores/{record_id}/versions", "Ddms.V3.Wellbores[{record_id}].Versions"),
        ("/liveness_check", "Liveness_check"),
    ])
    def test_ordinary_paths_are_derived(self, path, expected):
        assert derive_builder(path) == expected

    def test_an_action_suffix_refuses_to_guess(self):
        # Kiota renders `/records/{id}:delete` as a method, not an indexer. Guessing would
        # emit something subtly wrong; the manifest states it with `builder:` instead.
        with pytest.raises(ManifestError, match="builder"):
            derive_builder("/records/{id}:delete")

    def test_a_hyphenated_segment_refuses_to_guess(self):
        with pytest.raises(ManifestError, match="cannot derive"):
            derive_builder("/log-recognition")


class TestTypeMapping:
    """Kiota honours `format`, so the CLI has to as well."""

    @pytest.mark.parametrize("schema,expected", [
        ({"type": "string"}, "string"),
        ({"type": "integer", "format": "int32"}, "int?"),
        ({"type": "integer", "format": "int64"}, "long?"),
        ({"type": "integer"}, "int?"),
        ({"type": "boolean"}, "bool?"),
        ({"type": "number", "format": "float"}, "float?"),
        ({"type": "number"}, "double?"),
        ({"type": "array", "items": {"type": "string"}}, "string[]"),
    ])
    def test_schema_maps_to_the_type_kiota_uses(self, schema, expected):
        assert csharp_type(schema) == expected

    def test_int64_is_not_int32(self):
        # `/records/{id}/{version}` has an int64 version, and Kiota's indexer takes a long.
        # Getting this wrong was a compile error, which is the point of generating C#.
        assert csharp_type({"type": "integer", "format": "int64"}) == "long?"


class TestResponseShape:
    def test_a_2xx_with_content_returns_a_value(self):
        assert returns_value({"responses": {"200": {"content": {"application/json": {}}}}})

    def test_a_204_returns_nothing(self):
        assert not returns_value({"responses": {"204": {"description": "No Content"}}})

    def test_an_error_response_does_not_count(self):
        assert not returns_value({"responses": {"400": {"content": {"application/json": {}}}}})


class TestNestedFieldSchema:
    """A dotted body field still has to find its schema, and its enum."""

    SPEC = {"components": {"schemas": {
        "SortQuery": {"properties": {
            "order": {"type": "array", "items": {"enum": ["ASC", "DESC"]}}}}}}}
    PROPERTIES = {"sort": {"$ref": "#/components/schemas/SortQuery"}}

    def test_a_dotted_name_resolves_through_the_ref(self):
        schema = resolve_field_schema(self.PROPERTIES, "sort.order", self.SPEC)
        assert schema["items"]["enum"] == ["ASC", "DESC"]

    def test_an_unknown_path_yields_nothing_rather_than_raising(self):
        assert resolve_field_schema(self.PROPERTIES, "sort.nonsense", self.SPEC) == {}


class TestIdentifierShaping:
    @pytest.mark.parametrize("name,expected", [
        ("record", "record"),
        ("record_id", "recordId"),
        ("unit-system", "unitSystem"),
        ("sort order", "sortOrder"),
    ])
    def test_camel(self, name, expected):
        assert camel(name) == expected

    @pytest.mark.parametrize("name,expected", [
        ("record", "Record"),
        ("crs_catalog", "CrsCatalog"),
        ("unit-system", "UnitSystem"),
    ])
    def test_pascal(self, name, expected):
        assert pascal(name) == expected


# ---- FastAPI's optional-parameter wrapper ------------------------------------------------

from generate_cli import kiota_param_type, normalise_schema, shared_enum_type

NULLABLE_INT = {"anyOf": [{"type": "integer", "format": "int64"}, {"type": "null"}]}
SPEC = {"components": {"schemas": {"JSONOrient": {"type": "string",
                                                 "enum": ["split", "columns"]}}}}


def test_a_nullable_wrapper_is_unwrapped_to_its_one_real_branch():
    assert normalise_schema(NULLABLE_INT, {})["type"] == "integer"


def test_a_ref_is_resolved_so_its_enum_is_visible():
    schema = normalise_schema({"$ref": "#/components/schemas/JSONOrient"}, SPEC)
    assert schema["enum"] == ["split", "columns"]


def test_a_genuine_union_is_left_alone():
    # Two real branches have no single C# type; picking the first would be a guess.
    union = {"anyOf": [{"type": "integer"}, {"type": "string"}]}
    assert normalise_schema(union, {}) == union


def test_the_wrapper_costs_the_format_because_kiota_loses_it_too():
    # Spec says int64, but Kiota emits `int?` for a wrapped parameter. Following the spec
    # here produces `long?` and code that does not compile.
    assert kiota_param_type(normalise_schema(NULLABLE_INT, {}), wrapped=True) == "int?"


def test_an_unwrapped_int64_still_maps_to_long():
    assert kiota_param_type({"type": "integer", "format": "int64"}, wrapped=False) == "long?"


def test_a_wrapped_array_collapses_the_way_kiota_collapses_it():
    wrapped_array = {"anyOf": [{"type": "array", "items": {"type": "string"}},
                               {"type": "null"}]}
    assert kiota_param_type(normalise_schema(wrapped_array, {}), wrapped=True) == "string"


def test_a_boolean_parameter_becomes_a_flag():
    # `Option<bool?>` renders as `--describe <describe>`; no CLI asks for `--describe true`.
    assert kiota_param_type({"type": "boolean", "default": False}, wrapped=True) == "bool"


def test_a_boolean_defaulting_to_true_keeps_its_nullable_form():
    # Sending false unconditionally would flip it, so this one cannot be a plain flag.
    assert kiota_param_type({"type": "boolean", "default": True}, wrapped=True) == "bool?"


def test_a_shared_enum_resolves_to_the_models_namespace():
    # A $ref'd enum is generated once under Models, not per endpoint.
    got = shared_enum_type("WellboreDdms", {"$ref": "#/components/schemas/JSONOrient"})
    assert got == "global::Equinor.OsduCsharpClient.WellboreDdms.Models.JSONOrient"


def test_an_inline_enum_has_no_shared_type():
    assert shared_enum_type("WellboreDdms", {"type": "string", "enum": ["a"]}) is None
