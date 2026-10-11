// Terminates TLS for an http sim stack, so OpenIddict (which refuses plain http outside
// Development) runs the connect flow. Keeps the Host header, which must match OAuth:Issuer
// and AllowedHosts, and sets X-Forwarded-Proto, which the app trusts only from
// RateLimiting:TrustedProxies (the docker bridge here, not loopback).
// Usage: node tls-proxy.mjs <listen-port> <upstream-port> <cert.pem> <key.pem>
import https from "node:https";
import http from "node:http";
import fs from "node:fs";

const [listen, upstream, cert, key] = process.argv.slice(2);
https
  .createServer({ cert: fs.readFileSync(cert), key: fs.readFileSync(key) }, (req, res) => {
    const headers = { ...req.headers, "x-forwarded-proto": "https", "x-forwarded-for": req.socket.remoteAddress };
    const out = http.request({ host: "127.0.0.1", port: upstream, method: req.method, path: req.url, headers }, (up) => {
      res.writeHead(up.statusCode, up.rawHeaders);
      up.pipe(res);
    });
    out.on("error", (e) => { res.writeHead(502); res.end(String(e)); });
    req.pipe(out);
  })
  .listen(Number(listen), "127.0.0.1", () => console.log(`tls-proxy https://localhost:${listen} -> http://127.0.0.1:${upstream}`));
