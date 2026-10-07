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
import { AI_FEATURE_ENABLED } from '@/modules/chat/aiAvailability';
// Carregado com o app (e não só ao abrir o wizard): é o módulo que registra a limpeza do progresso ao sair da conta.
import '@/modules/openfinance/wizardStore';
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

  // Captura de notificações só com o aceite do usuário (consentimento por usuário; ver useCaptureConsentSync).
  useCaptureConsentSync();

  // AC-007: Register FCM device token once authenticated
  useEffect(() => {
    if (gate === 'app') {
      registerPushToken();
    }
  }, [gate]);

  if (gate !== 'app') {
    return (
      <View style={{ flex: 1, justifyContent: 'center', alignItems: 'center' }}>
        <ActivityIndicator />
      </View>
    );
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
        // Sete abas: sem margem lateral e com letra menor, o rótulo mais longo ("Transações") cabe inteiro.
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
      <Tabs.Screen name="chat/index" options={{ title: 'Chat IA', tabBarLabel: 'Chat IA', href: AI_FEATURE_ENABLED ? undefined : null, tabBarIcon: ({ color, size, focused }) => <AnimatedTabIcon name="chatbubble-ellipses-outline" color={color} size={size} focused={focused} /> }} />
      <Tabs.Screen name="settings/index" options={{ title: 'Config', tabBarLabel: 'Config', tabBarAccessibilityLabel: 'Configurações', tabBarIcon: ({ color, size, focused }) => <AnimatedTabIcon name="settings-outline" color={color} size={size} focused={focused} /> }} />
      <Tabs.Screen name="settings/alerts" options={{ href: null }} />
      <Tabs.Screen name="settings/group" options={{ href: null }} />
      <Tabs.Screen name="settings/change-password" options={{ href: null }} />
      <Tabs.Screen name="settings/verify-email" options={{ href: null }} />
      <Tabs.Screen name="settings/privacy" options={{ href: null }} />
      <Tabs.Screen name="settings/capture-consent" options={{ href: null }} />
      <Tabs.Screen name="settings/openfinance/index" options={{ href: null }} />
      <Tabs.Screen name="settings/openfinance/wizard" options={{ href: null }} />
      <Tabs.Screen name="ocr-upload" options={{ href: null }} />
      <Tabs.Screen name="ocr-review" options={{ href: null }} />
      <Tabs.Screen name="transactions/new" options={{ href: null }} />
      <Tabs.Screen name="transactions/edit" options={{ href: null }} />
    </Tabs>
  );
}
