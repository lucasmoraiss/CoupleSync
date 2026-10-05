// Corpo do POST /api/v1/integrations/events (backend: IngestNotificationEventRequest).
// Módulo puro (sem React Native / axios) para ser testável com Jest.
//
// Privacidade: o texto da notificação (título + corpo) NUNCA sai do aparelho. O contrato do
// backend ainda aceita os campos opcionais "rawNotificationText" e "description"; o app não
// os envia. Só seguem os dados estruturados abaixo.
import type { ParsedTransactionEvent } from './notificationParser';

export interface IngestNotificationEventRequest {
  /** Bank name as accepted by the backend (AllowedBanks), e.g. "Nubank", "Itau", "C6" */
  readonly bank: string;
  /** Transaction amount (must be > 0) */
  readonly amount: number;
  /** ISO 4217 currency code — 'BRL' | 'USD' | 'EUR' */
  readonly currency: string;
  /** ISO 8601 timestamp of when the event occurred */
  readonly eventTimestamp: string;
  /** Merchant or counter-party name, if extractable */
  readonly merchant?: string;
}

/**
 * Monta o corpo enviado ao servidor a partir da notificação já interpretada:
 * banco, valor, moeda, data/hora e estabelecimento (quando extraído). Nada além disso.
 */
export function buildIngestRequest(parsed: ParsedTransactionEvent): IngestNotificationEventRequest {
  return {
    bank: parsed.bankApiName,
    amount: parsed.amount,
    currency: 'BRL', // V1: Brazilian banks only
    eventTimestamp: parsed.receivedAt,
    merchant: parsed.merchant ?? undefined,
  };
}
