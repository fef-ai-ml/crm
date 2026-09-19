"""The API and Worker Dockerfiles must stay in step through the restore line (parallel image
builds share one cached restore layer only while the files are identical up to that line), and
both must expose the same opt-in IPv4-only switch before the restore runs."""
from __future__ import annotations

import re
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
API_DOCKERFILE = ROOT / "src" / "Hosts" / "Crm.Api" / "Dockerfile"
WORKER_DOCKERFILE = ROOT / "src" / "Hosts" / "Crm.Worker" / "Dockerfile"
COMPOSE = ROOT / "deploy" / "docker-compose.yml"


def _lines_up_to_restore(path: Path) -> list[str]:
    lines = path.read_text(encoding="utf-8").splitlines()
    kept = [line for line in lines if line.strip() and not line.strip().startswith("#")]
    for i, line in enumerate(kept):
        if line.startswith("RUN dotnet restore"):
            return kept[: i + 1]
    raise AssertionError(f"no 'RUN dotnet restore' line found in {path}")


class DockerfileTests(unittest.TestCase):
    def test_api_and_worker_dockerfiles_are_identical_through_the_restore_step(self):
        api_lines = _lines_up_to_restore(API_DOCKERFILE)
        worker_lines = _lines_up_to_restore(WORKER_DOCKERFILE)
        self.assertEqual(
            api_lines, worker_lines,
            "src/Hosts/Crm.Api/Dockerfile and src/Hosts/Crm.Worker/Dockerfile must be identical "
            "up to and including the restore line: parallel image builds share one cached restore "
            "layer only while the files are identical up to that line",
        )
        restore_line = api_lines[-1]
        self.assertIn("src/Hosts/Crm.Api/Crm.Api.csproj", restore_line)
        self.assertIn("src/Hosts/Crm.Worker/Crm.Worker.csproj", restore_line)

    def test_build_stage_has_the_opt_in_ipv4_switch_before_restore(self):
        for path in (API_DOCKERFILE, WORKER_DOCKERFILE):
            lines = path.read_text(encoding="utf-8").splitlines()
            build_from_index = next(
                i for i, line in enumerate(lines)
                if re.match(r"FROM .* AS build\s*$", line)
            )
            restore_index = next(
                i for i, line in enumerate(lines)
                if line.startswith("RUN dotnet restore")
            )
            arg_index = next(
                (i for i, line in enumerate(lines)
                 if line.strip() == "ARG DOTNET_SYSTEM_NET_DISABLEIPV6=0"),
                None,
            )
            env_index = next(
                (i for i, line in enumerate(lines)
                 if line.strip() == "ENV DOTNET_SYSTEM_NET_DISABLEIPV6=${DOTNET_SYSTEM_NET_DISABLEIPV6}"),
                None,
            )
            self.assertIsNotNone(arg_index, f"{path} is missing 'ARG DOTNET_SYSTEM_NET_DISABLEIPV6=0'")
            self.assertIsNotNone(
                env_index,
                f"{path} is missing 'ENV DOTNET_SYSTEM_NET_DISABLEIPV6=${{DOTNET_SYSTEM_NET_DISABLEIPV6}}'",
            )
            self.assertTrue(
                build_from_index < arg_index < env_index < restore_index,
                f"{path}: the IPv4-only switch must appear after 'FROM … AS build' and before the "
                f"restore line",
            )

    def test_compose_passes_the_switch_as_a_build_arg_defaulting_to_off(self):
        compose = COMPOSE.read_text(encoding="utf-8")
        occurrences = compose.count("DOTNET_SYSTEM_NET_DISABLEIPV6: ${DOTNET_SYSTEM_NET_DISABLEIPV6:-0}")
        self.assertEqual(
            occurrences, 2,
            "deploy/docker-compose.yml must pass DOTNET_SYSTEM_NET_DISABLEIPV6 as a build arg "
            "(defaulting to off) under both the api and worker services",
        )


if __name__ == "__main__":
    unittest.main()
