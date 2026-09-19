#!/usr/bin/env python3
"""Validate the rendered compose configuration against CT-005.

Usage:
  <compose> -f deploy/docker-compose.yml config --format json | python3 tools/compose_check.py
  <compose> -f deploy/docker-compose.yml -f deploy/docker-compose.observability.yml \
      --profile observability config --format json | python3 tools/compose_check.py --observability

Asserts (see docs/test/architecture-contract.md CT-005):
- the service set is exactly {sqlserver, kafka, kafka-init, redis, api, worker, web}
  (plus {prometheus, grafana, jaeger} with --observability)
- published ports: 8080->api, 8081->worker, 3000->web, 19092->kafka, and a published port mapped to
  container port 1433 on sqlserver / 6379 on redis (host ports for those are configurable via
  deploy/.env, so only the container-side target is fixed). With --observability: container ports
  9090 (prometheus), 3000 (grafana), 16686 (jaeger) are published, host side configurable
- sqlserver, kafka, redis, api and worker each declare a healthcheck (web's readiness comes from its
  image, so either is accepted); with --observability also prometheus and grafana (the jaeger image
  ships no shell to run one)
- worker's depends_on matches the infrastructure-only wiring: sqlserver and redis healthy,
  kafka-init completed successfully
- with --observability: api and worker export OTLP to http://jaeger:4317

Exits non-zero with a message naming every mismatch found.
"""
from __future__ import annotations

import json
import sys

BASE_SERVICES = {"sqlserver", "kafka", "kafka-init", "redis", "api", "worker", "web"}
OBSERVABILITY_SERVICES = {"prometheus", "grafana", "jaeger"}

BASE_HEALTHCHECKS = {"sqlserver", "kafka", "redis", "api", "worker"}
OBSERVABILITY_HEALTHCHECKS = {"prometheus", "grafana"}

# service -> (container target port, expected published port or None to accept any)
BASE_PORTS: dict[str, tuple[int, str | None]] = {
    "api": (8080, "8080"),
    "worker": (8081, "8081"),
    "web": (3000, "3000"),
    "kafka": (19092, "19092"),
    "sqlserver": (1433, None),
    "redis": (6379, None),
}
OBSERVABILITY_PORTS: dict[str, tuple[int, str | None]] = {
    "prometheus": (9090, None),
    "grafana": (3000, None),
    "jaeger": (16686, None),
}

WORKER_DEPENDS_ON = {
    "sqlserver": "service_healthy",
    "redis": "service_healthy",
    "kafka-init": "service_completed_successfully",
}

OTLP_VARIABLE = "OTEL_EXPORTER_OTLP_ENDPOINT"
OTLP_ENDPOINT = "http://jaeger:4317"
OTLP_SERVICES = ("api", "worker")


def find_port(service: dict, target: int) -> dict | None:
    for port in service.get("ports", []):
        try:
            if int(port.get("target")) == target:
                return port
        except (TypeError, ValueError):
            continue
    return None


def environment_of(service: dict) -> dict[str, str]:
    """Compose renders `environment` as a mapping; tolerate the KEY=value list form as well."""
    environment = service.get("environment") or {}
    if isinstance(environment, dict):
        return {str(key): "" if value is None else str(value) for key, value in environment.items()}
    result: dict[str, str] = {}
    for entry in environment:
        key, _, value = str(entry).partition("=")
        result[key] = value
    return result


def check(config: dict, observability: bool = False) -> list[str]:
    """Return every CT-005 mismatch in a rendered compose config; an empty list means valid."""
    expected_services = BASE_SERVICES | (OBSERVABILITY_SERVICES if observability else set())
    expected_ports = {**BASE_PORTS, **(OBSERVABILITY_PORTS if observability else {})}
    healthcheck_required = BASE_HEALTHCHECKS | (OBSERVABILITY_HEALTHCHECKS if observability else set())

    errors: list[str] = []
    services = config.get("services", {})
    actual_names = set(services.keys())

    if actual_names != expected_services:
        missing = expected_services - actual_names
        extra = actual_names - expected_services
        if missing:
            errors.append(f"missing services: {sorted(missing)}")
        if extra:
            errors.append(f"unexpected services: {sorted(extra)}")

    for name, (target, expected_published) in expected_ports.items():
        service = services.get(name)
        if service is None:
            continue  # already reported as missing above
        port = find_port(service, target)
        if port is None:
            errors.append(f"{name}: no published port maps to container port {target}")
            continue
        published = port.get("published")
        if expected_published is not None and str(published) != expected_published:
            errors.append(f"{name}: expected published port {expected_published} -> {target}, got {published!r}")
        elif published in (None, ""):
            errors.append(f"{name}: container port {target} has no published host port")

    for name in sorted(healthcheck_required):
        service = services.get(name)
        if service is None:
            continue
        healthcheck = service.get("healthcheck")
        if not healthcheck or not healthcheck.get("test"):
            errors.append(f"{name}: no healthcheck defined")

    worker = services.get("worker")
    if worker is not None:
        depends_on = worker.get("depends_on", {})
        for dep_name, expected_condition in WORKER_DEPENDS_ON.items():
            dep = depends_on.get(dep_name)
            if dep is None:
                errors.append(f"worker: missing depends_on entry for {dep_name}")
                continue
            condition = dep.get("condition") if isinstance(dep, dict) else None
            if condition != expected_condition:
                errors.append(f"worker: depends_on[{dep_name}] expected condition {expected_condition!r}, got {condition!r}")
        unexpected_deps = set(depends_on) - set(WORKER_DEPENDS_ON)
        if unexpected_deps:
            errors.append(f"worker: unexpected depends_on entries: {sorted(unexpected_deps)}")

    if observability:
        for name in OTLP_SERVICES:
            service = services.get(name)
            if service is None:
                continue
            actual = environment_of(service).get(OTLP_VARIABLE, "")
            if actual != OTLP_ENDPOINT:
                errors.append(f"{name}: expected {OTLP_VARIABLE}={OTLP_ENDPOINT}, got {actual!r}")

    return errors


def main(argv: list[str]) -> int:
    unknown = [arg for arg in argv if arg != "--observability"]
    if unknown:
        print(f"compose_check: unknown arguments {unknown}; the only flag is --observability", file=sys.stderr)
        return 2
    observability = "--observability" in argv

    try:
        config = json.loads(sys.stdin.read())
    except json.JSONDecodeError as ex:
        print(f"compose_check: stdin was not valid JSON (expected `<compose> config --format json` output): {ex}", file=sys.stderr)
        return 2

    errors = check(config, observability)
    if errors:
        print("compose_check: FAILED", file=sys.stderr)
        for error in errors:
            print(f"  - {error}", file=sys.stderr)
        return 1

    scope = "base + observability" if observability else "base"
    print(f"compose_check: OK ({scope}) — services, published ports, healthchecks and worker depends_on match CT-005")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
