"""Unit tests for tools/seed.py (Task 8b). Run: python3 -m unittest discover -s tools/tests -v"""
from __future__ import annotations

import re
import sys
import tempfile
import unittest
from pathlib import Path
from unittest import mock

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

import seed  # noqa: E402

ROOT = Path(__file__).resolve().parents[2]
API_KEY_DOMAIN = ROOT / "src" / "Modules" / "Tenancy" / "Tenancy.Domain" / "ApiKey.cs"


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


class SeedConstantsMatchTheDomainTests(unittest.TestCase):
    def test_seed_constants_match_the_domain(self):
        """seed.py's KEY_SCHEME / PREFIX_LENGTH duplicate ApiKey.KeyPrefix / PrefixLength (M3);
        if the domain constant ever changes, this must fail loudly instead of silently minting a new
        key on every `make seed`."""
        domain = API_KEY_DOMAIN.read_text(encoding="utf-8")
        key_prefix = re.search(r'KeyPrefix\s*=\s*"([^"]+)"', domain)
        prefix_length = re.search(r"PrefixLength\s*=\s*(\d+)", domain)
        self.assertIsNotNone(key_prefix, "ApiKey.KeyPrefix not found in ApiKey.cs")
        self.assertIsNotNone(prefix_length, "ApiKey.PrefixLength not found in ApiKey.cs")
        self.assertEqual(seed.KEY_SCHEME, key_prefix.group(1),
                          "seed.KEY_SCHEME has drifted from ApiKey.KeyPrefix")
        self.assertEqual(seed.PREFIX_LENGTH, int(prefix_length.group(1)),
                          "seed.PREFIX_LENGTH has drifted from ApiKey.PrefixLength")


DATASET_OK = {
    "status": "loaded", "profiles": 5000, "mergedProfiles": 50, "events": 100000, "elapsedMilliseconds": 8400,
    "segments": {"seed-pro-id": 412, "seed-purchasers": 2731, "seed-dormant": 1500,
                 "seed-no-plan": 500, "seed-not-pro": 4000, "seed-high-ltv-sg": 187},
}


class DatasetReportTests(unittest.TestCase):
    def test_summary_line_then_one_sorted_line_per_segment(self):
        lines = seed.dataset_report(DATASET_OK)
        self.assertEqual(lines[0], "seed: dataset loaded: 5000 profiles (+50 merged), 100000 events in 8.4 s")
        self.assertEqual(lines[1:], [
            "seed:   seed-dormant = 1500", "seed:   seed-high-ltv-sg = 187", "seed:   seed-no-plan = 500",
            "seed:   seed-not-pro = 4000", "seed:   seed-pro-id = 412", "seed:   seed-purchasers = 2731"])

    def test_exists_status_is_reported_as_such(self):
        self.assertIn("dataset exists:", seed.dataset_report({**DATASET_OK, "status": "exists"})[0])

    def test_exists_status_appends_the_drift_note(self):
        lines = seed.dataset_report({**DATASET_OK, "status": "exists"})
        self.assertEqual(lines[-1], "seed:   (counts are the tenant's current totals; they drift about 4 days after loading — docs/test/fixtures.md)")

    def test_loaded_status_has_no_drift_note(self):
        lines = seed.dataset_report(DATASET_OK)
        self.assertFalse(any("drift" in line for line in lines))


class LoadDatasetTests(unittest.TestCase):
    def test_posts_to_the_dev_endpoint_with_the_admin_token_and_a_long_timeout(self):
        with mock.patch.object(seed, "call", return_value=(200, DATASET_OK)) as call, mock.patch("builtins.print"):
            seed.load_dataset("admin-token")
        call.assert_called_once_with("POST", "/v1/dev/seed-dataset", {}, "admin-token", timeout=600)

    def test_404_explains_development_and_the_endpoint(self):
        with mock.patch.object(seed, "call", return_value=(404, None)):
            with self.assertRaises(SystemExit) as exit_:
                seed.load_dataset("t")
        self.assertIn("seed-dataset", str(exit_.exception))
        self.assertIn("Development", str(exit_.exception))

    def test_problem_detail_is_shown_on_failure(self):
        problem = {"code": "seed.count_mismatch", "detail": "seed-dormant: expected 1500, found 1499"}
        with mock.patch.object(seed, "call", return_value=(500, problem)):
            with self.assertRaises(SystemExit) as exit_:
                seed.load_dataset("t")
        self.assertIn("expected 1500, found 1499", str(exit_.exception))


EXPECTED_SEGMENT_COUNTS = {"seed-pro-id": 412, "seed-purchasers": 2731, "seed-dormant": 1500,
                           "seed-no-plan": 500, "seed-not-pro": 4000, "seed-high-ltv-sg": 187}

SEED_SEGMENTS_RESPONSE = [
    {"id": "1", "name": "seed-pro-id", "status": "Active", "runId": "r1"},
    {"id": "2", "name": "seed-purchasers", "status": "Active", "runId": "r2"},
    {"id": "3", "name": "seed-dormant", "status": "Active", "runId": "r3"},
    {"id": "4", "name": "seed-no-plan", "status": "Active", "runId": "r4"},
    {"id": "5", "name": "seed-not-pro", "status": "Active", "runId": "r5"},
    {"id": "6", "name": "seed-high-ltv-sg", "status": "Draft", "runId": None},
]


