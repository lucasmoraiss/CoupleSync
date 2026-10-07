// Permissão "Acesso às notificações" do Android, sempre atual: consultada ao entrar na tela e toda vez que o app
// volta ao primeiro plano (o usuário concede ou revoga a permissão fora do app, nas configurações do sistema).
import { useCallback, useEffect, useState } from 'react';
import { AppState, Platform } from 'react-native';
import { useFocusEffect } from 'expo-router';
import { checkNotificationListenerPermission, isNotificationBridgeAvailable } from './NotificationListenerBridge';

/** true/false conforme o Android; null enquanto não se sabe ou quando não se aplica (sem o módulo nativo). */
export function useNotificationPermission(): boolean | null {
  const [granted, setGranted] = useState<boolean | null>(null);

  const check = useCallback(() => {
    if (Platform.OS !== 'android' || !isNotificationBridgeAvailable()) return;
    void checkNotificationListenerPermission().then(setGranted);
  }, []);

  // As abas ficam montadas: conferir só na montagem deixaria o estado velho para sempre.
  useFocusEffect(check);

  useEffect(() => {
    const subscription = AppState.addEventListener('change', (state) => {
      if (state === 'active') check();
    });
    return () => subscription.remove();
  }, [check]);

  return granted;
}
