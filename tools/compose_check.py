#!/usr/bin/env python3
"""Validate the rendered compose configuration against CT-005.

Usage: <compose> config --format json | python3 tools/compose_check.py

Asserts (see docs/test/architecture-contract.md CT-005):
- the service set is exactly {sqlserver, kafka, kafka-init, redis, api, worker, web}
- published ports: 8080->api, 8081->worker, 3000->web, 19092->kafka,
  and a published port mapped to container port 1433 on sqlserver / 6379 on redis
  (the host port for those two is configurable via deploy/.env, so only the
  container-side target is fixed)
- sqlserver, kafka, redis, api and worker each declare a healthcheck (web's
  readiness comes from its image, so either is accepted)
- worker's depends_on matches the infrastructure-only wiring: sqlserver and
  redis healthy, kafka-init completed successfully

Exits non-zero with a message naming every mismatch found.
"""
from __future__ import annotations

import json
import sys

EXPECTED_SERVICES = {"sqlserver", "kafka", "kafka-init", "redis", "api", "worker", "web"}
HEALTHCHECK_REQUIRED = {"sqlserver", "kafka", "redis", "api", "worker"}

# service -> (container target port, expected published port or None to accept any)
EXPECTED_PORTS: dict[str, tuple[int, str | None]] = {
    "api": (8080, "8080"),
    "worker": (8081, "8081"),
    "web": (3000, "3000"),
    "kafka": (19092, "19092"),
    "sqlserver": (1433, None),
    "redis": (6379, None),
}

WORKER_DEPENDS_ON = {
    "sqlserver": "service_healthy",
    "redis": "service_healthy",
    "kafka-init": "service_completed_successfully",
}


def fail(errors: list[str]) -> None:
    print("compose_check: FAILED", file=sys.stderr)
    for error in errors:
        print(f"  - {error}", file=sys.stderr)
    sys.exit(1)


def find_port(service: dict, target: int) -> dict | None:
    for port in service.get("ports", []):
        try:
            if int(port.get("target")) == target:
                return port
        except (TypeError, ValueError):
            continue
    return None


def main() -> int:
    raw = sys.stdin.read()
    try:
        config = json.loads(raw)
    except json.JSONDecodeError as ex:
        fail([f"stdin was not valid JSON (expected `<compose> config --format json` output): {ex}"])
        return 2

    errors: list[str] = []
    services = config.get("services", {})
    actual_names = set(services.keys())

    if actual_names != EXPECTED_SERVICES:
        missing = EXPECTED_SERVICES - actual_names
        extra = actual_names - EXPECTED_SERVICES
        if missing:
            errors.append(f"missing services: {sorted(missing)}")
        if extra:
            errors.append(f"unexpected services: {sorted(extra)}")

    for name, (target, expected_published) in EXPECTED_PORTS.items():
        service = services.get(name)
        if service is None:
            continue  # already reported as missing above
        port = find_port(service, target)
        if port is None:
            errors.append(f"{name}: no published port maps to container port {target}")
            continue
        published = port.get("published")
        if expected_published is not None and str(published) != expected_published:
            errors.append(
                f"{name}: expected published port {expected_published} -> {target}, got {published!r}"
            )
        elif published in (None, ""):
            errors.append(f"{name}: container port {target} has no published host port")

    for name in HEALTHCHECK_REQUIRED:
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
                errors.append(
                    f"worker: depends_on[{dep_name}] expected condition {expected_condition!r}, got {condition!r}"
                )
        unexpected_deps = set(depends_on) - set(WORKER_DEPENDS_ON)
        if unexpected_deps:
            errors.append(f"worker: unexpected depends_on entries: {sorted(unexpected_deps)}")

    if errors:
        fail(errors)
        return 1

    print("compose_check: OK — services, published ports, healthchecks and worker depends_on match CT-005")
    return 0


if __name__ == "__main__":
    sys.exit(main())
