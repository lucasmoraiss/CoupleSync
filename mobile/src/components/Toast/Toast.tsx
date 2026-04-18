// AC-608: Toast visual component — animated slide-up, colored left border by variant
import React from 'react';
import { Animated, StyleSheet, Text, TouchableOpacity } from 'react-native';
import { borderRadius, colors, spacing, typography } from '@/theme';
import type { ToastVariant } from './ToastProvider';

export const VARIANT_COLORS: Record<ToastVariant, string> = {
  success: colors.success,
  error: colors.error,
  warning: colors.warning,
  info: colors.primary,
};

interface ToastProps {
  message: string;
  variant: ToastVariant;
  translateY: Animated.Value;
  onDismiss: () => void;
  bottomOffset: number;
}

export function Toast({ message, variant, translateY, onDismiss, bottomOffset }: ToastProps) {
  const accentColor = VARIANT_COLORS[variant];

  return (
    <Animated.View
      style={[
        styles.container,
        {
          bottom: bottomOffset,
          borderLeftColor: accentColor,
          transform: [{ translateY }],
        },
      ]}
      accessibilityRole="alert"
      accessibilityLiveRegion={variant === 'error' ? 'assertive' : 'polite'}
    >
      <TouchableOpacity
        style={styles.inner}
        onPress={onDismiss}
        activeOpacity={0.85}
        accessibilityLabel="Fechar notificação"
      >
        <Text style={styles.message} numberOfLines={3}>
          {message}
        </Text>
      </TouchableOpacity>
    </Animated.View>
  );
}

const styles = StyleSheet.create({
  container: {
    position: 'absolute',
    left: spacing.md,
    right: spacing.md,
    borderRadius: borderRadius.md,
    backgroundColor: colors.surface,
    borderLeftWidth: 4,
    shadowColor: '#000',
    shadowOffset: { width: 0, height: 2 },
    shadowOpacity: 0.3,
    shadowRadius: 6,
    elevation: 8,
    zIndex: 9999,
  },
  inner: {
    flexDirection: 'row',
    alignItems: 'center',
    paddingVertical: spacing.md,
    paddingHorizontal: spacing.md,
    minHeight: 52,
  },
  message: {
    flex: 1,
    color: colors.text,
    fontSize: typography.fontSize.md,
    fontWeight: typography.fontWeight.medium,
    lineHeight: 20,
  },
});
