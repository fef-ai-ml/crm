"""Testcontainers image literals must be images the compose stack also runs (ruling C8)."""
from __future__ import annotations

import re
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
FIXTURE = ROOT / "tests" / "Crm.IntegrationTests" / "Fixtures" / "CrmStackFixture.cs"
COMPOSE = ROOT / "deploy" / "docker-compose.yml"


class ImagePinTests(unittest.TestCase):
    def test_kafka_and_redis_builders_use_the_compose_images(self):
        fixture = FIXTURE.read_text(encoding="utf-8")
        compose = COMPOSE.read_text(encoding="utf-8")
        literals = re.findall(r'new (?:Kafka|Redis)Builder\("([^"]+)"\)', fixture)
        self.assertEqual(len(literals), 2, f"expected one Kafka and one Redis builder literal, found {literals}")
        for image in literals:
            self.assertIn(f"image: {image}", compose, f"{image} is not an image of deploy/docker-compose.yml")


if __name__ == "__main__":
    unittest.main()
