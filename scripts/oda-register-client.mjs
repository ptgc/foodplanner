#!/usr/bin/env node
// Registers ONE app-wide Oda OAuth client for ONE environment (T-053, ADR 0008): dynamic client registration at Oda's
// /o/register/ as a public client (token_endpoint_auth_method "none", so there is no client secret anywhere).
//
//   node scripts/oda-register-client.mjs <web-origin> [client-name]
//   ./scripts/oda-register-client.sh https://app.example.com
//   ./scripts/oda-register-client.sh http://localhost:3000        # local docker compose web origin
//
// The redirect URI registered is <web-origin>/oda/callback. It prints ONLY the client_id: put it in configuration as
// Oda__ClientId (and the same origin as Oda__WebOrigin). The app never registers a client during a user request, and
// nothing here is run by verify or CI. Run it once per environment (local, dev, test, prod) by hand.
//
// To remove a registration, remove the Foodplanner connection under Privacy & security in the Oda account; this
// script does not store or print anything else.

const origin = (process.argv[2] ?? "").replace(/\/+$/, "");
const name = process.argv[3] ?? "Foodplanner";
if (!/^https:\/\/[^/]+$/.test(origin) && !/^http:\/\/(localhost|127\.0\.0\.1)(:\d+)?$/.test(origin)) {
  console.error("Usage: oda-register-client <web-origin> [client-name]\nThe origin is https://host[:port], or http://localhost[:port] for local development (no path).");
  process.exit(2);
}

const mcp = new URL("https://oda.com/mcp");
const getJson = async (url) => {
  const response = await fetch(url, { headers: { accept: "application/json" } });
  if (!response.ok) throw new Error(`GET ${url} failed with HTTP ${response.status}`);
  return response.json();
};

const prm = await getJson(`${mcp.origin}/.well-known/oauth-protected-resource${mcp.pathname}`);
const issuer = new URL(String(prm.authorization_servers?.[0] ?? "").replace(/\/+$/, ""));
const meta = await getJson(`${issuer.origin}/.well-known/oauth-authorization-server${issuer.pathname}`);
const endpoint = new URL(meta.registration_endpoint);
if (endpoint.hostname !== "oda.com" && !endpoint.hostname.endsWith(".oda.com")) {
  throw new Error("The registration endpoint is not on oda.com; refusing.");
}

const response = await fetch(endpoint, {
  method: "POST",
  headers: { "content-type": "application/json", accept: "application/json" },
  body: JSON.stringify({
    client_name: name,
    redirect_uris: [`${origin}/oda/callback`],
    grant_types: ["authorization_code", "refresh_token"],
    response_types: ["code"],
    token_endpoint_auth_method: "none",
    scope: "mcp",
  }),
});
if (!response.ok) {
  // The status only: the body is not printed.
  console.error(`Registration was refused with HTTP ${response.status}. If this is about the redirect URI, raise a question (spec T-053, implementation check 1).`);
  process.exit(1);
}
const registered = await response.json();
if (registered.client_secret) {
  console.error("Oda issued a client secret, which this app does not use. Not printing it. Stop and raise a question.");
  process.exit(1);
}
console.log(registered.client_id);
