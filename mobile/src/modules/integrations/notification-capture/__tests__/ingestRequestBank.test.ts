import { classifyNotification } from '../notificationParser';
import { buildIngestRequest } from '../ingestRequest';

// Cópia da lista do backend (IngestNotificationEventRequestValidator.AllowedBanks).
// O servidor compara Bank.Trim().ToUpperInvariant() com esta lista e responde 400 fora dela.
const BACKEND_ALLOWED_BANKS = ['NUBANK', 'ITAU', 'INTER', 'C6', 'BRADESCO', 'XP', 'BTG', 'SANTANDER', 'CAIXA', 'BB'];

const PURCHASES: ReadonlyArray<readonly [string, string, string]> = [
  ['Nubank', 'com.nu.production', 'Compra no crédito: R$ 45,90 em PADARIA DO ZE'],
  ['Itaú', 'com.itau', 'Compra no cartão: R$ 1.234,56 - MAGAZINE LUIZA'],
  ['Inter', 'br.com.intermedium', 'Compra aprovada: R$ 89,90 em POSTO IPIRANGA'],
  ['C6 Bank', 'com.c6bank.app', 'Compra de R$ 250,00 em AMAZON BR aprovada'],
  ['Bradesco', 'com.bradesco', 'Compra de R$ 67,30 em DROGARIA SAO PAULO'],
];

describe('nome do banco enviado é aceito pelo validador do backend', () => {
  it.each(PURCHASES)('%s', (_bank, pkg, body) => {
    const decision = classifyNotification(pkg, 'Banco', body, Date.UTC(2026, 9, 5));
    if (decision.action !== 'upload') throw new Error(`não reconhecida: ${decision.reason}`);

    const request = buildIngestRequest(decision.event);

    expect(BACKEND_ALLOWED_BANKS).toContain(request.bank.trim().toUpperCase());
  });
});
