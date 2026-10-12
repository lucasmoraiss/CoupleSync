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

  it('a tela de importação passa o rótulo só na falha do job; o erro de envio fica com o padrão', () => {
    expect(screen).toContain('retryLabel={state.fromJob ? JOB_FAILURE_ACTION_LABEL : undefined}');
    expect(screen).toContain("setState({ phase: 'error', message: errorMessage, fromJob: true });");
    // Só a falha do job (a linha acima) marca fromJob; o erro de envio e o de não-PDF não podem ter a marca.
    expect(screen.match(/fromJob: true/g)).toHaveLength(1);
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
