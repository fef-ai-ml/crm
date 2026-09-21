# Test Fixtures

Shared inputs referenced by cases in [unit.md](unit.md), [integration.md](integration.md), and [e2e-load.md](e2e-load.md). The seed dataset is deterministic (fixed PRNG seed `20260915`) so every expected count below is exact and can be asserted.

## Seed Dataset

Produced by `SeedDatasetGenerator` (`Profiles.Application/Seeding`), loaded by `SeedDatasetLoader` — reached by `make seed` through the Development-only `POST /v1/dev/seed-dataset` and by integration tests through `SeedDataset.LoadAsync(factory, tenantId)`. Tenant slug `demo`, users `admin@demo.local` / `Demo-Passw0rd!` (Admin) and `viewer@demo.local` / `Demo-Passw0rd!` (Viewer), one API key named `sdk` whose plaintext is printed once by the seeder and written to `.env.local`.

| Dimension | Distribution |
|---|---|
| Profiles | 5,000; external ids `u-00001`…`u-05000`; emails `<first>.<n>@example.test` with first names cycling through a 50-name list starting `alice`; phones `+62812` + 7 digits for 60 % of profiles, absent for 40 % |
| `plan` | `pro` 20 % (1,000), `free` 70 %, absent 10 % |
| `country` | `ID` 40 %, `SG` 25 %, `MY` 15 %, `TH` 10 %, absent 10 % |
| `city` | set for 50 % of profiles from a 10-city list |
| `opted_in` | `true` 30 %, `false` 30 %, absent 40 % |
| `ltv` | numeric string with two decimals, right-skewed, present for 80 % |
| Events | 100,000 over the last 90 days; names `app_open` 60 %, `purchase` 20 %, `add_to_cart` 15 %, `logout` 5 %; `purchase.properties.amount` uniform 5–200 |
| Activity | 30 % of profiles have no event in the last 7 days; 5 % have no events at all |
| Merges | 50 profiles were created anonymous then identified, leaving 50 rows with `MergedIntoId` set (excluded from all counts below) |

The dataset is quota-based: a fixed cell table of plan × country × dormancy decides each profile's labels so the six counts below come out exact; the PRNG (SplitMix64, seed `20260915`) only shuffles profiles over those cells and spreads filler events. Event times are stored as ages relative to the load time, not absolute timestamps, so the six expected counts hold for four days after loading — dormancy (`app_open eq 0 in 7d`) and the 30-day purchase window both move with the clock after that. Profile `u-00001` (`alice.1@example.test`) is pinned as an active pro purchaser in `ID` with more than 120 events, for E2E-006. The 50 merged rows have no contact points; their `anon-seed-NNN` identities point at the winners `u-00100`, `u-00200`, … `u-05000`.

Expected counts (asserted by IT-014, IT-016, IT-042, E2E-002, E2E-004):

| Fixture | Rule | Expected members |
|---|---|---|
| `seed-pro-id.json` | `plan eq pro AND (country in [ID, SG] OR app_open eq 0 in 7d)` | 412 |
| `seed-purchasers.json` | `purchase gte 1 in 30d` | 2,731 |
| `seed-dormant.json` | `app_open eq 0 in 7d` | 1,500 |
| `seed-no-plan.json` | `plan not_exists` | 500 |
| `seed-not-pro.json` | `plan neq pro` | 4,000 (includes the 500 with no plan) |
| `seed-high-ltv-sg.json` | `ltv gte 150 AND country eq SG` | 187 |

The seeder asserts these counts after loading and fails if any differ, so a distribution change must update this table.

## Rule Fixtures

Directory `tests/fixtures/rules/`. Both the C# validator theory and the Zod test iterate every file (CT-002). File names are the identifiers used in unit cases.

`valid/`:

| File | Purpose |
|---|---|
| `attr-eq.json`, `attr-neq.json`, `attr-in.json`, `attr-nin.json` | one leaf each, keys `plan`/`tier`/`country` |
| `attr-gt.json`, `attr-gte.json`, `attr-lt.json`, `attr-lte.json` | numeric comparators on `ltv` |
| `attr-contains.json` | value `50%_off\` to exercise LIKE escaping |
| `attr-exists.json`, `attr-not-exists.json` | `tier` |
| `attr-bool.json` | `opted_in eq true` |
| `event-gte.json`, `event-eq-zero.json`, `event-eq-n.json`, `event-lte.json` | `purchase`/`app_open` with `30d`/`7d` windows |
| `group-nested.json` | the spec §5.1 example tree |
| `group-not.json` | `not (plan eq pro)` |
| `depth-5.json`, `leaves-20.json` | boundary values that must pass |
| `seed-*.json` | the six seed rules above |

`invalid/` (each file carries an `"expectedErrorPath"` sibling property that both validators must report first):

| File | Violation |
|---|---|
| `depth-6.json` | depth > 5 |
| `leaves-21.json` | more than 20 leaves |
| `not-two-children.json`, `not-no-children.json` | `not` arity |
| `window-366d.json`, `window-1w.json`, `window-0d.json`, `window-bare.json` | window format / range |
| `key-dot.json`, `key-quote.json`, `key-65.json` | key pattern |
| `cmp-unknown.json` | unknown comparator |
| `event-count-negative.json` | count < 0 |
| `type-unknown.json` | unknown node type |

## Golden SQL Files

Directory `tests/fixtures/golden/`, one `<name>.sql` per `valid/` rule fixture, produced by `RuleCompiler` for tenant `00000000-0000-0000-0000-000000000001` with clock `2026-09-16T00:00:00Z`. Format: the SQL text, a blank line, then one `@name = <literal>` line per parameter in order. Whitespace is normalized (single spaces, no trailing spaces) before comparison. Regenerate deliberately with `dotnet test --filter RuleCompilerGolden -- --update-golden` and review the diff; an unexpected golden change in a pull request is a review blocker.

Contract snapshots for CT-004 live in `tests/fixtures/contracts/<Type>.json` and follow the same regenerate-and-review rule.
