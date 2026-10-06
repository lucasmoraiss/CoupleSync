// Confirmação de e-mail: o código de 6 dígitos vai por e-mail no cadastro; aqui o usuário digita ou pede outro.
// Não bloqueia nada: a conta funciona por inteiro sem confirmar.
import React, { useEffect, useState } from 'react';
import {
  ActivityIndicator,
  Alert,
  KeyboardAvoidingView,
  Platform,
  SafeAreaView,
  ScrollView,
  StyleSheet,
  Text,
  TextInput,
  TouchableOpacity,
  View,
} from 'react-native';
import { router } from 'expo-router';
import { authApiClient } from '@/services/apiClient';
import {
  CODE_LENGTH,
  RESEND_COOLDOWN_SECONDS,
  VERIFY_UNAVAILABLE_MESSAGE,
  getEmailFlowErrorMessage,
  isCompleteCode,
  isEmailNotConfigured,
  normalizeCode,
} from '@/services/emailCodes';
import { queryClient } from '@/services/queryClient';
import { ME_QUERY_KEY } from '@/components/EmailVerificationBanner';
import { colors } from '@/theme';

export default function VerifyEmailScreen() {
  const [code, setCode] = useState('');
  const [loading, setLoading] = useState(false);
  const [unavailable, setUnavailable] = useState(false);
  const [cooldown, setCooldown] = useState(0);

  useEffect(() => {
    if (cooldown <= 0) return;
    const timer = setTimeout(() => setCooldown((s) => s - 1), 1000);
    return () => clearTimeout(timer);
  }, [cooldown]);

  const handleConfirm = async () => {
    if (!isCompleteCode(code)) {
      Alert.alert('Código incompleto', `Digite o código de ${CODE_LENGTH} dígitos que enviamos por e-mail.`);
      return;
    }
    setLoading(true);
    try {
      await authApiClient.confirmEmail(code);
      await queryClient.invalidateQueries({ queryKey: ME_QUERY_KEY });
      Alert.alert('E-mail confirmado', 'Obrigado! Seu e-mail foi confirmado.', [{ text: 'OK', onPress: () => router.back() }]);
    } catch (err) {
      if (isEmailNotConfigured(err)) setUnavailable(true);
      Alert.alert(
        'Não foi possível confirmar',
        getEmailFlowErrorMessage(err, 'Erro ao confirmar o e-mail. Tente novamente.', VERIFY_UNAVAILABLE_MESSAGE),
      );
    } finally {
      setLoading(false);
    }
  };

  const handleResend = async () => {
    if (cooldown > 0 || loading) return;
    setLoading(true);
    try {
      await authApiClient.resendEmailVerification();
      setUnavailable(false);
      setCooldown(RESEND_COOLDOWN_SECONDS);
      Alert.alert('Código enviado', 'Enviamos um novo código para o seu e-mail. O anterior deixa de valer.');
    } catch (err) {
      if (isEmailNotConfigured(err)) setUnavailable(true);
      Alert.alert(
        'Não foi possível enviar',
        getEmailFlowErrorMessage(err, 'Erro ao reenviar o código. Tente novamente.', VERIFY_UNAVAILABLE_MESSAGE),
      );
    } finally {
      setLoading(false);
    }
  };

  return (
    <SafeAreaView style={styles.container}>
      <KeyboardAvoidingView style={{ flex: 1 }} behavior={Platform.OS === 'android' ? 'height' : 'padding'}>
        <ScrollView contentContainerStyle={styles.content} keyboardShouldPersistTaps="handled">
          <TouchableOpacity onPress={() => router.back()} accessibilityRole="button" accessibilityLabel="Voltar">
            <Text style={styles.back}>← Voltar</Text>
          </TouchableOpacity>
          <Text style={styles.title} accessibilityRole="header">Confirmar e-mail</Text>
          <Text style={styles.hint}>
            Digite o código de 6 dígitos que enviamos para o seu e-mail (vale por 15 minutos). Você pode usar o app normalmente
            mesmo sem confirmar.
          </Text>

          {unavailable && (
            <View style={styles.notice} accessibilityRole="alert">
              <Text style={styles.noticeText}>{VERIFY_UNAVAILABLE_MESSAGE}</Text>
            </View>
          )}

          <Text style={styles.label}>Código</Text>
          <TextInput
            style={styles.input}
            value={code}
            onChangeText={(text) => setCode(normalizeCode(text))}
            keyboardType="number-pad"
            maxLength={CODE_LENGTH + 2}
            autoCorrect={false}
            editable={!loading}
            placeholder="000000"
            placeholderTextColor={colors.placeholder}
            accessibilityLabel="Código de 6 dígitos"
          />

          <View style={styles.spacer} />
          <TouchableOpacity
            style={[styles.button, loading && styles.disabled]}
            onPress={handleConfirm}
            disabled={loading}
            accessibilityRole="button"
            accessibilityLabel="Confirmar e-mail"
          >
            {loading ? <ActivityIndicator color={colors.text} /> : <Text style={styles.buttonText}>Confirmar</Text>}
          </TouchableOpacity>

          <TouchableOpacity
            style={styles.link}
            onPress={handleResend}
            disabled={cooldown > 0 || loading}
            accessibilityRole="button"
            accessibilityLabel="Reenviar código"
          >
            <Text style={[styles.linkText, (cooldown > 0 || loading) && styles.linkDisabled]}>
              {cooldown > 0 ? `Reenviar código (${cooldown}s)` : 'Reenviar código'}
            </Text>
          </TouchableOpacity>
        </ScrollView>
      </KeyboardAvoidingView>
    </SafeAreaView>
  );
}

const styles = StyleSheet.create({
  container: { flex: 1, backgroundColor: colors.background },
  content: { paddingHorizontal: 20, paddingTop: 24, paddingBottom: 40 },
  back: { minHeight: 44, textAlignVertical: 'center', color: colors.primaryLight, fontSize: 15, marginBottom: 16 },
  title: { fontSize: 26, fontWeight: '700', color: colors.text, marginBottom: 8 },
  hint: { fontSize: 14, color: colors.textMuted, marginBottom: 20, lineHeight: 20 },
  notice: {
    backgroundColor: colors.surface,
    borderWidth: 1,
    borderColor: colors.warning,
    borderRadius: 12,
    padding: 12,
    marginBottom: 16,
  },
  noticeText: { color: colors.text, fontSize: 14, lineHeight: 20 },
  label: { fontSize: 14, color: colors.textMuted, marginBottom: 6, marginTop: 12 },
  input: {
    backgroundColor: colors.surface,
    borderWidth: 1,
    borderColor: colors.border,
    borderRadius: 12,
    paddingHorizontal: 16,
    paddingVertical: 14,
    fontSize: 22,
    letterSpacing: 6,
    textAlign: 'center',
    color: colors.text,
  },
  spacer: { height: 24 },
  button: { backgroundColor: colors.primary, borderRadius: 12, paddingVertical: 16, alignItems: 'center' },
  buttonText: { color: colors.text, fontSize: 16, fontWeight: '700' },
  disabled: { opacity: 0.6 },
  link: { minHeight: 44, textAlignVertical: 'center', alignItems: 'center', marginTop: 20, paddingVertical: 8 },
  linkText: { color: colors.primaryLight, fontSize: 14, fontWeight: '600' },
  linkDisabled: { color: colors.textDisabled },
});
