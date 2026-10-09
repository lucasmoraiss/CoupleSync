// Ao abrir o app (e ao voltar para ele), se a minha conexão do Open Finance tem a última sincronização há mais
// de 6 horas, pede uma em silêncio: nenhum erro aparece. A regra está em sync.ts (pura, testada); aqui só a
// ligação com o React, a sessão e o estado do app.
//
// O que fica em memória (quando foi a última tentativa, e de quem) é do usuário logado: o limpador registrado em
// userData.ts apaga ao sair da conta. O pedido fica preso à época da sessão (autoSyncOnOpen confere).
import { useEffect } from 'react';
import { AppState } from 'react-native';
import { openFinanceApiClient } from '@/services/apiClient';
import { getSessionEpoch, useSessionStore } from '@/state/sessionStore';
import { registerUserDataCleaner } from '@/state/userData';
import { aiUploadConsent } from '@/modules/ai/aiStatus';
import { currentAiStatus } from '@/modules/ai/aiStatusStore';
import { autoSyncOnOpen } from './sync';

/** Entre duas tentativas do mesmo usuário no mesmo grupo (o servidor também só aceita uma a cada 10 minutos). */
const RETRY_AFTER_MS = 10 * 60 * 1000;

let lastAttempt: { key: string; at: number } | null = null;

registerUserDataCleaner(() => {
  lastAttempt = null;
});

function attempt(userId: string, coupleId: string): void {
  const key = `${userId}:${coupleId}`;
  const now = Date.now();
  if (lastAttempt && lastAttempt.key === key && now - lastAttempt.at < RETRY_AFTER_MS) return;
  lastAttempt = { key, at: now };
  void autoSyncOnOpen({
    getEpoch: getSessionEpoch,
    now: Date.now,
    loadStatus: async () => (await openFinanceApiClient.getStatus()).data,
    requestSync: (connectionId, query) => openFinanceApiClient.requestSync(connectionId, query),
    aiConsent: () => aiUploadConsent(currentAiStatus()),
  });
}

/** `active`: o usuário está logado e com grupo (as abas do app estão montadas). */
export function useAutoSyncOnOpen(active: boolean): void {
  const userId = useSessionStore((state) => state.userId);
  const coupleId = useSessionStore((state) => state.coupleId);

  useEffect(() => {
    if (!active || !userId || !coupleId) return;
    attempt(userId, coupleId);
    // `AppState` é do próprio React Native (existe em todo APK já instalado).
    const subscription = AppState.addEventListener('change', (state) => {
      if (state === 'active') attempt(userId, coupleId);
    });
    return () => subscription.remove();
  }, [active, userId, coupleId]);
}
