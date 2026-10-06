// AC-009: Event uploader — queued POST to /api/v1/integrations/events with exponential backoff.
// Uses the existing axiosInstance (auth interceptor already attached).
import axiosInstance from '@/services/apiClient';
import { registerUserDataCleaner } from '@/state/userData';
import { getSessionEpoch, useSessionStore } from '@/state/sessionStore';
import { isCaptureAllowedNow } from '@/modules/privacy/consentStore';
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
  /** Grupo e sessão em que a notificação foi capturada: só é reenviada enquanto forem os mesmos. */
  binding: CaptureBinding;
  attempts: number;
  nextRetryAt: number;
}

/**
 * A quem pertence uma notificação capturada: o grupo ativo e a sessão daquele instante. O servidor lança o evento
 * no grupo do token que acompanha o envio; por isso um evento capturado com o grupo A ativo nunca pode ser enviado
 * depois que o grupo ativo (ou o usuário) mudou. A época da sessão sobe em login, logout e troca de grupo; renovar
 * o token não a altera.
 */
interface CaptureBinding {
  readonly coupleId: string;
  readonly sessionEpoch: number;
}

/** O vínculo de agora, ou null quando não há grupo ativo (nada é capturado sem grupo). */
function currentBinding(): CaptureBinding | null {
  const { coupleId } = useSessionStore.getState();
  return coupleId ? { coupleId, sessionEpoch: getSessionEpoch() } : null;
}

function stillBound(binding: CaptureBinding): boolean {
  const now = currentBinding();
  return now !== null && now.coupleId === binding.coupleId && now.sessionEpoch === binding.sessionEpoch;
}

const RETRY_DELAYS_MS = [1_000, 2_000, 4_000, 8_000, 16_000] as const;
const MAX_ATTEMPTS = RETRY_DELAYS_MS.length + 1; // 6 total (1 initial + 5 retries)
const QUEUE_POLL_INTERVAL_MS = 3_000;

let retryQueue: QueueEntry[] = [];
// Sobe a cada limpeza. Um envio ou flush que começou antes da limpeza compara e descarta o resultado,
// em vez de recolocar na fila eventos do usuário que saiu.
let queueGeneration = 0;
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
  if (!isCaptureAllowedNow()) {
    // Consentimento retirado (ou de outro usuário): nada pendente deve sair do aparelho.
    clearPendingEvents();
    return;
  }

  // O grupo ativo (ou a sessão) mudou desde a captura: esses eventos são descartados, nunca enviados ao grupo novo.
  retryQueue = retryQueue.filter((e) => stillBound(e.binding));

  const generation = queueGeneration;
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
        if (!stillBound(entry.binding)) return; // o grupo mudou durante o envio: não volta para a fila
        const nextAttempt = entry.attempts + 1;
        if (nextAttempt >= MAX_ATTEMPTS) {
          // Exhausted retries — drop silently; integration status endpoint on backend
          // will reflect the missing events via last_error field (AC-009)
          return;
        }
        const delayMs = RETRY_DELAYS_MS[Math.min(entry.attempts, RETRY_DELAYS_MS.length - 1)];
        still.push({
          request: entry.request,
          binding: entry.binding,
          attempts: nextAttempt,
          nextRetryAt: Date.now() + delayMs,
        });
      }
    }),
  );

  if (generation !== queueGeneration) return; // saiu da conta durante o envio

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
  // Porta de consentimento (autoritativa, do lado do JS): sem aceite, ou com a captura desligada,
  // a notificação nem é lida. Vale mesmo que o módulo nativo (APK antigo) continue entregando eventos.
  if (!isCaptureAllowedNow()) {
    return false;
  }

  const decision = classifyNotification(
    event.packageName,
    event.title,
    event.body,
    event.timestampMs,
  );

  if (decision.action !== 'upload') {
    return false; // Not an expense — nothing leaves the device
  }

  // Lido aqui, no instante da captura e sem nenhuma espera antes do envio: o token que acompanha a requisição é o
  // deste mesmo grupo. Sem grupo ativo não há onde lançar.
  const binding = currentBinding();
  if (!binding) {
    return false;
  }

  const request = buildIngestRequest(decision.event);
  const generation = queueGeneration;

  try {
    await postEvent(request);
    return true;
  } catch {
    if (generation !== queueGeneration) return true; // saiu da conta durante o envio: não enfileira
    if (!stillBound(binding)) return true; // o grupo mudou durante o envio: não enfileira para o grupo novo
    // Enqueue for retry (AC-009)
    const delayMs = RETRY_DELAYS_MS[0];
    retryQueue.push({
      request,
      binding,
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
  queueGeneration += 1;
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
  if (!isCaptureAllowedNow()) return;
  const binding = currentBinding();
  if (!binding) return;
  const generation = queueGeneration;
  try {
    await postEvent(request);
  } catch {
    if (generation !== queueGeneration) return; // saiu da conta durante o envio
    if (!stillBound(binding)) return; // o grupo mudou durante o envio
    const delayMs = RETRY_DELAYS_MS[0];
    retryQueue.push({
      request,
      binding,
      attempts: 1,
      nextRetryAt: Date.now() + delayMs,
    });
    ensurePolling();
  }
}
