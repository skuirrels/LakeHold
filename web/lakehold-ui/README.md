# Lakehold UI

Angular 22 workbench and public site for Lakehold. The root
[`README.md`](../../README.md) contains the complete development and deployment guide; this file
covers the frontend-only loop.

## Requirements

- Node.js 20 or newer
- npm 11 (the repository pins the package-manager release in `package.json`)
- The Lakehold API on `http://localhost:5200` for workbench requests

Install the locked dependency graph:

```bash
npm ci
```

## Development server

```bash
npm start
```

Open <http://localhost:5399>. The Angular dev server hot-reloads source changes and proxies `/api`
to `NG_API_URL`, falling back to `http://localhost:5200`.

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
authenticated workbench remains client-rendered.

## Scaffolding

Run the project-local CLI through npm rather than depending on a global Angular installation:

```bash
npm exec ng generate component component-name
```
