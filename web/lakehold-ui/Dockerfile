# Production image for the Lakehold website: the Angular bundle, built once and served by nginx.
#
# Build context is the repository root, matching the API image:
#
#   docker build -f web/lakehold-ui/Dockerfile -t lakehold-ui .

FROM node:24-alpine AS build
WORKDIR /app

# Lockfile first so a source change does not re-install dependencies.
COPY web/lakehold-ui/package.json web/lakehold-ui/package-lock.json ./
RUN npm ci --no-audit --no-fund

COPY web/lakehold-ui/ ./
RUN npm run build

FROM nginx:alpine AS runtime

COPY web/lakehold-ui/nginx.conf /etc/nginx/conf.d/default.conf

# Angular's application builder emits the browser bundle under dist/<project>/browser; the parent
# directory also holds prerender metadata that must not be served.
COPY --from=build /app/dist/lakehold-ui/browser /usr/share/nginx/html

# The stock image's main config writes the pid and temp files under paths only root may use, so the
# unprivileged variant's directories are prepared here instead.
RUN touch /var/run/nginx.pid \
    && chown -R nginx:nginx /var/run/nginx.pid /var/cache/nginx /usr/share/nginx/html
USER nginx

EXPOSE 8080

# 127.0.0.1 rather than localhost: busybox wget resolves localhost to ::1 first, nginx listens on
# IPv4 only, and the check reports the container unhealthy while it is serving traffic perfectly
# well — which then blocks anything that waits on it.
HEALTHCHECK --interval=15s --timeout=5s --start-period=10s --retries=5 \
    CMD wget -qO- http://127.0.0.1:8080/ >/dev/null || exit 1
