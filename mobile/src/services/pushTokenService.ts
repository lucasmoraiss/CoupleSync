// AC-007: FCM device token registration using expo-notifications
import { Platform } from 'react-native';
import * as Notifications from 'expo-notifications';
import { notificationsApiClient } from '@/services/apiClient';

/**
 * Requests notification permission, obtains the native FCM device token,
 * and registers it with the backend POST /api/v1/devices/token.
 *
 * Non-blocking: permission denial or FCM errors are handled gracefully.
 * V1 scope: Android only.
 */
export async function registerPushToken(): Promise<void> {
  if (Platform.OS !== 'android') {
    return;
  }

  const { status: existing } = await Notifications.getPermissionsAsync();
  let finalStatus = existing;

  if (existing !== 'granted') {
    const { status } = await Notifications.requestPermissionsAsync();
    finalStatus = status;
  }

  if (finalStatus !== 'granted') {
    // User declined — non-fatal, push notifications remain disabled
    return;
  }

  try {
    const tokenData = await Notifications.getDevicePushTokenAsync();
    await notificationsApiClient.registerDeviceToken(tokenData.data, 'android');
  } catch (err) {
    // Non-fatal: device may not have Play Services configured (emulator),
    // google-services.json may be missing, or network may be unavailable.
    if (__DEV__) {
      console.warn('[pushTokenService] Failed to register push token:', err);
    }
  }
}
