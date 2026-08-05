# Lakehold operations.
#
#   make deploy       Update this deployment to the current published images.
#   make production   Update the private workbench and services from source.
#   make status       What is running, in either stack, and whether it is healthy.
#   make logs         Follow the running stack's logs.
#   make stop         Stop whichever stack is running. State volumes survive.
#   make backup-state Archive the state volume to a tarball in the working directory.
#   make test         Run the complete backend, frontend, integration, and browser test suite.
#   make dev          Start the local development stack with hot reload.
#   make demo         Pull, build, and start the public demo with C# LINQ enabled.
#   make prune-worktrees  List finished agent worktrees; APPLY=1 removes them.
#
# Deployment targets drive compose.production.yaml and `make dev` drives compose.yaml, but the three
# targets you reach for when something is already running — `status`, `logs`, and `stop` — act on
# whichever project is actually up rather than on a file chosen in advance. The development stack
# bind-mounts source and runs a file watcher, so it has no build step to redo — "rebuild and
# restart" is not a thing you do to it, you just save a file.
#
# `make deploy` is the ordinary path and needs nothing but Docker and the compose file: images come
# from the registry, so a deployment host does not need a checkout, a compiler, or this Makefile.
# `make production` is the from-source variant, for a host that deploys a commit rather than a
# release. Both are safe to re-run, and both order their steps so that everything which can fail
# does so before anything is taken down — a broken build or a diverged checkout leaves the current
# containers serving traffic untouched.

COMPOSE        := docker compose -f compose.production.yaml
COMPOSE_SOURCE := docker compose -f compose.production.yaml -f compose.build.yaml
COMPOSE_DEMO   := $(COMPOSE_SOURCE) -f compose.demo.yaml --profile linq
COMPOSE_DEV    := docker compose
BRANCH         := $(shell git rev-parse --abbrev-ref HEAD 2>/dev/null)

# The demo compiler stays authenticated without making an operator provision a feature key. One
# high-entropy value is generated in memory for the whole `make demo` invocation, exported to both
# API and compiler through Compose, and rotated together on the next demo deployment. It is neither
# printed nor persisted. Other production paths still require an operator-managed key when LINQ is
# enabled because their compiler may be deployed outside this single-host topology.
ifneq ($(filter demo build-demo,$(MAKECMDGOALS)),)
ifeq ($(strip $(LAKEHOLD_LINQ_PLANNER_KEY)),)
override LAKEHOLD_LINQ_PLANNER_KEY := $(shell od -An -N32 -tx1 /dev/urandom | tr -d ' \n')
endif
export LAKEHOLD_LINQ_PLANNER_KEY
endif

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

# Published port for the bundled development identity provider.
KEYCLOAK_PORT ?= 5401

.DEFAULT_GOAL := help

.PHONY: help test dev demo deploy production check-tree pull build build-demo up status logs stop backup-state prune-worktrees

help:
	@echo "Lakehold — make targets"
	@echo ""
	@echo "  test          Run every test, including live integrations and both E2E suites"
	@echo "  dev           Start the local development stack with hot reload"
	@echo "  demo          Pull, build, and start the public website and demo workbench"
	@echo "  deploy        Update this deployment to the current published images"
	@echo "  production    Update the private workbench and services from source"
	@echo "  status        Show whichever stack is running, and its health"
	@echo "  logs          Follow the running stack's logs"
	@echo "  stop          Stop whichever stack is running, keeping state volumes"
	@echo "  backup-state  Archive the state volume to a tarball here"
	@echo ""
	@echo "  prune-worktrees  List finished agent worktrees (APPLY=1 to remove them)"
	@echo ""
	@echo "  Overrides:    WAIT_TIMEOUT=$(WAIT_TIMEOUT) (seconds to wait for healthy containers)"
	@echo "                LAKEHOLD_PORT=<port> (host port; defaults to 8080)"
	@echo "                LAKEHOLD_TAG=2.0.1 or v2.0.1 (which published images deploy pulls)"
	@echo "                ARCHIVE=<file> (what backup-state writes, in this directory)"

# The test script owns a uniquely named Compose project and removes only that project's volumes.
# It never reuses or stops the development or production stack, so running the full destructive
# browser simulation cannot touch a developer's catalog.
test:
	@./scripts/test-all.sh

# Keep the development stack attached so its logs are visible and Ctrl+C stops it. Compose uses the
# default compose.yaml here, which bind-mounts the source and runs the API and UI file watchers.
#
# The C# LINQ planner is included. It is profile-gated so that production deployments opt in with a
# managed secret, but leaving it out here meant the language picker offered SQL only and the browser
# suite could not pass against a stack started this way — the LINQ journey failed for a missing
# service rather than a real defect. Development has a default planner key in compose.yaml, so this
# costs an operator nothing.
dev:
	@echo "==> starting local development stack"
	@echo "==> website:  http://localhost:5399"
	@echo "==> API:      http://localhost:5200"
	@echo "==> MCP:      http://localhost:5200/mcp"
	@echo "==> identity: http://localhost:$(KEYCLOAK_PORT) (Keycloak console: admin / admin)"
	@echo "==> languages: SQL and C# LINQ"
	@echo ""
	@echo "    Sign in at the website with either seeded user, password 'lakehold':"
	@echo "      analyst  owns the demo workspace  (queries, writes, maintenance)"
	@echo "      admin    administers the instance (provisions tenants and credentials)"
	@echo "    Machines and agents use API tokens instead; see docs/IDENTITY-PROVIDER-SETUP.md."
	$(COMPOSE_DEV) --profile linq up

