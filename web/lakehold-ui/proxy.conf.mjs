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
  '/.well-known/oauth-protected-resource': {
    target,
    secure: false,
    changeOrigin: false,
  },
};
