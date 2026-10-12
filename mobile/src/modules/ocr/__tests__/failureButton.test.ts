// O Jest daqui não renderiza React Native: como nos testes das telas da IA, lê o código da tela e do componente.
import * as fs from 'fs';
import * as path from 'path';
import { JOB_FAILURE_ACTION_LABEL } from '../failureMessage';

const MOBILE_DIR = path.resolve(__dirname, '../../../..');
const read = (relative: string) => fs.readFileSync(path.join(MOBILE_DIR, relative), 'utf8');

describe('botão da tela de falha da importação', () => {
  const screen = read('app/(main)/ocr-upload.tsx');
  const errorState = read('src/components/ErrorState.tsx');

  it('o rótulo é "Escolher arquivo" (o botão só volta para a escolha do arquivo)', () => {
    expect(JOB_FAILURE_ACTION_LABEL).toBe('Escolher arquivo');
  });

  it('a tela de importação entrega o rótulo do estado de erro ao botão (nada de rótulo fixo)', () => {
    expect(screen).toContain('retryLabel={state.retryLabel}');
    expect(screen).not.toContain('fromJob');
  });

  it('falha do job, arquivo ilegível e não-PDF pedem outro arquivo; o erro de envio decide pelo código da API', () => {
    expect(screen).toContain(
      "setState({ phase: 'error', message: errorMessage, retryLabel: JOB_FAILURE_ACTION_LABEL });",
    );
    expect(screen).toContain(
      "setState({ phase: 'error', message: PDF_READABILITY_ERROR, retryLabel: JOB_FAILURE_ACTION_LABEL });",
    );
    expect(screen).toContain("setState({ phase: 'error', message: NOT_PDF_ERROR, retryLabel: JOB_FAILURE_ACTION_LABEL });");
    // O erro do envio (rede, servidor, limite, arquivo grande) passa pela função testada em failureMessage.test.ts:
    // sem ela, erro de rede ficaria sem "Tentar novamente" ou o limite com ele.
    expect(screen).toContain("setState({ phase: 'error', message, retryLabel: uploadErrorRetryLabel(err) });");
  });

  it("todo setState de erro da tela diz o rótulo de forma explícita: nenhum erro novo herda 'Escolher arquivo' sem querer", () => {
    const errorStates = screen.match(/setState\(\{ phase: 'error'[^;]*\);/g) ?? [];
    expect(errorStates).toHaveLength(4);
    for (const call of errorStates) expect(call).toContain('retryLabel:');
  });

  it('ErrorState: rótulo opcional, padrão "Tentar novamente" para os demais usos, acessibilidade acompanha', () => {
    expect(errorState).toContain("retryLabel = 'Tentar novamente'");
    expect(errorState).toContain('accessibilityLabel={retryLabel}');
    expect(errorState).toContain('<Text style={styles.retryText}>{retryLabel}</Text>');
  });

  it('nenhum outro uso de ErrorState passa retryLabel', () => {
    const offenders: string[] = [];
    const walk = (dir: string) => {
      for (const e of fs.readdirSync(dir, { withFileTypes: true })) {
        const full = path.join(dir, e.name);
        if (e.isDirectory()) walk(full);
        else if (/\.tsx$/.test(e.name) && /retryLabel=/.test(fs.readFileSync(full, 'utf8'))) offenders.push(path.relative(MOBILE_DIR, full));
      }
    };
    walk(path.join(MOBILE_DIR, 'app'));
    walk(path.join(MOBILE_DIR, 'src'));
    expect(offenders.map((o) => o.split(path.sep).join('/'))).toEqual(['app/(main)/ocr-upload.tsx']);
  });
});
