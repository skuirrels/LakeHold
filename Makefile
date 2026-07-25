# Lakehold operations.
#
#   make production   Update this deployment in place: pull, rebuild, restart.
#   make status       What is running, and whether it is healthy.
#   make logs         Follow the running stack's logs.
#   make stop         Stop the stack. The state volume survives.
#
# This drives compose.production.yaml and never compose.yaml. The development stack bind-mounts
# source and runs a file watcher, so it has no build step to redo — "rebuild and restart" is not a
# thing you do to it, you just save a file.
#
# `make production` is for a host that already runs the stack, and is safe to re-run. The step order
# is deliberate: everything that can fail does so before anything is taken down, so a broken build
# or a diverged checkout leaves the current containers serving traffic untouched.

COMPOSE := docker compose -f compose.production.yaml
BRANCH  := $(shell git rev-parse --abbrev-ref HEAD 2>/dev/null)

# How long `up` waits for both healthchecks before it calls the deployment failed. The API's own
# start-period is 45s and a cold DuckDB open lands on top of that, so this leaves real headroom.
WAIT_TIMEOUT ?= 180

.DEFAULT_GOAL := help

.PHONY: help production check-tree pull build up status logs stop

help:
	@echo "Lakehold — make targets"
	@echo ""
	@echo "  production   Update this deployment: git pull, rebuild images, restart containers"
	@echo "  status       Show the running containers and their health"
	@echo "  logs         Follow the stack's logs"
	@echo "  stop         Stop the stack, keeping the state volume"
	@echo ""
	@echo "  Overrides:   WAIT_TIMEOUT=$(WAIT_TIMEOUT) (seconds to wait for healthy containers)"

production: check-tree pull build up status
	@echo ""
	@echo "==> deployed $(BRANCH) at $$(git rev-parse --short HEAD)"

# A deploy host should be a clean checkout. Local edits to tracked files mean either someone
# debugged in production and forgot, or the pull is about to fail halfway; both are worth stopping
# for while the old containers are still up. Untracked files are fine — .env lives here.
check-tree:
	@git diff --quiet && git diff --cached --quiet || { \
		echo "error: tracked files are modified — commit, stash, or discard before deploying:"; \
		git status --short; \
		exit 1; \
	}

# --ff-only so a deploy can never invent a merge commit. If this host has diverged from the remote,
# that is a person's decision to resolve, not something to paper over mid-deployment.
pull:
	@echo "==> git pull --ff-only ($(BRANCH))"
	git pull --ff-only

# --pull refreshes the base images too. Both Dockerfiles track floating tags (aspnet:10.0,
# nginx:alpine), so without this a long-lived host keeps rebuilding onto whatever base it cached
# months ago and never picks up their security updates.
build:
	@echo "==> building images"
	$(COMPOSE) build --pull

# `up -d` is the stop-and-start: compose recreates exactly the containers whose image changed and
# leaves the rest alone, so downtime is a few seconds rather than the length of a full down/up. A
# `down` first would also drop the network and leave the site hard-down for the whole build.
#
# --wait makes this honest about failure — it exits non-zero unless both healthchecks pass, so a
# container that starts and immediately crashes fails the deploy instead of reporting success.
up:
	@echo "==> restarting changed containers"
	$(COMPOSE) up -d --remove-orphans --wait --wait-timeout $(WAIT_TIMEOUT)

status:
	@$(COMPOSE) ps

logs:
	@$(COMPOSE) logs -f --tail 100

# Never `down -v` here. The lakehold-state volume is the catalog, the Parquet, the backups, and the
# eject bundles — everything in this stack that cannot be rebuilt from the repository.
stop:
	@$(COMPOSE) down
