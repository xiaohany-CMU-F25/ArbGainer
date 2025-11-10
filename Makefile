SHELL := /usr/bin/env bash
COMPOSE := docker compose
PROJECT := arbitragegainer-starter
APP_PORT ?= 8082

.PHONY: up down restart ps logs logs-app logs-mongo sh sh-mongo build rebuild fresh curl health rm-orphans who-owns-port

# Bring the stack up.
up:
	$(COMPOSE) up -d --build --remove-orphans
	@echo "App health:  http://localhost:$(APP_PORT)/health"

# Bring the stack down + remove volumes + kill orphans.
down:
	$(COMPOSE) down -v --remove-orphans

restart: down up

ps:
	$(COMPOSE) ps

logs:
	$(COMPOSE) logs -f --tail=200

logs-app:
	$(COMPOSE) logs -f --tail=200 app

logs-mongo:
	$(COMPOSE) logs -f --tail=200 mongo

# Rebuild app cleanly (no cache) and bring up.
rebuild:
	$(COMPOSE) build --no-cache app
	$(COMPOSE) up -d --remove-orphans

# Nuke everything for this project: containers, image, volume.
fresh:
	$(COMPOSE) down -v --remove-orphans || true
	docker rmi -f arbitragegainer:starter 2>/dev/null || true
	docker volume rm $(PROJECT)_mongo-data 2>/dev/null || true

# shell helpers
sh:
	$(COMPOSE) exec app sh

sh-mongo:
	$(COMPOSE) exec mongo bash -lc 'mongosh mongodb://mongo:27017'

# Health check (host -> container via published port)
health:
	@curl -fsS http://localhost:$(APP_PORT)/health || (echo "health check failed" && exit 1)

# Alias so `make curl` works as expected
curl: health

# Clean up stray containers/networks not tracked by compose (safe).
rm-orphans:
	docker container prune -f >/dev/null || true
	docker network prune -f >/dev/null || true

# Check who is using APP_PORT
who-owns-port:
	@echo "Docker using $(APP_PORT):"; \
	docker ps --format 'table {{.ID}}\t{{.Names}}\t{{.Ports}}' | grep $(APP_PORT) || true; \
	echo "Processes using $(APP_PORT):"; \
	lsof -nP -iTCP:$(APP_PORT) | grep LISTEN || true
