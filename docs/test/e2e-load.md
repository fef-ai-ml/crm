# End-to-End and Load Test Cases

End-to-end cases (`E2E`) run Playwright against the full `docker compose` stack after `make up && make seed`; the seeded Admin is `admin@demo.local` (see [fixtures.md](fixtures.md)). Load cases (`LT`) run k6 against the same stack; thresholds are the local budgets from NFR-01 and NFR-02 and results are recorded in the root README with the machine profile.

## End-to-End

### E2E-001 — login reaches the dashboard
- Traces: FR-19
- Given the seeded Admin credentials
- When the user submits the login form
- Then the browser lands on `/segments`, the access token is held in memory only (not in `localStorage`), and a `crm_refresh` cookie is set; a wrong password shows the problem+json `detail`
- Implemented: src/Hosts/Crm.Web/e2e/login.spec.ts

### E2E-002 — build a nested segment with live count
- Traces: FR-21
- Given `/segments/new`
- When the user adds an `and` group with `plan eq pro`, nests an `or` group with `country in [ID, SG]` and `app_open eq 0 in 7d`, and waits 500 ms
- Then the live count shows `412` (fixture `seed-pro-id`) and the badge reads "batch" (the `eq 0` leaf makes it ineligible); saving redirects to `/segments/{id}`
- Implemented: src/Hosts/Crm.Web/e2e/segment-builder.spec.ts

### E2E-003 — validation errors surface on the field
- Traces: FR-21
- Given the builder with an event leaf whose window is `400d`
- When the user clicks Save
- Then the window input shows "window must match ^\d+(d|h)$ and be at most 365 days" (the server's actual message, from `errors["definition.children[0].window"]` — the catalogue's shorter wording above predated plan H's exact copy) and no segment is created
- Implemented: src/Hosts/Crm.Web/e2e/segment-builder.spec.ts

### E2E-004 — run now shows members and runs
- Traces: FR-15, FR-21
- Given a saved segment
- When the user clicks Run now and opens the Members and Runs tabs
- Then within 10 s the Runs tab shows a Finished run with `412` members and trigger Manual, and the Members tab lists profiles with external id, email, and last seen, paging at 50
- Implemented: src/Hosts/Crm.Web/e2e/segment-run.spec.ts

### E2E-005 — segment list columns and badges
- Traces: FR-22
- Given seed segments `seed-pro-id` (batch, Active) and `seed-purchasers` (real-time, Active) plus one Draft
- When the user opens `/segments`
- Then each row shows member count, last run time, status, and a "real-time" or "batch" badge matching `isRealtimeEligible`
- Implemented: src/Hosts/Crm.Web/e2e/segment-list.spec.ts

### E2E-006 — profile explorer search and timeline
- Traces: FR-20
- Given the seed dataset
- When the user types `alice` in `/profiles`, opens the first result, and scrolls the timeline
- Then the results table shows the matching profile, the drawer shows core fields and an attributes JSON viewer, and the timeline loads a second page of 50 events on scroll
- Planned: src/Hosts/Crm.Web/e2e/profile-explorer.spec.ts

### E2E-007 — API key lifecycle in the UI
- Traces: FR-23
- Given the Admin on the API keys screen
- When they create key "sdk", copy it, close the dialog, and then revoke it
- Then the full key is visible only in the creation dialog, the list shows the 8-character prefix, and after revocation the row shows a revoked timestamp and `POST /v1/track` with that key returns 401
- Planned: src/Hosts/Crm.Web/e2e/api-keys.spec.ts

### E2E-008 — Viewer sees a read-only UI
- Traces: FR-19
- Given the seeded Viewer user logs in
- When they open `/segments` and a segment detail
- Then Save, Run now, and API-key actions are absent or disabled, and lists and detail views render
- Planned: src/Hosts/Crm.Web/e2e/viewer-role.spec.ts

## Load

### LT-001 — sustained ingest throughput
- Traces: NFR-01, NFR-02
- Given `tests/Crm.LoadTests/ingest.js` with 1,000 events/s constant arrival rate for 60 s across 10,000 distinct user ids
- When k6 runs against `http://localhost:8080/v1/track`
- Then thresholds hold: `http_req_failed < 0.1%`, `http_req_duration p(95) < 50 ms`, all responses 202; `crm_consumer_lag` returns to 0 within 30 s after the run ends
- During the run also capture `sys.dm_db_index_operational_stats` deltas for `IX_Profiles_TenantId_LastSeenAt_Id` (`leaf_allocation_count`, `page_latch_wait_count`, `page_latch_wait_in_ms`) and "Number of Deadlocks/sec" next to `crm_dlq_total`; revisit the index if latch wait exceeds ~1% of run wall time (ruling G2, focus A).
- Planned: tests/Crm.LoadTests/ingest.js

### LT-002 — ingest-to-timeline latency under load
- Traces: NFR-02
- Given LT-001 running, and a probe user tracked once per second with a client timestamp
- When the probe polls `GET /v1/profiles/{id}/events` until the event appears
- Then p95 of (appearance time − send time) over the 60 probes is < 2 s
- Planned: tests/Crm.LoadTests/timeline-latency.js

### LT-003 — real-time segment latency under load
- Traces: NFR-02
- Given LT-001 running and eligible segment `seed-purchasers` (`purchase gte 1 in 30d`)
- When the probe user tracks a `purchase` once per second and the probe polls `GET /v1/segments/{id}` for `realtimeMemberCount` to increment
- Then p95 of the increment latency is < 2 s
- Plan I note: the script probes its own real-time-eligible segment `lt-003-probe` (`lt003_probe gte 1 in 1h`, created on first run) instead of `seed-purchasers`, because LT-001's traffic also contains purchases by new users and would move that count; one probe at a time, a fresh user per probe, so every increment is the probe's own. First measurement (plan I task 5, cloud dev container, 4 vCPU): idle p95 60 ms (pass); under an ad-hoc 1,000 events/s `/v1/batch` load p95 8.2 s (fail), bounded by the profiles consumer's backlog on `events.raw` while the segmentation consumer's lag stayed at 3–5 records — the official run alongside LT-001 belongs to the load-test plan
- Implemented: tests/Crm.LoadTests/realtime-latency.js

### LT-004 — rate limit sheds load without errors
- Traces: NFR-07
- Given one API key and 5,000 requests/min offered for 30 s
- When k6 runs
- Then about 2,000 per minute succeed with 202, the remainder are 429 with `Retry-After`, no 5xx occurs, and the API's p95 for accepted requests stays < 50 ms
- Planned: tests/Crm.LoadTests/rate-limit.js

### LT-005 — cold start and quickstart
- Traces: NFR-10
- Given a machine with Docker allocated 8 GB RAM and no running containers
- When `make up`, `make seed`, and the README curl example are executed in order
- Then all services are healthy within 3 minutes, seeding finishes within 2 minutes (5k profiles, 100k events), the curl returns 202, and the whole quickstart takes three commands as documented
- Implemented — `coldstart.sh` measures `make up` (≤ 3 min) and `make seed` (≤ 2 min, now including the 5k/100k dataset and its self-verification) and runs the quickstart probe; results table in the root README.
