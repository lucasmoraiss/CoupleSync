// Esqueci minha senha: primeiro pede o código por e-mail; depois recebe o código e a senha nova.
// O servidor responde igual exista ou não a conta, então a tela nunca diz se o e-mail está cadastrado.
// Redefinir a senha encerra a sessão em todos os aparelhos: ao terminar, volta para o login.
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
  RESET_UNAVAILABLE_MESSAGE,
  getEmailFlowErrorMessage,
  isCompleteCode,
  isEmailNotConfigured,
  normalizeCode,
} from '@/services/emailCodes';
import { colors } from '@/theme';

type Step = 'request' | 'reset';

export default function ForgotPasswordScreen() {
  const [step, setStep] = useState<Step>('request');
  const [email, setEmail] = useState('');
  const [code, setCode] = useState('');
  const [newPassword, setNewPassword] = useState('');
  const [confirmPassword, setConfirmPassword] = useState('');
  const [showPasswords, setShowPasswords] = useState(false);
  const [loading, setLoading] = useState(false);
  const [unavailable, setUnavailable] = useState(false);
  const [cooldown, setCooldown] = useState(0);

  useEffect(() => {
    if (cooldown <= 0) return;
    const timer = setTimeout(() => setCooldown((s) => s - 1), 1000);
    return () => clearTimeout(timer);
  }, [cooldown]);

  const requestCode = async (): Promise<boolean> => {
    if (!email.trim()) {
      Alert.alert('E-mail obrigatório', 'Informe o e-mail da sua conta.');
      return false;
    }
    setLoading(true);
    try {
      await authApiClient.forgotPassword(email.trim());
      setUnavailable(false);
      setCooldown(RESEND_COOLDOWN_SECONDS);
      return true;
    } catch (err) {
      if (isEmailNotConfigured(err)) setUnavailable(true);
      Alert.alert(
        'Não foi possível enviar',
        getEmailFlowErrorMessage(err, 'Erro ao pedir o código. Tente novamente.', RESET_UNAVAILABLE_MESSAGE),
      );
      return false;
    } finally {
      setLoading(false);
    }
  };

  const handleRequest = async () => {
    if (await requestCode()) setStep('reset');
  };

  const handleResend = async () => {
    if (cooldown > 0 || loading) return;
    if (await requestCode()) {
      Alert.alert('Código reenviado', 'Se o e-mail estiver cadastrado, você receberá um novo código. O anterior deixa de valer.');
    }
  };

  const handleReset = async () => {
    if (!isCompleteCode(code)) {
      Alert.alert('Código incompleto', `Digite o código de ${CODE_LENGTH} dígitos que enviamos por e-mail.`);
      return;
    }
    if (!newPassword || !confirmPassword) {
      Alert.alert('Campos obrigatórios', 'Preencha a senha nova e a confirmação.');
      return;
    }
    if (newPassword !== confirmPassword) {
      Alert.alert('Senhas diferentes', 'A senha nova e a confirmação não conferem.');
      return;
    }

    setLoading(true);
    try {
      await authApiClient.resetPassword({ email: email.trim(), code, newPassword });
      Alert.alert('Senha redefinida', 'Pronto! Entre com a senha nova. Em todos os aparelhos será preciso entrar de novo.', [
        { text: 'OK', onPress: () => router.replace('/login' as any) },
      ]);
    } catch (err) {
      // Código errado/vencido e senha fora da regra voltam da API com a mensagem exata.
      Alert.alert(
        'Não foi possível redefinir',
        getEmailFlowErrorMessage(err, 'Erro ao redefinir a senha. Tente novamente.', RESET_UNAVAILABLE_MESSAGE),
      );
    } finally {
      setLoading(false);
    }
  };

  return (
    <SafeAreaView style={styles.container}>
      <KeyboardAvoidingView style={{ flex: 1 }} behavior={Platform.OS === 'android' ? 'height' : 'padding'}>
        <ScrollView contentContainerStyle={styles.content} keyboardShouldPersistTaps="handled">
          <TouchableOpacity
            onPress={() => (step === 'reset' ? setStep('request') : router.back())}
            accessibilityRole="button"
            accessibilityLabel="Voltar"
          >
            <Text style={styles.back}>← Voltar</Text>
          </TouchableOpacity>

          <Text style={styles.title}>Esqueci minha senha</Text>

          {unavailable && (
            <View style={styles.notice} accessibilityRole="alert">
              <Text style={styles.noticeText}>{RESET_UNAVAILABLE_MESSAGE}</Text>
            </View>
          )}

          {step === 'request' ? (
            <>
              <Text style={styles.hint}>Informe o e-mail da sua conta. Se ele estiver cadastrado, enviaremos um código de 6 dígitos.</Text>
              <Text style={styles.label}>E-mail</Text>
              <TextInput
                style={styles.input}
                value={email}
                onChangeText={setEmail}
                keyboardType="email-address"
                autoCapitalize="none"
                autoCorrect={false}
                editable={!loading}
                placeholder="seu@email.com"
                placeholderTextColor={colors.placeholder}
              />
              <View style={styles.spacer} />
              <TouchableOpacity
                style={[styles.button, loading && styles.disabled]}
                onPress={handleRequest}
                disabled={loading}
                accessibilityRole="button"
                accessibilityLabel="Enviar código"
              >
                {loading ? <ActivityIndicator color={colors.text} /> : <Text style={styles.buttonText}>Enviar código</Text>}
              </TouchableOpacity>
              <TouchableOpacity style={styles.link} onPress={() => setStep('reset')} accessibilityRole="button">
                <Text style={styles.linkText}>Já tenho um código</Text>
              </TouchableOpacity>
            </>
          ) : (
            <>
              <Text style={styles.hint}>
                Digite o código de 6 dígitos enviado para o seu e-mail (vale por 15 minutos) e escolha a senha nova. A senha precisa
                ter pelo menos 8 caracteres, com letras e números.
              </Text>

              <Text style={styles.label}>E-mail</Text>
              <TextInput
                style={styles.input}
                value={email}
                onChangeText={setEmail}
                keyboardType="email-address"
                autoCapitalize="none"
                autoCorrect={false}
                editable={!loading}
                placeholder="seu@email.com"
                placeholderTextColor={colors.placeholder}
              />

              <Text style={styles.label}>Código</Text>
              <TextInput
                style={[styles.input, styles.codeInput]}
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

              <Text style={styles.label}>Senha nova</Text>
              <TextInput
                style={styles.input}
                value={newPassword}
                onChangeText={setNewPassword}
                secureTextEntry={!showPasswords}
                autoCapitalize="none"
                autoCorrect={false}
                editable={!loading}
                placeholder="Mínimo 8 caracteres, com letras e números"
                placeholderTextColor={colors.placeholder}
              />

              <Text style={styles.label}>Confirmar senha nova</Text>
              <TextInput
                style={styles.input}
                value={confirmPassword}
                onChangeText={setConfirmPassword}
                secureTextEntry={!showPasswords}
                autoCapitalize="none"
                autoCorrect={false}
                editable={!loading}
                placeholder="Repita a senha nova"
                placeholderTextColor={colors.placeholder}
              />

              <TouchableOpacity
                onPress={() => setShowPasswords((v) => !v)}
                accessibilityRole="button"
                accessibilityLabel={showPasswords ? 'Ocultar senhas' : 'Mostrar senhas'}
              >
                <Text style={styles.toggle}>{showPasswords ? 'Ocultar senhas' : 'Mostrar senhas'}</Text>
              </TouchableOpacity>

              <View style={styles.spacer} />
              <TouchableOpacity
                style={[styles.button, loading && styles.disabled]}
                onPress={handleReset}
                disabled={loading}
                accessibilityRole="button"
                accessibilityLabel="Redefinir senha"
              >
                {loading ? <ActivityIndicator color={colors.text} /> : <Text style={styles.buttonText}>Redefinir senha</Text>}
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
            </>
          )}
        </ScrollView>
      </KeyboardAvoidingView>
    </SafeAreaView>
  );
}

const styles = StyleSheet.create({
  container: { flex: 1, backgroundColor: colors.background },
  content: { paddingHorizontal: 20, paddingTop: 24, paddingBottom: 40 },
  back: { color: colors.primaryLight, fontSize: 15, marginBottom: 16 },
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
    fontSize: 16,
    color: colors.text,
  },
  codeInput: { fontSize: 22, letterSpacing: 6, textAlign: 'center' },
  toggle: { color: colors.primaryLight, fontSize: 14, marginTop: 16 },
  spacer: { height: 24 },
  button: { backgroundColor: colors.primary, borderRadius: 12, paddingVertical: 16, alignItems: 'center' },
  buttonText: { color: colors.text, fontSize: 16, fontWeight: '700' },
  disabled: { opacity: 0.6 },
  link: { alignItems: 'center', marginTop: 20, paddingVertical: 8 },
  linkText: { color: colors.primaryLight, fontSize: 14, fontWeight: '600' },
  linkDisabled: { color: colors.textDisabled },
});
