// Troca de senha: pede a senha atual. O servidor valida a senha nova (uma só regra para todo o app) e
// devolve um novo par de tokens; os outros aparelhos saem da conta, este continua logado.
import React, { useState } from 'react';
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
import { authApiClient } from '@/services/apiClient';
import { getApiErrorMessage } from '@/services/apiError';
import { useSessionStore } from '@/state/sessionStore';
import { goToParent, resetOnFocus } from '@/navigation/resetOnFocus';
import { colors } from '@/theme';

function ChangePasswordScreen() {
  const [currentPassword, setCurrentPassword] = useState('');
  const [newPassword, setNewPassword] = useState('');
  const [confirmPassword, setConfirmPassword] = useState('');
  const [showPasswords, setShowPasswords] = useState(false);
  const [loading, setLoading] = useState(false);

  const handleSubmit = async () => {
    if (!currentPassword || !newPassword || !confirmPassword) {
      Alert.alert('Campos obrigatórios', 'Preencha a senha atual, a senha nova e a confirmação.');
      return;
    }
    if (newPassword !== confirmPassword) {
      Alert.alert('Senhas diferentes', 'A senha nova e a confirmação não conferem.');
      return;
    }

    setLoading(true);
    try {
      const { data } = await authApiClient.changePassword({ currentPassword, newPassword });
      const session = useSessionStore.getState();
      await session.setTokens(data.accessToken, data.refreshToken || session.refreshToken || '');
      // As senhas não ficam na memória da tela depois de usadas (ela continua montada como aba oculta).
      setCurrentPassword('');
      setNewPassword('');
      setConfirmPassword('');
      setShowPasswords(false);
      Alert.alert('Senha alterada', 'Sua senha foi alterada. Nos outros aparelhos será preciso entrar de novo.', [
        { text: 'OK', onPress: () => goToParent('settings/change-password') },
      ]);
    } catch (err) {
      // Senha atual errada e senha nova fora da regra voltam da API com a mensagem exata do que falta.
      Alert.alert('Não foi possível alterar', getApiErrorMessage(err, 'Erro ao alterar a senha. Tente novamente.'));
    } finally {
      setLoading(false);
    }
  };

  return (
    <SafeAreaView style={styles.container}>
      <KeyboardAvoidingView style={{ flex: 1 }} behavior={Platform.OS === 'android' ? 'height' : 'padding'}>
        <ScrollView contentContainerStyle={styles.content} keyboardShouldPersistTaps="handled">
          <TouchableOpacity onPress={() => goToParent('settings/change-password')} accessibilityRole="button" accessibilityLabel="Voltar">
            <Text style={styles.back}>← Voltar</Text>
          </TouchableOpacity>
          <Text style={styles.title} accessibilityRole="header">Alterar senha</Text>
          <Text style={styles.hint}>
            A senha nova precisa ter pelo menos 8 caracteres, com letras e números, e não pode ser uma senha comum.
          </Text>

          <Text style={styles.label}>Senha atual</Text>
          <TextInput accessibilityLabel="Senha atual"
            style={styles.input}
            value={currentPassword}
            onChangeText={setCurrentPassword}
            secureTextEntry={!showPasswords}
            autoCapitalize="none"
            autoCorrect={false}
            editable={!loading}
            placeholder="Sua senha atual"
            placeholderTextColor={colors.placeholder}
          />

          <Text style={styles.label}>Senha nova</Text>
          <TextInput accessibilityLabel="Senha nova"
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
          <TextInput accessibilityLabel="Confirmar a senha nova"
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

          <TouchableOpacity style={{ minHeight: 44, justifyContent: 'center' }}
            onPress={() => setShowPasswords((v) => !v)}
            accessibilityRole="button"
            accessibilityLabel={showPasswords ? 'Ocultar senhas' : 'Mostrar senhas'}
          >
            <Text style={styles.toggle}>{showPasswords ? 'Ocultar senhas' : 'Mostrar senhas'}</Text>
          </TouchableOpacity>

          <View style={styles.spacer} />
          <TouchableOpacity
            style={[styles.button, loading && styles.disabled]}
            onPress={handleSubmit}
            disabled={loading}
            accessibilityRole="button"
            accessibilityLabel="Alterar senha"
          >
            {loading ? <ActivityIndicator color={colors.text} /> : <Text style={styles.buttonText}>Alterar senha</Text>}
          </TouchableOpacity>
        </ScrollView>
      </KeyboardAvoidingView>
    </SafeAreaView>
  );
}

// Sair da tela (ou voltar a ela) descarta o que foi digitado: senha atual, senha nova e "mostrar senhas".
export default resetOnFocus(ChangePasswordScreen);

const styles = StyleSheet.create({
  container: { flex: 1, backgroundColor: colors.background },
  content: { paddingHorizontal: 20, paddingTop: 24, paddingBottom: 40 },
  back: { minHeight: 44, textAlignVertical: 'center', color: colors.primaryLight, fontSize: 15, marginBottom: 16 },
  title: { fontSize: 26, fontWeight: '700', color: colors.text, marginBottom: 8 },
  hint: { fontSize: 14, color: colors.textMuted, marginBottom: 20, lineHeight: 20 },
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
  toggle: { color: colors.primaryLight, fontSize: 14, marginTop: 16 },
  spacer: { height: 24 },
  button: { backgroundColor: colors.primary, borderRadius: 12, paddingVertical: 16, alignItems: 'center' },
  buttonText: { color: colors.text, fontSize: 16, fontWeight: '700' },
  disabled: { opacity: 0.6 },
});
