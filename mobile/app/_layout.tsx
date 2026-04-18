// AC-010: Root layout — QueryClientProvider wraps the entire app
import React, { useEffect, useState } from 'react';
import { ActivityIndicator, View } from 'react-native';
import { Slot } from 'expo-router';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
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

  useEffect(() => {
    hydrateFromStore().finally(() => setHydrated(true));
  }, [hydrateFromStore]);

  if (!hydrated) {
    return (
      <View style={{ flex: 1, backgroundColor: colors.background, justifyContent: 'center', alignItems: 'center' }}>
        <ActivityIndicator color={colors.primaryLight} size="large" />
      </View>
    );
  }

  return (
    <QueryClientProvider client={queryClient}>
      <ToastProvider>
        <Slot />
      </ToastProvider>
    </QueryClientProvider>
  );
}
