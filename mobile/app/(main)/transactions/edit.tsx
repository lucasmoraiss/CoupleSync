// Edição de uma transação do grupo: valor, descrição, data/hora (Brasília) e categoria.
// Recebe os valores atuais pela rota (a API não tem leitura por id) e envia só o que mudou.
// A regra de "de qual transação são estes campos" e "o que mudou" está em modules/transactions/editForm.ts.
import { getApiErrorMessage } from '@/services/apiError';
import React, { useMemo, useState } from 'react';
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
import { useLocalSearchParams } from 'expo-router';
import { Ionicons } from '@expo/vector-icons';
import { useQueryClient } from '@tanstack/react-query';
import { transactionsApiClient, isCoupleRequiredError } from '@/services/apiClient';
import {
  buildEditSubmission,
  createEditForm,
  formFor,
  originalFromParams,
  type EditFormState,
} from '@/modules/transactions/editForm';
import { goToParent, resetOnFocus } from '@/navigation/resetOnFocus';
import { useCategories } from '@/modules/transactions/useCategories';
import { colors, spacing, typography, borderRadius } from '@/theme';

type EditParams = {
  id: string;
  amount: string;
  description?: string;
  merchant?: string;
  category: string;
  eventTimestampUtc: string;
};

function EditTransactionScreen() {
  const params = useLocalSearchParams<EditParams>();
  const queryClient = useQueryClient();
  const categories = useCategories();

  // A transação que a rota manda AGORA. A tela é uma aba oculta e continua montada entre visitas.
  const original = useMemo(
    () => originalFromParams(params),
    [params.id, params.amount, params.description, params.merchant, params.category, params.eventTimestampUtc],
  );

  // Os campos pertencem a uma transação (ver editForm.ts). formFor() descarta, já na renderização, um formulário
  // que seja de outra transação: os valores de uma nunca aparecem nem são enviados para outra.
  const [storedForm, setStoredForm] = useState<EditFormState>(() => createEditForm(original));
  const form = formFor(storedForm, original);
  const [submitting, setSubmitting] = useState(false);

  const setField = <K extends 'amountText' | 'merchant' | 'description' | 'dateText' | 'timeText' | 'category'>(
    field: K,
    value: EditFormState[K],
  ) => setStoredForm((previous) => ({ ...formFor(previous, original), [field]: value }));

  const handleSubmit = async () => {
    const submission = buildEditSubmission(form, original);
    if (submission.kind === 'stale') {
      // Não deveria acontecer (formFor já alinhou o formulário); se acontecer, nada é enviado.
      setStoredForm(createEditForm(original));
      Alert.alert('Transação', 'Os dados desta transação foram recarregados. Confira e salve de novo.');
      return;
    }
    if (submission.kind === 'invalid') {
      Alert.alert(submission.title, submission.message);
      return;
    }
    if (submission.kind === 'unchanged') {
      goToParent('transactions/edit');
      return;
    }

    setSubmitting(true);
    try {
      await transactionsApiClient.update(submission.id, submission.body);

      await Promise.all([
        queryClient.invalidateQueries({ queryKey: ['transactions'] }),
        queryClient.invalidateQueries({ queryKey: ['dashboard'] }),
        queryClient.invalidateQueries({ queryKey: ['reports'] }),
        queryClient.invalidateQueries({ queryKey: ['budget'] }),
        queryClient.invalidateQueries({ queryKey: ['goals'] }),
      ]);

      // Sempre para a lista de transações, onde a alteração aparece (não depende do histórico de abas).
      goToParent('transactions/edit');
    } catch (err: any) {
      if (isCoupleRequiredError(err)) return;
      Alert.alert('Erro', getApiErrorMessage(err, 'Não foi possível salvar a transação.'));
    } finally {
      setSubmitting(false);
    }
  };

  const { amountText, merchant, description, dateText, timeText, category } = form;

  return (
    <SafeAreaView style={styles.container} edges={['top']}>
      <KeyboardAvoidingView
        behavior={Platform.OS === 'ios' ? 'padding' : undefined}
        style={{ flex: 1 }}
      >
        <ScrollView contentContainerStyle={styles.scroll} keyboardShouldPersistTaps="handled">
          <View style={styles.headerRow}>
            <Pressable onPress={() => goToParent('transactions/edit')} hitSlop={12} accessibilityRole="button" accessibilityLabel="Voltar">
              <Ionicons name="arrow-back" size={24} color={colors.text} />
            </Pressable>
            <Text style={styles.title} accessibilityRole="header">Editar transação</Text>
            <View style={{ width: 24 }} />
          </View>

          <Text style={styles.label}>Valor (R$)</Text>
          <TextInput accessibilityLabel="Valor em reais"
            style={styles.input}
            keyboardType="decimal-pad"
            placeholder="0,00"
            placeholderTextColor={colors.placeholder}
            value={amountText}
            onChangeText={(text) => setField('amountText', text)}
            editable={!submitting}
          />

          <Text style={styles.label}>Estabelecimento</Text>
          <TextInput accessibilityLabel="Estabelecimento"
            style={styles.input}
            placeholder="Ex: Pão de Açúcar"
            placeholderTextColor={colors.placeholder}
            value={merchant}
            onChangeText={(text) => setField('merchant', text)}
            editable={!submitting}
            maxLength={512}
          />

          <Text style={styles.label}>Descrição</Text>
          <TextInput accessibilityLabel="Descrição"
            style={styles.input}
            placeholder="Ex: Mercado"
            placeholderTextColor={colors.placeholder}
            value={description}
            onChangeText={(text) => setField('description', text)}
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
              onChangeText={(text) => setField('dateText', text)}
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
              onChangeText={(text) => setField('timeText', text)}
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
                <Pressable accessibilityLabel={`Categoria ${c.label}`}
                  key={c.value}
                  style={[styles.categoryChip, selected && styles.categoryChipActive]}
                  onPress={() => setField('category', c.value)}
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

          <Pressable accessibilityLabel="Salvar alterações"
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

// Remontada a cada visita e a cada transação: os campos começam sempre dos valores da transação aberta agora.
export default resetOnFocus(EditTransactionScreen);

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
