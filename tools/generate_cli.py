#!/usr/bin/env python3
"""Generate System.CommandLine command trees from OpenAPI specs + CLI manifests.

The OpenAPI spec says what a service *can* do. The manifest in ``cli-manifest/`` says what
the CLI *should* expose, what to call it, and what a human wants to see. This script joins
the two and emits C#; everything it cannot derive is a manifest field, and everything the
manifest does not account for is a build error.

Usage::

    python3 tools/generate_cli.py                 # generate
    python3 tools/generate_cli.py --check         # CI gate: fail on drift, write nothing

Mirrors the conventions of osdu-csharp-client/generate_all.py, which generates the client
layer this sits on top of.
"""

from __future__ import annotations

import argparse
import fnmatch
import json
import re
import sys
from dataclasses import dataclass, field
from pathlib import Path

import yaml

ROOT = Path(__file__).resolve().parent.parent
MANIFEST_DIR = ROOT / "cli-manifest"
SPECS_DIR = ROOT.parent / "osdu-csharp-client" / "openapi_specs"
OUTPUT_DIR = ROOT / "src" / "OsduCli" / "Commands" / "Generated"

HTTP_METHODS = ("get", "put", "post", "delete", "patch")


class ManifestError(Exception):
    """A manifest is inconsistent with its spec. Always fatal — never generate through one."""


class _NoTimestampLoader(yaml.SafeLoader):
    """SafeLoader that leaves ISO date/datetime values as strings.

    Same reason as in osdu-csharp-client/generate_all.py: OpenAPI ``example`` values like
    ``2021-01-26T02:24:13.843Z`` otherwise become ``datetime`` objects.
    """


_NoTimestampLoader.yaml_implicit_resolvers = {
    k: [(tag, regexp) for tag, regexp in v if tag != "tag:yaml.org,2002:timestamp"]
    for k, v in yaml.SafeLoader.yaml_implicit_resolvers.items()
}


# --------------------------------------------------------------------------------------
# Loading
# --------------------------------------------------------------------------------------

def resolve_spec(name: str) -> Path:
    """Locate a service's OpenAPI document.

    osdu-csharp-client keeps specs at ``openapi_specs/<service>/openapi.{yaml,json}``. The
    older flat ``openapi_specs/<Service>.yaml`` layout is still accepted so this works
    against either checkout while that restructuring lands — CI builds the client's default
    branch, which is still flat.

    The flat filenames are not a mechanical transform of the service name: ``crs_catalog``
    is ``CRS_Catalog.yaml``, ``schema_service`` is ``Schema_Service.yaml``, ``unit/v3`` is
    ``Unit_v3.yaml``. Rather than encode that, the fallback compares names with separators
    and case removed, which matches all of them and any similar spelling.
    """
    candidates = [
        SPECS_DIR / name / "openapi.yaml",
        SPECS_DIR / name / "openapi.json",
        SPECS_DIR / name,          # a literal filename, if a manifest gives one
    ]
    for candidate in candidates:
        if candidate.is_file():
            return candidate

    def normalise(value: str) -> str:
        return re.sub(r"[^a-z0-9]", "", value.lower())

    wanted = normalise(name)
    if SPECS_DIR.is_dir():
        for candidate in sorted(SPECS_DIR.iterdir()):
            if (candidate.is_file()
                    and candidate.suffix.lower() in {".yaml", ".yml", ".json"}
                    and normalise(candidate.stem) == wanted):
                return candidate

    raise ManifestError(
        f"no spec found for {name!r}. Looked for "
        + ", ".join(str(c.relative_to(SPECS_DIR.parent)) for c in candidates)
        + f", and any file in openapi_specs matching {wanted!r} ignoring case and separators"
    )


def load_spec(path: Path) -> dict:
    text = path.read_text(encoding="utf-8")
    if path.suffix.lower() in {".yaml", ".yml"}:
        return yaml.load(text, Loader=_NoTimestampLoader)
    return json.loads(text)


def spec_operations(spec: dict) -> dict[tuple[str, str], dict]:
    """Index a spec's operations by ``(method, path)``.

    Keyed on method+path rather than ``operationId`` because OpenAPI guarantees the former
    is unique and OSDU specs do not always give the latter (Storage has both
    ``getAllRecords`` and ``getAllRecords_1``).
    """
    operations: dict[tuple[str, str], dict] = {}
    for path, item in (spec.get("paths") or {}).items():
        for method, operation in (item or {}).items():
            if method in HTTP_METHODS:
                operations[(method, path)] = operation
    return operations


def deprecated_operations(operations: dict[tuple[str, str], dict]) -> set[tuple[str, str]]:
    """Operations the spec marks ``deprecated: true``.

    These are dropped from the coverage requirement rather than needing an `exclude:` entry
    each: OSDU retires endpoints in bulk (all 28 Unit v2 operations at once), and 28 hand
    written exclusions all saying "deprecated" would bury the real editorial ones. Mapping
    one to a command is an error — see build_service.
    """
    return {key for key, operation in operations.items() if operation.get("deprecated")}


# --------------------------------------------------------------------------------------
# Naming
# --------------------------------------------------------------------------------------

def kiota_segment(segment: str) -> str:
    """PascalCase a path segment the way Kiota names the request-builder property.

    Kiota capitalises the first character and leaves the rest alone, so ``liveness_check``
    becomes ``Liveness_check`` and ``v3`` becomes ``V3``.
    """
    return segment[:1].upper() + segment[1:]


def camel(name: str) -> str:
    parts = re.split(r"[_\-\s]+", name)
    return parts[0].lower() + "".join(p[:1].upper() + p[1:] for p in parts[1:])


def pascal(name: str) -> str:
    parts = re.split(r"[_\-\s]+", name)
    return "".join(p[:1].upper() + p[1:] for p in parts)


def csharp_string(value: str) -> str:
    return '"' + value.replace("\\", "\\\\").replace('"', '\\"').replace("\n", " ").strip() + '"'


