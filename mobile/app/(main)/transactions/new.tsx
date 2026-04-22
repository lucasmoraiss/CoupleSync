// Manual transaction entry screen — allows the user to add an expense by hand,
// independent of OCR / push-notification parsing. Extends existing flows, does NOT replace.
import React, { useState } from 'react';
import {
  View,
  Text,
  TextInput,
  Pressable,
  ScrollView,
  StyleSheet,
  ActivityIndicator,
  Alert,
  KeyboardAvoidingView,
  Platform,
} from 'react-native';
import { SafeAreaView } from 'react-native-safe-area-context';
import { router } from 'expo-router';
import { Ionicons } from '@expo/vector-icons';
import { useQueryClient } from '@tanstack/react-query';
import { transactionsApiClient } from '@/services/apiClient';
import { PREDEFINED_CATEGORIES } from '@/modules/transactions/categories';
import { colors, spacing, typography, borderRadius } from '@/theme';

export default function NewTransactionScreen() {
  const queryClient = useQueryClient();

  const [amountText, setAmountText] = useState('');
  const [description, setDescription] = useState('');
  const [merchant, setMerchant] = useState('');
  const [category, setCategory] = useState<string>(PREDEFINED_CATEGORIES[0].value);
  const [submitting, setSubmitting] = useState(false);

  const parseAmount = (raw: string): number | null => {
    // Accept both "12,34" and "12.34"
    const normalized = raw.replace(/\s/g, '').replace(',', '.');
    const n = Number(normalized);
    if (!Number.isFinite(n) || n <= 0) return null;
    // Round to 2 decimals
    return Math.round(n * 100) / 100;
  };

  const handleSubmit = async () => {
    const amount = parseAmount(amountText);
    if (amount === null) {
      Alert.alert('Valor inválido', 'Informe um valor positivo (ex: 42,90).');
      return;
    }
    if (!category) {
      Alert.alert('Categoria', 'Selecione uma categoria.');
      return;
    }

    setSubmitting(true);
    try {
      await transactionsApiClient.createManual({
        amount,
        currency: 'BRL',
        description: description.trim() || undefined,
        merchant: merchant.trim() || undefined,
        category,
        eventTimestampUtc: new Date().toISOString(),
      });

      // Invalidate caches so dashboard / transactions list refresh immediately
      await Promise.all([
        queryClient.invalidateQueries({ queryKey: ['transactions'] }),
        queryClient.invalidateQueries({ queryKey: ['dashboard'] }),
        queryClient.invalidateQueries({ queryKey: ['reports'] }),
        queryClient.invalidateQueries({ queryKey: ['budget'] }),
      ]);

      router.back();
    } catch (err: any) {
      const apiMsg = err?.response?.data?.message;
      Alert.alert('Erro', apiMsg ?? 'Não foi possível registrar a transação.');
    } finally {
      setSubmitting(false);
    }
  };

  return (
    <SafeAreaView style={styles.container} edges={['top']}>
      <KeyboardAvoidingView
        behavior={Platform.OS === 'ios' ? 'padding' : undefined}
        style={{ flex: 1 }}
      >
        <ScrollView contentContainerStyle={styles.scroll} keyboardShouldPersistTaps="handled">
          <View style={styles.headerRow}>
            <Pressable onPress={() => router.back()} hitSlop={12} accessibilityRole="button">
              <Ionicons name="arrow-back" size={24} color={colors.text} />
            </Pressable>
            <Text style={styles.title}>Nova transação</Text>
            <View style={{ width: 24 }} />
          </View>

          <Text style={styles.label}>Valor (R$)</Text>
          <TextInput
            style={styles.input}
            keyboardType="decimal-pad"
            placeholder="0,00"
            placeholderTextColor={colors.placeholder}
            value={amountText}
            onChangeText={setAmountText}
            editable={!submitting}
          />

          <Text style={styles.label}>Descrição (opcional)</Text>
          <TextInput
            style={styles.input}
            placeholder="Ex: Mercado"
            placeholderTextColor={colors.placeholder}
            value={description}
            onChangeText={setDescription}
            editable={!submitting}
          />

          <Text style={styles.label}>Estabelecimento (opcional)</Text>
          <TextInput
            style={styles.input}
            placeholder="Ex: Pão de Açúcar"
            placeholderTextColor={colors.placeholder}
            value={merchant}
            onChangeText={setMerchant}
            editable={!submitting}
          />

          <Text style={styles.label}>Categoria</Text>
          <View style={styles.categoryGrid}>
            {PREDEFINED_CATEGORIES.map((c) => {
              const selected = category === c.value;
              return (
                <Pressable
                  key={c.value}
                  style={[styles.categoryChip, selected && styles.categoryChipActive]}
                  onPress={() => setCategory(c.value)}
                  disabled={submitting}
                  accessibilityRole="button"
                  accessibilityState={{ selected }}
                >
                  <Ionicons
                    name={c.icon as any}
                    size={18}
                    color={selected ? colors.white : colors.textMuted}
                    style={{ marginRight: 6 }}
                  />
                  <Text style={[styles.categoryChipText, selected && styles.categoryChipTextActive]}>
                    {c.label}
                  </Text>
                </Pressable>
              );
            })}
          </View>

          <Pressable
            style={[styles.submitBtn, submitting && styles.submitBtnDisabled]}
            onPress={handleSubmit}
            disabled={submitting}
            accessibilityRole="button"
          >
            {submitting ? (
              <ActivityIndicator color={colors.white} />
            ) : (
              <Text style={styles.submitBtnText}>Salvar</Text>
            )}
          </Pressable>
        </ScrollView>
      </KeyboardAvoidingView>
    </SafeAreaView>
  );
}

