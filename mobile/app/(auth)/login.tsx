import React, { useState } from 'react';
import {
  View,
  Text,
  TextInput,
  TouchableOpacity,
  StyleSheet,
  KeyboardAvoidingView,
  Platform,
  ActivityIndicator,
  Alert,
  ScrollView,
} from 'react-native';
import { Ionicons } from '@expo/vector-icons';
import { router } from 'expo-router';
import axios from 'axios';
import { authApiClient, coupleApiClient } from '@/services/apiClient';
import { useSessionStore } from '@/state/sessionStore';
import { colors } from '@/theme';

export default function LoginScreen() {
  const [email, setEmail] = useState('');
  const [password, setPassword] = useState('');
  const [loading, setLoading] = useState(false);
  const [showPassword, setShowPassword] = useState(false);

  const handleLogin = async () => {
    if (!email.trim() || !password.trim()) {
      Alert.alert('Campos obrigatórios', 'Preencha e-mail e senha.');
      return;
    }

    setLoading(true);
    try {
      const { data } = await authApiClient.login({ email: email.trim(), password });
      
      // Try to get couple info — user may not have one yet
      let coupleId: string | null = null;
      try {
        // Need to set session first so the token is available for the couple request
        await useSessionStore.getState().setSession(
          data.accessToken,
          data.refreshToken,
          data.user.id,
          null
        );
        const coupleRes = await coupleApiClient.getMyCouple();
        coupleId = coupleRes.data.coupleId;
        await useSessionStore.getState().setCoupleId(coupleId);
      } catch {
        // No couple yet — that's fine
      }

      if (coupleId) {
        router.replace('/' as any);
      } else {
        router.replace('/couple-setup' as any);
      }
    } catch (err: any) {
      let msg: string;
      if (err.code === 'ECONNABORTED' || err.code === 'ETIMEDOUT') {
        msg = 'Tempo esgotado. Tente novamente.';
      } else if (axios.isAxiosError(err) && err.response) {
        const status = err.response.status;
        if (status === 401) {
          msg = 'E-mail ou senha incorretos.';
        } else if (status >= 500 && status < 600) {
          msg = 'Servidor com problemas. Tente novamente mais tarde.';
        } else {
          msg = 'Erro inesperado. Tente novamente.';
        }
      } else if (err.request) {
        msg = 'Servidor indisponível. Verifique a conexão com o servidor.';
      } else {
        msg = 'Sem conexão. Verifique sua internet.';
      }
      if (__DEV__) console.log('[Login] Error:', { code: err?.code, status: err?.response?.status, message: err?.response?.data?.message ?? err?.message });
      Alert.alert('Erro', msg);
    } finally {
      setLoading(false);
    }
  };

  return (
    <KeyboardAvoidingView
      style={styles.container}
      behavior={Platform.OS === 'android' ? 'height' : 'padding'}
    >
      <ScrollView
        contentContainerStyle={styles.scrollContent}
        keyboardShouldPersistTaps="handled"
      >
        <View style={styles.header}>
          <Text style={styles.logo}>💰</Text>
          <Text style={styles.appName}>CoupleSync</Text>
          <Text style={styles.subtitle}>Finanças a dois, simplificadas</Text>
        </View>

        <View style={styles.form}>
          <Text style={styles.label}>E-mail</Text>
          <TextInput
            style={styles.input}
            placeholder="seu@email.com"
            placeholderTextColor={colors.placeholder}
            value={email}
            onChangeText={setEmail}
            keyboardType="email-address"
            autoCapitalize="none"
            autoCorrect={false}
            editable={!loading}
          />

          <Text style={styles.label}>Senha</Text>
          <View style={styles.inputWrapper}>
            <TextInput
              style={styles.inputField}
              placeholder="••••••••"
              placeholderTextColor={colors.placeholder}
              value={password}
              onChangeText={setPassword}
              secureTextEntry={!showPassword}
              editable={!loading}
            />
            <TouchableOpacity
              onPress={() => setShowPassword(!showPassword)}
              style={styles.eyeButton}
              hitSlop={{ top: 8, bottom: 8, left: 8, right: 8 }}
            >
              <Ionicons name={showPassword ? 'eye-off' : 'eye'} size={22} color={colors.textMuted} />
            </TouchableOpacity>
          </View>

          <TouchableOpacity
            style={[styles.button, loading && styles.buttonDisabled]}
            onPress={handleLogin}
            disabled={loading}
            activeOpacity={0.8}
          >
            {loading ? (
              <ActivityIndicator color={colors.white} />
            ) : (
              <Text style={styles.buttonText}>Entrar</Text>
            )}
          </TouchableOpacity>

          {/* TODO(future): Forgot password flow — link here to password recovery screen */}
          <TouchableOpacity
            style={styles.linkButton}
            onPress={() => router.push('/register' as any)}
            disabled={loading}
          >
            <Text style={styles.linkText}>
              Não tem conta? <Text style={styles.linkTextBold}>Cadastre-se</Text>
            </Text>
          </TouchableOpacity>
        </View>
      </ScrollView>
    </KeyboardAvoidingView>
  );
}

const styles = StyleSheet.create({
  container: {
    flex: 1,
    backgroundColor: colors.background,
  },
  scrollContent: {
    flexGrow: 1,
    justifyContent: 'center',
    paddingHorizontal: 24,
    paddingVertical: 48,
  },
  header: {
    alignItems: 'center',
    marginBottom: 48,
  },
  logo: {
    fontSize: 56,
    marginBottom: 12,
  },
  appName: {
    fontSize: 32,
    fontWeight: '700',
    color: colors.text,
    letterSpacing: -0.5,
  },
  subtitle: {
    fontSize: 16,
    color: colors.textMuted,
    marginTop: 4,
  },
  form: {
    width: '100%',
  },
  label: {
    fontSize: 14,
    fontWeight: '600',
    color: colors.textSubtle,
    marginBottom: 6,
    marginLeft: 2,
  },
  input: {
    backgroundColor: colors.surface,
    borderRadius: 12,
    paddingHorizontal: 16,
    paddingVertical: 14,
    fontSize: 16,
    color: colors.text,
    marginBottom: 16,
    borderWidth: 1,
    borderColor: colors.border,
  },
  button: {
    backgroundColor: colors.primary,
    borderRadius: 12,
    paddingVertical: 16,
    alignItems: 'center',
    marginTop: 8,
  },
  buttonDisabled: {
    opacity: 0.6,
  },
  buttonText: {
    color: colors.white,
    fontSize: 16,
    fontWeight: '700',
  },
  linkButton: {
    alignItems: 'center',
    marginTop: 24,
    paddingVertical: 8,
  },
  linkText: {
    color: colors.textMuted,
    fontSize: 14,
  },
  linkTextBold: {
    color: colors.primaryLight,
    fontWeight: '600',
  },
  inputWrapper: {
    backgroundColor: colors.surface,
    borderRadius: 12,
    marginBottom: 16,
    borderWidth: 1,
    borderColor: colors.border,
    flexDirection: 'row',
    alignItems: 'center',
  },
  inputField: {
    flex: 1,
    paddingHorizontal: 16,
    paddingVertical: 14,
    fontSize: 16,
    color: colors.text,
  },
  eyeButton: {
    padding: 12,
  },
});