# --------------------------------------------------------------------------------------
# Derivation from the spec
# --------------------------------------------------------------------------------------

def kiota_enum_type(models_root: str, path: str, method: str, param: str) -> str:
    """Fully-qualified name of the enum Kiota generates for a constrained query parameter.

    Kiota mirrors the URL in its namespace — ``/groups/{group_email}/members`` becomes
    ``Groups.Item.Members`` — and names the type after the operation and parameter. Derived
    rather than declared in the manifest because it is entirely mechanical, and a wrong
    guess is a compile error rather than a runtime one.
    """
    segments = []
    for segment in [s for s in path.split("/") if s]:
        segments.append("Item" if segment.startswith("{") else pascal(segment))
    namespace = ".".join([f"global::Equinor.OsduCsharpClient.{models_root}"] + segments)
    return f"{namespace}.{method.capitalize()}{pascal(param)}QueryParameterType"


def derive_builder(path: str) -> str:
    """Derive the Kiota request-builder accessor chain for an OpenAPI path.

    ``/records/{id}/{version}`` becomes ``Records[{id}][{version}]``; ``/query/records``
    becomes ``Query.Records``. Placeholders stay in ``{name}`` form and are substituted with
    C# variables by the emitter.

    Refuses to guess for segments Kiota renames non-obviously — anything with a ``:`` action
    suffix or a character outside ``[A-Za-z0-9_]``. Those need an explicit ``builder:`` in
    the manifest, which is a one-line fix rather than a silently wrong call.
    """
    accessor = ""
    for segment in [s for s in path.split("/") if s]:
        if segment.startswith("{") and segment.endswith("}"):
            accessor += f"[{segment}]"
            continue
        if not re.fullmatch(r"[A-Za-z0-9_]+", segment):
            raise ManifestError(
                f"cannot derive a Kiota accessor for path segment {segment!r} in {path!r}; "
                f"add an explicit `builder:` to the manifest entry"
            )
        accessor += ("." if accessor else "") + kiota_segment(segment)
    return accessor


def returns_value(operation: dict) -> bool:
    """True when the operation has a 2xx response carrying a body.

    Determines whether the emitted call is ``var result = await ...`` or a bare ``await``.

    Statuses that are defined to have no body are ignored even when the spec attaches a
    schema to them, which several OSDU specs do: Legal's ``DELETE /legaltags/{name}``
    declares a 204 whose content is an enum of every HTTP status name. Kiota reads those
    the same way and generates a void method, so trusting the spec here would emit
    ``var result = await ...`` against a void call and fail to compile.
    """
    bodyless = {"204", "205", "304"}
    for status, response in (operation.get("responses") or {}).items():
        status = str(status)
        if status.startswith("2") and status not in bodyless and (response or {}).get("content"):
            return True
    return False


def csharp_type(schema: dict) -> str:
    """Map an OpenAPI parameter schema to a nullable C# type.

    Follows Kiota's own mapping, including ``format`` — it renders ``integer`` as ``int``
    or ``long`` depending on ``int32`` vs ``int64``, and a path parameter is passed
    straight into a request-builder indexer typed that way. Getting this wrong is a
    compile error rather than a runtime one, which is the point of generating C#.
    """
    kind = schema.get("type")
    if kind == "array":
        return csharp_type(schema.get("items") or {"type": "string"}).rstrip("?") + "[]"
    fmt = schema.get("format")
    if kind == "integer":
        return "long?" if fmt == "int64" else "int?"
    if kind == "number":
        return "float?" if fmt == "float" else "double?"
    if kind == "boolean":
        return "bool?"
    return "string"


def resolve_field_schema(properties: dict, dotted_name: str, spec: dict) -> dict:
    """Resolve a possibly-dotted body field name to its schema.

    ``sort.order`` has to reach the enum on Search's ``SortQuery``; looking only at the
    top-level properties would find nothing and silently drop the allowed values.
    """
    schemas = (spec.get("components") or {}).get("schemas") or {}
    current, schema = properties, {}
    for segment in dotted_name.split("."):
        schema = current.get(segment) or {}
        if ref := schema.get("$ref"):
            schema = schemas.get(ref.rsplit("/", 1)[-1]) or {}
        current = schema.get("properties") or {}
    return schema


def body_schema_properties(operation: dict, spec: dict) -> dict:
    """
    Return the property schemas of an operation's JSON request body, resolving one level
    of ``$ref`` into ``components/schemas``.

    Used so that a ``body: { fields: … }`` entry inherits whatever the spec already knows
    about each field — currently its ``enum``. The manifest names which fields to expose
    and what to call the flags; it should not have to restate the allowed values, which
    would then silently rot when the spec changes.
    """
    content = ((operation.get("requestBody") or {}).get("content") or {}).get("application/json")
    schema = (content or {}).get("schema") or {}
    if schema.get("type") == "array":
        schema = schema.get("items") or {}
    if ref := schema.get("$ref"):
        name = ref.rsplit("/", 1)[-1]
        schema = ((spec.get("components") or {}).get("schemas") or {}).get(name) or {}
    return schema.get("properties") or {}


def body_model(operation: dict, spec_title: str) -> tuple[str, bool]:
    """Return ``(model class name, is_collection)`` for an operation's JSON request body."""
    content = ((operation.get("requestBody") or {}).get("content") or {}).get("application/json")
    if not content or "schema" not in content:
        raise ManifestError(f"{spec_title}: request body has no application/json schema")

    schema = content["schema"]
    collection = schema.get("type") == "array"
    if collection:
        schema = schema.get("items") or {}

    ref = schema.get("$ref")
    if not ref:
        raise ManifestError(
            f"{spec_title}: request body schema is inline, not a $ref; "
            f"add an explicit `model:` to the manifest entry"
        )
    return ref.rsplit("/", 1)[-1], collection


# --------------------------------------------------------------------------------------
# Model
# --------------------------------------------------------------------------------------