# Demo is deliberately a separate opt-in overlay; it is the only target that enables the public
# website and starts the isolated C# LINQ compiler. The standard production configuration serves
# the authentication-protected workbench and contains no demo settings. The build overlay is
# required here so this target runs the current checkout rather than whichever published images
# happen to be cached.
# As with `production`, local tracked changes fail before pulling so an update can never overwrite
# an uncommitted deployment-host edit.
demo: check-tree pull build-demo
	@echo "==> restarting changed containers with the public website, demo access, and C# LINQ"
	$(COMPOSE_DEMO) up -d --remove-orphans --wait --wait-timeout $(WAIT_TIMEOUT)
	@$(COMPOSE_DEMO) ps
	@echo ""
	@echo "==> deployed demo from $(BRANCH) at $$(git rev-parse --short HEAD)"

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
	@echo "==> deployed private workbench from $(BRANCH) at $$(git rev-parse --short HEAD)"

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

# Profiled services are excluded from a plain Compose build. Build through the fully activated demo
# topology so `up` never has to pull an old compiler image or fail because no compiler image exists.
build-demo:
	@echo "==> building demo images, including the C# LINQ compiler"
	$(COMPOSE_DEMO) build --pull

# `up -d` is the stop-and-start: compose recreates exactly the containers whose image changed and
# leaves the rest alone, so downtime is a few seconds rather than the length of a full down/up. A
# `down` first would also drop the network and leave the site hard-down for the whole build.
#
# --wait makes this honest about failure — it exits non-zero unless both healthchecks pass, so a
# container that starts and immediately crashes fails the deploy instead of reporting success.
up:
	@echo "==> restarting changed containers"
	$(COMPOSE) up -d --remove-orphans --wait --wait-timeout $(WAIT_TIMEOUT)

# Reports on whichever stacks are up, for the same reason `stop` does: naming only the deployment
# project meant `make status` answered "nothing here" while a development stack was serving. Both
# are shown when both run, because on a demo host that is a real state and the answer "which one is
# this?" is the whole question being asked.
#
# Detection is `docker compose -p <project>`, which needs no compose file — that is what lets one
# Makefile ask about a stack whose file it is not currently pointed at.
status:
	@shown=0; \
	for spec in "lakehold-dev:development" "lakehold:deployment"; do \
	  project=$${spec%%:*}; label=$${spec##*:}; \
	  if [ -n "$$(docker compose --profile "*" -p $$project ps --quiet 2>/dev/null)" ]; then \
	    shown=1; \
	    echo "==> $$label stack ($$project)"; \
	    docker compose --profile "*" -p $$project ps; \
	    echo ""; \
	  fi; \
	done; \
	[ $$shown -eq 1 ] || echo "==> nothing running"

# One stream can only follow one project, so this picks rather than merges. With both up it keeps
# following the deployment stack — the historical behaviour, and the one a deployment host wants —
# and says how to reach the other instead of choosing silently.
logs:
	@dev="$$(docker compose --profile "*" -p lakehold-dev ps --quiet 2>/dev/null)"; \
	deployment="$$(docker compose --profile "*" -p lakehold ps --quiet 2>/dev/null)"; \
	if [ -n "$$deployment" ] && [ -n "$$dev" ]; then \
	  echo "==> both stacks are running; following the deployment stack (lakehold)"; \
	  echo "    development stack: docker compose logs -f --tail 100"; \
	  $(COMPOSE) --profile "*" logs -f --tail 100; \
	elif [ -n "$$deployment" ]; then \
	  $(COMPOSE) --profile "*" logs -f --tail 100; \
	elif [ -n "$$dev" ]; then \
	  echo "==> following the development stack (lakehold-dev)"; \
	  $(COMPOSE_DEV) --profile "*" logs -f --tail 100; \
	else \
	  echo "==> nothing running"; \
	fi

# Stops whichever stack is actually up, because `make dev` and the deployment targets drive
# different Compose *projects* — `lakehold-dev` from compose.yaml, `lakehold` from
# compose.production.yaml. This used to name only the deployment project, so someone who started
# with `make dev`, closed the terminal, and reached for the documented way to stop it got a silent
# no-op and a stack still holding :5399, :5200, and :5401.
#
# Each project is checked before it is stopped rather than running `down` against both, so the
# output says what happened instead of printing a confusing teardown for something that was never
# running. Both are attempted, so a host running the demo alongside a dev stack is fully stopped.
#
# Never `down -v` here. The lakehold-state volume can hold local Parquet, backups, and eject bundles,
# while a demo deployment's PostgreSQL metadata lives in its own persistent volume. The standard
# production stack still keeps PostgreSQL outside Compose.
# `--profile "*"` matters: `down` without it leaves profile-gated services running, so stopping a
# stack that had the LINQ compiler up left one container and its network behind — and the next
# `make stop` reported it was stopping a stack it could not actually finish.
define stop_project
	if [ -n "$$(docker compose --profile "*" -p $(2) ps --quiet 2>/dev/null)" ]; then \
	  echo "==> stopping the $(3) stack ($(2))"; \
	  $(1) --profile "*" down --remove-orphans; \
	else \
	  echo "==> no $(3) stack running ($(2))"; \
	fi
endef

stop:
	@$(call stop_project,$(COMPOSE_DEV),lakehold-dev,development)
	@$(call stop_project,$(COMPOSE),lakehold,deployment)

# A disaster copy of node-local state: local Parquet, backup generations, and eject bundles. It does
# not include the PostgreSQL control plane or PostgreSQL DuckLake metadata.
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

# Agent sessions leave a worktree behind per task. Left alone they pin branches against deletion and
# eventually one of them holds main, at which point this checkout can never simply be on the default
# branch. Lists by default and needs APPLY=1 to remove, because "clean and merged" cannot distinguish
# a finished session from one that has only just started.
prune-worktrees:
	@./scripts/prune-worktrees.sh $(if $(APPLY),--apply,)
