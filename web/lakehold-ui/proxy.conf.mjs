// Dev-server proxy for /api.
//
// This is JavaScript rather than JSON so the target can come from the environment. The API is not
// always at localhost: inside a container "localhost" is the UI container itself, so a hard-coded
// target silently proxies to nothing and every request 500s. compose sets NG_API_URL to the API
// service; on the host nothing sets it and the fallback applies, so `npm start` is unchanged.
export default {
  '/api': {
    target: process.env.NG_API_URL ?? 'http://localhost:5200',
    secure: false,
    changeOrigin: true,
  },
};