const styles = StyleSheet.create({
  container: {
    flex: 1,
    backgroundColor: colors.background,
  },
  scroll: {
    padding: spacing.md,
    paddingBottom: spacing.xxl,
    gap: spacing.sm,
  },
  headerRow: {
    flexDirection: 'row',
    alignItems: 'center',
    justifyContent: 'space-between',
    marginBottom: spacing.md,
  },
  title: {
    fontSize: typography.fontSize.xl,
    fontWeight: typography.fontWeight.bold,
    color: colors.text,
  },
  label: {
    fontSize: typography.fontSize.sm,
    fontWeight: typography.fontWeight.semibold,
    color: colors.textSubtle,
    marginTop: spacing.md,
    marginBottom: spacing.xs,
  },
  input: {
    backgroundColor: colors.surface,
    borderRadius: borderRadius.md,
    borderWidth: 1,
    borderColor: colors.border,
    paddingHorizontal: spacing.md,
    paddingVertical: spacing.sm + 2,
    color: colors.text,
    fontSize: typography.fontSize.md,
  },
  categoryGrid: {
    flexDirection: 'row',
    flexWrap: 'wrap',
    gap: spacing.sm,
    marginTop: spacing.xs,
  },
  categoryChip: {
    flexDirection: 'row',
    alignItems: 'center',
    paddingHorizontal: spacing.md,
    paddingVertical: spacing.xs + 2,
    borderRadius: borderRadius.full,
    borderWidth: 1,
    borderColor: colors.border,
    backgroundColor: colors.surface,
  },
  categoryChipActive: {
    backgroundColor: colors.primary,
    borderColor: colors.primary,
  },
  categoryChipText: {
    color: colors.textMuted,
    fontSize: typography.fontSize.sm,
    fontWeight: typography.fontWeight.semibold,
  },
  categoryChipTextActive: {
    color: colors.white,
  },
  submitBtn: {
    backgroundColor: colors.primary,
    borderRadius: borderRadius.md,
    paddingVertical: spacing.md,
    alignItems: 'center',
    marginTop: spacing.lg,
  },
  submitBtnDisabled: {
    opacity: 0.6,
  },
  submitBtnText: {
    color: colors.white,
    fontSize: typography.fontSize.lg,
    fontWeight: typography.fontWeight.bold,
  },
});
