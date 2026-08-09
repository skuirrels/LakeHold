// Dev-server proxy for the API and MCP command surface.
//
// This is JavaScript rather than JSON so the target can come from the environment. The API is not
// always at localhost: inside a container "localhost" is the UI container itself, so a hard-coded
// target silently proxies to nothing and every request 500s. compose sets NG_API_URL to the API
// service; on the host nothing sets it and the fallback applies, so `npm start` is unchanged.
const target = process.env.NG_API_URL ?? 'http://localhost:5200';

export default {
  '/api': {
    target,
    secure: false,
    changeOrigin: true,
  },
  '/auth': {
    target,
    secure: false,
    changeOrigin: false,
  },
  '/mcp': {
    target,
    secure: false,
    changeOrigin: false,
    timeout: 0,
    proxyTimeout: 0,
  },
  // The whole `/.well-known` prefix, not just the one document LakeHold publishes.
  //
  // An MCP client discovering this endpoint probes several paths in turn: the RFC 9728
  // protected-resource document, then RFC 8414 `oauth-authorization-server`, then the OIDC
  // `openid-configuration` fallbacks — each with and without the resource path suffix. LakeHold
  // answers the first and deliberately does not serve the rest: RFC 9728 has the resource advertise
  // its issuer, and the client reads authorization-server metadata from *there*. A 404 is the
  // correct answer to the others, and it is the answer the client knows how to act on.
  //
  // Proxying only the published document left the rest falling through to the Angular router, which
  // answers every unrecognised route with `index.html`. The client got HTML where it expected JSON,
  // failed with `Unrecognized token '<'`, and never reached the authorization server it had already
  // been told about. Nothing under `/.well-known` is ever an application route, so the prefix goes
  // to the API whole rather than being enumerated one discovery path at a time.
  '/.well-known': {
    target,
    secure: false,
    changeOrigin: false,
  },
  // The three endpoints an authorization server would publish, forwarded so the API can 404 them.
  //
  // LakeHold is a resource server. When a client cannot discover authorization-server metadata it
  // falls back to the older MCP assumption that the MCP server is also its own authorization server,
  // and probes exactly these paths on this origin. Unproxied they reach the Angular router, which
  // answers anything it does not recognise by sending the client to the app — and a response that
  // is not a 404 reads as "this endpoint exists". The client then commits to the fallback and opens
  // `http://localhost:5399/authorize`, a page that has never existed, instead of the Keycloak URL
  // named in `authorization_servers`.
  //
  // Enumerated rather than pattern-matched because these three names are a fixed part of the OAuth
  // vocabulary, and because the app must never own a route called `/authorize`, `/token`, or
  // `/register` — spelling them out here is also the record of that.
  '/authorize': { target, secure: false, changeOrigin: false },
  '/token': { target, secure: false, changeOrigin: false },
  '/register': { target, secure: false, changeOrigin: false },
};
