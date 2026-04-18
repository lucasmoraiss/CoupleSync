# Cloud Deployment Analysis — CoupleSync V1.5

> Decision date: April 2026  
> Scope: Free-tier cloud stack for CoupleSync pilot (up to 10 users / 5 couples)  
> Decision status: **Confirmed** (see ADR-001)

---

## 1. Problem Statement

CoupleSync V1 runs on a local development machine. V1.5 must make the backend reachable to pilot testers without incurring any monthly cost. Constraints:

- Pilot: ≤ 10 users, ≤ 5 couples, low-concurrency workload.
- Zero monthly spend — all services must fit 100% in free tiers.
- Keep the existing .NET 8 Web API and PostgreSQL as-is (no rewrites).
- Secrets (JWT keys, FCM credentials, OCR keys) must never be committed.

---

## 2. Evaluation Criteria

| Criterion | Weight | Notes |
|---|---|---|
| Cost (free tier exists) | Critical | Must be $0/month for pilot |
| Cold-start latency | High | Budget < 5 s after scale-to-zero |
| PostgreSQL compatibility | High | EF Core Npgsql, 9 existing migrations |
| Container support (.NET 8) | High | No code changes desired |
| Free tier generosity | High | Enough for real usage, not toy limits |
| DX / deployment friction | Medium | Simple GitHub Actions setup |
| Compliance / privacy | Medium | Brazilian financial data |

---

## 3. Compute Layer Evaluation

### Option A — Azure Container Apps (Consumption Plan) ✅ CHOSEN
- **Free allowance**: 180,000 vCPU-seconds + 2,000,000 requests/month forever (not trial).
- **Suitability**: Accepts any Docker image; runs .NET 8 API container unchanged. HTTPS ingress managed. Scale-to-zero supported; cold starts ~1–3 s for .NET 8 distroless images.
- **Limitations**: Consumption Plan containers share underlying infrastructure; occasional cold-start spikes to 5 s possible.
- **Verdict**: Best fit — generous free tier, container-native, .NET first-class.

### Option B — Azure App Service (Free F1)
- **Free allowance**: 1 shared CPU core, 1 GB RAM, 60 CPU minutes/day, no custom domains on HTTPS.
- **Limitations**: 60 CPU-minutes/day is inadequate for a real workload (a single test run consumes several minutes). No container support on F1.
- **Verdict**: Rejected — CPU quota too restrictive and no container support on free tier.

### Option C — Render.com (Free)
- **Free allowance**: 0.1 CPU, 512 MB RAM, sleeps after 15 min inactivity, cold-start 30–60 s.
- **Limitations**: 30–60 s cold-start is unacceptable for a mobile app (NFR-101 < 5 s). Free plan was deprecated for new projects in late 2024.
- **Verdict**: Rejected — cold-start exceeds NFR and free plan availability uncertain.

### Option D — Railway (Hobby)
- **Free allowance**: $5 credit/month, no perpetually-free plan.
- **Limitations**: Credit expires; not truly free.
- **Verdict**: Rejected — not $0/month.

### Option E — Fly.io (Free)
- **Free allowance**: 3 shared CPU VMs, 256 MB RAM each; free tier reduced in 2024.
- **Limitations**: .NET 8 memory footprint typically > 256 MB at runtime for a WebAPI with EF Core.
- **Verdict**: Rejected — RAM constrained for .NET 8.

### Option F — Google Cloud Run (Free)
- **Free allowance**: 2 million requests/month, 360,000 vCPU-seconds. No container registry free tier with GCR.
- **Limitations**: Similar to Azure Container Apps but requires Google Cloud project setup; less straightforward with existing Firebase project overlap.
- **Verdict**: Viable backup, but Azure Container Apps chosen for alignment with Azure AI Document Intelligence OCR service.

---

## 4. Database Layer Evaluation

### Option A — Neon.tech PostgreSQL (Serverless Free) ✅ CHOSEN
- **Free allowance**: 1 project, 1 branch, 10 GB storage, compute hibernates when idle.
- **PostgreSQL version**: 16 (full compatibility with Npgsql + EF Core).
- **Cold-start**: Database wakes in ~500 ms on first connection after hibernation.
- **Connection limit**: ~20 concurrent connections on shared free-tier compute.
- **Verdict**: Best fit — true PostgreSQL, generous storage, serverless cold-start well within tolerance.

### Option B — Supabase (Free)
- **Free allowance**: 500 MB storage, project paused after 1 week of inactivity (requires manual resume).
- **Limitations**: 1-week auto-pause is operationally disruptive; 500 MB too small once OCR import jobs store JSON.
- **Verdict**: Rejected — auto-pause and storage limit too restrictive.

### Option C — ElephantSQL (Free Tiny Turtle)
- **Free allowance**: 20 MB storage, 5 concurrent connections.
- **Limitations**: 20 MB is inadequate even for early pilot data.
- **Verdict**: Rejected — storage critically insufficient.

### Option D — CockroachDB Serverless (Free)
- **Free allowance**: 10 GB storage, 5 RU/s burst limit.
- **Limitations**: Not standard PostgreSQL; Npgsql compatibility partial, EF Core migration behavior differs for distributed transactions.
- **Verdict**: Rejected — compatibility risk with existing migrations.

