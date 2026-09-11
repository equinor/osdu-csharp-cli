"""Golden-file tests for the C# `emit_command` produces.

The rest of the suite tests what the generator *rejects*; the C# suite tests how the
generated tree *behaves*. Between them sits a gap: emission that is valid C# and compiles
cleanly, but is subtly the wrong C#. Nothing catches a query parameter assigned to the wrong
property, a guard that inverts, or a nested body that loses a level.

Each case below fixes one emission path. When output changes legitimately, review the diff
and re-record:

    UPDATE_GOLDEN=1 python3 -m pytest tests/generator/test_emission.py
"""

import os
import pathlib

import pytest

from generate_cli import Service, build_command, emit_command

GOLDEN = pathlib.Path(__file__).parent / "golden"

SPEC = {"components": {"schemas": {
    "QueryRequest": {"properties": {
        "kind": {"type": "string"},
        "limit": {"type": "integer", "format": "int32"},
        "returnedFields": {"type": "array", "items": {"type": "string"}},
        "sort": {"$ref": "#/components/schemas/SortQuery"},
        "spatialFilter": {"$ref": "#/components/schemas/SpatialFilter"},
    }},
    "SortQuery": {"properties": {
        "order": {"type": "array", "items": {"type": "string", "enum": ["ASC", "DESC"]}}}},
    "SpatialFilter": {"properties": {"field": {"type": "string"}}},
    "Record": {"properties": {"kind": {"type": "string"}}},
}}}

JSON_200 = {"200": {"content": {"application/json": {"schema": {"type": "object"}}}}}


def operation(*, parameters=(), body=None, responses=None):
    op = {"parameters": list(parameters), "responses": responses or JSON_200}
    if body:
        op["requestBody"] = {"content": {"application/json": {"schema": body}}}
    return op


def query(name, fmt=None):
    schema = {"type": "integer", "format": fmt} if fmt else {"type": "string"}
    return {"name": name, "in": "query", "schema": schema}


def path_param(name, fmt=None):
    schema = {"type": "integer", "format": fmt} if fmt else {"type": "string"}
    return {"name": name, "in": "path", "required": True, "schema": schema}


