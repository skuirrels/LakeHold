# Lakehold operations.
#
#   make deploy       Update this deployment to the current published images.
#   make production   Update it from source instead: pull the repository, rebuild, restart.
#   make status       What is running, and whether it is healthy.
#   make logs         Follow the running stack's logs.
#   make stop         Stop the stack. The state volume survives.
#   make backup-state Archive the state volume to a tarball in the working directory.
#   make test         Run the complete backend, frontend, integration, and browser test suite.
#   make demo         Build and start the opt-in demo deployment overlay.
#
# This drives compose.production.yaml and never compose.yaml. The development stack bind-mounts
# source and runs a file watcher, so it has no build step to redo — "rebuild and restart" is not a
# thing you do to it, you just save a file.
#
# `make deploy` is the ordinary path and needs nothing but Docker and the compose file: images come
# from the registry, so a deployment host does not need a checkout, a compiler, or this Makefile.
# `make production` is the from-source variant, for a host that deploys a commit rather than a
# release. Both are safe to re-run, and both order their steps so that everything which can fail
# does so before anything is taken down — a broken build or a diverged checkout leaves the current
# containers serving traffic untouched.

COMPOSE        := docker compose -f compose.production.yaml
COMPOSE_SOURCE := docker compose -f compose.production.yaml -f compose.build.yaml
COMPOSE_DEMO   := $(COMPOSE_SOURCE) -f compose.demo.yaml
BRANCH         := $(shell git rev-parse --abbrev-ref HEAD 2>/dev/null)

# Compose prefixes volumes with the project name, which compose.production.yaml pins to `lakehold`.
STATE_VOLUME := lakehold_lakehold-state

# Simply-expanded, so the timestamp is taken once. With `=` semantics the shell would run again at
# every reference, and backup-state names the archive three times — the message, the tar, and the
# listing could each land on a different second and disagree about what was written.
STAMP        := $(shell date -u +%Y%m%dT%H%M%SZ)
ARCHIVE      ?= lakehold-state-$(STAMP).tar.gz

# How long `up` waits for both healthchecks before it calls the deployment failed. The API's own
# start-period is 45s and a cold DuckDB open lands on top of that, so this leaves real headroom.
WAIT_TIMEOUT ?= 180

.DEFAULT_GOAL := help

.PHONY: help test demo deploy production check-tree pull build up status logs stop backup-state

help:
	@echo "Lakehold — make targets"
	@echo ""
	@echo "  test          Run every test, including live integrations and both E2E suites"
	@echo "  demo          Build local source and start the opt-in demo deployment overlay"
	@echo "  deploy        Update this deployment to the current published images"
	@echo "  production    Update it from source: git pull, rebuild images, restart containers"
	@echo "  status        Show the running containers and their health"
	@echo "  logs          Follow the stack's logs"
	@echo "  stop          Stop the stack, keeping the state volume"
	@echo "  backup-state  Archive the state volume to a tarball here"
	@echo ""
	@echo "  Overrides:    WAIT_TIMEOUT=$(WAIT_TIMEOUT) (seconds to wait for healthy containers)"
	@echo "                LAKEHOLD_PORT=<port> (host port; defaults to 8080)"
	@echo "                LAKEHOLD_TAG=<version> (which published images deploy pulls)"
	@echo "                ARCHIVE=<file> (what backup-state writes, in this directory)"

# The test script owns a uniquely named Compose project and removes only that project's volumes.
# It never reuses or stops the development or production stack, so running the full destructive
# browser simulation cannot touch a developer's catalog.
test:
	@./scripts/test-all.sh

# Demo is deliberately a separate opt-in overlay; the standard production configuration remains
# authentication-protected and contains no demo settings. The build overlay is required here so
# this target runs the current checkout rather than whichever published images happen to be cached.
demo:
	$(COMPOSE_DEMO) up -d --build

# The published-image path. No git, no build: whatever LAKEHOLD_TAG names is pulled and started.
# Pinning that to a released version rather than the default `latest` is what makes a redeploy
# reproducible — `latest` moves under you the next time someone tags a release.
deploy:
	@echo "==> pulling images"
	$(COMPOSE) pull
	@$(MAKE) --no-print-directory up status
	@echo ""
	@echo "==> deployed $${LAKEHOLD_TAG:-latest}"

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
	$(COMPOSE_SOURCE) build --pull

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

# A disaster copy of that volume: the catalog database, the Parquet, the backup generations, and the
# eject bundles, as one tarball. Read-only mount, so this cannot damage what it is copying.
#
# It is not a substitute for Lakehold's own catalog backup or an eject. Those run through the
# catalog and are consistent by construction; this is a file copy, so a container writing during it
# can land a torn page in the archive. Stop the stack first when the archive has to be restorable
# with certainty — `make stop backup-state deploy` is a few seconds of downtime for a copy nobody
# has to think about afterwards.
backup-state:
	@case "$(ARCHIVE)" in */*) \
		echo "error: ARCHIVE is a file name in this directory, not a path: $(ARCHIVE)"; \
		exit 1 ;; \
	esac
	@docker volume inspect $(STATE_VOLUME) >/dev/null 2>&1 || { \
		echo "error: volume $(STATE_VOLUME) does not exist — has the stack ever run here?"; \
		exit 1; \
	}
	@$(COMPOSE) ps --status running --quiet | grep -q . \
		&& echo "note: the stack is running; see the comment above this target" || true
	@echo "==> archiving $(STATE_VOLUME) to $(ARCHIVE)"
	@docker run --rm \
		-v $(STATE_VOLUME):/state:ro \
		-v "$(CURDIR)":/archive \
		alpine tar czf "/archive/$(ARCHIVE)" -C /state .
	@ls -lh "$(ARCHIVE)"
