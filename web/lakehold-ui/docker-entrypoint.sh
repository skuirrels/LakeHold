#!/bin/sh
set -eu

case "${LAKEHOLD_UI_MODE:-workbench}" in
  workbench)
    server_config="/etc/nginx/lakehold/workbench.conf"
    ;;
  website)
    server_config="/etc/nginx/lakehold/website.conf"

    measurement_id="${LAKEHOLD_GOOGLE_ANALYTICS_ID:-}"
    if [ -n "$measurement_id" ] && ! printf '%s' "$measurement_id" | grep -Eq '^G-[A-Z0-9]+$'; then
      echo "error: LAKEHOLD_GOOGLE_ANALYTICS_ID must be a GA4 measurement ID beginning with G-" >&2
      exit 1
    fi

    # This endpoint exists only in website mode. The browser does not request it until it reaches a
    # public route, keeping both the ID and Google's script out of private Workbench deployments.
    umask 077
    printf '{"measurementId":"%s"}\n' "$measurement_id" > /tmp/lakehold-analytics.json
    ;;
  *)
    echo "error: LAKEHOLD_UI_MODE must be 'workbench' or 'website'" >&2
    exit 1
    ;;
esac

# The container runs as nginx, so select the immutable image-owned configuration through a
# disposable symlink rather than rewriting /etc at startup.
ln -sf "$server_config" /tmp/lakehold-server.conf

exec nginx -g "daemon off;"
