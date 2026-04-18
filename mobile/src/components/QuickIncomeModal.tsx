import React, { useState } from 'react';
import {
  Modal,
  View,
  Text,
  TextInput,
  TouchableOpacity,
  StyleSheet,
  KeyboardAvoidingView,
  Platform,
  Pressable,
} from 'react-native';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { budgetApiClient } from '@/services/apiClient';
import { useToast } from '@/components/Toast/useToast';
import { colors } from '@/theme';

interface QuickIncomeModalProps {
  visible: boolean;
  currentIncome?: number;
  onClose: () => void;
}

export function QuickIncomeModal({ visible, currentIncome, onClose }: QuickIncomeModalProps) {
  const [income, setIncome] = useState(currentIncome != null ? String(currentIncome) : '');
  const queryClient = useQueryClient();
  const { toast } = useToast();

  const mutation = useMutation({
    mutationFn: (grossIncome: number) =>
      budgetApiClient.updateIncome({ grossIncome }),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['dashboard'] });
      queryClient.invalidateQueries({ queryKey: ['budget'] });
      onClose();
    },
    onError: () => {
      toast.error('Não foi possível atualizar a renda. Tente novamente.');
    },
  });

  const handleSave = () => {
    const parsed = parseFloat(income.replace(',', '.'));
    if (isNaN(parsed) || parsed <= 0) {
      toast.error('Informe um valor de renda válido.');
      return;
    }
    mutation.mutate(parsed);
  };

  return (
    <Modal
      visible={visible}
      transparent
      animationType="slide"
      onRequestClose={onClose}
      accessibilityViewIsModal
    >
      <Pressable style={styles.backdrop} onPress={onClose} accessibilityRole="button" accessibilityLabel="Fechar modal">
        <KeyboardAvoidingView
          behavior={Platform.OS === 'ios' ? 'padding' : 'height'}
          style={styles.kav}
        >
          <Pressable style={styles.sheet} onPress={(e) => e.stopPropagation()}>
            <Text style={styles.title}>Atualizar renda mensal</Text>
            <Text style={styles.label}>Renda bruta (R$)</Text>
            <TextInput
              style={styles.input}
              value={income}
              onChangeText={setIncome}
              keyboardType="numeric"
              placeholder="Ex: 5000"
              placeholderTextColor={colors.placeholder}
              accessibilityLabel="Valor da renda mensal"
              returnKeyType="done"
              onSubmitEditing={handleSave}
              autoFocus
            />
            <View style={styles.actions}>
              <TouchableOpacity style={styles.cancelBtn} onPress={onClose} accessibilityRole="button">
                <Text style={styles.cancelText}>Cancelar</Text>
              </TouchableOpacity>
              <TouchableOpacity
                style={[styles.saveBtn, mutation.isPending && styles.saveBtnDisabled]}
                onPress={handleSave}
                disabled={mutation.isPending}
                accessibilityRole="button"
              >
                <Text style={styles.saveText}>
                  {mutation.isPending ? 'Salvando...' : 'Salvar'}
                </Text>
              </TouchableOpacity>
            </View>
          </Pressable>
        </KeyboardAvoidingView>
      </Pressable>
    </Modal>
  );
}

const styles = StyleSheet.create({
  backdrop: {
    flex: 1,
    backgroundColor: colors.overlay,
    justifyContent: 'flex-end',
  },
  kav: {
    width: '100%',
  },
  sheet: {
    backgroundColor: colors.surface,
    borderTopLeftRadius: 20,
    borderTopRightRadius: 20,
    padding: 24,
    paddingBottom: 40,
  },
  title: {
    fontSize: 18,
    fontWeight: '700',
    color: colors.text,
    marginBottom: 20,
  },
  label: {
    fontSize: 13,
    color: colors.textMuted,
    marginBottom: 8,
  },
  input: {
    backgroundColor: colors.background,
    borderWidth: 1,
    borderColor: colors.border,
    borderRadius: 10,
    paddingHorizontal: 16,
    paddingVertical: 14,
    fontSize: 18,
    color: colors.text,
    marginBottom: 24,
  },
  actions: {
    flexDirection: 'row',
    gap: 12,
  },
  cancelBtn: {
    flex: 1,
    paddingVertical: 14,
    borderRadius: 10,
    borderWidth: 1,
    borderColor: colors.border,
    alignItems: 'center',
  },
  cancelText: {
    color: colors.textMuted,
    fontSize: 15,
    fontWeight: '600',
  },
  saveBtn: {
    flex: 1,
    paddingVertical: 14,
    borderRadius: 10,
    backgroundColor: colors.primary,
    alignItems: 'center',
  },
  saveBtnDisabled: {
    opacity: 0.6,
  },
  saveText: {
    color: colors.white,
    fontSize: 15,
    fontWeight: '700',
  },
});
