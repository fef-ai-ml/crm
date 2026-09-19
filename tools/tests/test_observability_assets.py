"""Invariants of the baked observability assets. Run: python3 -m unittest discover -s tools/tests -v"""
from __future__ import annotations

import json
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
OBSERVABILITY = ROOT / "deploy" / "observability"
DASHBOARD = OBSERVABILITY / "grafana" / "dashboards" / "crm-overview.json"

REQUIRED_METRICS = [
    "http_server_request_duration_seconds",
    "crm_ingest_events_total",
    "crm_ingest_rejected_total",
    "crm_consumer_lag",
    "crm_dlq_total",
    "crm_segment_run_seconds",
    "crm_realtime_eval_seconds",
]


class DashboardTests(unittest.TestCase):
    def setUp(self):
        self.dashboard = json.loads(DASHBOARD.read_text(encoding="utf-8"))

    def test_identity(self):
        self.assertEqual(self.dashboard["uid"], "crm-overview")
        self.assertEqual(self.dashboard["title"], "CRM overview")

    def test_every_panel_uses_the_provisioned_datasource(self):
        for panel in self.dashboard["panels"]:
            self.assertEqual(panel["datasource"], {"type": "prometheus", "uid": "prometheus"}, panel["title"])
            for target in panel["targets"]:
                self.assertEqual(target["datasource"], {"type": "prometheus", "uid": "prometheus"}, panel["title"])

    def test_panel_ids_are_unique(self):
        ids = [panel["id"] for panel in self.dashboard["panels"]]
        self.assertEqual(len(ids), len(set(ids)))

    def test_every_required_metric_is_queried(self):
        expressions = " ".join(t["expr"] for p in self.dashboard["panels"] for t in p["targets"])
        for metric in REQUIRED_METRICS:
            self.assertIn(metric, expressions)


class ProvisioningTests(unittest.TestCase):
    def test_datasource_points_at_the_prometheus_service_with_the_uid_the_dashboard_uses(self):
        text = (OBSERVABILITY / "grafana" / "provisioning" / "datasources" / "prometheus.yml").read_text(encoding="utf-8")
        self.assertIn("uid: prometheus", text)
        self.assertIn("url: http://prometheus:9090", text)

    def test_dashboard_provider_reads_the_directory_the_image_copies_into(self):
        provider = (OBSERVABILITY / "grafana" / "provisioning" / "dashboards" / "crm.yml").read_text(encoding="utf-8")
        dockerfile = (OBSERVABILITY / "grafana" / "Dockerfile").read_text(encoding="utf-8")
        self.assertIn("path: /etc/grafana/dashboards", provider)
        self.assertIn("COPY dashboards /etc/grafana/dashboards", dockerfile)

    def test_prometheus_scrapes_api_and_worker(self):
        text = (OBSERVABILITY / "prometheus" / "prometheus.yml").read_text(encoding="utf-8")
        self.assertIn("api:8080", text)
        self.assertIn("worker:8081", text)


if __name__ == "__main__":
    unittest.main()
