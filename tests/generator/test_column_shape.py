"""Column paths must match the shape the response actually has.

`record list` declared `id`, `version` and `kind` columns over Storage's
`DatastoreQueryResult`, whose `results` is an array of bare record ids. Resolving a property of
a string yields nothing, so the command printed a header and one blank row per record — the
request succeeded, the data came back, and the user saw an empty table. A tester found it by
comparing against the Python CLI.

It survived because that endpoint needs an entitlement the author lacks, so the command could
never be run. Being unrunnable does not make its output spec unverifiable: the spec says what
comes back.
"""

import pytest

from generate_cli import ManifestError, check_column_shape

SPEC = {
    "components": {
        "schemas": {
            "DatastoreQueryResult": {
                "type": "object",
                "properties": {
                    "cursor": {"type": "string"},
                    "results": {"type": "array", "items": {"type": "string"}},
                },
            },
            "RecordList": {
                "type": "object",
                "properties": {
                    "records": {"type": "array", "items": {"$ref": "#/components/schemas/Record"}},
                },
            },
            "Record": {"type": "object", "properties": {"id": {"type": "string"}}},
        }
    }
}


def operation(schema_name):
    return {"responses": {"200": {"content": {"application/json": {
        "schema": {"$ref": f"#/components/schemas/{schema_name}"}}}}}}


def entry(columns, root="results"):
    return {"command": "record list", "output": {"root": root, "columns": columns}}


def test_property_columns_on_a_string_array_are_rejected():
    with pytest.raises(ManifestError, match="array of string"):
        check_column_shape(entry({"Id": "id"}), operation("DatastoreQueryResult"), SPEC, "here")


def test_the_error_says_what_to_use_instead():
    with pytest.raises(ManifestError, match=r"Use '\.'"):
        check_column_shape(entry({"Id": "id"}), operation("DatastoreQueryResult"), SPEC, "here")


def test_the_self_path_is_accepted():
    check_column_shape(entry({"Id": "."}), operation("DatastoreQueryResult"), SPEC, "here")


def test_property_columns_on_an_array_of_objects_are_fine():
    check_column_shape(
        entry({"Id": "id"}, root="records"), operation("RecordList"), SPEC, "here")


def test_a_command_with_no_columns_is_not_checked():
    check_column_shape({"command": "x", "output": {"root": "results"}},
                       operation("DatastoreQueryResult"), SPEC, "here")


def test_an_operation_with_no_json_response_is_not_checked():
    check_column_shape(entry({"Id": "id"}), {"responses": {"200": {}}}, SPEC, "here")
