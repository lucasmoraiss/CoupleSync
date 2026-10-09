// Abre o download do APK no navegador do aparelho. Baixar e instalar sozinho é outro item (issue #4).
import { Linking } from 'react-native';

/** `Linking` é do próprio React Native (existe em todo APK já instalado). Se nenhum navegador abrir, a tela avisa. */
export async function openApkDownload(url: string, onFailure: (message: string) => void): Promise<void> {
  try {
    await Linking.openURL(url);
  } catch {
    onFailure(`Não foi possível abrir o navegador. Abra ${url.replace('https://', '')} manualmente.`);
  }
}
