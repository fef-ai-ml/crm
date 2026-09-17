# CRM platform — developer entry points.

# Prefer the Compose plugin (docker compose) when present; fall back to the standalone
# docker-compose binary otherwise. Keeps this Makefile working on either install.
COMPOSE_BIN  ?= $(shell docker compose version >/dev/null 2>&1 && echo "docker compose" || echo docker-compose)
COMPOSE      := $(COMPOSE_BIN) --project-directory deploy -f deploy/docker-compose.yml
ENV_FILE     := deploy/.env
ENV_EXAMPLE  := deploy/.env.example

# deploy/.env is optional here; when present its values (MSSQL_IMAGE, MSSQL_HOST_PORT, ...)
# are visible to the targets below as well as to compose.
-include $(ENV_FILE)

# --- remote container engine over ssh ---------------------------------------
# The container engine may live on another machine (a home-lab box running Podman).
# `make tunnel` forwards its Podman API socket to $(ENGINE_SOCK) and maps the stack's
# published ports back to localhost, so appsettings, tests and browsers keep using
# localhost. Point docker at it with: docker context create home-lab --docker host=unix://$(ENGINE_SOCK)
ENGINE_SSH_HOST  ?= home-lab
ENGINE_REMOTE_SOCK ?= /run/user/1000/podman/podman.sock
ENGINE_SOCK      ?= $(HOME)/.local/share/containers/$(ENGINE_SSH_HOST).sock
MSSQL_HOST_PORT  ?= 1433
REDIS_HOST_PORT  ?= 6380
# Infrastructure ports are always forwarded. App ports (api 8080, worker 8081, web 3000) only
# with `make tunnel-apps`, i.e. when the apps run in compose on the engine — forwarding them
# while running `dotnet run` locally would shadow the local listeners.
TUNNEL_PORTS     := -L 1433:localhost:$(MSSQL_HOST_PORT) -L 6380:localhost:$(REDIS_HOST_PORT) -L 19092:localhost:19092
TUNNEL_APP_PORTS := -L 8080:localhost:8080 -L 8081:localhost:8081 -L 3000:localhost:3000

# $(call open_tunnel,<-L flags>,<probe port>,<label>) — no-op when a tunnel forwarding <probe port> is already up.
define open_tunnel
if DOCKER_HOST=unix://$(ENGINE_SOCK) docker version >/dev/null 2>&1 && pgrep -f "ssh -fNT .*-L $(2):localhost" >/dev/null; then \
	echo "tunnel to $(ENGINE_SSH_HOST) already up ($(3))"; \
else \
	pkill -f "ssh -fNT .*$(ENGINE_SOCK)" 2>/dev/null || true; sleep 1; \
	mkdir -p $(dir $(ENGINE_SOCK)); \
	ssh -fNT -o ExitOnForwardFailure=yes -o StreamLocalBindUnlink=yes -o ServerAliveInterval=30 \
		-L $(ENGINE_SOCK):$(ENGINE_REMOTE_SOCK) $(1) $(ENGINE_SSH_HOST) && \
	echo "tunnel to $(ENGINE_SSH_HOST) up: socket $(ENGINE_SOCK); $(3)"; \
fi
endef

.PHONY: help up down logs ps env tunnel tunnel-apps tunnel-down test-unit test-integration test-integration-run test docs-check compose-check seed migrate migrate-run openapi web-dev web-install e2e

help: ## list targets
	@grep -hE '^[a-zA-Z0-9_-]+:.*## ' $(MAKEFILE_LIST) | awk 'BEGIN {FS = ":.*## "}; {printf "  %-18s %s\n", $$1, $$2}'

env: ## create deploy/.env from the example when absent
	@test -f $(ENV_FILE) || (cp $(ENV_EXAMPLE) $(ENV_FILE) && echo "created $(ENV_FILE) from example")

tunnel: ## ssh tunnel to the remote engine: podman socket + infra ports (1433, 6380, 19092)
	@$(call open_tunnel,$(TUNNEL_PORTS),1433,localhost:1433->$(MSSQL_HOST_PORT) 6380 19092)

tunnel-apps: ## like tunnel, also forwards api/worker/web ports 8080 8081 3000 (apps running in compose)
	@$(call open_tunnel,$(TUNNEL_PORTS) $(TUNNEL_APP_PORTS),8080,localhost:1433->$(MSSQL_HOST_PORT) 6380 19092 8080 8081 3000)

tunnel-down: ## close the ssh tunnel
	@pkill -f "ssh -fNT .*$(ENGINE_SOCK)" && echo "tunnel closed" || echo "no tunnel running"