@dataclass
class Param:
    name: str            # OpenAPI parameter name
    location: str        # "path" | "query"
    flag: str
    short: str | None
    required: bool
    help: str
    cs_type: str          # nullable form, e.g. "long?" or "string"
    enum_values: list[str] = field(default_factory=list)
    enum_type: str = ""   # fully-qualified Kiota enum, when the spec constrains the values
    var: str = ""
    option_var: str = ""

    def __post_init__(self):
        self.var = camel(self.name)
        self.option_var = f"{self.var}Option"

    @property
    def is_value_type(self) -> bool:
        return self.cs_type.endswith("?")

    @property
    def option_type(self) -> str:
        """A required value-type option is declared non-nullable so the read needs no unwrap.

        `Option<long?>` would hand a `long?` to an indexer typed `long`; `Option<long>` with
        `Required = true` hands over a `long` directly.
        """
        return self.cs_type[:-1] if self.required and self.is_value_type else self.cs_type

    @property
    def bang(self) -> str:
        """Required reference types still read as nullable from ParseResult."""
        return "!" if self.required and not self.is_value_type else ""


@dataclass
class BodyField:
    """One JSON property of a request body, supplied by a command-line option."""
    name: str
    flag: str
    short: str | None
    required: bool
    help: str
    cs_type: str
    enum_values: list[str]

    @property
    def var(self) -> str:
        # `sort.order` -> sortOrderValue: dots cannot appear in an identifier, and two
        # fields under different parents must not collide.
        return camel(self.name.replace(".", "_")) + "Value"

    @property
    def option_var(self) -> str:
        return camel(self.name.replace(".", "_")) + "BodyOption"

    @property
    def is_value_type(self) -> bool:
        return self.cs_type in {"int", "bool", "double"}

    @property
    def is_collection(self) -> bool:
        return self.cs_type.endswith("[]")


@dataclass
class Body:
    flag: str
    short: str | None
    required: bool
    help: str
    model: str
    collection: bool
    wrap_single: bool
    fields: list[BodyField] = field(default_factory=list)
    var: str = "fileOption"

    @property
    def from_fields(self) -> bool:
        """True when the body is assembled from options rather than read from a file."""
        return bool(self.fields)


@dataclass
class Command:
    path: list[str]       # e.g. ["storage", "version", "get"]
    summary: str
    method: str
    builder: str
    params: list[Param]
    body: Body | None
    spec_path: str
    require_one_of: list[str]
    mutually_exclusive: list[str]
    returns: bool
    root: str | None
    columns: list[tuple[str, str]]
    columns_from: str | None
    total_from: str | None
    message: str | None

    @property
    def name(self) -> str:
        return self.path[-1]

    @property
    def builder_method(self) -> str:
        return self.method.capitalize() + "Async"

    @property
    def factory(self) -> str:
        # Full path, not path[1:]: one manifest can own several nouns (Entitlements has both
        # `group list` and `member list`), and those would otherwise both be BuildList.
        return "Build" + "".join(pascal(p) for p in self.path)


@dataclass
class Service:
    name: str
    client: str
    models: str
    description: str
    spec_file: str
    commands: list[Command]
    groups: dict[str, str]
    coverage: "Coverage" = field(default=None)  # type: ignore[assignment]

    @property
    def class_name(self) -> str:
        return pascal(self.name) + "Commands"

    @property
    def roots(self) -> list[str]:
        """Top-level nouns this service contributes, in manifest order."""
        return list(dict.fromkeys(c.path[0] for c in self.commands))

    @property
    def nodes(self) -> list[tuple[str, ...]]:
        """Every group node on the way to a leaf, shortest first, deduplicated."""
        seen: list[tuple[str, ...]] = []
        for command in self.commands:
            for depth in range(1, len(command.path)):
                if command.path[:depth] not in seen:
                    seen.append(tuple(command.path[:depth]))
        return seen


@dataclass
class Coverage:
    generated: int
    handwritten: int
    excluded: int
    deprecated: int
    in_scope: int
    total: int
    untriaged: list[tuple[str, str]]
    missing: list[tuple[str, str]]


# --------------------------------------------------------------------------------------
# Manifest -> model
# --------------------------------------------------------------------------------------

def op_key(entry: dict, where: str) -> tuple[str, str]:
    op = entry.get("op")
    if not op or "method" not in op or "path" not in op:
        raise ManifestError(f"{where}: entry is missing `op: {{ method, path }}`")
    return op["method"].lower(), op["path"]


