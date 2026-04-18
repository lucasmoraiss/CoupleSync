# ADR-001 — Cloud Stack Selection

**Session**: 2026-04-17_v15-cloud-budgets-ocr  
**Date**: April 2026  
**Status**: Accepted  
**Deciders**: User + Architect agent  

---

## Context

CoupleSync V1 is functionally complete and runs locally on a developer machine. For V1.5 the backend API and database must move to publicly accessible cloud infrastructure to enable pilot testing on physical Android devices. Hard constraints:

- 100% free tier — zero monthly spend required for the entire pilot.
- Pilot scale: ≤ 10 users / 5 couples (low-concurrency, < 100 requests/day expected).
- Existing .NET 8 Web API must run **unchanged** — no rewrites.
- PostgreSQL (EF Core + Npgsql + 9 existing migrations) must be preserved.
- All secrets managed through environment injection; none committed to source.
- Cold-start latency after scale-to-zero must be < 5 seconds (NFR-101).

---

## Decision

**Compute**: Azure Container Apps — Consumption Plan  
**Database**: Neon.tech PostgreSQL — Serverless Free Tier  
**File storage (OCR)**: Firebase Storage — Free tier  
**APK distribution**: EAS Build (preview profile) + Firebase App Distribution  
**AI chat**: Gemini Flash 2.0 — Google AI Studio free tier  

---

## Alternatives Considered

### Compute alternatives

| Option | Free tier | Rejection reason |
|---|---|---|
| Azure App Service F1 | 60 CPU-min/day, no containers | CPU quota exhausted by a single test run; no container support |
| Render.com Free | 0.1 CPU, sleeps after 15 min, 30–60 s cold-start | Cold-start violates NFR-101 (< 5 s); free plan deprecated for new projects |
| Railway Hobby | $5 credit/month | Not perpetually free |
| Fly.io Free | 256 MB RAM | Too small for .NET 8 WebAPI footprint |
| Google Cloud Run | 2M req/mo, 360K vCPU-s | Viable backup; Azure chosen for alignment with Azure AI Doc Intelligence |

### Database alternatives

| Option | Free tier | Rejection reason |
|---|---|---|
| Supabase Free | 500 MB, project paused after 1 week inactive | Auto-pause is disruptive; 500 MB too small for OCR JSON fields |
| ElephantSQL Tiny Turtle | 20 MB storage | Critically insufficient |
| CockroachDB Serverless | 10 GB, 5 RU/s | Not standard PostgreSQL; EF Core migration compatibility risk |
| Railway PostgreSQL | Included in $5 credit | Not perpetually free |

---

## Consequences

### Positive
- Azure Container Apps Consumption Plan is perpetually free within 180,000 vCPU-s + 2M requests/month; pilot traffic is orders of magnitude below these limits.
- Neon.tech provides a real PostgreSQL 16 instance compatible with all existing EF Core migrations and Npgsql without any schema changes.
- Scale-to-zero (both ACA and Neon) reduces resource usage to zero between sessions, keeping costs zero even as usage approaches free-tier limits.
- Firebase Storage and Firebase App Distribution require no new accounts (Firebase is already in use for FCM).
- Gemini Flash 2.0 free tier (1M tokens/day, 15 RPM) comfortably covers pilot AI chat usage.

### Negative / Risks
- Neon free tier has ~20 concurrent connections; managed by `MaxPoolSize=10` in Npgsql connection string.
- Neon auto-hibernates after 5 minutes idle; wakes in ~500 ms — acceptable for pilot but adds latency to the first request after inactivity.
- Azure AI Document Intelligence free tier (5,000 pages/month) requires an Azure subscription (credit card may be required for account creation, but no charges incurred).
- EAS Build free tier allows 30 builds/month; excess builds fall back to local Gradle (documented in `apk-generation-guide.md`).

---

## Connection String

```
Host=<neon-host>.neon.tech;Database=couplesync;Username=<user>;Password=<pwd>;
SslMode=Require;TrustServerCertificate=true;MaxPoolSize=10;MinPoolSize=1;ConnectionIdleLifetime=60
```

- `SslMode=Require` — mandatory for Neon free-tier TLS enforcement.
- `TrustServerCertificate=true` — Neon free tier uses a certificate that may not resolve via standard chain on Linux containers.
- `MaxPoolSize=10` — keeps connection count within Neon's ~20 connection limit.

---

## Notes

- This decision is scoped to V1.5 pilot. V2 may revisit if usage grows beyond free tiers.
- Azure Container Apps minimum replicas can be set to 1 (`--min-replicas 1`) to eliminate cold starts entirely while remaining within the free vCPU-second budget for pilot traffic.
- Google Cloud Run remains a viable fallback compute option if Azure Container Apps availability is interrupted.
- Full cost and comparison analysis: `docs/architecture/cloud-deployment-analysis.md`.
