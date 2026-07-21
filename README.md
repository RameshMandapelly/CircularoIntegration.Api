# Circularo Integration Middleware — Run Book (Demo)

Two webhook APIs that automate the Qlub SME document-signing flow:

| API | URL | Called by | Does |
|-----|-----|-----------|------|
| #1 Initiate | `POST /api/v1/signing/initiate` | LeadSquared webhook (stage → "Phase 1 Doc Sign") | Fetches lead email → sends Circularo doc for signature |
| #2 Completed | `POST /api/v1/signing/completed` | Circularo webhook (document signed) | Attaches signed PDF → moves lead to "Phase 2 Doc Sign" |

Stack: .NET 8 · ASP.NET Core Web API · PostgreSQL (EF Core/Npgsql) · Serilog · shared-secret API key on inbound webhooks.

---

## Step 1 — Restore & first run (10 min)

```bash
cd CircularoIntegration.Api
dotnet restore
dotnet build
```

Edit `appsettings.json`:
1. `ConnectionStrings:Postgres` → your local PostgreSQL password (same server you use for MWFinanceEnterprise is fine).
2. Leave the rest for the next steps.

Create the database once (psql or pgAdmin): `CREATE DATABASE circularo_integration;`

```bash
dotnet run
```

Open Swagger: `https://localhost:<port>/swagger` — tables are auto-created and Phase 1 config is seeded on first run. Check `/health` returns UP.

## Step 2 — Wire credentials (15 min)

`appsettings.json`:
- `Security:WebhookApiKey` → invent a long random string. You will paste the same value as a custom header `X-Webhook-Key` in BOTH webhook configurations.
- `Circularo:ApiKey` → from Circularo dashboard → My Account → Manage API Keys (the key you created during the KT).
- `LeadSquared:AccessKey` / `SecretKey` → LeadSquared → Settings → API and Webhooks → API Keys.
- `LeadSquared:BaseUrl` → confirm your tenant's API host from the same screen (e.g. `https://api-in21.leadsquared.com` for India DC tenants).

## Step 3 — Prepare the Circularo document (10 min)

Using the imported Postman collection (set your API key on the collection's Authorization tab first):
1. `Create file` (`POST /api/v1/files/saveFile`) — upload any dummy PDF → copy the returned **file hash**.
2. `Create document` (`POST /api/v1/documents`) — put the hash in `pdfFile.content` → copy the returned **document id**.
   (Or upload via the Circularo dashboard and take the document id from there.)
3. Put the document id into the DB:

```sql
UPDATE phase_document_config SET "CircularoDocumentId" = '<doc-id>' WHERE "PhaseNo" = 1;
```

## Step 4 — Local end-to-end simulation (15 min) — DO THIS BEFORE ANY WEBHOOK SETUP

Simulate the LeadSquared webhook with Postman against your local API:

```
POST https://localhost:<port>/api/v1/signing/initiate
Headers: X-Webhook-Key: <your secret>
Body (raw JSON): { "LeadId": "<a real lead id from LSQ>", "After": "Phase 1 Doc Sign" }
```

Expected: `200 {"status":"SENT", ...}` and a signing email arrives at the lead's email address.
Sign it, then simulate the Circularo callback:

```
POST https://localhost:<port>/api/v1/signing/completed
Headers: X-Webhook-Key: <your secret>
Body: { "documentId": "<the doc id>", "event": "signed" }
```

Expected: `200 {"status":"COMPLETED", "nextStage":"Phase 2 Doc Sign"}` and the lead's stage changes in LeadSquared.

If anything fails, the logs (`logs/` folder + console) contain the full inbound payload and every outbound call/response.

## Step 5 — Expose publicly & configure real webhooks (with Siddhesh)

The API must be reachable from the internet. Either Siddhesh hosts it (his offer), or for the demo use a tunnel from your machine:
`ngrok http https://localhost:<port>` (or `devtunnel`/VS dev tunnels) → gives a public URL.

1. **LeadSquared** → Settings → API and Webhooks → Webhooks → create:
   - Event: Lead Stage Change, from Any Stage → to `Phase 1 Doc Sign`
   - URL: `https://<public-url>/api/v1/signing/initiate`
   - Custom header: `X-Webhook-Key: <your secret>`
2. **Circularo** → Administration → Webhooks (admin access) → create:
   - Event: document fully signed / workflow completed
   - Callback URL: `https://<public-url>/api/v1/signing/completed`
   - Custom header if supported: `X-Webhook-Key: <your secret>` (if custom headers are not supported, temporarily blank `Security:WebhookApiKey` — note it as a to-fix)

**Capture one real payload from each webhook** (they are logged in full). If the field names differ from the parser's guesses, the `FindFirstString` helper in `SigningController` already searches nested objects — extend the candidate name lists there if needed.

## Step 6 — Demo script (Friday)

1. Show the lead in LeadSquared → change stage to **Phase 1 Doc Sign**.
2. Show the log line "INITIATE inbound payload" appear → customer inbox receives the Circularo email.
3. Open the email → sign → submit.
4. Show the log line "COMPLETED inbound payload" → lead stage in CRM is now **Phase 2 Doc Sign**, signed PDF attached (if attach enabled).
5. Show `signing_transaction` table: Sent → Signed → Attached with timestamps.

Re-run trick: `DELETE FROM signing_transaction WHERE "LeadId" = '<lead-id>';` then set the lead stage back.

## Known items to confirm (also in the tech spec, section 13)

- Exact LSQ email field name (`LeadSquared:EmailField`, default `EmailAddress`).
- LSQ attachment API endpoint (`AttachEnabled=false` until verified — stage update still demos fine).
- Circularo webhook payload shape + whether it supports custom headers.
- Circularo file-download endpoint template for the signed PDF.
