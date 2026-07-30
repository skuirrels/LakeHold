import { createServer } from 'node:http';

let failuresRemaining = 0;
let deliveries = [];

function json(response, status, value) {
  const body = JSON.stringify(value);
  response.writeHead(status, {
    'content-type': 'application/json',
    'content-length': Buffer.byteLength(body),
  });
  response.end(body);
}

async function bodyOf(request) {
  const chunks = [];
  for await (const chunk of request) {
    chunks.push(chunk);
  }
  return Buffer.concat(chunks);
}

createServer(async (request, response) => {
  const url = new URL(request.url ?? '/', 'http://receiver');

  if (request.method === 'GET' && url.pathname === '/health') {
    response.writeHead(204);
    response.end();
    return;
  }

  if (request.method === 'GET' && url.pathname === '/state') {
    json(response, 200, { failuresRemaining, deliveries });
    return;
  }

  if (request.method === 'POST' && url.pathname === '/reset') {
    failuresRemaining = 0;
    deliveries = [];
    json(response, 200, { reset: true });
    return;
  }

  if (request.method === 'POST' && url.pathname === '/fail-next') {
    const body = await bodyOf(request);
    const requested = Number(JSON.parse(body.toString('utf8')).count);
    failuresRemaining = Number.isSafeInteger(requested) && requested > 0 ? requested : 0;
    json(response, 200, { failuresRemaining });
    return;
  }

  if (request.method === 'POST' && url.pathname === '/hook') {
    const body = await bodyOf(request);
    const status = failuresRemaining > 0 ? 503 : 204;
    failuresRemaining = Math.max(0, failuresRemaining - 1);
    deliveries.push({
      body: body.toString('utf8'),
      delivery: request.headers['x-lakehold-delivery'] ?? null,
      signature: request.headers['x-lakehold-signature'] ?? null,
      timestamp: request.headers['x-lakehold-timestamp'] ?? null,
      signatureVersion: request.headers['x-lakehold-signature-version'] ?? null,
      status,
    });
    response.writeHead(status);
    response.end();
    return;
  }

  json(response, 404, { error: 'not found' });
}).listen(9080, '0.0.0.0');