def build_service(manifest_path: Path) -> Service:
    manifest = yaml.safe_load(manifest_path.read_text(encoding="utf-8"))
    name = manifest["service"]
    spec_path = resolve_spec(manifest["spec"])
    spec = load_spec(spec_path)
    operations = spec_operations(spec)
    deprecated = deprecated_operations(operations)
    # Error messages name the spec by its path within openapi_specs; both layouts give
    # something a reader can find on disk.
    spec_label = spec_path.relative_to(SPECS_DIR).as_posix()
    commands: list[Command] = []
    claimed: set[tuple[str, str]] = set()

    def refuse_if_deprecated(key: tuple[str, str], where: str) -> None:
        if key in deprecated:
            raise ManifestError(
                f"{where}: {key[0].upper()} {key[1]} is marked deprecated in "
                f"{spec_label} and must not be exposed. If the spec is wrong, fix it "
                f"upstream; do not work around it here."
            )

    for entry in manifest.get("commands") or []:
        where = f"{manifest_path.name}: {entry.get('command', '<unnamed>')}"
        key = op_key(entry, where)
        if key not in operations:
            raise ManifestError(
                f"{where}: {key[0].upper()} {key[1]} is not in {spec_label}. "
                f"The endpoint was renamed or removed upstream."
            )
        refuse_if_deprecated(key, where)
        claimed.add(key)
        commands.append(build_command(entry, operations[key], where,
                                      manifest.get("models", manifest["client"]), spec))

    for entry in manifest.get("handwritten") or []:
        where = f"{manifest_path.name}: handwritten {entry.get('command', '?')}"
        key = op_key(entry, where)
        if key not in operations:
            raise ManifestError(f"{where}: {key[0].upper()} {key[1]} is not in {spec_label}")
        refuse_if_deprecated(key, where)
        claimed.add(key)

    for entry in manifest.get("exclude") or []:
        key = op_key(entry, f"{manifest_path.name}: exclude")
        if key not in operations:
            raise ManifestError(
                f"{manifest_path.name}: excluded {key[0].upper()} {key[1]} is not in "
                f"{spec_label} — the exclusion is stale and should be deleted"
            )
        if key in deprecated:
            raise ManifestError(
                f"{manifest_path.name}: excluded {key[0].upper()} {key[1]} is already "
                f"excluded automatically because {spec_label} marks it deprecated — "
                f"delete the entry"
            )
        if not entry.get("reason"):
            raise ManifestError(
                f"{manifest_path.name}: exclude {key[0].upper()} {key[1]} needs a `reason:`"
            )
        claimed.add(key)

    spec_version = ((spec.get("info") or {}).get("version") or "").strip()

    service = Service(
        name=name,
        client=manifest["client"],
        # Kiota's namespace does not always match the facade property: the File service is
        # OsduClient.File but Equinor.OsduCsharpClient.FileNamespace, because `File` collides
        # with System.IO.File.
        models=manifest.get("models", manifest["client"]),
        description=manifest.get("description", f"{name} service."),
        # The manifest's declared spec name and the spec's own version, not the on-disk
        # path: osdu-csharp-client is mid-restructure and the same document lives at
        # openapi_specs/storage/openapi.yaml on one branch and openapi_specs/Storage.yaml
        # on another. Embedding the path would make generated output differ between
        # checkouts and fail CI's `git diff --exit-code` staleness gate for no real reason.
        spec_file=f"{manifest['spec']}{f' ({spec_version})' if spec_version else ''}",
        commands=commands,
        groups=manifest.get("groups") or {},
    )
    service.coverage = compute_coverage(operations, claimed, manifest.get("scope"),
                                        len(commands),
                                        len(manifest.get("handwritten") or []),
                                        len(manifest.get("exclude") or []),
                                        deprecated)
    return service


def build_command(entry: dict, operation: dict, where: str, models_root: str,
                  spec: dict) -> Command:
    path = entry["command"].split()
    spec_params = {p["name"]: p for p in (operation.get("parameters") or [])}

    params: list[Param] = []
    for name, cfg in (entry.get("params") or {}).items():
        if name not in spec_params:
            raise ManifestError(
                f"{where}: parameter {name!r} is not declared on this operation. "
                f"Available: {sorted(spec_params)}"
            )
        spec_param = spec_params[name]
        location = spec_param.get("in")
        if location not in ("path", "query"):
            raise ManifestError(
                f"{where}: parameter {name!r} is `in: {location}` — only path and query "
                f"parameters can be mapped to options"
            )
        schema = spec_param.get("schema") or {}
        enum_values = [str(v) for v in (schema.get("enum") or [])]
        params.append(Param(
            name=name,
            location=location,
            flag=cfg["flag"],
            short=cfg.get("short"),
            required=bool(cfg.get("required", spec_param.get("required", False))),
            help=cfg.get("help", spec_param.get("description", "")),
            cs_type=csharp_type(schema),
            enum_values=enum_values,
            # Kiota types a constrained query parameter as a generated enum, so passing the
            # raw string does not compile. Path parameters stay strings — they go into an
            # indexer, which Kiota leaves untyped.
            enum_type=(kiota_enum_type(models_root, entry["op"]["path"],
                                       entry["op"]["method"], name)
                       if enum_values and location == "query" else ""),
        ))

    body = None
    if body_cfg := entry.get("body"):
        # Only interrogate the spec when the manifest hasn't already named the model.
        # Kiota synthesises a type for every inline body schema, so an inline schema is
        # workable — it just can't be *derived*, and the manifest has to say the name.
        if "model" in body_cfg:
            model, collection = body_cfg["model"], False
        else:
            model, collection = body_model(operation, where)
        fields_cfg = body_cfg.get("fields") or {}
        if fields_cfg and "flag" in body_cfg:
            raise ManifestError(
                f"{where}: `body:` has both `flag:` and `fields:`. A body is either read "
                "from a file or assembled from options, not both."
            )
        if not fields_cfg and "flag" not in body_cfg:
            raise ManifestError(
                f"{where}: `body:` needs either `flag:` (read a JSON file) or `fields:` "
                "(assemble the body from options)."
            )

        fields = []
        field_schemas = body_schema_properties(operation, spec)
        for field_name, field_cfg in fields_cfg.items():
            if "flag" not in field_cfg:
                raise ManifestError(f"{where}: body field {field_name!r} needs a `flag:`")
            field_schema = resolve_field_schema(field_schemas, field_name, spec)
            fields.append(BodyField(
                name=field_name,
                flag=field_cfg["flag"],
                short=field_cfg.get("short"),
                required=bool(field_cfg.get("required", False)),
                help=field_cfg.get("help", ""),
                cs_type=field_cfg.get("type", "string"),
                # An array field carries its enum on `items`, not on the property. Missing
                # that would silently drop the allowed values for exactly the fields most
                # likely to have them — projections and filters are usually lists.
                enum_values=[str(v) for v in (
                    field_schema.get("enum")
                    or (field_schema.get("items") or {}).get("enum")
                    or [])],
            ))

        body = Body(
            flag=body_cfg.get("flag", ""),
            short=body_cfg.get("short"),
            required=bool(body_cfg.get("required", True)),
            help=body_cfg.get("help", "JSON file containing the request body."),
            model=body_cfg.get("model", model),
            collection=body_cfg.get("collection", collection),
            wrap_single=bool(body_cfg.get("wrap-single", False)),
            fields=fields,
        )

    # `require-one-of` names params of which at least one must be supplied. The spec cannot
    # express this — it marks each optional, because each is individually optional — so the
    # service answers a request with neither by rejecting it. Checking at parse time means a
    # missing argument costs nothing: no config load, no token, no round trip.
    require_one_of = [str(name) for name in (entry.get("require-one-of") or [])]
    for name in require_one_of:
        if name not in (entry.get("params") or {}):
            raise ManifestError(
                f"{where}: require-one-of names {name!r}, which is not one of this "
                f"command's params: {sorted((entry.get('params') or {}))}")
    if len(require_one_of) == 1:
        raise ManifestError(
            f"{where}: require-one-of needs at least two params; a single mandatory param "
            f"should be marked `required: true` instead")

    # `mutually-exclusive` names options that contradict each other. Search's
    # returnedFields and excludedFields are the case: one says "only these", the other
    # "everything but these", and the service does not document what it does with both.
    # Rejecting at parse time beats finding out.
    mutually_exclusive = [str(name) for name in (entry.get("mutually-exclusive") or [])]
    selectable = set(entry.get("params") or {}) | set(
        ((entry.get("body") or {}).get("fields") or {}))
    for name in mutually_exclusive:
        if name not in selectable:
            raise ManifestError(
                f"{where}: mutually-exclusive names {name!r}, which is neither a param nor "
                f"a body field of this command: {sorted(selectable)}")
    if len(mutually_exclusive) == 1:
        raise ManifestError(
            f"{where}: mutually-exclusive needs at least two names to conflict")

    builder = entry.get("builder") or derive_builder(entry["op"]["path"])

    output = entry.get("output") or {}
    if output == "raw":
        output = {}
    if not isinstance(output, dict):
        raise ManifestError(f"{where}: `output:` must be a mapping or the literal `raw`")

    columns_from = output.get("columns-from")
    if columns_from:
        body_field_names = {name for name in ((entry.get("body") or {}).get("fields") or {})}
        if columns_from not in body_field_names:
            raise ManifestError(
                f"{where}: columns-from names {columns_from!r}, which is not one of this "
                f"command's body fields: {sorted(body_field_names)}")

    return Command(
        path=path,
        summary=entry.get("summary", ""),
        method=entry["op"]["method"].lower(),
        builder=builder,
        params=params,
        body=body,
        spec_path=entry["op"]["path"],
        require_one_of=require_one_of,
        mutually_exclusive=mutually_exclusive,
        returns=returns_value(operation),
        root=output.get("root"),
        columns=list((output.get("columns") or {}).items()),
        columns_from=columns_from,
        total_from=output.get("total-from"),
        message=output.get("message"),
    )


