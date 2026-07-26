#!/bin/sh
set -eu

case "${LAKEHOLD_UI_MODE:-workbench}" in
  workbench)
    server_config="/etc/nginx/lakehold/workbench.conf"
    ;;
  website)
    server_config="/etc/nginx/lakehold/website.conf"
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
