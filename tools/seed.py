#!/usr/bin/env python3
"""Bootstrap the demo tenant against a running Development API (make seed).

Idempotent: an existing tenant (409) is reused; a new API key is minted only when
.env.local does not already hold one. Standard library only.
"""
import json
import os
import sys
import urllib.error
import urllib.request

BASE = os.environ.get("CRM_API_URL", "http://localhost:8080").rstrip("/")
ENV_LOCAL = os.environ.get("CRM_ENV_LOCAL", ".env.local")
ADMIN_EMAIL, VIEWER_EMAIL, PASSWORD = "admin@demo.local", "viewer@demo.local", "Demo-Passw0rd!"


def call(method, path, body=None, token=None):
    data = json.dumps(body).encode() if body is not None else None
    request = urllib.request.Request(BASE + path, data=data, method=method)
    request.add_header("Content-Type", "application/json")
    if token:
        request.add_header("Authorization", "Bearer " + token)
    try:
        with urllib.request.urlopen(request, timeout=30) as response:
            raw = response.read()
            return response.status, json.loads(raw) if raw else None
    except urllib.error.HTTPError as error:
        raw = error.read()
        return error.code, json.loads(raw) if raw else None


def fail(step, status, body):
    detail = (body or {}).get("detail") or body
    sys.exit(f"seed: {step} failed with HTTP {status}: {detail}")


def existing_key():
    if not os.path.exists(ENV_LOCAL):
        return None
    with open(ENV_LOCAL, encoding="utf-8") as handle:
        for line in handle:
            if line.startswith("CRM_API_KEY=") and line.strip() != "CRM_API_KEY=":
                return line.strip().split("=", 1)[1]
    return None


def main():
    status, body = call("POST", "/v1/tenants", {
        "name": "Demo", "slug": "demo",
        "adminEmail": ADMIN_EMAIL, "adminPassword": PASSWORD,
        "viewerEmail": VIEWER_EMAIL, "viewerPassword": PASSWORD,
    })
    if status == 201:
        print(f"seed: created tenant demo ({body['tenantId']})")
    elif status == 409:
        print("seed: tenant demo already exists")
    elif status == 404:
        sys.exit("seed: POST /v1/tenants is not mapped; the API must run in Development")
    else:
        fail("bootstrap", status, body)

    status, body = call("POST", "/v1/auth/login", {"email": ADMIN_EMAIL, "password": PASSWORD})
    if status != 200:
        fail("login", status, body)
    token = body["accessToken"]
    print(f"seed: login ok as {ADMIN_EMAIL} (expires in {body['expiresIn']} s)")

    if existing_key():
        print(f"seed: {ENV_LOCAL} already holds CRM_API_KEY; not minting another")
        return

    status, body = call("POST", "/v1/api-keys", {"name": "sdk"}, token)
    if status != 201:
        fail("create api key", status, body)
    with open(ENV_LOCAL, "a", encoding="utf-8") as handle:
        handle.write(f"CRM_API_KEY={body['key']}\n")
    print(f"seed: API key sdk ({body['prefix']}…) written to {ENV_LOCAL}")


if __name__ == "__main__":
    main()
