"""Unit tests for tools/seed.py (Task 8b). Run: python3 -m unittest discover -s tools/tests -v"""
from __future__ import annotations

import sys
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

import seed  # noqa: E402


class KeyPrefixTests(unittest.TestCase):
    def test_valid_key_returns_first_eight_chars_of_secret(self):
        self.assertEqual(seed.key_prefix("crm_live_abcdefghijklmnopqrstuvwxyz012345"), "abcdefgh")

    def test_wrong_scheme_returns_none(self):
        self.assertIsNone(seed.key_prefix("sk_live_abcdefghijklmnop"))

    def test_secret_shorter_than_eight_returns_none(self):
        self.assertIsNone(seed.key_prefix("crm_live_abc"))

    def test_none_input_returns_none(self):
        self.assertIsNone(seed.key_prefix(None))


class ActivePrefixesTests(unittest.TestCase):
    def test_revoked_items_are_excluded(self):
        items = [
            {"prefix": "abcdefgh", "revokedAt": "2024-01-01T00:00:00Z"},
            {"prefix": "ijklmnop", "revokedAt": None},
        ]
        self.assertEqual(seed.active_prefixes(items), {"ijklmnop"})

    def test_missing_revoked_at_is_treated_as_active(self):
        items = [{"prefix": "qrstuvwx"}]
        self.assertEqual(seed.active_prefixes(items), {"qrstuvwx"})


class ShouldMintTests(unittest.TestCase):
    def test_no_stored_key_mints(self):
        self.assertTrue(seed.should_mint(None, {"abcdefgh"}))
        self.assertTrue(seed.should_mint("", {"abcdefgh"}))

    def test_stored_key_not_parseable_mints(self):
        self.assertTrue(seed.should_mint("not-a-key", {"abcdefgh"}))

    def test_stored_key_prefix_not_active_mints(self):
        stored = "crm_live_zzzzzzzzijklmnopqrstuvwxyz012345"
        self.assertTrue(seed.should_mint(stored, {"abcdefgh"}))

    def test_stored_key_prefix_active_keeps_it(self):
        stored = "crm_live_abcdefghijklmnopqrstuvwxyz012345"
        self.assertFalse(seed.should_mint(stored, {"abcdefgh"}))


class WriteKeyTests(unittest.TestCase):
    def test_creates_missing_file(self):
        with tempfile.TemporaryDirectory() as tmp:
            path = str(Path(tmp) / ".env.local")
            seed.write_key(path, "k1")
            self.assertEqual(Path(path).read_text(encoding="utf-8"), "CRM_API_KEY=k1\n")

    def test_replaces_existing_key_line_in_place(self):
        with tempfile.TemporaryDirectory() as tmp:
            path = str(Path(tmp) / ".env.local")
            Path(path).write_text("A=1\nCRM_API_KEY=old\nB=2\n", encoding="utf-8")
            seed.write_key(path, "new")
            self.assertEqual(Path(path).read_text(encoding="utf-8"), "A=1\nCRM_API_KEY=new\nB=2\n")

    def test_appends_when_no_key_line_exists_even_without_trailing_newline(self):
        with tempfile.TemporaryDirectory() as tmp:
            path = str(Path(tmp) / ".env.local")
            Path(path).write_text("A=1", encoding="utf-8")
            seed.write_key(path, "new")
            self.assertEqual(Path(path).read_text(encoding="utf-8"), "A=1\nCRM_API_KEY=new\n")

    def test_duplicate_key_lines_collapse_to_one(self):
        with tempfile.TemporaryDirectory() as tmp:
            path = str(Path(tmp) / ".env.local")
            Path(path).write_text("CRM_API_KEY=old1\nA=1\nCRM_API_KEY=old2\n", encoding="utf-8")
            seed.write_key(path, "new")
            self.assertEqual(Path(path).read_text(encoding="utf-8"), "CRM_API_KEY=new\nA=1\n")


class ExistingKeyTests(unittest.TestCase):
    def test_returns_value_when_present(self):
        with tempfile.TemporaryDirectory() as tmp:
            path = str(Path(tmp) / ".env.local")
            Path(path).write_text("A=1\nCRM_API_KEY=abc\n", encoding="utf-8")
            self.assertEqual(seed.existing_key(path), "abc")

    def test_returns_none_for_missing_file(self):
        with tempfile.TemporaryDirectory() as tmp:
            path = str(Path(tmp) / "nope.local")
            self.assertIsNone(seed.existing_key(path))

    def test_returns_none_for_empty_key_line(self):
        with tempfile.TemporaryDirectory() as tmp:
            path = str(Path(tmp) / ".env.local")
            Path(path).write_text("CRM_API_KEY=\n", encoding="utf-8")
            self.assertIsNone(seed.existing_key(path))


if __name__ == "__main__":
    unittest.main()