def compute_coverage(operations, claimed, scope, generated, handwritten, excluded,
                     deprecated=frozenset()) -> Coverage:
    def in_scope(path: str) -> bool:
        return not scope or any(fnmatch.fnmatch(path, pattern) for pattern in scope)

    # Deprecated operations are not part of the surface the manifest has to account for.
    live = {key for key in operations if key not in deprecated}
    scoped = {key for key in live if in_scope(key[1])}
    return Coverage(
        generated=generated,
        handwritten=handwritten,
        excluded=excluded,
        deprecated=len(deprecated),
        in_scope=len(scoped),
        total=len(live),
        untriaged=sorted(key for key in live if key not in scoped and key not in claimed),
        missing=sorted(scoped - claimed),
    )


# --------------------------------------------------------------------------------------
# Emitting C#
# --------------------------------------------------------------------------------------

HEADER = """// <auto-generated/>
//
// Generated by tools/generate_cli.py from:
//   spec: {spec}
//   manifest: cli-manifest/{manifest}
//
// Do not edit. Behaviour that the manifest can express belongs in the manifest; anything
// it cannot belongs in a partial class under Commands/Handwritten/ via the Customize hook.
"""


def option_declaration(flag: str, short: str | None, cs_type: str, help_text: str,
                       required: bool, var: str, allowed: list[str] | None = None) -> list[str]:
    aliases = f'{csharp_string(flag)}, {csharp_string(short)}' if short else csharp_string(flag)
    if allowed:
        # Put the permitted values in the help text as well as enforcing them. `-h` is the
        # only discovery mechanism a user has before running the command, so an option that
        # rejects everything except six magic strings has to say which six.
        listed = ", ".join(allowed)
        help_text = f"{help_text.rstrip()} One of: {listed}." if help_text else f"One of: {listed}."
    lines = [f"        var {var} = new Option<{cs_type}>({aliases})", "        {"]
    if help_text:
        lines.append(f"            Description = {csharp_string(help_text)},")
    if required:
        lines.append("            Required = true,")
    if cs_type.endswith("[]"):
        lines.append("            AllowMultipleArgumentsPerToken = true,")
    lines += ["        };"]
    if allowed:
        # Rejects a bad value at parse time with the alternatives listed, and completes them
        # on TAB — neither needs the network or a token.
        values = ", ".join(csharp_string(value) for value in allowed)
        lines.append(f"        {var}.AcceptOnlyFromAmong({values});")
    return lines


def output_expression(command: Command) -> str:
    """The OutputSpec expression for a command, as C# source.

    With ``columns-from`` the spec is chosen at run time: the fields the caller projected
    become the columns, because otherwise the manifest's fixed columns would be rendered and
    the projected fields — fetched, at the user's request — would not appear.
    """
    if command.columns_from:
        field = next(f for f in command.body.fields if f.name == command.columns_from)
        default = _static_output_expression(command)
        root = csharp_string(command.root) if command.root else "null"
        return (f"{field.var} is {{ Length: > 0 }}\n"
                f"                    ? OutputSpec.FromFields({root}, {field.var})\n"
                f"                    : {default}")
    return _static_output_expression(command)


