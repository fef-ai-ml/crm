#!/usr/bin/env python3
"""Bootstrap the demo tenant against a running Development API (make seed).

Idempotent: an existing tenant (409) is reused. The CRM_API_KEY stored in
.env.local is kept only when it is an active (non-revoked) key of the tenant,
per GET /v1/api-keys; otherwise (no stored key, an unparsable one, or one
belonging to a deleted or revoked key) a new key is minted and replaces it.
Also loads the 5k-profile / 100k-event demo dataset through the
Development-only POST /v1/dev/seed-dataset (idempotent on the server: a
second run reports "exists"), then upserts and runs the six demo segments
through the Development-only POST /v1/dev/seed-segments (H22; also
idempotent — an existing segment is left untouched and an already-run one
is not re-queued). Standard library only.
"""
import json
import os
import sys
import time
import urllib.error
import urllib.request

BASE = os.environ.get("CRM_API_URL", "http://localhost:8080").rstrip("/")
ENV_LOCAL = os.environ.get("CRM_ENV_LOCAL", ".env.local")
ADMIN_EMAIL, VIEWER_EMAIL, PASSWORD = "admin@demo.local", "viewer@demo.local", "Demo-Passw0rd!"
KEY_SCHEME = "crm_live_"
PREFIX_LENGTH = 8


def parse(raw):
    if not raw:
        return None
    try:
        return json.loads(raw)
    except json.JSONDecodeError:
        return {"detail": raw.decode(errors="replace")[:200]}


def call(method, path, body=None, token=None, timeout=30):
    data = json.dumps(body).encode() if body is not None else None
    request = urllib.request.Request(BASE + path, data=data, method=method)
    request.add_header("Content-Type", "application/json")
    if token:
        request.add_header("Authorization", "Bearer " + token)
    try:
        with urllib.request.urlopen(request, timeout=timeout) as response:
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


def key_prefix(key):
    if not key or not key.startswith(KEY_SCHEME):
        return None
    secret = key[len(KEY_SCHEME):]
    if len(secret) < PREFIX_LENGTH:
        return None
    return secret[:PREFIX_LENGTH]


def active_prefixes(items) -> set:
    return {item["prefix"] for item in items if not item.get("revokedAt")}


def should_mint(stored_key, active: set) -> bool:
    prefix = key_prefix(stored_key)
    return not stored_key or prefix is None or prefix not in active


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


def dataset_report(body):
    """Lines printed for the answer of POST /v1/dev/seed-dataset."""
    seconds = body["elapsedMilliseconds"] / 1000
    lines = [f"seed: dataset {body['status']}: {body['profiles']} profiles (+{body['mergedProfiles']} merged), "
             f"{body['events']} events in {seconds:.1f} s"]
    lines += [f"seed:   {name} = {count}" for name, count in sorted(body["segments"].items())]
    if body["status"] == "exists":
        lines.append("seed:   (counts are the tenant's current totals; they drift about 4 days after loading — docs/test/fixtures.md)")
    return lines


def load_dataset(token):
    """5k profiles / 100k events (docs/test/fixtures.md). Idempotent on the server: a second run reports 'exists'.
    Returns the response's own `segments` dict (name -> count) so `seed_segments` never duplicates these numbers."""
    status, body = call("POST", "/v1/dev/seed-dataset", {}, token, timeout=600)
    if status == 404:
        sys.exit("seed: POST /v1/dev/seed-dataset is not available — the API must run in Development and include plan F")
    if status != 200:
        fail("load seed dataset", status, body)
    for line in dataset_report(body):
        print(line)
    return body["segments"]


def compare_segment_counts(expected: dict, actual: dict) -> list:
    """Pure comparison (unit-tested): one message per name in `expected` that is missing from `actual` or whose
    count differs from it. A name present in `actual` but not `expected` is not this function's concern.
    Callers pass only the names that were actually run — a Draft seed segment never has a `memberCount` to
    compare and is checked separately by `check_draft_segments`."""
    messages = []
    for name, count in expected.items():
        if name not in actual:
            messages.append(f"{name}: missing from GET /v1/segments")
        elif actual[name] != count:
            messages.append(f"{name}: expected {count}, found {actual[name]}")
    return messages