def _segments_page(counts, finished):
    """A GET /v1/segments body: every EXPECTED_SEGMENT_COUNTS name, with `lastRunAt` set once `finished` for the
    five Active names — `seed-high-ltv-sg` (Draft) never gets one, matching the real endpoint."""
    return {"items": [{"name": name, "memberCount": counts.get(name, 0),
                        "lastRunAt": "2026-09-22T00:00:00Z" if finished and name != "seed-high-ltv-sg" else None}
                       for name in EXPECTED_SEGMENT_COUNTS], "nextCursor": None}


class CompareSegmentCountsTests(unittest.TestCase):
    def test_no_mismatches_when_everything_matches(self):
        self.assertEqual(seed.compare_segment_counts(EXPECTED_SEGMENT_COUNTS, EXPECTED_SEGMENT_COUNTS), [])

    def test_missing_name_is_reported(self):
        actual = {k: v for k, v in EXPECTED_SEGMENT_COUNTS.items() if k != "seed-dormant"}
        self.assertEqual(seed.compare_segment_counts(EXPECTED_SEGMENT_COUNTS, actual),
                          ["seed-dormant: missing from GET /v1/segments"])

    def test_wrong_count_is_reported(self):
        actual = {**EXPECTED_SEGMENT_COUNTS, "seed-not-pro": 3999}
        self.assertEqual(seed.compare_segment_counts(EXPECTED_SEGMENT_COUNTS, actual),
                          ["seed-not-pro: expected 4000, found 3999"])

    def test_multiple_mismatches_are_all_reported(self):
        actual = {**EXPECTED_SEGMENT_COUNTS, "seed-not-pro": 1, "seed-dormant": 2}
        messages = seed.compare_segment_counts(EXPECTED_SEGMENT_COUNTS, actual)
        self.assertEqual(len(messages), 2)

    def test_extra_actual_name_is_not_a_mismatch(self):
        actual = {**EXPECTED_SEGMENT_COUNTS, "some-other-segment": 1}
        self.assertEqual(seed.compare_segment_counts(EXPECTED_SEGMENT_COUNTS, actual), [])


class SeedSegmentsTests(unittest.TestCase):
    def test_posts_then_polls_until_every_active_segment_has_run_and_reports_no_mismatch(self):
        with mock.patch.object(seed, "call", side_effect=[
            (200, SEED_SEGMENTS_RESPONSE),
            (200, _segments_page(EXPECTED_SEGMENT_COUNTS, finished=False)),
            (200, _segments_page(EXPECTED_SEGMENT_COUNTS, finished=True)),
        ]) as call, mock.patch("time.sleep"), mock.patch("builtins.print"):
            seed.seed_segments("admin-token", EXPECTED_SEGMENT_COUNTS)

        self.assertEqual(call.call_count, 3)
        call.assert_any_call("POST", "/v1/dev/seed-segments", {}, "admin-token", timeout=60)

    def test_draft_segment_is_never_waited_on(self):
        """seed-high-ltv-sg is Draft (never queued) — a lastRunAt-less Draft row must not stall the poll."""
        with mock.patch.object(seed, "call", side_effect=[
            (200, SEED_SEGMENTS_RESPONSE),
            (200, _segments_page(EXPECTED_SEGMENT_COUNTS, finished=True)),
        ]), mock.patch("time.sleep") as sleep_, mock.patch("builtins.print"):
            seed.seed_segments("admin-token", EXPECTED_SEGMENT_COUNTS)

        sleep_.assert_not_called()

    def test_exits_nonzero_on_a_count_mismatch_once_runs_finish(self):
        wrong_counts = {**EXPECTED_SEGMENT_COUNTS, "seed-dormant": 1}
        with mock.patch.object(seed, "call", side_effect=[
            (200, SEED_SEGMENTS_RESPONSE),
            (200, _segments_page(wrong_counts, finished=True)),
        ]), mock.patch("builtins.print"):
            with self.assertRaises(SystemExit) as exit_:
                seed.seed_segments("admin-token", EXPECTED_SEGMENT_COUNTS)

        self.assertIn("seed-dormant: expected 1500, found 1", str(exit_.exception))

    def test_times_out_when_a_run_never_finishes(self):
        pending = _segments_page(EXPECTED_SEGMENT_COUNTS, finished=False)
        clock = {"t": 0.0}

        with mock.patch.object(seed, "call", side_effect=[(200, SEED_SEGMENTS_RESPONSE)] + [(200, pending)] * 5):
            with mock.patch("time.time", side_effect=lambda: clock["t"]), \
                 mock.patch("time.sleep", side_effect=lambda _: clock.__setitem__("t", clock["t"] + 121)):
                with self.assertRaises(SystemExit) as exit_:
                    seed.seed_segments("admin-token", EXPECTED_SEGMENT_COUNTS)

        self.assertIn("120 s", str(exit_.exception))

    def test_404_explains_development_and_the_endpoint(self):
        with mock.patch.object(seed, "call", return_value=(404, None)):
            with self.assertRaises(SystemExit) as exit_:
                seed.seed_segments("t", EXPECTED_SEGMENT_COUNTS)
        self.assertIn("seed-segments", str(exit_.exception))
        self.assertIn("Development", str(exit_.exception))


if __name__ == "__main__":
    unittest.main()
