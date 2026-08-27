"""Unknown keys must fail the gate.

An invented or mistyped key is otherwise ignored in silence, and the rule it was meant to
express simply does not happen — `mutually-exclusive-2` was written by hand once and would
have disabled an existing rule without a word.
"""

import pytest

from generate_cli import ManifestError, check_keys


def test_a_known_key_is_accepted():
    check_keys("command", {"command": "record get", "op": {}}, "here")


def test_an_unknown_key_is_rejected():
    with pytest.raises(ManifestError, match="mutually-exclusive-2"):
        check_keys("command", {"command": "x", "mutually-exclusive-2": []}, "here")


def test_the_error_lists_what_is_allowed():
    with pytest.raises(ManifestError, match="require-one-of"):
        check_keys("command", {"nonsense": 1}, "here")


@pytest.mark.parametrize("level,key", [
    ("top", "descriptoin"),
    ("param", "requried"),
    ("body field", "typ"),
    ("output", "colums"),
    ("example", "arguments"),
])
def test_every_level_is_checked(level, key):
    with pytest.raises(ManifestError, match=key):
        check_keys(level, {key: "x"}, "here")


def test_prose_turned_into_a_key_is_explained():
    # `help: Filter by authority, e.g. osdu.` in a flow mapping ends the help at the comma
    # and makes `e.g. osdu.` a key. Three parameters had lost their examples this way.
    with pytest.raises(ManifestError, match="unquoted comma"):
        check_keys("param", {"flag": "--authority", "e.g. osdu.": None}, "here")


def test_a_plain_typo_does_not_get_the_comma_hint():
    with pytest.raises(ManifestError) as raised:
        check_keys("param", {"requried": True}, "here")
    assert "unquoted comma" not in str(raised.value)


def test_a_non_mapping_is_ignored():
    # `output: raw` is a string, not a mapping, and must not trip the checker.
    check_keys("output", "raw", "here")
    check_keys("output", None, "here")


def test_section_is_a_known_top_level_key():
    # `section:` groups a service's root nouns under their own help heading. It is optional,
    # so a typo would otherwise be ignored and the nouns would silently stay ungrouped.
    check_keys("top", {"service": "wellbore_ddms", "section": "Wellbore DDMS"}, "here")


def test_a_mistyped_section_key_is_rejected():
    with pytest.raises(ManifestError, match="sections"):
        check_keys("top", {"service": "wellbore_ddms", "sections": "Wellbore DDMS"}, "here")


def test_a_global_option_alias_cannot_be_reused():
    # `-c` is the global --config. A command that claims it either shadows the global or
    # makes the parse ambiguous; `--curves` did exactly this.
    from generate_cli import check_alias

    with pytest.raises(ManifestError, match="global option"):
        check_alias("-c", "here", "parameter 'curves' short alias")


def test_an_unreserved_alias_is_accepted():
    from generate_cli import check_alias

    check_alias("--curves", "here", "parameter 'curves' flag")
