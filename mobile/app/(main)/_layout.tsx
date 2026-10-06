// AC-010: Main app tab layout with auth guard
import React, { useEffect, useState } from 'react';
import { ActivityIndicator, View } from 'react-native';
import { Tabs, router } from 'expo-router';
import { Ionicons } from '@expo/vector-icons';
import Animated, { useAnimatedStyle, withSpring, useSharedValue } from 'react-native-reanimated';
import { useSessionStore } from '@/state/sessionStore';
import { useCaptureConsentSync } from '@/modules/integrations/notification-capture/useCaptureConsentSync';
import { registerPushToken } from '@/services/pushTokenService';
import { colors } from '@/theme';

// AI Chat tab is visible only when EXPO_PUBLIC_AI_CHAT_ENABLED=true
const AI_CHAT_ENABLED = process.env.EXPO_PUBLIC_AI_CHAT_ENABLED === 'true';

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
  const [hydrated, setHydrated] = useState(false);

  useEffect(() => {
    // Wait one tick after root layout has hydrated the session store
    setHydrated(true);
  }, []);

  useEffect(() => {
    if (hydrated && !accessToken) {
      router.replace('/login' as any);
    }
  }, [hydrated, accessToken]);

  // Captura de notificações só com o aceite do usuário (consentimento por usuário; ver useCaptureConsentSync).
  useCaptureConsentSync();

  // AC-007: Register FCM device token once authenticated
  useEffect(() => {
    if (hydrated && accessToken) {
      registerPushToken();
    }
  }, [hydrated, accessToken]);

  if (!hydrated || !accessToken) {
    return (
      <View style={{ flex: 1, justifyContent: 'center', alignItems: 'center' }}>
        <ActivityIndicator />
      </View>
    );
  }

  return (
    <Tabs
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
      <Tabs.Screen name="chat/index" options={{ title: 'Chat IA', tabBarLabel: 'Chat IA', href: AI_CHAT_ENABLED ? undefined : null, tabBarIcon: ({ color, size, focused }) => <AnimatedTabIcon name="chatbubble-ellipses-outline" color={color} size={size} focused={focused} /> }} />
      <Tabs.Screen name="settings/index" options={{ title: 'Config', tabBarLabel: 'Config', tabBarAccessibilityLabel: 'Configurações', tabBarIcon: ({ color, size, focused }) => <AnimatedTabIcon name="settings-outline" color={color} size={size} focused={focused} /> }} />
      <Tabs.Screen name="settings/alerts" options={{ href: null }} />
      <Tabs.Screen name="settings/group" options={{ href: null }} />
      <Tabs.Screen name="settings/change-password" options={{ href: null }} />
      <Tabs.Screen name="settings/verify-email" options={{ href: null }} />
      <Tabs.Screen name="settings/privacy" options={{ href: null }} />
      <Tabs.Screen name="settings/capture-consent" options={{ href: null }} />
      <Tabs.Screen name="ocr-upload" options={{ href: null }} />
      <Tabs.Screen name="ocr-review" options={{ href: null }} />
      <Tabs.Screen name="transactions/new" options={{ href: null }} />
      <Tabs.Screen name="transactions/edit" options={{ href: null }} />
    </Tabs>
  );
}
