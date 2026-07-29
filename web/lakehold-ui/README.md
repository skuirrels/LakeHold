# LakeHold UI

Angular 22 Workbench and demo-only public site for LakeHold. The root
[`README.md`](../../README.md) contains the complete development and deployment guide; this file
covers the frontend-only loop.

## Requirements

- Node.js 20 or newer
- npm 11 (the repository pins the package-manager release in `package.json`)
- The LakeHold API on `http://localhost:5200` for workbench requests

Install the locked dependency graph:

```bash
npm ci
```

## Development server

```bash
npm start
```

Open <http://localhost:5399>. The Angular dev server hot-reloads source changes and proxies `/api`,
`/mcp`, and MCP authorization metadata to `NG_API_URL`, falling back to
`http://localhost:5200`.

## Tests

```bash
npm test -- --watch=false
```

The unit suite uses Vitest with jsdom. There is currently no configured end-to-end target; do not
use `ng e2e` until an e2e harness is added to `angular.json`.

## Production build

```bash
npm run build
```

The static output is written under `dist/lakehold-ui/`. Public routes are prerendered; the
authenticated Workbench remains client-rendered. The container defaults to `LAKEHOLD_UI_MODE=workbench`,
which serves only `/workbench`, `/api`, and static UI assets. `compose.demo.yaml` is the sole
deployment overlay that selects `LAKEHOLD_UI_MODE=website` and exposes the prerendered routes.

To enable Google Analytics on that public website deployment, give the demo stack its GA4
measurement ID:

```bash
LAKEHOLD_GOOGLE_ANALYTICS_ID=G-XXXXXXXXXX \
  docker compose -f compose.production.yaml -f compose.demo.yaml up -d
```

The ID is read at container startup, not compiled into the shared Angular artifact. The private
Workbench mode does not expose the analytics configuration endpoint, and the browser integration
disables collection on `/workbench` even when a visitor reaches it from the public site. Keep
**Enhanced measurement → Page views → Page changes based on browser history events** enabled in
the GA4 web data stream so Angular route changes are counted once.

## Scaffolding

Run the project-local CLI through npm rather than depending on a global Angular installation:

```bash
npm exec ng generate component component-name
```
