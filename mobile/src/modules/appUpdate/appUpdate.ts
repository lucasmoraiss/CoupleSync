// O app avisa quando há APK novo (issue #3). Lógica pura, coberta por __tests__/appUpdate.test.ts.
//
// O ponto que define tudo: a versão que vem dentro do pacote JavaScript (expo.version) é a do OTA, igual em todos
// os aparelhos. O que se compara aqui é a versão NATIVA instalada (o versionName do APK, lido em
// installedVersion.ts) com a última publicada e com a mínima aceita, as duas vindas de GET /api/v1/app/version.
// Qualquer coisa desconhecida (versão instalada, resposta do servidor) dá "nenhum": não avisa e não bloqueia.

/** Link fixo do APK da Release mais recente (o mesmo que a API devolve em `downloadUrl`). */
export const APK_DOWNLOAD_URL = 'https://github.com/lucasmoraiss/CoupleSync/releases/latest/download/couplesync.apk';

// Um endereço vindo do servidor só é aberto se for das Releases deste repositório; senão vale o link fixo.
const RELEASES_PREFIX = 'https://github.com/lucasmoraiss/CoupleSync/releases/';
const SAFE_URL = /^[A-Za-z0-9._\/:-]+$/;

export const APP_UPDATE_TEXT = {
  notice: 'Há uma versão nova do app',
  download: 'Baixar',
  notNow: 'Agora não',
  updateApp: 'Atualizar o app',
  required: 'Esta versão não é mais aceita. Baixe a nova.',
  signOut: 'Sair da conta',
} as const;

export type VersionParts = readonly [number, number, number];

const VERSION = /^[vV]?([0-9]{1,9})\.([0-9]{1,9})\.([0-9]{1,9})(?:[-+].*)?$/;

/** `v1.1.0`, `1.1.0-pit` → [1, 1, 0]. O que não for `X.Y.Z` (com `v` e sufixo opcionais) → null. */
export function parseVersion(text: unknown): VersionParts | null {
  if (typeof text !== 'string') return null;
  const match = VERSION.exec(text.trim());
  if (!match) return null;
  return [Number(match[1]), Number(match[2]), Number(match[3])];
}

/** -1, 0 ou 1; null quando uma das duas não é uma versão ("desconhecida"). Nunca lança. */
export function compareVersions(a: unknown, b: unknown): -1 | 0 | 1 | null {
  const left = parseVersion(a);
  const right = parseVersion(b);
  if (!left || !right) return null;
  for (let i = 0; i < 3; i++) {
    if (left[i] !== right[i]) return left[i] < right[i] ? -1 : 1;
  }
  return 0;
}

function formatVersion(parts: VersionParts): string {
  return parts.join('.');
}

export type UpdateDecision = 'nenhum' | 'aviso' | 'obrigatorio';

/** Instalada menor que a mínima: obrigatório. Menor que a última: aviso. Qualquer versão desconhecida: nada. */
export function decideUpdate(versions: { installed: unknown; latest: unknown; minimum: unknown }): UpdateDecision {
  if (compareVersions(versions.installed, versions.minimum) === -1) return 'obrigatorio';
  if (compareVersions(versions.installed, versions.latest) === -1) return 'aviso';
  return 'nenhum';
}

export interface AppUpdateState {
  readonly decision: UpdateDecision;
  /** `X.Y.Z` da versão nativa instalada, ou null (módulo nativo ausente). */
  readonly installedVersion: string | null;
  /** `X.Y.Z` da última versão publicada, ou null (servidor não soube dizer). */
  readonly latestVersion: string | null;
  readonly downloadUrl: string;
}

function field(data: unknown, name: string): unknown {
  return data !== null && typeof data === 'object' ? (data as Record<string, unknown>)[name] : undefined;
}

function downloadUrlOf(data: unknown): string {
  const url = field(data, 'downloadUrl');
  const fromOurReleases =
    typeof url === 'string' && url.startsWith(RELEASES_PREFIX) && SAFE_URL.test(url) && !url.includes('..');
  return fromOurReleases ? url : APK_DOWNLOAD_URL;
}

/**
 * Tudo o que as telas precisam, a partir da versão instalada e do que o servidor respondeu. `serverData` é o
 * corpo de GET /api/v1/app/version ou `undefined` (sem resposta: rede, 5xx, servidor antigo sem a rota); campos
 * ausentes, null ou de outro tipo contam como desconhecidos.
 */
export function appUpdateState(installed: string | null | undefined, serverData: unknown): AppUpdateState {
  const installedParts = parseVersion(installed);
  const latestParts = parseVersion(field(serverData, 'latestVersion'));
  return {
    decision: decideUpdate({
      installed,
      latest: field(serverData, 'latestVersion'),
      minimum: field(serverData, 'minimumVersion'),
    }),
    installedVersion: installedParts ? formatVersion(installedParts) : null,
    latestVersion: latestParts ? formatVersion(latestParts) : null,
    downloadUrl: downloadUrlOf(serverData),
  };
}

export interface DismissalState {
  /** O que foi dispensado neste aparelho já foi lido do armazenamento. */
  readonly loaded: boolean;
  /** A última versão para a qual o usuário tocou em "Agora não", ou null. */
  readonly dismissedVersion: string | null;
}

/**
 * Aviso do Painel: só no caso "aviso", e só se a última versão for MAIOR que a dispensada ("Agora não" vale até
 * a próxima versão publicada). Antes de saber o que foi dispensado o aviso não aparece, para não piscar.
 */
export function showDashboardNotice(state: AppUpdateState, dismissal: DismissalState): boolean {
  if (state.decision !== 'aviso' || !dismissal.loaded) return false;
  const againstDismissed = compareVersions(state.latestVersion, dismissal.dismissedVersion);
  return againstDismissed === null || againstDismissed === 1;
}

/** Linha de Configurações: a versão instalada (se conhecida) e, havendo versão nova, "Atualizar o app". Não é dispensável. */
export function settingsVersionRow(state: AppUpdateState): { versionText: string | null; showUpdate: boolean } {
  return {
    versionText: state.installedVersion ? `Versão do app: ${state.installedVersion}` : null,
    showUpdate: state.decision !== 'nenhum',
  };
}

/** Versão instalada abaixo da mínima: a tela de bloqueio entra no lugar das abas. Não pode ser dispensado. */
export function blocksApp(state: AppUpdateState): boolean {
  return state.decision === 'obrigatorio';
}