def _static_output_expression(command: Command) -> str:
    root = csharp_string(command.root) if command.root else "null"
    if command.columns:
        columns = ", ".join(f"({csharp_string(h)}, {csharp_string(p)})" for h, p in command.columns)
        return f"OutputSpec.Table({root}, {columns})"
    if command.root:
        return f"OutputSpec.Unwrap({csharp_string(command.root)})"
    return "OutputSpec.Raw"


def emit_command(service: Service, command: Command) -> list[str]:
    query = [p for p in command.params if p.location == "query"]
    lines: list[str] = []

    lines.append(f"    /// <summary>{command.summary}</summary>")
    lines.append(f"    /// <remarks>{command.method.upper()} {command.spec_path} "
                 f"on the {service.client} service.</remarks>")
    lines.append(f"    private static Command {command.factory}()")
    lines.append("    {")

    for param in command.params:
        lines += option_declaration(param.flag, param.short, param.option_type, param.help,
                                    param.required, param.option_var, param.enum_values)
    if command.body:
        if command.body.from_fields:
            for bodyfield in command.body.fields:
                cs_type = bodyfield.cs_type + ("?" if bodyfield.is_value_type else "")
                lines += option_declaration(bodyfield.flag, bodyfield.short, cs_type,
                                            bodyfield.help, bodyfield.required,
                                            bodyfield.option_var,
                                            allowed=bodyfield.enum_values)
        else:
            lines += option_declaration(command.body.flag, command.body.short, "string",
                                        command.body.help, command.body.required,
                                        command.body.var)

    lines.append("")
    lines.append(f"        var command = new Command({csharp_string(command.name)}, "
                 f"{csharp_string(command.summary)});")
    for param in command.params:
        lines.append(f"        command.Options.Add({param.option_var});")
    if command.body:
        if command.body.from_fields:
            for bodyfield in command.body.fields:
                lines.append(f"        command.Options.Add({bodyfield.option_var});")
        else:
            lines.append(f"        command.Options.Add({command.body.var});")

    if command.require_one_of:
        by_name = {param.name: param for param in command.params}
        chosen = [by_name[name] for name in command.require_one_of]
        flags = " or ".join(param.flag for param in chosen)
        checks = " && ".join(f"result.GetResult({param.option_var}) is null" for param in chosen)
        lines.append("")
        lines.append("        // A parse-time validator, so this costs no config load, no")
        lines.append("        // token and no round trip. The service rejects the request")
        lines.append("        // anyway; it should not have to.")
        lines.append("        command.Validators.Add(result =>")
        lines.append("        {")
        lines.append(f"            if ({checks})")
        lines.append(f"                result.AddError({csharp_string('One of ' + flags + ' is required.')});")
        lines.append("        });")

    if command.mutually_exclusive:
        by_name = {param.name: param for param in command.params}
        by_name.update({field.name: field for field in (command.body.fields if command.body else [])})
        chosen = [by_name[name] for name in command.mutually_exclusive]
        flags = " and ".join(item.flag for item in chosen)
        checks = " && ".join(f"result.GetResult({item.option_var}) is not null" for item in chosen)
        lines.append("")
        lines.append("        // Contradictory options, rejected before the service has to")
        lines.append("        // decide which one it believes.")
        lines.append("        command.Validators.Add(result =>")
        lines.append("        {")
        lines.append(f"            if ({checks})")
        lines.append(f"                result.AddError({csharp_string(flags + ' cannot be used together.')});")
        lines.append("        });")

    lines.append("")
    lines.append("        command.SetAction((parseResult, cancellationToken) =>")
    lines.append("            CliRunner.RunAsync(parseResult, async (context, cancellationToken) =>")
    lines.append("        {")

    for param in command.params:
        lines.append(f"            var {param.var} = "
                     f"parseResult.GetValue({param.option_var}){param.bang};")

    if command.body:
        if command.body.from_fields:
            # Assembled from options rather than read from a file. Built as JSON and then
            # put through the same Kiota deserialiser as a file body, so the request is
            # identical either way and the model stays the single source of truth for shape.
            lines.append("            var bodyNode = new JsonObject();")
            for bodyfield in command.body.fields:
                value = (f"parseResult.GetValue({bodyfield.option_var})"
                         f"{'!' if bodyfield.required and not bodyfield.is_value_type else ''}")
                lines.append(f"            var {bodyfield.var} = {value};")
                if bodyfield.is_collection:
                    assign = (f"new JsonArray({bodyfield.var}"
                              f".Select(item => (JsonNode)JsonValue.Create(item)!).ToArray())")
                else:
                    assign = f"JsonValue.Create({bodyfield.var})"
                if "." in bodyfield.name:
                    *parents, leaf = bodyfield.name.split(".")
                    target = "bodyNode"
                    for parent in parents:
                        target = f"CliContext.Child({target}, {csharp_string(parent)})"
                    slot = f"{target}[{csharp_string(leaf)}]"
                else:
                    slot = f"bodyNode[{csharp_string(bodyfield.name)}]"

                if bodyfield.required:
                    lines.append(f"            {slot} = {assign};")
                else:
                    # Omitted rather than null: OSDU services treat an explicit null as a
                    # value and reject it where they would happily accept a missing key.
                    # An unsupplied array option arrives as an empty array rather than null,
                    # so length is checked too — otherwise every request carried a spurious
                    # `"attributes":[]`, which states "project nothing" as loudly as a real
                    # projection would.
                    guard = (f"{bodyfield.var} is {{ Length: > 0 }}"
                             if bodyfield.is_collection
                             else f"{bodyfield.var} is not null")
                    lines.append(f"            if ({guard})")
                    lines.append(f"                {slot} = {assign};")
            lines.append("            var bodyJson = bodyNode.ToJsonString();")
        else:
            read = (f"await CliContext.ReadBodyFileAsync("
                    f"parseResult.GetValue({command.body.var})!, cancellationToken)")
            if command.body.wrap_single:
                read = f"CliContext.WrapAsArray({read})"
            lines.append(f"            var bodyJson = {read};")

        if command.body.collection:
            lines.append(f"            var body = (await KiotaJsonSerializer"
                         f".DeserializeCollectionAsync<{command.body.model}>(")
            lines.append(f"                bodyJson, {command.body.model}"
                         f".CreateFromDiscriminatorValue, cancellationToken)).ToList();")
        else:
            lines.append(f"            var body = await KiotaJsonSerializer"
                         f".DeserializeAsync<{command.body.model}>(")
            lines.append(f"                bodyJson, {command.body.model}"
                         f".CreateFromDiscriminatorValue, cancellationToken);")

    accessor = re.sub(r"\{(\w+)\}", lambda m: camel(m.group(1)), command.builder)
    target = f"context.Client.{service.client}.{accessor}"

    arguments = ["body"] if command.body else []
    lines.append("")
    if query:
        arguments.append("configuration =>")
        call_open = f"{target}.{command.builder_method}({', '.join(arguments)}"
        lines.append(f"            {'var result = ' if command.returns else ''}await {call_open}")
        lines.append("            {")
        for param in query:
            if param.enum_type:
                # Kiota types this parameter as a generated enum. The option stays a string
                # so `--role owner` works and the value shows in help; parsing is
                # case-insensitive because the spec's spellings are SHOUTED.
                lines.append(f"                if ({param.var} is not null)")
                lines.append(f"                    configuration.QueryParameters"
                             f".{pascal(param.name)} = "
                             f"Enum.Parse<{param.enum_type}>({param.var}, true);")
            else:
                lines.append(f"                configuration.QueryParameters"
                             f".{pascal(param.name)} = {param.var};")
        lines.append("            }, cancellationToken);")
    else:
        arguments.append("cancellationToken: cancellationToken")
        call = f"{target}.{command.builder_method}({', '.join(arguments)});"
        lines.append(f"            {'var result = ' if command.returns else ''}await {call}")

    lines.append("")
    if command.returns:
        if command.total_from:
            # Materialised, because the count and the table are read from the same response
            # and serialising twice would be wasteful and could disagree.
            lines.append("            var json = await OsduJson.ToJsonAsync(result);")
            lines.append(f"            context.Output.WriteTotal(json, "
                         f"{csharp_string(command.total_from)});")
            lines.append("            return context.Output.Write(")
            lines.append("                json,")
        else:
            lines.append("            return context.Output.Write(")
            lines.append("                await OsduJson.ToJsonAsync(result),")
        lines.append(f"                {output_expression(command)});")
    else:
        message = command.message or "Done."
        lines.append(f"            return context.Output.WriteMessage({csharp_string(message)});")

    lines.append("        }, cancellationToken));")
    lines.append("")
    lines.append("        return command;")
    lines.append("    }")
    return lines


