// AC-010: Main app tab layout with auth guard
import React, { useEffect, useState } from 'react';
import { ActivityIndicator, View } from 'react-native';
import { Tabs, router } from 'expo-router';
import { Ionicons } from '@expo/vector-icons';
import Animated, { useAnimatedStyle, withSpring, useSharedValue } from 'react-native-reanimated';
import { useSessionStore } from '@/state/sessionStore';
import { useGroupEpoch } from '@/state/groupEpoch';
import { mainAreaGate } from '@/modules/couple/groups';
import { TABS_BACK_BEHAVIOR } from '@/navigation/routes';
import { useCaptureConsentSync } from '@/modules/integrations/notification-capture/useCaptureConsentSync';
import { registerPushToken } from '@/services/pushTokenService';
// Carregado com o app (e não só ao abrir o wizard): é o módulo que registra a limpeza do progresso ao sair da conta.
import '@/modules/openfinance/wizardStore';
import { useAutoSyncOnOpen } from '@/modules/openfinance/useAutoSync';
import { blocksApp } from '@/modules/appUpdate/appUpdate';
import { useAppUpdate } from '@/modules/appUpdate/useAppUpdate';
import { AppUpdateRequiredScreen } from '@/components/AppUpdateRequiredScreen';
import { colors } from '@/theme';


type IoniconsName = React.ComponentProps<typeof Ionicons>['name'];

function AnimatedTabIcon({ name, color, size, focused }: { name: IoniconsName; color: string; size: number; focused: boolean }) {
  const scale = useSharedValue(focused ? 1.15 : 1.0);
  useEffect(() => {
    scale.value = withSpring(focused ? 1.15 : 1.0, { damping: 14, stiffness: 160 });
  }, [focused, scale]);
  const animStyle = useAnimatedStyle(() => ({ transform: [{ scale: scale.value }] }));
  return (
    <Animated.View style={animStyle}>
      <Ionicons name={name} size={size} color={color} />
    </Animated.View>
  );
}