### Option E — Railway PostgreSQL
- **Free allowance**: Included in $5 credit.
- **Limitations**: Not perpetually free.
- **Verdict**: Rejected — same as Railway compute above.

---

## 5. File Storage Evaluation (OCR uploads)

### Option A — Firebase Storage (free tier) ✅ CHOSEN
- **Free allowance**: 5 GB storage, 1 GB/day download, 20,000 uploads/day — forever (not trial).
- **Reasoning**: Firebase is already used for FCM push notifications. Adding Storage requires no new service account. SDK already available in mobile (Expo Firebase integration).
- **Couple-scoped paths**: `uploads/{coupleId}/{uploadId}.{ext}` enforced at upload time. Firebase Storage Rules prevent cross-couple reads.
- **Verdict**: Best fit — zero additional service overhead, already-integrated ecosystem.

### Option B — Azure Blob Storage
- **Free allowance**: 5 GB LRS for 12 months only (trial), then ~$0.018/GB/month.
- **Limitations**: Not perpetually free; adds new SDK dependency.
- **Verdict**: Viable as code path alternative (see `OcrOptions.StorageProvider`), but Firebase Storage chosen as default.

### Option C — Cloudflare R2
- **Free allowance**: 10 GB, 10 million Class A ops/month — permanently free.
- **Limitations**: Requires separate Cloudflare account and API token; no mobile SDK that integrates natively with Expo.
- **Verdict**: Good option for future; rejected for V1.5 due to mobile integration friction.

---

## 6. OCR Processing Evaluation

### Option A — Azure AI Document Intelligence ✅ CHOSEN (primary)
- **Free allowance**: 5,000 pages/month (free tier, Azure subscription required).
- **Model**: `prebuilt-invoice` or `prebuilt-receipt` handles structured documents well. `prebuilt-layout` as fallback for arbitrary bank statement PDFs.
- **Latency**: Typically 3–10 s per page via async polling model.
- **Verdict**: Best fit for structured financial documents; generous monthly quota for pilot scale.

### Option B — Google Cloud Vision
- **Free allowance**: 1,000 units/month (text detection).
- **Limitations**: General OCR, not financial-document–aware; no table extraction for multi-row bank statements.
- **Verdict**: Fallback only (see `OcrOptions.ProcessingProvider`); insufficient quota as primary.

### Option C — Tesseract (self-hosted)
- **Limitations**: Requires bundling binaries or a sidecar container; accuracy on scanned bank statements without preprocessing is poor (~70–80% F1); adds container size and CPU overhead.
- **Verdict**: Rejected for pilot; not a realistic baseline for financial accuracy.

---

## 7. Push Notifications

Firebase FCM — already implemented in V1. Unchanged. Free for unlimited notifications.

---

## 8. AI Chat

Gemini Flash 2.0 via Google AI Studio — free tier: 15 RPM, 1 million tokens/day. No credit card required. Only service requiring a Google AI Studio project.

---

## 9. Final Stack and Cost Table

| Layer | Service | Free Tier Limits | Monthly Cost |
|---|---|---|---|
| Compute | Azure Container Apps (Consumption) | 180K vCPU-s + 2M req/mo | $0 |
| Database | Neon.tech PostgreSQL | 10 GB, ~20 conn | $0 |
| Push notifications | Firebase FCM | Unlimited | $0 |
| File storage (OCR) | Firebase Storage | 5 GB, 1 GB/day DL | $0 |
| OCR processing | Azure AI Document Intelligence | 5,000 pages/mo | $0 |
| AI chat | Gemini Flash 2.0 (AI Studio) | 1M tokens/day | $0 |
| APK distribution | Firebase App Distribution | Unlimited testers | $0 |
| APK build | EAS Build (Expo) | 30 builds/month (free tier) | $0 |
| Container registry | GitHub Container Registry (GHCR) | 500 MB free, public free | $0 |
| **Total** | | | **$0/month** |

---

## 10. Risk Register

| Risk | Likelihood | Impact | Mitigation |
|---|---|---|---|
| Neon auto-hibernate cold starts spike to > 2 s under load | Low | Medium | Keep `MinPoolSize=1` to maintain a warm connection; acceptable for pilot |
| Azure AI Doc Intelligence quota (5,000 pages/mo) exhausted | Low | Medium | Return 429 `quota_exhausted` with reset date; mobile surfaces guidance. Google Cloud Vision fallback wired |
| Azure Container Apps revision spin-up > 5 s | Low | Medium | App keeps minimum 1 replica via `--min-replicas 1` on the container app (still free under 180K vCPU-s) |
| EAS Build free tier (30 builds/mo) exceeded | Low | Low | Use local Gradle build as fallback (see `apk-generation-guide.md`) |
| Gemini Flash rate limit (15 RPM) hit | Low | Low | All single-user interactions; 15 RPM ample for pilot. Return 429 to mobile |

---

## 11. Architecture Decision Record Reference

- ADR-001: Cloud stack selection → `adr/ADR-001-cloud-stack.md`
- ADR-002: OCR provider selection → `adr/ADR-002-ocr-provider.md`
