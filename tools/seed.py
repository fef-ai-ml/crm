#!/usr/bin/env python3
"""Bootstrap the demo tenant against a running Development API (make seed).

Idempotent: an existing tenant (409) is reused, keeping any API key already in
.env.local; a freshly created tenant (201) means the stored key is stale, so a
new one is minted and replaces it. Standard library only.
"""
import json
import os
import sys
import urllib.error
import urllib.request

BASE = os.environ.get("CRM_API_URL", "http://localhost:8080").rstrip("/")
ENV_LOCAL = os.environ.get("CRM_ENV_LOCAL", ".env.local")
ADMIN_EMAIL, VIEWER_EMAIL, PASSWORD = "admin@demo.local", "viewer@demo.local", "Demo-Passw0rd!"


def parse(raw):
    if not raw:
        return None
    try:
        return json.loads(raw)
    except json.JSONDecodeError:
        return {"detail": raw.decode(errors="replace")[:200]}


def call(method, path, body=None, token=None):
    data = json.dumps(body).encode() if body is not None else None
    request = urllib.request.Request(BASE + path, data=data, method=method)
    request.add_header("Content-Type", "application/json")
    if token:
        request.add_header("Authorization", "Bearer " + token)
    try:
        with urllib.request.urlopen(request, timeout=30) as response:
            return response.status, parse(response.read())
    except urllib.error.HTTPError as error:
        return error.code, parse(error.read())
    except urllib.error.URLError as error:
        sys.exit(f"seed: cannot reach {BASE}: {error.reason}")


def fail(step, status, body):
    detail = (body or {}).get("detail") or body
    sys.exit(f"seed: {step} failed with HTTP {status}: {detail}")


def existing_key(path: str = ENV_LOCAL):
    if not os.path.exists(path):
        return None
    with open(path, encoding="utf-8") as handle:
        for line in handle:
            if line.startswith("CRM_API_KEY=") and line.strip() != "CRM_API_KEY=":
                return line.strip().split("=", 1)[1]
    return None


def should_mint(tenant_created: bool, stored_key) -> bool:
    return tenant_created or not stored_key


def write_key(path: str, key: str) -> None:
    lines = []
    if os.path.exists(path):
        with open(path, encoding="utf-8") as handle:
            lines = handle.read().splitlines()

    new_line = f"CRM_API_KEY={key}"
    replaced = False
    result = []
    for line in lines:
        if line.startswith("CRM_API_KEY="):
            if not replaced:
                result.append(new_line)
                replaced = True
        else:
            result.append(line)
    if not replaced:
        result.append(new_line)

    with open(path, "w", encoding="utf-8") as handle:
        handle.write("\n".join(result) + "\n")


def main():
    status, body = call("POST", "/v1/tenants", {
        "name": "Demo", "slug": "demo",
        "adminEmail": ADMIN_EMAIL, "adminPassword": PASSWORD,
        "viewerEmail": VIEWER_EMAIL, "viewerPassword": PASSWORD,
    })
    tenant_created = status == 201
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

    stored = existing_key()
    if not should_mint(tenant_created, stored):
        print(f"seed: {ENV_LOCAL} already holds CRM_API_KEY; not minting another")
        return
    if stored:
        print(f"seed: tenant was just created, so the CRM_API_KEY in {ENV_LOCAL} is stale; replacing it")

    status, body = call("POST", "/v1/api-keys", {"name": "sdk"}, token)
    if status != 201:
        fail("create api key", status, body)
    write_key(ENV_LOCAL, body['key'])
    print(f"seed: API key sdk ({body['prefix']}…) written to {ENV_LOCAL}")


if __name__ == "__main__":
    main()