def emit_service(service: Service, manifest_name: str) -> str:
    needs_models = any(c.body for c in service.commands)
    needs_json_nodes = any(c.body and c.body.from_fields for c in service.commands)

    lines = [HEADER.format(spec=service.spec_file, manifest=manifest_name)]
    lines.append("using System.CommandLine;")
    if any(c.require_one_of or c.mutually_exclusive for c in service.commands):
        lines.append("using System.CommandLine.Parsing;")
    if needs_json_nodes:
        lines.append("using System.Text.Json.Nodes;")
    if needs_models:
        lines.append("using Microsoft.Kiota.Abstractions.Serialization;")
    lines.append("using Equinor.OsduCli.Runtime;")
    if needs_models:
        lines.append(f"using Equinor.OsduCsharpClient.{service.models}.Models;")
    lines.append("")
    lines.append("namespace Equinor.OsduCli.Commands.Generated;")
    lines.append("")
    lines.append(f"/// <summary>{service.description}</summary>")
    lines.append(f"public static partial class {service.class_name}")
    lines.append("{")

    lines.append("    /// <summary>Attaches this service's commands to the global tree.</summary>")
    lines.append("    public static void Attach(CommandTree tree)")
    lines.append("    {")
    for command in service.commands:
        parent = " ".join(command.path[:-1])
        lines.append(f"        tree.Node({csharp_string(parent)})"
                     f".Subcommands.Add({command.factory}());")
    lines.append("")
    lines.append("        Customize(tree);")
    lines.append("    }")
    lines.append("")
    lines.append("    /// <summary>")
    lines.append("    /// Implemented in Commands/Handwritten/ to add commands this manifest cannot")
    lines.append("    /// express, or to adjust generated ones. No-op when nothing implements it.")
    lines.append("    /// </summary>")
    lines.append("    static partial void Customize(CommandTree tree);")

    for command in service.commands:
        lines.append("")
        lines += emit_command(service, command)

    lines.append("}")
    return "\n".join(lines) + "\n"


def group_descriptions(services: list[Service]) -> dict[str, str]:
    """Help text for every group node in the global tree, checked for conflicts.

    A node can be described by any manifest that reaches it, because a noun may be shared:
    `crs` is reached from both CRS manifests. Two manifests describing the same node
    differently is a mistake worth failing on rather than resolving by load order.
    """
    described: dict[str, str] = {}
    source: dict[str, str] = {}

    for service in services:
        for label, description in service.groups.items():
            if label in described and described[label] != description:
                raise ManifestError(
                    f"group {label!r} is described differently by {source[label]} and "
                    f"{service.name}:\n  {source[label]}: {described[label]}\n"
                    f"  {service.name}: {description}\n"
                    "Group help is global — the two manifests must agree."
                )
            described[label] = description
            source[label] = service.name

    needed = {" ".join(node) for service in services for node in service.nodes}
    for label in sorted(described):
        if label not in needed:
            raise ManifestError(
                f"{source[label]}: `groups:` describes {label!r}, which no command sits "
                "under. Stale entry — delete it."
            )
    for label in sorted(needed):
        if label not in described:
            raise ManifestError(
                f"group {label!r} has no description. Add it to the `groups:` map of one "
                "of the manifests that contributes commands under it — it is what the user "
                "sees in `osdu --help`."
            )
    return described


