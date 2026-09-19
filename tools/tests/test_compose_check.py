"""Unit tests for tools/compose_check.py (CT-005). Run: python3 -m unittest discover -s tools/tests -v"""
from __future__ import annotations

import copy
import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

import compose_check  # noqa: E402


def svc(target: int | None = None, published: str | None = None, healthcheck: bool = False, **extra) -> dict:
    service: dict = dict(extra)
    if target is not None:
        service["ports"] = [{"target": target, "published": published or str(target)}]
    if healthcheck:
        service["healthcheck"] = {"test": ["CMD", "true"]}
    return service


def base_config() -> dict:
    infra_deps = {
        "sqlserver": {"condition": "service_healthy"},
        "redis": {"condition": "service_healthy"},
        "kafka-init": {"condition": "service_completed_successfully"},
    }
    return {
        "services": {
            "sqlserver": svc(1433, "11433", healthcheck=True),
            "kafka": svc(19092, healthcheck=True),
            "kafka-init": svc(),
            "redis": svc(6379, "6380", healthcheck=True),
            "api": svc(8080, healthcheck=True, environment={"OTEL_EXPORTER_OTLP_ENDPOINT": ""}),
            "worker": svc(8081, healthcheck=True, depends_on=infra_deps, environment={"OTEL_EXPORTER_OTLP_ENDPOINT": ""}),
            "web": svc(3000),
        }
    }


def observability_config() -> dict:
    config = base_config()
    services = config["services"]
    services["prometheus"] = svc(9090, healthcheck=True)
    services["grafana"] = svc(3000, "3001", healthcheck=True)
    services["jaeger"] = svc(16686)
    for name in ("api", "worker"):
        services[name]["environment"]["OTEL_EXPORTER_OTLP_ENDPOINT"] = "http://jaeger:4317"
    return config


class BaseModeTests(unittest.TestCase):
    def test_valid_base_config_has_no_errors(self):
        self.assertEqual(compose_check.check(base_config()), [])

    def test_unexpected_service_is_reported(self):
        config = base_config()
        config["services"]["adminer"] = svc(8888)
        errors = compose_check.check(config)
        self.assertTrue(any("unexpected services" in e and "adminer" in e for e in errors), errors)

    def test_observability_services_are_unexpected_in_base_mode(self):
        errors = compose_check.check(observability_config())
        self.assertTrue(any("unexpected services" in e and "jaeger" in e for e in errors), errors)

    def test_wrong_published_port_is_reported(self):
        config = base_config()
        config["services"]["api"]["ports"][0]["published"] = "9999"
        errors = compose_check.check(config)
        self.assertTrue(any(e.startswith("api:") and "8080" in e for e in errors), errors)

    def test_missing_worker_dependency_is_reported(self):
        config = base_config()
        del config["services"]["worker"]["depends_on"]["redis"]
        errors = compose_check.check(config)
        self.assertTrue(any("worker: missing depends_on entry for redis" in e for e in errors), errors)


class ObservabilityModeTests(unittest.TestCase):
    def test_valid_observability_config_has_no_errors(self):
        self.assertEqual(compose_check.check(observability_config(), observability=True), [])

    def test_base_config_is_missing_the_observability_services(self):
        errors = compose_check.check(base_config(), observability=True)
        self.assertTrue(any("missing services" in e and "prometheus" in e for e in errors), errors)

    def test_grafana_without_healthcheck_is_reported(self):
        config = observability_config()
        del config["services"]["grafana"]["healthcheck"]
        errors = compose_check.check(config, observability=True)
        self.assertIn("grafana: no healthcheck defined", errors)

    def test_api_without_the_otlp_endpoint_is_reported(self):
        config = observability_config()
        config["services"]["api"]["environment"]["OTEL_EXPORTER_OTLP_ENDPOINT"] = ""
        errors = compose_check.check(config, observability=True)
        self.assertTrue(any(e.startswith("api:") and "OTEL_EXPORTER_OTLP_ENDPOINT" in e for e in errors), errors)

    def test_environment_in_list_form_is_understood(self):
        config = observability_config()
        config["services"]["worker"]["environment"] = ["OTEL_EXPORTER_OTLP_ENDPOINT=http://jaeger:4317"]
        self.assertEqual(compose_check.check(config, observability=True), [])

    def test_check_does_not_mutate_its_input(self):
        config = observability_config()
        snapshot = copy.deepcopy(config)
        compose_check.check(config, observability=True)
        self.assertEqual(config, snapshot)


if __name__ == "__main__":
    unittest.main()