CASES = {
    "query_params_and_table": (
        {"command": "record list", "summary": "List records.",
         "op": {"method": "get", "path": "/query/records"},
         "params": {"kind": {"flag": "--kind", "short": "-k", "required": True,
                             "help": "Kind."},
                    "limit": {"flag": "--limit", "type": "int", "help": "Max."}},
         "output": {"root": "results", "columns": {"Id": "id", "Kind": "kind"}}},
        operation(parameters=[query("kind"), query("limit", "int32")]),
    ),
    "nested_path_indexers": (
        {"command": "record version get", "summary": "Get a version.",
         "op": {"method": "get", "path": "/records/{id}/{version}"},
         "params": {"id": {"flag": "--id", "required": True, "help": "Id."},
                    "version": {"flag": "--version", "required": True, "help": "Version."}},
         "output": "raw"},
        operation(parameters=[path_param("id"), path_param("version", "int64")]),
    ),
    "void_response_with_message": (
        {"command": "record delete", "summary": "Delete a record.",
         "op": {"method": "post", "path": "/records/{id}:delete"},
         "builder": "Records.WithIdDelete({id})",
         "params": {"id": {"flag": "--id", "required": True, "help": "Id."}},
         "output": {"message": "1 record deleted"}},
        operation(parameters=[path_param("id")],
                  responses={"204": {"description": "No Content"}}),
    ),
    "body_from_file": (
        {"command": "record add", "summary": "Add records.",
         "op": {"method": "put", "path": "/records"},
         "body": {"flag": "--file", "short": "-f", "required": True,
                  "help": "JSON file.", "wrap-single": True},
         "output": {"root": "recordIds"}},
        operation(body={"type": "array", "items": {"$ref": "#/components/schemas/Record"}}),
    ),
    "body_from_flags_with_enum": (
        {"command": "record search", "summary": "Search records.",
         "op": {"method": "post", "path": "/query"},
         "body": {"fields": {
             "kind": {"flag": "--kind", "short": "-k", "required": True, "help": "Kind."},
             "returnedFields": {"flag": "--returned-fields", "short": "-f",
                                "type": "string[]", "help": "Fields."},
             "sort.order": {"flag": "--sort-order", "type": "string[]", "help": "Order."}}},
         "output": {"root": "results", "columns-from": "returnedFields",
                    "total-from": "totalCount", "columns": {"Id": "id"}}},
        operation(body={"$ref": "#/components/schemas/QueryRequest"}),
    ),
    # Fixed values are written before the option-driven fields, including through a nested
    # parent, and never behind a guard: they are sent on every call.
    "body_with_fixed_values": (
        {"command": "record aggregate", "summary": "Count values.",
         "op": {"method": "post", "path": "/query"},
         "body": {"fixed": {"limit": 1, "spatialFilter.field": "data.Location"},
                  "fields": {
                      "kind": {"flag": "--kind", "required": True, "help": "Kind."}}},
         "output": {"root": "aggregations", "columns": {"Value": "key", "Count": "count"}}},
        operation(body={"$ref": "#/components/schemas/QueryRequest"}),
    ),
    "parts_and_validators": (
        {"command": "record geo", "summary": "Spatial search.",
         "op": {"method": "post", "path": "/query"},
         "body": {"fields": {
             "kind": {"flag": "--kind", "required": True, "help": "Kind."},
             "spatialFilter.field": {"flag": "--spatial-field", "help": "Geo field."},
             "spatialFilter.byBoundingBox": {
                 "flag": "--bbox", "type": "double[]", "help": "Box.",
                 "parts": ["spatialFilter.byBoundingBox.topLeft.latitude",
                           "spatialFilter.byBoundingBox.topLeft.longitude",
                           "spatialFilter.byBoundingBox.bottomRight.latitude",
                           "spatialFilter.byBoundingBox.bottomRight.longitude"]}}},
         # An arbitrary pair — these two are used together in reality. The point here is
         # to exercise the validator emission alongside `parts`, not to state a real rule.
         "mutually-exclusive": [["spatialFilter.field", "spatialFilter.byBoundingBox"]],
         "output": {"root": "results", "columns": {"Id": "id"}}},
        operation(body={"$ref": "#/components/schemas/QueryRequest"}),
    ),
    "require_one_of": (
        {"command": "crs get", "summary": "Get a CRS.",
         "op": {"method": "get", "path": "/v3/coordinate-reference-system"},
         "builder": "V3.CoordinateReferenceSystem",
         "params": {"recordId": {"flag": "--record-id", "help": "Record id."},
                    "dataId": {"flag": "--data-id", "help": "Data id."}},
         "require-one-of": ["recordId", "dataId"],
         "output": "raw"},
        operation(parameters=[query("recordId"), query("dataId")]),
    ),
}


def emit(entry, op):
    service = Service(name="test", client="Storage", models="Storage", description="Test.",
                      spec_file="test.yaml", commands=[], groups={})
    command = build_command(entry, op, "test", "Storage", SPEC)
    return "\n".join(emit_command(service, command)) + "\n"


@pytest.mark.parametrize("name", sorted(CASES))
def test_emission_matches_the_recorded_output(name):
    entry, op = CASES[name]
    produced = emit(entry, op)
    golden = GOLDEN / f"{name}.cs"

    if os.environ.get("UPDATE_GOLDEN"):
        golden.write_text(produced, encoding="utf-8")
        pytest.skip(f"recorded {golden.name}")

    assert golden.exists(), (
        f"{golden.name} has not been recorded. Review the output, then run "
        f"UPDATE_GOLDEN=1 python3 -m pytest")
    assert produced == golden.read_text(encoding="utf-8")