def emit_registry(services: list[Service]) -> str:
    described = group_descriptions(services)

    # Roots in manifest order, then deeper nodes, so `osdu --help` lists top-level commands
    # in the order the manifests are read rather than by whichever leaf was attached first.
    ordered = list(dict.fromkeys(
        [root for service in services for root in service.roots]
        + [" ".join(node) for service in services for node in service.nodes]))

    lines = [
        "// <auto-generated/>",
        "//",
        "// Generated by tools/generate_cli.py. Assembles the global command tree.",
        "//",
        "// The tree is built here rather than by each service because commands are named for",
        "// the resource, not the service: Entitlements contributes both `group` and `member`,",
        "// while `crs` is contributed by two services. See Runtime/CommandTree.cs.",
        "",
        "using System.CommandLine;",
        "using Equinor.OsduCli.Runtime;",
        "",
        "namespace Equinor.OsduCli.Commands.Generated;",
        "",
        "/// <summary>Every generated command, assembled into one tree.</summary>",
        "public static class GeneratedCommands",
        "{",
        "    public static IReadOnlyList<Command> All()",
        "    {",
        "        var tree = new CommandTree();",
        "",
    ]
    lines += [f"        tree.Describe({csharp_string(label)}, "
              f"{csharp_string(described[label])});" for label in ordered]
    lines.append("")
    lines += [f"        tree.Node({csharp_string(label)});" for label in ordered]
    lines.append("")
    lines += [f"        {service.class_name}.Attach(tree);" for service in services]
    lines += ["", "        return tree.Roots;", "    }", "}"]
    return "\n".join(lines) + "\n"


# --------------------------------------------------------------------------------------
# Entry point
# --------------------------------------------------------------------------------------

def report(service: Service) -> list[str]:
    c = service.coverage
    scope_note = f", {c.total - c.in_scope} out of scope" if c.in_scope != c.total else ""
    deprecated_note = f", {c.deprecated} deprecated upstream" if c.deprecated else ""
    return [
        f"  {service.name}: {c.generated} generated, {c.handwritten} handwritten, "
        f"{c.excluded} excluded of {c.in_scope} in scope{scope_note}{deprecated_note}"
    ]


def check_tree(services: list[Service]) -> None:
    """Validate the assembled tree across all manifests.

    Per-manifest checks cannot see these: two services are free to contribute to one noun,
    so nothing local catches two of them claiming `record get`, or a leaf sitting at the
    same path as a group.
    """
    owner: dict[str, str] = {}
    for service in services:
        for command in service.commands:
            label = " ".join(command.path)
            if label in owner:
                raise ManifestError(
                    f"command {label!r} is defined by both {owner[label]} and "
                    f"{service.name}. Command paths are global and must be unique."
                )
            owner[label] = service.name

    groups = {" ".join(node) for service in services for node in service.nodes}
    for label in sorted(groups & owner.keys()):
        raise ManifestError(
            f"{owner[label]}: {label!r} is both a command and a group with subcommands. "
            "Give one of them a different name."
        )

    group_descriptions(services)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--check", action="store_true",
                        help="validate manifests and fail on drift without writing files")
    parser.add_argument("--strict-scope", action="store_true",
                        help="also fail when a spec has operations outside the manifest scope")
    args = parser.parse_args()

    manifests = sorted(MANIFEST_DIR.glob("*.yaml"))
    if not manifests:
        print(f"No manifests found in {MANIFEST_DIR}", file=sys.stderr)
        return 1

    services: list[Service] = []
    failures: list[str] = []

    for manifest_path in manifests:
        try:
            services.append(build_service(manifest_path))
        except ManifestError as error:
            failures.append(f"  {error}")
        except KeyError as error:
            failures.append(f"  {manifest_path.name}: missing required key {error}")

    if failures:
        print("Manifest errors:", file=sys.stderr)
        print("\n".join(failures), file=sys.stderr)
        return 1

    try:
        check_tree(services)
    except ManifestError as error:
        print(f"Manifest errors:\n  {error}", file=sys.stderr)
        return 1

    print(f"Coverage ({len(services)} service(s)):")
    drift = False
    for service in services:
        print("\n".join(report(service)))
        for method, path in service.coverage.missing:
            print(f"    UNMAPPED {method.upper()} {path}", file=sys.stderr)
            drift = True
        if args.strict_scope:
            for method, path in service.coverage.untriaged:
                print(f"    OUT-OF-SCOPE {method.upper()} {path}", file=sys.stderr)
                drift = True
        elif service.coverage.untriaged:
            print(f"    ({len(service.coverage.untriaged)} operation(s) outside scope, not triaged)")

    if drift:
        print("\nEvery in-scope operation must appear in `commands`, `handwritten` or "
              "`exclude`.\nAdd the missing entries to the manifest.", file=sys.stderr)
        return 1

    if args.check:
        print("\n--check: manifests are consistent with their specs; nothing written.")
        return 0

    OUTPUT_DIR.mkdir(parents=True, exist_ok=True)
    for existing in OUTPUT_DIR.glob("*.g.cs"):
        existing.unlink()

    written = []
    for service, manifest_path in zip(services, manifests):
        target = OUTPUT_DIR / f"{service.class_name}.g.cs"
        target.write_text(emit_service(service, manifest_path.name), encoding="utf-8")
        written.append(target)

    registry = OUTPUT_DIR / "GeneratedCommands.g.cs"
    registry.write_text(emit_registry(services), encoding="utf-8")
    written.append(registry)

    print(f"\nWrote {len(written)} file(s) to {OUTPUT_DIR.relative_to(ROOT)}:")
    for target in written:
        print(f"  {target.name}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
