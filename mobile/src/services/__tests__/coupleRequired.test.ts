import { decideCoupleRequired } from '../coupleRequired';

const httpError = (status: number, data: unknown) => ({ isAxiosError: true, response: { status, data } });
const coupleRequired = (message = 'Você não participa mais deste grupo.') =>
  httpError(403, { code: 'COUPLE_REQUIRED', message });

describe('403 COUPLE_REQUIRED (M-M2)', () => {
  it('resposta da sessão atual: trata e mostra a mensagem do servidor', () => {
    expect(decideCoupleRequired(coupleRequired(), 7, 7)).toEqual({
      handle: true,
      message: 'Você não participa mais deste grupo.',
    });
  });

  it('resposta atrasada do grupo anterior (a época mudou com a troca de grupo): ignora, o grupo novo fica', () => {
    expect(decideCoupleRequired(coupleRequired(), 7, 8)).toEqual({ handle: false });
  });

  it('mostra a mensagem específica do servidor, não um texto fixo', () => {
    const decision = decideCoupleRequired(coupleRequired('Você foi removido do grupo Casa.'), 7, 7);
    expect(decision).toEqual({ handle: true, message: 'Você foi removido do grupo Casa.' });
  });

  it('requisição sem época anotada continua sendo tratada', () => {
    expect(decideCoupleRequired(coupleRequired(), undefined, 3).handle).toBe(true);
  });

  it('outros erros não são COUPLE_REQUIRED', () => {
    expect(decideCoupleRequired(httpError(403, { code: 'FORBIDDEN', message: 'Sem permissão.' }), 7, 7)).toEqual({ handle: false });
    expect(decideCoupleRequired(httpError(404, { code: 'COUPLE_REQUIRED', message: 'x' }), 7, 7)).toEqual({ handle: false });
    expect(decideCoupleRequired(new Error('Network Error'), 7, 7)).toEqual({ handle: false });
    expect(decideCoupleRequired(null, 7, 7)).toEqual({ handle: false });
  });
});
