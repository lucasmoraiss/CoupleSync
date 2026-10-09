// A versão NATIVA instalada (o versionName do APK), lida do módulo nativo ExpoApplication COM GUARDA.
//
// Este JavaScript chega por OTA a APKs que já estão instalados, então o módulo é procurado pelo nome com
// `requireOptionalNativeModule` (devolve null em vez de lançar) e tudo fica dentro de try/catch: módulo ausente
// ou com defeito = versão "desconhecida" (null), e aí o app não avisa nem bloqueia.
//
// O módulo vem do pacote expo-application, que os APKs 1.0.0 (tag v1.0.0-pit) e 1.1.0 (tag v1.1.0) já trazem
// como dependência do expo-notifications (conferido com `expo prebuild` das duas tags; ver o relatório da issue
// #3). O pacote não é importado aqui de propósito: ele não está declarado em package.json, e ler o módulo pelo
// nome dispensa declarar (e mexer no lock).
import { requireOptionalNativeModule } from 'expo-modules-core';

interface ExpoApplicationNativeModule {
  readonly nativeApplicationVersion?: unknown;
}

const NATIVE_MODULE_NAME = 'ExpoApplication';

/** Lê a versão do módulo devolvido por `load`. Qualquer falha ou valor que não seja texto: null. Nunca lança. */
export function readInstalledVersion(load: () => unknown): string | null {
  try {
    const nativeModule = load() as ExpoApplicationNativeModule | null | undefined;
    const version = nativeModule?.nativeApplicationVersion;
    return typeof version === 'string' && version.trim() !== '' ? version.trim() : null;
  } catch {
    return null;
  }
}

let cached: { readonly version: string | null } | null = null;

/** A versão do APK instalado, ou null se desconhecida. Não muda com o app aberto: lida uma vez. */
export function getInstalledVersion(): string | null {
  if (!cached) {
    cached = { version: readInstalledVersion(() => requireOptionalNativeModule(NATIVE_MODULE_NAME)) };
  }
  return cached.version;
}

export function resetInstalledVersionForTests(): void {
  cached = null;
}
