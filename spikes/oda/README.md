# Oda MCP feasibility spike (T-001)

Throwaway console app. Not part of `Foodplanner.slnx`, not built by `scripts/verify.sh`. It is a minimal MCP client (Streamable HTTP, hand-rolled JSON-RPC) for `https://oda.com/mcp` with OAuth authorization code + PKCE and dynamic client registration. It never checks out, pays, or touches addresses or delivery slots.

## Run
From the repo root (all commands: `dotnet run --project spikes/oda -- <command>`):

| Command | What it does |
|---|---|
| `login` | Registers a client (DCR), opens your browser to sign in to Oda, catches the redirect on `127.0.0.1`, stores tokens. Prints only non-secret metadata (expiry, scope). |
| `tools` | Lists the server's tools and saves them with input schemas to `findings/tools.json`. |
| `search <text>` | Product search: name, price, unit price, brand, id, image (if any), URL. |
| `search-raw <text>` | Read-only. Like `search` (size 3) but prints each product's full raw JSON, then lists offer-like field names (offer, campaign, discount, promo, sale, deal, original, before, compare, badge, label) or "no offer-like fields". For the T-107 check. |
| `recs <likely_to_buy\|unique_for_you>` | Read-only. Calls one of those two tools (anything else is rejected) with only `{"intent": ...}`, prints the raw result and the offer-like field summary per product. For the T-107 check. |
| `search-catalog` | Searches 20 Foodplanner catalog names (nb) and writes `findings/search-quality.md`. |
| `cart` | Shows the cart (lines, count, total). |
| `add <productId> <qty>` | Adds the product to the cart (qty is a delta). Records what it added. |
| `clear-test-items` | Removes only what `add` recorded (exact quantity), never `clear_cart`. |

## Where tokens go
In .NET user secrets for this project (`UserSecretsId` in `OdaSpike.csproj`), the same `secrets.json` that `dotnet user-secrets` manages (Windows: `%APPDATA%\Microsoft\UserSecrets\foodplanner-oda-spike-t001\secrets.json`). Nothing is written in the repo and values are never printed. Tokens are refreshed automatically when expired.

Remove everything (also revoke the connection in Oda under Privacy & security):

```
dotnet user-secrets clear --project spikes/oda
```

## Findings
`findings/` holds raw public responses (headers/bodies, no tokens) and the generated tools/search results. The summary is in `docs/adr/0008-oda.md`.
