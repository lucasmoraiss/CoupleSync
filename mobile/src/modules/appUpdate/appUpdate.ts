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

/**
 * Instalada menor que a última: aviso; se também for menor que a mínima: obrigatório. Qualquer versão
 * desconhecida: nada. Só se bloqueia quem tem para onde ir: quem está na última publicada (ou acima), ou quando a
 * última é desconhecida, nunca é bloqueado, qualquer que seja a mínima. Assim uma mínima válida mas errada no
 * servidor não tranca todo mundo numa tela cujo "Baixar" entrega o mesmo APK.
 */
export function decideUpdate(versions: { installed: unknown; latest: unknown; minimum: unknown }): UpdateDecision {
  if (compareVersions(versions.installed, versions.latest) !== -1) return 'nenhum';
  return compareVersions(versions.installed, versions.minimum) === -1 ? 'obrigatorio' : 'aviso';
}

export interface AppUpdateState {
  readonly decision: UpdateDecision;
  /** `X.Y.Z` da versão nativa instalada, ou null (módulo nativo ausente). */
  readonly installedVersion: string | null;
  /** `X.Y.Z` da última versão publicada, ou null (servidor não soube dizer). */
  readonly latestVersion: string | null;
  readonly downloadUrl: string;
}

function normalized(value: unknown): string | null {
  const parts = parseVersion(value);
  return parts ? formatVersion(parts) : null;
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
  return {
    decision: decideUpdate({
      installed,
      latest: field(serverData, 'latestVersion'),
      minimum: field(serverData, 'minimumVersion'),
    }),
    installedVersion: normalized(installed),
    latestVersion: normalized(field(serverData, 'latestVersion')),
    downloadUrl: downloadUrlOf(serverData),
  };
}

/** O que o aparelho guarda da última resposta do servidor: só as duas versões, já como `X.Y.Z` ou null. */
export interface RememberedAnswer {
  readonly latestVersion: string | null;
  readonly minimumVersion: string | null;
}

function isRecord(data: unknown): data is Record<string, unknown> {
  return data !== null && typeof data === 'object' && !Array.isArray(data);
}

/**
 * O servidor disse qual é a última versão? Sem isso a resposta é um "não sei" (a API acordou mas a consulta dela
 * ao GitHub falhou): não decide nada e não é guardada, e continua valendo a lembrança do aparelho. Uma resposta
 * que SABE a última e vem sem mínima é uma decisão ("não há mais mínima"): substitui a lembrança e destrava.
 */
export function knowsLatestVersion(serverData: unknown): boolean {
  return parseVersion(field(serverData, 'latestVersion')) !== null;
}

/** O texto a guardar para uma resposta do servidor, ou null se ela não diz a última versão (nada é guardado). */
export function serializeAnswer(serverData: unknown): string | null {
  if (!isRecord(serverData) || !knowsLatestVersion(serverData)) return null;
  const answer: RememberedAnswer = {
    latestVersion: normalized(serverData.latestVersion),
    minimumVersion: normalized(serverData.minimumVersion),
  };
  return JSON.stringify(answer);
}

/** Lê o texto guardado. Ausente ou ilegível: null (o app abre normal). Nunca lança. */
export function parseRememberedAnswer(text: unknown): RememberedAnswer | null {
  if (typeof text !== 'string' || text === '') return null;
  try {
    const data: unknown = JSON.parse(text);
    if (!isRecord(data)) return null;
    return { latestVersion: normalized(data.latestVersion), minimumVersion: normalized(data.minimumVersion) };
  } catch {
    return null;
  }
}

export interface UpdateMemory {
  /** A lembrança deste aparelho já foi lida do armazenamento. */
  readonly loaded: boolean;
  readonly answer: RememberedAnswer | null;
}

export interface ResolvedUpdate extends AppUpdateState {
  /**
   * Ainda não se sabe se este aparelho já foi bloqueado antes: a lembrança não foi lida e o servidor não
   * respondeu (ou respondeu sem saber a última versão). O layout espera (a leitura é local e rápida) antes de
   * montar as abas. Nunca é espera de rede.
   */
  readonly waitingForMemory: boolean;
}

/**
 * O estado que as telas usam. Vale a resposta do servidor desta abertura (`live`; `undefined` = ainda sem
 * resposta) desde que ela diga a última versão; enquanto ela não chega, ou se ela é um "não sei", vale a
 * lembrança do aparelho. A versão instalada é sempre a atual.
 */
export function resolveUpdate(input: { installed: string | null | undefined; live: unknown; memory: UpdateMemory }): ResolvedUpdate {
  const { installed, live, memory } = input;
  const hasLive = knowsLatestVersion(live);
  const state = appUpdateState(installed, hasLive ? live : memory.loaded ? memory.answer ?? undefined : undefined);
  return { ...state, waitingForMemory: !hasLive && !memory.loaded && state.installedVersion !== null };
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
