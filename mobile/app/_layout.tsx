// AC-010: Root layout — QueryClientProvider wraps the entire app
import React, { useEffect, useState } from 'react';
import { ActivityIndicator, StatusBar, View } from 'react-native';
import { Slot } from 'expo-router';
import { SafeAreaProvider } from 'react-native-safe-area-context';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import {
  Ionicons,
  MaterialIcons,
  MaterialCommunityIcons,
  FontAwesome,
} from '@expo/vector-icons';
import * as Font from 'expo-font';
import { useSessionStore } from '@/state/sessionStore';
import { colors } from '@/theme';
import { ToastProvider } from '@/components/Toast/ToastProvider';

const queryClient = new QueryClient({
  defaultOptions: {
    queries: { retry: 2, staleTime: 30_000 },
  },
});

export default function RootLayout() {
  const hydrateFromStore = useSessionStore((s) => s.hydrateFromStore);
  const [hydrated, setHydrated] = useState(false);
  const [fontsLoaded, setFontsLoaded] = useState(false);

  useEffect(() => {
    // Preload icon fonts explicitly. Needed for production APK builds where
    // transitive font assets from @expo/vector-icons may not load in time.
    Font.loadAsync({
      ...Ionicons.font,
      ...MaterialIcons.font,
      ...MaterialCommunityIcons.font,
      ...FontAwesome.font,
    })
      .catch((err) => {
        if (__DEV__) console.warn('[fonts] Failed to preload icon fonts:', err);
      })
      .finally(() => setFontsLoaded(true));
  }, []);

  useEffect(() => {
    hydrateFromStore().finally(() => setHydrated(true));
  }, [hydrateFromStore]);

  if (!hydrated || !fontsLoaded) {
    return (
      <View style={{ flex: 1, backgroundColor: colors.background, justifyContent: 'center', alignItems: 'center' }}>
        <ActivityIndicator color={colors.primaryLight} size="large" />
      </View>
    );
  }

  return (
    <SafeAreaProvider style={{ backgroundColor: colors.background }}>
      <StatusBar barStyle="light-content" backgroundColor={colors.background} />
      <QueryClientProvider client={queryClient}>
        <ToastProvider>
          <View style={{ flex: 1, backgroundColor: colors.background }}>
            <Slot />
          </View>
        </ToastProvider>
      </QueryClientProvider>
    </SafeAreaProvider>
  );
}