up: env ## start the stack: infrastructure, api, worker, web (builds images)
	$(COMPOSE) up -d --build --wait
	@echo ""
	@echo "SQL Server  localhost:1433  (sa / see deploy/.env; engine-side port $(MSSQL_HOST_PORT))"
	@echo "Kafka       localhost:19092 (external listener)"
	@echo "Redis       localhost:$(REDIS_HOST_PORT)"
	@echo "API         http://localhost:8080  (Scalar UI at /swagger, health at /health/ready)"
	@echo "Worker      http://localhost:8081/health/ready"
	@echo "Web         http://localhost:3000"

down: ## stop the stack and remove volumes (KEEP=1 keeps data)
ifeq ($(KEEP),1)
	$(COMPOSE) down
else
	$(COMPOSE) down -v
endif

ps: ## show service status
	$(COMPOSE) ps

logs: ## follow logs
	$(COMPOSE) logs -f --tail=100

docs-check: ## validate the documentation set
	python3 tools/docs_check.py

compose-check: ## validate compose services, ports and healthchecks (CT-005)
	$(COMPOSE) config --format json | python3 tools/compose_check.py

# --- wired by plan A -------------------------------------------------------

test-unit: ## run unit + architecture + contract tests
	dotnet test Crm.sln --filter "Category!=Integration"

# Testcontainers: engine socket from DOCKER_HOST when set (e.g. the ssh tunnel socket), else the
# active docker context; no Ryuk on Podman; SQL image from deploy/.env; host override for remote engines.
TESTCONTAINERS_HOST_OVERRIDE ?=

test-integration: env ## run the Testcontainers suite against the active engine (see docs/dev-setup.md)
	@$(MAKE) --no-print-directory test-integration-run

# -include deploy/.env (top of file) is evaluated when make parses the Makefile, before the `env`
# prerequisite above creates the file on a fresh clone; MSSQL_IMAGE below would resolve empty on that
# first invocation. The sub-make re-parses the (now-existing) file, so this target sees the real value.
test-integration-run:
	DOCKER_HOST=$${DOCKER_HOST:-$$(docker context inspect --format '{{.Endpoints.docker.Host}}')} \
	TESTCONTAINERS_RYUK_DISABLED=true \
	MSSQL_IMAGE=$(MSSQL_IMAGE) \
	TESTCONTAINERS_HOST_OVERRIDE=$(TESTCONTAINERS_HOST_OVERRIDE) \
	dotnet test Crm.sln --filter "Category=Integration"

test: test-unit test-integration ## run everything

openapi: ## export docs/api/openapi.json from the API project (CT-001)
	ConnectionStrings__Crm="Server=localhost;Database=crm;User Id=sa;Password=unused;TrustServerCertificate=True" \
	Redis__Configuration="localhost:6379" Kafka__BootstrapServers="localhost:9092" \
	Auth__SigningKey="openapi-export-only-0123456789abcdef0123456789" \
	dotnet run --project src/Hosts/Crm.Api -c Release --no-launch-profile -- --export-openapi $(CURDIR)/docs/api/openapi.json

CRM_CONNECTION_STRING ?= Server=localhost,1433;Database=crm;User Id=sa;Password=$(MSSQL_SA_PASSWORD);TrustServerCertificate=True;Encrypt=False

migrate: env ## apply EF Core migrations (module contexts with migrations: Tenancy)
	@$(MAKE) --no-print-directory migrate-run

# -include deploy/.env (top of file) is evaluated when make parses the Makefile, before the `env`
# prerequisite above creates the file on a fresh clone; MSSQL_SA_PASSWORD (and so
# CRM_CONNECTION_STRING) below would resolve empty on that first invocation. The sub-make re-parses
# the (now-existing) file, so this target sees the real password.
migrate-run:
	dotnet tool restore
	ConnectionStrings__Crm="$(CRM_CONNECTION_STRING)" dotnet ef database update \
		--project src/Modules/Tenancy/Tenancy.Infrastructure \
		--startup-project src/Modules/Tenancy/Tenancy.Infrastructure \
		--context TenancyDbContext

seed: ## bootstrap the demo tenant, Admin + Viewer users and an API key (API must run in Development)
	python3 tools/seed.py

web-dev: ## run the React dev server
	cd src/Hosts/Crm.Web && npm run dev

web-install: ## install web dependencies
	cd src/Hosts/Crm.Web && npm ci

e2e: ## run Playwright against the compose stack (make up first)
	cd src/Hosts/Crm.Web && npx playwright install --with-deps chromium && npm run e2e