def check_draft_segments(draft_names, segments: dict) -> list:
    """Pure comparison (unit-tested): a Draft seed segment is never queued (`SeedSegmentsEndpoint` only runs
    `Active` ones), so it must stay unmaterialised — `memberCount == 0` and no `lastRunAt`. One message per
    name that is missing from `segments` or fails that check (C-1: a Draft segment compared against the rule's
    match count, as the dataset endpoint reports it, would always fail)."""
    messages = []
    for name in sorted(draft_names):
        item = segments.get(name)
        if item is None:
            messages.append(f"{name}: missing from GET /v1/segments")
        elif item.get("memberCount") != 0 or item.get("lastRunAt") is not None:
            messages.append(f"{name}: expected an unmaterialised Draft (memberCount 0, no lastRunAt), "
                             f"found memberCount={item.get('memberCount')}, lastRunAt={item.get('lastRunAt')}")
    return messages


def seed_segments(token, expected_counts):
    """H22: upserts the six demo segments (`SeedSegments`, C# source of truth) through the Development-only
    POST /v1/dev/seed-segments and queues a run for each Active one, then polls GET /v1/segments until every
    Active seed segment has a `lastRunAt` (timeout 120 s, 1 s interval) and compares `memberCount` against
    `expected_counts` — the same six counts `load_dataset`'s own response just printed, never duplicated here.
    A Draft seed segment (`seed-high-ltv-sg`) is never queued and so never has a `memberCount` to compare
    against the rule's match count (C-1); it is checked separately by `check_draft_segments` and printed as
    "not materialised". Idempotent on the server: an existing segment is left untouched and an already-run
    one is not re-queued."""
    status, body = call("POST", "/v1/dev/seed-segments", {}, token, timeout=60)
    if status == 404:
        sys.exit("seed: POST /v1/dev/seed-segments is not available — the API must run in Development and include plan H")
    if status != 200:
        fail("seed segments", status, body)

    active_names = {item["name"] for item in body if item["status"] == "Active"}
    draft_names = {item["name"] for item in body if item["status"] != "Active"}

    deadline = time.time() + 120
    segments = {}
    while True:
        status, body = call("GET", "/v1/segments?limit=50", token=token)
        if status != 200:
            fail("list segments", status, body)
        segments = {item["name"]: item for item in body["items"]}
        pending = sorted(name for name in active_names if not segments.get(name, {}).get("lastRunAt"))
        if not pending:
            break
        if time.time() > deadline:
            sys.exit(f"seed: segments did not finish their first run within 120 s: {pending}")
        time.sleep(1)

    actual_counts = {name: item["memberCount"] for name, item in segments.items()}
    active_expected = {name: count for name, count in expected_counts.items() if name in active_names}
    for name in sorted(active_expected):
        print(f"seed:   {name} = {actual_counts.get(name, '?')} (expected {active_expected[name]})")
    for name in sorted(draft_names):
        print(f"seed:   {name} = draft — not materialised (rule would match {expected_counts.get(name, '?')})")

    mismatches = compare_segment_counts(active_expected, actual_counts)
    mismatches += check_draft_segments(draft_names, segments)
    if mismatches:
        sys.exit("seed: segment counts do not match the seed dataset: " + "; ".join(mismatches))


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

    # ApiKeyEndpoints.ListAsync takes no cursor parameter and always returns nextCursor: null, so a
    # single call sees every key; a non-null cursor means the server grew pagination this script does
    # not know about, and mis-validating a partial list is worse than failing loudly.
    status, body = call("GET", "/v1/api-keys", token=token)
    if status != 200:
        fail("list api keys", status, body)
    items = body["items"]
    if body.get("nextCursor"):
        fail("list api keys", status, {"detail": "server returned a non-null nextCursor; "
                                                  "the seed script must learn pagination"})

    stored = existing_key()
    if not should_mint(stored, active_prefixes(items)):
        print(f"seed: {ENV_LOCAL} already holds CRM_API_KEY; not minting another")
    else:
        if stored:
            print(f"seed: the CRM_API_KEY in {ENV_LOCAL} is not an active key of this tenant "
                  f"(database recreated or key revoked); replacing it")

        status, body = call("POST", "/v1/api-keys", {"name": "sdk"}, token)
        if status != 201:
            fail("create api key", status, body)
        write_key(ENV_LOCAL, body['key'])
        print(f"seed: API key sdk ({body['prefix']}…) written to {ENV_LOCAL}")

    dataset_segments = load_dataset(token)
    seed_segments(token, dataset_segments)


if __name__ == "__main__":
    main()