export default function MainLayout() {
  const accessToken = useSessionStore((s) => s.accessToken);
  const coupleId = useSessionStore((s) => s.coupleId);
  // Muda a cada troca de grupo: todas as telas são remontadas e buscam os dados do grupo novo.
  const groupEpoch = useGroupEpoch((s) => s.epoch);
  const [hydrated, setHydrated] = useState(false);
  const update = useAppUpdate();

  useEffect(() => {
    // Wait one tick after root layout has hydrated the session store
    setHydrated(true);
  }, []);

  // Sem sessão: login. Com sessão mas sem grupo ativo (saiu do último grupo, foi removido): escolher ou criar um
  // grupo. Nos dois casos nenhuma aba é montada, então nenhuma consulta de dados do grupo sai sem grupo.
  const gate = mainAreaGate({ hydrated, accessToken, coupleId });
  useEffect(() => {
    if (gate === 'login') {
      router.replace('/login' as any);
    } else if (gate === 'group-setup') {
      router.replace('/(auth)/couple-setup' as any);
    }
  }, [gate]);

  // Ordem dos portões: sessão e grupo ativo primeiro (os dois saem desta área na hora, sem montar aba), depois a
  // versão mínima. O bloqueio só existe com a resposta do servidor em mãos; posto antes deles, só valeria quando a
  // resposta chegasse antes do redirecionamento. Com ele na tela, as abas não são montadas (e com elas as
  // perguntas da IA e do Open Finance, que moram no Painel) e o que este layout dispara sozinho fica parado.
  const blocked = gate === 'app' && blocksApp(update);
  const appOpen = gate === 'app' && !blocked;

  // Captura de notificações só com o aceite do usuário (consentimento por usuário; ver useCaptureConsentSync).
  // Sob o bloqueio a tela de consentimento não tem onde abrir: a pergunta (que só é feita uma vez) fica para depois.
  useCaptureConsentSync(!blocked);

  // Open Finance: ao abrir o app, pede em silêncio a sincronização da minha conexão se a última tem mais de 6 horas.
  useAutoSyncOnOpen(appOpen);

  // AC-007: Register FCM device token once authenticated
  useEffect(() => {
    if (appOpen) {
      registerPushToken();
    }
  }, [appOpen]);

  if (gate !== 'app') {
    return (
      <View style={{ flex: 1, justifyContent: 'center', alignItems: 'center' }}>
        <ActivityIndicator />
      </View>
    );
  }

  // Versão instalada abaixo da mínima aceita: a tela de bloqueio entra no lugar das abas. Nenhuma aba é montada,
  // então nenhuma consulta de dados do grupo sai enquanto o bloqueio vale.
  if (blocked) {
    return <AppUpdateRequiredScreen update={update} />;
  }

  return (
    <Tabs
      key={groupEpoch}
      // Voltar (botão do Android) refaz o caminho das abas visitadas; o padrão da biblioteca pula para o Painel.
      backBehavior={TABS_BACK_BEHAVIOR}
      screenOptions={{
        headerShown: false,
        tabBarStyle: {
          backgroundColor: colors.background,
          borderTopColor: colors.surface,
          borderTopWidth: 1,
          height: 60,
          paddingBottom: 8,
          paddingTop: 4,
        },
        tabBarActiveTintColor: colors.primaryLight,
        tabBarInactiveTintColor: colors.textDisabled,
        // Abas lado a lado: sem margem lateral e com letra menor, o rótulo mais longo ("Transações") cabe inteiro.
        tabBarItemStyle: { paddingHorizontal: 0 },
        tabBarLabelStyle: { fontSize: 9.5, fontWeight: '600', letterSpacing: -0.2 },
        tabBarAllowFontScaling: false,
      }}
    >
      <Tabs.Screen name="index" options={{ title: 'Painel', tabBarLabel: 'Painel', tabBarIcon: ({ color, size, focused }) => <AnimatedTabIcon name="grid-outline" color={color} size={size} focused={focused} /> }} />
      <Tabs.Screen name="transactions/index" options={{ title: 'Transações', tabBarLabel: 'Transações', tabBarIcon: ({ color, size, focused }) => <AnimatedTabIcon name="receipt-outline" color={color} size={size} focused={focused} /> }} />
      <Tabs.Screen name="goals/index" options={{ title: 'Metas', tabBarLabel: 'Metas', tabBarIcon: ({ color, size, focused }) => <AnimatedTabIcon name="flag-outline" color={color} size={size} focused={focused} /> }} />
      <Tabs.Screen name="cashflow/index" options={{ title: 'Fluxo', tabBarLabel: 'Fluxo', tabBarAccessibilityLabel: 'Fluxo de caixa', tabBarIcon: ({ color, size, focused }) => <AnimatedTabIcon name="trending-up-outline" color={color} size={size} focused={focused} /> }} />
      <Tabs.Screen name="budget/index" options={{ title: 'Rendas', tabBarLabel: 'Rendas', tabBarAccessibilityLabel: 'Rendas e orçamento', tabBarIcon: ({ color, size, focused }) => <AnimatedTabIcon name="wallet-outline" color={color} size={size} focused={focused} /> }} />
      <Tabs.Screen name="reports/index" options={{ title: 'Relatórios', tabBarLabel: 'Relatórios', tabBarIcon: ({ color, size, focused }) => <AnimatedTabIcon name="pie-chart-outline" color={color} size={size} focused={focused} /> }} />
      <Tabs.Screen name="settings/index" options={{ title: 'Config', tabBarLabel: 'Config', tabBarAccessibilityLabel: 'Configurações', tabBarIcon: ({ color, size, focused }) => <AnimatedTabIcon name="settings-outline" color={color} size={size} focused={focused} /> }} />
      {/* O Assistente não é aba: abre pelo botão do Painel. Se aparece ou não vem do servidor (GET /ai/status). */}
      <Tabs.Screen name="chat/index" options={{ title: 'Assistente', href: null }} />
      <Tabs.Screen name="ai/welcome" options={{ href: null }} />
      <Tabs.Screen name="settings/ai" options={{ href: null }} />
      <Tabs.Screen name="settings/alerts" options={{ href: null }} />
      <Tabs.Screen name="settings/group" options={{ href: null }} />
      <Tabs.Screen name="settings/change-password" options={{ href: null }} />
      <Tabs.Screen name="settings/verify-email" options={{ href: null }} />
      <Tabs.Screen name="settings/privacy" options={{ href: null }} />
      <Tabs.Screen name="settings/capture-consent" options={{ href: null }} />
      <Tabs.Screen name="settings/openfinance/index" options={{ href: null }} />
      <Tabs.Screen name="settings/openfinance/wizard" options={{ href: null }} />
      <Tabs.Screen name="openfinance/review" options={{ href: null }} />
      <Tabs.Screen name="ocr-upload" options={{ href: null }} />
      <Tabs.Screen name="ocr-review" options={{ href: null }} />
      <Tabs.Screen name="transactions/new" options={{ href: null }} />
      <Tabs.Screen name="transactions/edit" options={{ href: null }} />
    </Tabs>
  );
}
