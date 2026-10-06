// AC-009: Event uploader — queued POST to /api/v1/integrations/events with exponential backoff.
// Uses the existing axiosInstance (auth interceptor already attached).
import axiosInstance from '@/services/apiClient';
import { registerUserDataCleaner } from '@/state/userData';
import { classifyNotification } from './notificationParser';
import { buildIngestRequest, type IngestNotificationEventRequest } from './ingestRequest';

export type { IngestNotificationEventRequest };

// ── Internal raw event as forwarded from the Kotlin bridge ───────────────────
export interface RawNotificationEvent {
  readonly packageName: string;
  readonly title: string;
  readonly body: string;
  readonly timestampMs: number;
}

// ── Retry queue entry ────────────────────────────────────────────────────────
interface QueueEntry {
  request: IngestNotificationEventRequest;
  attempts: number;
  nextRetryAt: number;
}

const RETRY_DELAYS_MS = [1_000, 2_000, 4_000, 8_000, 16_000] as const;
const MAX_ATTEMPTS = RETRY_DELAYS_MS.length + 1; // 6 total (1 initial + 5 retries)
const QUEUE_POLL_INTERVAL_MS = 3_000;

let retryQueue: QueueEntry[] = [];
let pollHandle: ReturnType<typeof setInterval> | null = null;

function ensurePolling(): void {
  if (pollHandle !== null) return;
  pollHandle = setInterval(flushQueue, QUEUE_POLL_INTERVAL_MS);
}

async function postEvent(request: IngestNotificationEventRequest): Promise<void> {
  await axiosInstance.post('/api/v1/integrations/events', request);
}

async function flushQueue(): Promise<void> {
  if (retryQueue.length === 0) return;

  const now = Date.now();
  const due = retryQueue.filter((e) => e.nextRetryAt <= now);
  const notDue = retryQueue.filter((e) => e.nextRetryAt > now);

  const still: QueueEntry[] = [];
  await Promise.allSettled(
    due.map(async (entry) => {
      try {
        await postEvent(entry.request);
        // Success — entry dropped from queue
      } catch {
        const nextAttempt = entry.attempts + 1;
        if (nextAttempt >= MAX_ATTEMPTS) {
          // Exhausted retries — drop silently; integration status endpoint on backend
          // will reflect the missing events via last_error field (AC-009)
          return;
        }
        const delayMs = RETRY_DELAYS_MS[Math.min(entry.attempts, RETRY_DELAYS_MS.length - 1)];
        still.push({
          request: entry.request,
          attempts: nextAttempt,
          nextRetryAt: Date.now() + delayMs,
        });
      }
    }),
  );

  retryQueue = [...notDue, ...still];

  if (retryQueue.length === 0 && pollHandle !== null) {
    clearInterval(pollHandle);
    pollHandle = null;
  }
}

/**
 * Parse a raw notification event and attempt to upload it.
 * If the upload fails, the event is queued for retry with exponential backoff.
 * Returns false when nothing is uploaded: unknown bank, credit (Pix recebido, estorno…),
 * declined purchase, advertising, or no bank-specific expense pattern matched.
 * The backend records expenses only, so credits must never be sent.
 */
export async function handleRawNotificationEvent(
  event: RawNotificationEvent,
): Promise<boolean> {
  const decision = classifyNotification(
    event.packageName,
    event.title,
    event.body,
    event.timestampMs,
  );

  if (decision.action !== 'upload') {
    return false; // Not an expense — nothing leaves the device
  }

  const request = buildIngestRequest(decision.event);

  try {
    await postEvent(request);
    return true;
  } catch {
    // Enqueue for retry (AC-009)
    const delayMs = RETRY_DELAYS_MS[0];
    retryQueue.push({
      request,
      attempts: 1,
      nextRetryAt: Date.now() + delayMs,
    });
    ensurePolling();
    return true; // Event was recognised; upload deferred
  }
}

/**
 * Drops events captured but not yet delivered. They belong to the user who is leaving; if kept, the next
 * user on this device would upload them into their own group.
 */
export function clearPendingEvents(): void {
  retryQueue = [];
  if (pollHandle !== null) {
    clearInterval(pollHandle);
    pollHandle = null;
  }
}

registerUserDataCleaner(clearPendingEvents);

/** Current number of events pending retry (for diagnostics/testing). */
export function getPendingRetryCount(): number {
  return retryQueue.length;
}

/**
 * Directly upload a pre-built request without parsing.
 * Exposed for use from other modules that already have the request shape.
 */
export async function uploadEvent(request: IngestNotificationEventRequest): Promise<void> {
  try {
    await postEvent(request);
  } catch {
    const delayMs = RETRY_DELAYS_MS[0];
    retryQueue.push({
      request,
      attempts: 1,
      nextRetryAt: Date.now() + delayMs,
    });
    ensurePolling();
  }
}
