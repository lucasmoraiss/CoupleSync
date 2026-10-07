// Token push (FCM) deste aparelho, para o servidor desregistrá-lo quando o usuário sai da conta.
import { Platform } from 'react-native';
import * as Notifications from 'expo-notifications';

const LOOKUP_TIMEOUT_MS = 3_000;

/** Token push do aparelho, ou null (sem permissão, sem Play Services, demorou demais). Nunca rejeita. */
export async function getDevicePushToken(): Promise<string | null> {
  if (Platform.OS !== 'android') return null;
  const lookup = (async () => {
    const { status } = await Notifications.getPermissionsAsync();
    if (status !== 'granted') return null;
    const token = await Notifications.getDevicePushTokenAsync();
    return typeof token.data === 'string' && token.data ? token.data : null;
  })().catch(() => null);
  const timeout = new Promise<null>((resolve) => setTimeout(() => resolve(null), LOOKUP_TIMEOUT_MS));
  return Promise.race([lookup, timeout]);
}
