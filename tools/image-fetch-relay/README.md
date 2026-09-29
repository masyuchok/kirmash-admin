# Image fetch relay (residential / alternate egress)

Small ASP.NET service that downloads allowlisted supplier image URLs and returns raw bytes.
Production backend calls this when Direct download gets 403/429/HTML bot-wall.

## Why admin.kirma.sh showed Direct 403 without Relay

Cascade **is** registered (`IRemoteImageFetcher` → `CascadingRemoteImageFetcher`).
HTTP 403 **does** trigger fallback. If `fetchSource=direct` after 403, Relay was **not configured**
(`IMAGE_FETCH_RELAY_URL` empty) — cascade returns the Direct failure and never calls Relay.

`admin.kirma.sh` is the SSH-deployed VPS (GitHub Actions), **not** the local Docker stack.
Local Docker can have relay configured while Cloudflare origin still has empty `IMAGE_FETCH_RELAY_URL`.

## Run locally (host; backend in Docker)

```bash
cd tools/image-fetch-relay
set IMAGE_FETCH_RELAY_TOKEN=<same as backend>
dotnet run --urls http://0.0.0.0:5099
```

Backend `.env`:

```
IMAGE_FETCH_RELAY_URL=http://host.docker.internal:5099
IMAGE_FETCH_RELAY_TOKEN=<same token>
```

## Production (VPS behind admin.kirma.sh)

1. Deploy backend that includes cascade + diagnostics.
2. On the **VPS** set `IMAGE_FETCH_RELAY_URL` to a **publicly reachable** relay that can download kamunikat
   (home machine via Cloudflare Tunnel / residential VPS). `host.docker.internal` only works for local Docker.
3. Set matching `IMAGE_FETCH_RELAY_TOKEN` (never commit it).
4. Restart backend; startup log must show `RelayConfigured=True RelayUrlHost=...`.
5. Authenticated `POST /books/fetch-cover` should return `directStatus=403`, `relayAttempted=true`,
   `fetchSource=relay`, `tempMediaId`, then `GET /books/temp-media/{id}` → `image/jpeg`.

## Contract

`POST /fetch`  
`Authorization: Bearer <token>`

```json
{ "url": "https://kamunikat.shop/…jpg", "referer": "https://kamunikat.shop/…" }
```

Success: raw image bytes + `Content-Type: image/*`  
Error: JSON `{ "error": "…" }`

Do not put Shopify credentials on the relay.
