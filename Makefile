# CRM platform — developer entry points.
# Targets marked (plan A/B) are wired up by later plans; they fail loudly until then.

COMPOSE      := docker-compose --project-directory deploy -f deploy/docker-compose.yml
ENV_FILE     := deploy/.env
ENV_EXAMPLE  := deploy/.env.example

.PHONY: help up down logs ps env test-unit test-integration test docs-check seed migrate openapi web-dev

help: ## list targets
	@grep -E '^[a-zA-Z_-]+:.*?## ' $(MAKEFILE_LIST) | awk 'BEGIN {FS = ":.*?## "}; {printf "  %-18s %s\n", $$1, $$2}'

env: ## create deploy/.env from the example when absent
	@test -f $(ENV_FILE) || (cp $(ENV_EXAMPLE) $(ENV_FILE) && echo "created $(ENV_FILE) from example")

up: env ## start the stack (infrastructure now; api/worker/web after plan A)
	$(COMPOSE) up -d --wait
	@echo ""
	@echo "SQL Server  localhost:1433  (sa / see deploy/.env)"
	@echo "Kafka       localhost:19092 (external listener)"
	@echo "Redis       localhost:$${REDIS_HOST_PORT:-6380}"

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

# --- wired by plan A -------------------------------------------------------

test-unit: ## run unit + architecture + contract tests (plan A)
	dotnet test Crm.sln --filter "Category!=Integration"

test-integration: ## run Testcontainers-based tests against the local engine (plan A)
	DOCKER_HOST=$$(docker context inspect --format '{{.Endpoints.docker.Host}}') \
	TESTCONTAINERS_RYUK_DISABLED=true \
	dotnet test Crm.sln --filter "Category=Integration"

test: test-unit test-integration ## run everything

openapi: ## export docs/api/openapi.json from the API project (plan B)
	dotnet run --project src/Hosts/Crm.Api -- --export-openapi docs/api/openapi.json

migrate: ## apply EF Core migrations for every module (plan B)
	@echo "wired by plan B" && exit 1

seed: ## bootstrap the demo tenant, users and API key (plan B)
	@echo "wired by plan B" && exit 1

web-dev: ## run the React dev server (plan A)
	cd src/Hosts/Crm.Web && npm run dev
