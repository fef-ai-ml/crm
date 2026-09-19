"""Unit tests for tools/docs_check.py's Planned/Implemented tag check. Run: python3 -m unittest discover -s tools/tests -v"""
from __future__ import annotations

import sys
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

import docs_check  # noqa: E402


class PlannedTagTests(unittest.TestCase):
    def test_planned_bullet_whose_path_exists_is_an_error(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            target = root / "tests" / "Example.cs"
            target.parent.mkdir(parents=True)
            target.write_text("// exists")
            text = "### IT-999 — example case\n- Given ...\n- Planned: tests/Example.cs\n"

            errors = docs_check.check_planned_tags(root, text)

            self.assertTrue(
                any("IT-999" in e and "tests/Example.cs" in e and "tag it Implemented" in e for e in errors),
                errors,
            )

    def test_planned_bullet_whose_path_is_missing_is_not_an_error(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            text = "### IT-998 — example case\n- Given ...\n- Planned: tests/DoesNotExist.cs\n"

            self.assertEqual(docs_check.check_planned_tags(root, text), [])

    def test_implemented_bullet_is_never_flagged(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            target = root / "tests" / "Example.cs"
            target.parent.mkdir(parents=True)
            target.write_text("// exists")
            text = "### IT-997 — example case\n- Given ...\n- Implemented: tests/Example.cs\n"

            self.assertEqual(docs_check.check_planned_tags(root, text), [])

    def test_bullet_outside_any_case_heading_is_ignored(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            target = root / "tests" / "Example.cs"
            target.parent.mkdir(parents=True)
            target.write_text("// exists")
            text = "- Planned: tests/Example.cs\n"

            self.assertEqual(docs_check.check_planned_tags(root, text), [])

    def test_bullet_is_attributed_to_the_most_recent_case_heading(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            target = root / "tests" / "Example.cs"
            target.parent.mkdir(parents=True)
            target.write_text("// exists")
            text = (
                "### IT-001 — first case\n- Given ...\n- Planned: tests/DoesNotExist.cs\n"
                "### IT-002 — second case\n- Given ...\n- Planned: tests/Example.cs\n"
            )

            errors = docs_check.check_planned_tags(root, text)

            self.assertTrue(any(e.startswith("IT-002") for e in errors), errors)
            self.assertFalse(any(e.startswith("IT-001") for e in errors), errors)


if __name__ == "__main__":
    unittest.main()
