// Manual transaction entry screen — allows the user to add an expense by hand,
// independent of OCR / push-notification parsing. Extends existing flows, does NOT replace.
import { getApiErrorMessage } from '@/services/apiError';
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
import { router, useFocusEffect } from 'expo-router';
import { Ionicons } from '@expo/vector-icons';
import { useQueryClient } from '@tanstack/react-query';
import { transactionsApiClient } from '@/services/apiClient';
import { PREDEFINED_CATEGORIES } from '@/modules/transactions/categories';
import { useCategories } from '@/modules/transactions/useCategories';
import { parseAmountText } from '@/utils/amount';
import { colors, spacing, typography, borderRadius } from '@/theme';

export default function NewTransactionScreen() {
  const queryClient = useQueryClient();
  const categories = useCategories();

  const [amountText, setAmountText] = useState('');
  const [description, setDescription] = useState('');
  const [merchant, setMerchant] = useState('');
  const [category, setCategory] = useState<string>(PREDEFINED_CATEGORIES[0].value);
  const [submitting, setSubmitting] = useState(false);

  useFocusEffect(
    React.useCallback(() => {
      if (!submitting) {
        setAmountText('');
        setDescription('');
        setMerchant('');
        setCategory(PREDEFINED_CATEGORIES[0].value);
      }
    }, [submitting])
  );

  const handleSubmit = async () => {
    // Mesma regra da API: maior que zero, até R$ 999.999.999,99 e no máximo duas casas decimais.
    const parsed = parseAmountText(amountText);
    if (!parsed.ok) {
      Alert.alert('Valor inválido', parsed.message);
      return;
    }
    const amount = parsed.value;
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
      Alert.alert('Erro', getApiErrorMessage(err, 'Não foi possível registrar a transação.'));
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
            <Pressable accessibilityLabel="Voltar" onPress={() => router.back()} hitSlop={12} accessibilityRole="button">
              <Ionicons name="arrow-back" size={24} color={colors.text} />
            </Pressable>
            <Text style={styles.title} accessibilityRole="header">Nova transação</Text>
            <View style={{ width: 24 }} />
          </View>

          <Text style={styles.label}>Valor (R$)</Text>
          <TextInput accessibilityLabel="Valor em reais"
            style={styles.input}
            keyboardType="decimal-pad"
            placeholder="0,00"
            placeholderTextColor={colors.placeholder}
            value={amountText}
            onChangeText={setAmountText}
            editable={!submitting}
          />

          <Text style={styles.label}>Descrição (opcional)</Text>
          <TextInput accessibilityLabel="Descrição"
            style={styles.input}
            placeholder="Ex: Mercado"
            placeholderTextColor={colors.placeholder}
            value={description}
            onChangeText={setDescription}
            editable={!submitting}
          />

          <Text style={styles.label}>Estabelecimento (opcional)</Text>
          <TextInput accessibilityLabel="Estabelecimento"
            style={styles.input}
            placeholder="Ex: Pão de Açúcar"
            placeholderTextColor={colors.placeholder}
            value={merchant}
            onChangeText={setMerchant}
            editable={!submitting}
          />

          <Text style={styles.label}>Categoria</Text>
          <View style={styles.categoryGrid}>
            {categories.map((c) => {
              const selected = category === c.value;
              return (
                <Pressable accessibilityLabel={`Categoria ${c.label}`}
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

          <Pressable accessibilityLabel="Salvar transação"
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
    minHeight: 44,
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
