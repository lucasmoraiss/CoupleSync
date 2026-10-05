import { classifyNotification } from '../notificationParser';
import { buildIngestRequest } from '../ingestRequest';

const TS = Date.UTC(2026, 9, 5, 14, 30, 0);

function parse(pkg: string, title: string, body: string) {
  const decision = classifyNotification(pkg, title, body, TS);
  if (decision.action !== 'upload') throw new Error(`notificação não reconhecida: ${decision.reason}`);
  return decision.event;
}

describe('A09 — corpo enviado ao servidor não leva o texto da notificação', () => {
  const title = 'Pix enviado';
  const body = 'Pix enviado: R$ 150,00 para Mercearia Boa Vista. Solicitado por Maria Aparecida Souza, CPF ***.123.456-**';

  it('envia somente banco, valor, moeda, data/hora e estabelecimento', () => {
    const request = buildIngestRequest(parse('com.nu.production', title, body));

    expect(request).toEqual({
      bank: 'Nubank',
      amount: 150,
      currency: 'BRL',
      eventTimestamp: '2026-10-05T14:30:00.000Z',
      merchant: 'Mercearia Boa Vista',
    });
  });

  it('o JSON enviado não contém o texto bruto nem nomes de terceiros que estavam na notificação', () => {
    const json = JSON.stringify(buildIngestRequest(parse('com.nu.production', title, body)));

    expect(json).not.toContain('rawNotificationText');
    expect(json).not.toContain(body);
    expect(json).not.toContain('Maria Aparecida');
    expect(json).not.toContain('CPF');
    expect(json).not.toContain('Solicitado');
  });

  it('o evento interpretado não guarda o texto bruto', () => {
    const event = parse('com.nu.production', title, body);

    expect(Object.keys(event).sort()).toEqual(
      ['amount', 'bank', 'matchedPatternId', 'merchant', 'packageName', 'receivedAt'].sort(),
    );
    expect(JSON.stringify(event)).not.toContain('Maria Aparecida');
  });

  it('sem estabelecimento, o campo merchant não é enviado', () => {
    const request = buildIngestRequest(parse('com.bradesco', 'Bradesco', 'Débito de R$ 120,00 em sua conta'));

    expect(JSON.parse(JSON.stringify(request))).toEqual({
      bank: 'Bradesco',
      amount: 120,
      currency: 'BRL',
      eventTimestamp: '2026-10-05T14:30:00.000Z',
    });
  });
});
