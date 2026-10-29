import http from 'node:http';

export async function serverFor(t, handler) {
  const sockets = new Set();
  const server = http.createServer(handler);
  server.on('connection', socket => {
    sockets.add(socket);
    socket.on('close', () => sockets.delete(socket));
  });
  await new Promise((resolve, reject) => {
    server.once('error', reject);
    server.listen(0, '127.0.0.1', resolve);
  });
  t.after(async () => {
    const closed = new Promise(resolve => server.close(resolve));
    for (const socket of sockets) socket.destroy();
    await closed;
  });
  const port = server.address().port;
  return {
    server,
    port,
    origin: `http://fixture.invalid:${port}`,
    socketOrigin: `http://127.0.0.1:${port}`,
    sockets,
  };
}

export function get(url) {
  return new Promise((resolve, reject) => {
    const request = http.get(url, { agent: false }, response => {
      const chunks = [];
      response.on('data', chunk => chunks.push(chunk));
      response.once('error', reject);
      response.once('end', () => resolve({ status: response.statusCode, body: Buffer.concat(chunks).toString() }));
    });
    request.once('error', reject);
  });
}
