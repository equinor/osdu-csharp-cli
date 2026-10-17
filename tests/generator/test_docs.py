"""The command reference, generated from the manifests and the specs' documented roles."""

import yaml

import generate_cli
import generate_docs

SPEC = """
openapi: 3.0.1
info: { title: t, version: "1" }
paths:
  /index:
    delete:
      description: "Delete Index for the given kind. Required roles: `users.datalake.ops`"
      responses: { "200": { description: ok } }
  /reindex:
    post:
      description: "Re-index a kind. Required roles: `users.datalake.ops`"
      responses: { "200": { description: ok } }
  /info:
    get:
      description: "Version info. Required roles: `service.search.admin`"
      responses: { "200": { description: ok } }
"""


def manifests(tmp_path, monkeypatch, handwritten):
    (tmp_path / "specs" / "tiny").mkdir(parents=True)
    (tmp_path / "specs" / "tiny" / "openapi.yaml").write_text(SPEC, encoding="utf-8")
    (tmp_path / "manifests").mkdir()
    (tmp_path / "manifests" / "tiny.yaml").write_text(yaml.safe_dump({
        "service": "tiny", "spec": "tiny", "client": "Tiny",
        "commands": [{"command": "index delete", "summary": "Delete.", "op": {"method": "delete", "path": "/index"},
                      "output": "raw"}],
        "handwritten": handwritten,
    }), encoding="utf-8")
    monkeypatch.setattr(generate_cli, "SPECS_DIR", tmp_path / "specs")
    monkeypatch.setattr(generate_docs, "MANIFEST_DIR", tmp_path / "manifests")


def test_an_operator_command_is_listed_with_what_it_needs(tmp_path, monkeypatch):
    manifests(tmp_path, monkeypatch, [{"command": "index reindex", "op": {"method": "post", "path": "/reindex"},
                                       "reason": "r"}])

    page = generate_docs.render()

    assert "| [`osducs index delete`](#osducs-index-delete) | users.datalake.ops |" in page
    assert "| `osducs index reindex` (hand-written) | users.datalake.ops |" in page
    assert "Requires users.datalake.ops — an operator role." in page


def test_an_unnamed_handwritten_entry_has_no_row(tmp_path, monkeypatch):
    # `command` is optional; such an entry only accounts for an operation, and broke the page.
    manifests(tmp_path, monkeypatch, [{"op": {"method": "get", "path": "/info"}, "reason": "status"},
                                      {"op": {"method": "post", "path": "/reindex"}, "reason": "r",
                                       "roles": "users.datalake.ops"}])

    _, handwritten = generate_docs.load()
    table = generate_docs.render().split("| Command | Requires |\n|---|---|\n")[1].split("\n\n")[0]

    assert handwritten == []
    assert table == "| [`osducs index delete`](#osducs-index-delete) | users.datalake.ops |"
