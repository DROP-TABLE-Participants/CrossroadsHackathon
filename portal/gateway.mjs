import http from 'node:http'
import net from 'node:net'

const listenPort = 8080
const portalTarget = parseHttpTarget(process.argv[2] || 'http://127.0.0.1:3000', 'Portal upstream')
const apiTarget = parseHttpTarget(process.env.ERP_GENERATOR_API_INTERNAL_URL, 'ERP_GENERATOR_API_INTERNAL_URL')

const server = http.createServer((request, response) => {
  const target = request.url?.startsWith('/realtime/') ? apiTarget : portalTarget
  const upstream = http.request({
    hostname: target.hostname,
    port: target.port,
    method: request.method,
    path: request.url,
    headers: forwardedHeaders(request),
  }, (upstreamResponse) => {
    response.writeHead(upstreamResponse.statusCode || 502, upstreamResponse.headers)
    upstreamResponse.pipe(response)
  })

  upstream.on('error', () => {
    if (!response.headersSent) response.writeHead(502, { 'content-type': 'text/plain' })
    response.end('Portal upstream is unavailable.')
  })
  request.pipe(upstream)
})

server.on('upgrade', (request, socket, head) => {
  if (!request.url?.startsWith('/realtime/')) {
    socket.destroy()
    return
  }

  const upstream = net.connect(Number(apiTarget.port), apiTarget.hostname)
  upstream.once('connect', () => {
    upstream.write(`${request.method} ${request.url} HTTP/${request.httpVersion}\r\n`)
    for (const [name, value] of Object.entries(request.headers)) {
      if (Array.isArray(value)) {
        for (const item of value) upstream.write(`${name}: ${item}\r\n`)
      } else if (value !== undefined) {
        upstream.write(`${name}: ${value}\r\n`)
      }
    }
    upstream.write('\r\n')
    if (head.length > 0) upstream.write(head)
    socket.pipe(upstream).pipe(socket)
  })
  upstream.on('error', () => socket.destroy())
  socket.on('error', () => upstream.destroy())
})

server.listen(listenPort, '0.0.0.0')

function parseHttpTarget(value, label) {
  if (!value) throw new Error(`${label} is required.`)
  const target = new URL(value)
  if (target.protocol !== 'http:' || target.pathname !== '/' || target.search || target.hash) {
    throw new Error(`${label} must be an HTTP origin without a path.`)
  }
  return {
    hostname: target.hostname,
    port: target.port || '80',
  }
}

function forwardedHeaders(request) {
  const headers = { ...request.headers }
  const remoteAddress = request.socket.remoteAddress
  headers['x-forwarded-for'] = headers['x-forwarded-for']
    ? `${headers['x-forwarded-for']}, ${remoteAddress}`
    : remoteAddress
  headers['x-forwarded-proto'] = headers['x-forwarded-proto'] || 'http'
  return headers
}
