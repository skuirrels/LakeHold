# Production image for the Lakehold workbench and opt-in demo website: the Angular bundle, built
# once and served by nginx. Runtime mode decides which routes nginx exposes; the standard install
# defaults to the private workbench.
#
# Build context is the repository root, matching the API image:
#
#   docker build -f web/lakehold-ui/Dockerfile -t lakehold-ui .

# Pinned to the builder's own architecture. The output of this stage is JavaScript, CSS, and HTML,
# which have no architecture, so a multi-platform build would otherwise run npm under emulation once
# per platform for byte-identical results.
FROM --platform=$BUILDPLATFORM node:24-alpine AS build
WORKDIR /app

# Lockfile first so a source change does not re-install dependencies.
COPY web/lakehold-ui/package.json web/lakehold-ui/package-lock.json ./
RUN npm ci --no-audit --no-fund

COPY web/lakehold-ui/ ./
# The operational website pages import the repository runbooks directly. Keep them outside /app at
# the same absolute path used by the development Compose mount so the relative imports resolve in
# both build environments without maintaining a second copy.
COPY docs/ /docs/
RUN npm run build

FROM nginx:alpine AS runtime

COPY web/lakehold-ui/nginx.base.conf /etc/nginx/nginx.conf
COPY web/lakehold-ui/nginx.conf /etc/nginx/lakehold/website.conf
COPY web/lakehold-ui/nginx.workbench.conf /etc/nginx/lakehold/workbench.conf
COPY web/lakehold-ui/docker-entrypoint.sh /usr/local/bin/lakehold-nginx

# Angular's application builder emits the browser bundle under dist/<project>/browser; the parent
# directory also holds prerender metadata that must not be served.
COPY --from=build /app/dist/lakehold-ui/browser /usr/share/nginx/html

# The stock image's main config writes the pid and temp files under paths only root may use, so the
# unprivileged variant's directories are prepared here instead.
RUN touch /var/run/nginx.pid \
    && chmod +x /usr/local/bin/lakehold-nginx \
    && chown -R nginx:nginx /var/run/nginx.pid /var/cache/nginx /usr/share/nginx/html
USER nginx

EXPOSE 8080

ENTRYPOINT ["/usr/local/bin/lakehold-nginx"]

# 127.0.0.1 rather than localhost: busybox wget resolves localhost to ::1 first, nginx listens on
# IPv4 only, and the check reports the container unhealthy while it is serving traffic perfectly
# well — which then blocks anything that waits on it.
HEALTHCHECK --interval=15s --timeout=5s --start-period=10s --retries=5 \
    CMD wget -qO- http://127.0.0.1:8080/workbench >/dev/null || exit 1
