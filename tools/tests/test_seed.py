"""Unit tests for tools/seed.py (Task 8b). Run: python3 -m unittest discover -s tools/tests -v"""
from __future__ import annotations

import sys
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

import seed  # noqa: E402


class ShouldMintTests(unittest.TestCase):
    def test_fresh_tenant_means_stale_key_so_mint(self):
        self.assertTrue(seed.should_mint(True, "crm_live_old"))

    def test_existing_tenant_with_key_does_not_mint(self):
        self.assertFalse(seed.should_mint(False, "crm_live_old"))

    def test_existing_tenant_without_key_mints(self):
        self.assertTrue(seed.should_mint(False, None))
        self.assertTrue(seed.should_mint(False, ""))


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
