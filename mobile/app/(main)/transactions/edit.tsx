// Edição de uma transação do grupo: valor, descrição, data/hora (Brasília) e categoria.
// Recebe os valores atuais pela rota (a API não tem leitura por id) e envia só o que mudou.
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
import { router, useLocalSearchParams } from 'expo-router';
import { Ionicons } from '@expo/vector-icons';
import { useQueryClient } from '@tanstack/react-query';
import { transactionsApiClient, isCoupleRequiredError } from '@/services/apiClient';
import type { UpdateTransactionBody } from '@/services/apiClient';
import { toCategoryKey } from '@/modules/transactions/categories';
import { useCategories } from '@/modules/transactions/useCategories';
import { parseAmountText } from '@/utils/amount';
import { formatBrazilDate, formatBrazilTime, parseBrazilDateTime } from '@/utils/brazilDateTime';
import { colors, spacing, typography, borderRadius } from '@/theme';

function formatAmountText(amount: number): string {
  return amount.toFixed(2).replace('.', ',');
}

export default function EditTransactionScreen() {
  const params = useLocalSearchParams<{
    id: string;
    amount: string;
    description?: string;
    category: string;
    eventTimestampUtc: string;
  }>();
  const queryClient = useQueryClient();
  const categories = useCategories();

  const original = {
    amount: Number(params.amount),
    description: params.description ?? '',
    category: toCategoryKey(params.category ?? '') ?? params.category ?? '',
    eventTimestampUtc: params.eventTimestampUtc ?? new Date().toISOString(),
  };
  const originalDate = formatBrazilDate(original.eventTimestampUtc);
  const originalTime = formatBrazilTime(original.eventTimestampUtc);

  const [amountText, setAmountText] = useState(formatAmountText(original.amount));
  const [description, setDescription] = useState(original.description);
  const [dateText, setDateText] = useState(originalDate);
  const [timeText, setTimeText] = useState(originalTime);
  const [category, setCategory] = useState<string>(original.category);
  const [submitting, setSubmitting] = useState(false);

  const handleSubmit = async () => {
    const body: UpdateTransactionBody = {};

    const parsed = parseAmountText(amountText);
    if (!parsed.ok) {
      Alert.alert('Valor inválido', parsed.message);
      return;
    }
    if (parsed.value !== original.amount) body.amount = parsed.value;

    if (description.trim() !== original.description.trim()) body.description = description.trim();

    if (dateText.trim() !== originalDate || timeText.trim() !== originalTime) {
      const when = parseBrazilDateTime(dateText, timeText);
      if (!when.ok) {
        Alert.alert('Data inválida', when.message);
        return;
      }
      body.eventTimestampUtc = when.iso;
    }

    if (category !== original.category) body.category = category;

    if (Object.keys(body).length === 0) {
      router.back();
      return;
    }

    setSubmitting(true);
    try {
      await transactionsApiClient.update(params.id, body);

      await Promise.all([
        queryClient.invalidateQueries({ queryKey: ['transactions'] }),
        queryClient.invalidateQueries({ queryKey: ['dashboard'] }),
        queryClient.invalidateQueries({ queryKey: ['reports'] }),
        queryClient.invalidateQueries({ queryKey: ['budget'] }),
        queryClient.invalidateQueries({ queryKey: ['goals'] }),
      ]);

      router.back();
    } catch (err: any) {
      if (isCoupleRequiredError(err)) return;
      Alert.alert('Erro', getApiErrorMessage(err, 'Não foi possível salvar a transação.'));
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
            <Pressable onPress={() => router.back()} hitSlop={12} accessibilityRole="button" accessibilityLabel="Voltar">
              <Ionicons name="arrow-back" size={24} color={colors.text} />
            </Pressable>
            <Text style={styles.title}>Editar transação</Text>
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

          <Text style={styles.label}>Descrição</Text>
          <TextInput
            style={styles.input}
            placeholder="Ex: Mercado"
            placeholderTextColor={colors.placeholder}
            value={description}
            onChangeText={setDescription}
            editable={!submitting}
            maxLength={512}
          />

          <Text style={styles.label}>Data e hora (horário de Brasília)</Text>
          <View style={styles.dateRow}>
            <TextInput
              style={[styles.input, styles.dateInput]}
              keyboardType="numbers-and-punctuation"
              placeholder="dd/mm/aaaa"
              placeholderTextColor={colors.placeholder}
              value={dateText}
              onChangeText={setDateText}
              editable={!submitting}
              maxLength={10}
              accessibilityLabel="Data"
            />
            <TextInput
              style={[styles.input, styles.timeInput]}
              keyboardType="numbers-and-punctuation"
              placeholder="hh:mm"
              placeholderTextColor={colors.placeholder}
              value={timeText}
              onChangeText={setTimeText}
              editable={!submitting}
              maxLength={5}
              accessibilityLabel="Hora"
            />
          </View>

          <Text style={styles.label}>Categoria</Text>
          <View style={styles.categoryGrid}>
            {categories.map((c) => {
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
              <Text style={styles.submitBtnText}>Salvar alterações</Text>
            )}
          </Pressable>
        </ScrollView>
      </KeyboardAvoidingView>
    </SafeAreaView>
  );
}

const styles = StyleSheet.create({
  container: { flex: 1, backgroundColor: colors.background },
  scroll: { padding: spacing.md, paddingBottom: spacing.xxl, gap: spacing.sm },
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
  dateRow: { flexDirection: 'row', gap: spacing.sm },
  dateInput: { flex: 2 },
  timeInput: { flex: 1 },
  categoryGrid: { flexDirection: 'row', flexWrap: 'wrap', gap: spacing.sm, marginTop: spacing.xs },
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
  categoryChipActive: { backgroundColor: colors.primary, borderColor: colors.primary },
  categoryChipText: {
    color: colors.textMuted,
    fontSize: typography.fontSize.sm,
    fontWeight: typography.fontWeight.semibold,
  },
  categoryChipTextActive: { color: colors.white },
  submitBtn: {
    backgroundColor: colors.primary,
    borderRadius: borderRadius.md,
    paddingVertical: spacing.md,
    alignItems: 'center',
    marginTop: spacing.lg,
  },
  submitBtnDisabled: { opacity: 0.6 },
  submitBtnText: {
    color: colors.white,
    fontSize: typography.fontSize.lg,
    fontWeight: typography.fontWeight.bold,
  },
});
