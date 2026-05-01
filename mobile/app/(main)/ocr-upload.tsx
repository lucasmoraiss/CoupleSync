// AC-121, AC-129, AC-130: OCR upload screen — camera + file picker + status polling
import React, { useState, useCallback, useRef } from 'react';
import {
  View,
  Text,
  StyleSheet,
  SafeAreaView,
  TouchableOpacity,
  Alert,
} from 'react-native';
import { router } from 'expo-router';
import { Ionicons } from '@expo/vector-icons';
import * as ImagePicker from 'expo-image-picker';
import * as DocumentPicker from 'expo-document-picker';
import { ocrApiClient } from '@/services/apiClient';
import { colors } from '@/theme';
import { LoadingState } from '@/components/LoadingState';
import { ErrorState } from '@/components/ErrorState';

// ─── Design tokens ────────────────────────────────────────────────────────────
const BG = colors.background;
const CARD = colors.surface;
const PRIMARY = colors.primary;
const ACCENT = colors.primaryLight;
const TEXT = colors.text;
const MUTED = colors.textMuted;
const BORDER = colors.border;
const ERROR = colors.error;

// ─── Polling config ───────────────────────────────────────────────────────────
const POLL_DELAYS_MS = [1000, 2000, 4000, 8000, 15000];

type ScreenState =
  | { phase: 'idle' }
  | { phase: 'uploading' }
  | { phase: 'polling'; uploadId: string; jobStatus?: string }
  | { phase: 'error'; message: string };

export default function OcrUploadScreen() {
  const [state, setState] = useState<ScreenState>({ phase: 'idle' });
  const isMounted = useRef(true);

  React.useEffect(() => {
    return () => {
      isMounted.current = false;
    };
  }, []);

  // ─── Polling with exponential back-off ──────────────────────────────────────
  const pollStatus = useCallback(async (uploadId: string) => {
    let attempt = 0;
    while (isMounted.current) {
      const delay = POLL_DELAYS_MS[Math.min(attempt, POLL_DELAYS_MS.length - 1)];
      await new Promise<void>((res) => setTimeout(res, delay));

      if (!isMounted.current) return;

      try {
        const res = await ocrApiClient.getStatus(uploadId);
        const { status, errorCode, quotaResetDate } = res.data;

        if (status === 'Ready') {
          if (isMounted.current) {
            router.replace(`/(main)/ocr-review?uploadId=${uploadId}` as any);
          }
          return;
        }

        if (status === 'Failed') {
          if (!isMounted.current) return;
          if (__DEV__) console.log('[OCR] Failed:', { errorCode, status });
          let errorMessage: string;
          if (errorCode === 'quota_exhausted') {
            const dateStr = quotaResetDate
              ? new Date(quotaResetDate).toLocaleDateString('pt-BR', {
                  day: '2-digit',
                  month: '2-digit',
                  year: 'numeric',
                })
              : '—';
            errorMessage = `OCR indisponível este mês. Cota atingida. Tente novamente em ${dateStr}.`;
          } else if (errorCode === 'PDF_ENCRYPTED') {
            errorMessage = 'O PDF está protegido por senha. Por enquanto, exporte o extrato sem senha e tente novamente. (Suporte a senha será adicionado em breve.)';
          } else if (errorCode === 'PDF_TOO_SHORT') {
            errorMessage = 'O PDF parece ser uma imagem digitalizada. Envie um extrato em PDF digital (texto selecionável).';
          } else if (errorCode === 'NO_TRANSACTIONS_FOUND') {
            errorMessage = 'Nenhuma transação encontrada. Verifique se o PDF é um extrato bancário válido.';
          } else if (errorCode === 'BANK_FORMAT_UNKNOWN') {
            errorMessage = 'Formato do banco não reconhecido. Tente um extrato de outro banco ou cadastre as transações manualmente.';
          } else {
            errorMessage = 'Falha no processamento. Tente novamente.';
          }
          setState({ phase: 'error', message: errorMessage });
          return;
        }

        // Non-terminal status — update jobStatus for contextual pt-BR message
        if (isMounted.current) {
          setState({ phase: 'polling', uploadId, jobStatus: status });
        }
      } catch {
        // network hiccup — keep polling
      }

      attempt += 1;
    }
  }, []);

  // ─── Upload flow ─────────────────────────────────────────────────────────────
  const uploadFile = useCallback(
    async (uri: string, mimeType: string, fileName: string) => {
      setState({ phase: 'uploading' });

      const formData = new FormData();
      formData.append('file', {
        uri,
        type: mimeType,
        name: fileName,
      } as any);

      try {
        const res = await ocrApiClient.upload(formData);
        const { uploadId } = res.data;
        if (!isMounted.current) return;
        setState({ phase: 'polling', uploadId });
        pollStatus(uploadId);
      } catch (err: any) {
        if (isMounted.current) {
          let message: string;
          if (!err?.response) {
            message = 'Sem conexão. Verifique sua internet e tente novamente.';
          } else {
            const data = err.response.data;
            const isObj = typeof data === 'object' && data !== null;
            if (isObj && typeof (data as any).message === 'string') {
              message = (data as any).message;
            } else if (isObj && typeof (data as any).error === 'string') {
              message = (data as any).error;
            } else if (err.response.statusText) {
              message = err.response.statusText;
            } else {
              message = 'Falha ao enviar o arquivo. Tente novamente.';
            }
          }
          setState({ phase: 'error', message });
        }
      }
    },
    [pollStatus]
  );

  // ─── Camera handler ───────────────────────────────────────────────────────────
  const handleCamera = useCallback(async () => {
    const { status } = await ImagePicker.requestCameraPermissionsAsync();
    if (status !== 'granted') {
      Alert.alert('Permissão necessária', 'Habilite o acesso à câmera nas configurações do dispositivo.');
      return;
    }
    const result = await ImagePicker.launchCameraAsync({
      mediaTypes: ImagePicker.MediaTypeOptions.Images,
      quality: 0.85,
      allowsEditing: false,
    });
    if (result.canceled || result.assets.length === 0) return;
    const asset = result.assets[0];
    const ext = asset.uri.split('.').pop() ?? 'jpg';
    await uploadFile(asset.uri, asset.mimeType ?? `image/${ext}`, `captura.${ext}`);
  }, [uploadFile]);

  // ─── File picker handler ──────────────────────────────────────────────────────
  const handleFilePicker = useCallback(async () => {
    const result = await DocumentPicker.getDocumentAsync({
      type: ['image/*', 'application/pdf'],
      copyToCacheDirectory: true,
    });
    if (result.canceled || result.assets.length === 0) return;
    const asset = result.assets[0];
    await uploadFile(asset.uri, asset.mimeType ?? 'application/octet-stream', asset.name);
  }, [uploadFile]);

  const handleRetry = useCallback(() => setState({ phase: 'idle' }), []);

  // ─── Render ──────────────────────────────────────────────────────────────────
  return (
    <SafeAreaView style={styles.container}>
      {/* Header */}
      <View style={styles.header}>
        <TouchableOpacity
          style={styles.backBtn}
          onPress={() => router.back()}
          accessibilityLabel="Voltar"
        >
          <Ionicons name="arrow-back" size={22} color={TEXT} />
        </TouchableOpacity>
        <Text style={styles.headerTitle}>Importar extrato</Text>
        <View style={styles.backBtn} />
      </View>

      {/* Idle — pick source */}
      {state.phase === 'idle' && (
        <View style={styles.body}>
          <Ionicons name="cloud-upload-outline" size={56} color={ACCENT} style={styles.icon} />
          <Text style={styles.title}>Importar via OCR</Text>
          <Text style={styles.subtitle}>
            Fotografe ou selecione um extrato bancário para importar transações automaticamente.
          </Text>

          <TouchableOpacity
            style={styles.optionBtn}
            onPress={handleCamera}
            accessibilityLabel="Usar câmera"
            activeOpacity={0.8}
          >
            <Ionicons name="camera-outline" size={24} color={PRIMARY} style={styles.optionIcon} />
            <View style={styles.optionText}>
              <Text style={styles.optionLabel}>Câmera</Text>
              <Text style={styles.optionHint}>Fotografe o extrato impresso</Text>
            </View>
            <Ionicons name="chevron-forward" size={18} color={MUTED} />
          </TouchableOpacity>

          <TouchableOpacity
            style={styles.optionBtn}
            onPress={handleFilePicker}
            accessibilityLabel="Selecionar arquivo"
            activeOpacity={0.8}
          >
            <Ionicons name="document-outline" size={24} color={PRIMARY} style={styles.optionIcon} />
            <View style={styles.optionText}>
              <Text style={styles.optionLabel}>Arquivo</Text>
              <Text style={styles.optionHint}>PDF ou imagem do dispositivo</Text>
            </View>
            <Ionicons name="chevron-forward" size={18} color={MUTED} />
          </TouchableOpacity>
        </View>
      )}

      {/* Uploading */}
      {state.phase === 'uploading' && <LoadingState message="Enviando arquivo..." />}

      {/* Polling */}
      {state.phase === 'polling' && (
        <LoadingState
          message={
            state.jobStatus === 'Pending'
              ? 'Aguardando processamento...'
              : 'Processando extrato...'
          }
        />
      )}

      {/* Error */}
      {state.phase === 'error' && (
        <ErrorState message={state.message} onRetry={handleRetry} />
      )}
    </SafeAreaView>
  );
}

const styles = StyleSheet.create({
  container: { flex: 1, backgroundColor: BG },
  header: {
    flexDirection: 'row',
    alignItems: 'center',
    justifyContent: 'space-between',
    paddingHorizontal: 16,
    paddingTop: 24,
    paddingBottom: 16,
  },
  backBtn: { width: 36, height: 36, alignItems: 'center', justifyContent: 'center' },
  headerTitle: { fontSize: 17, fontWeight: '700', color: TEXT },
  body: {
    flex: 1,
    alignItems: 'center',
    justifyContent: 'center',
    paddingHorizontal: 28,
  },
  icon: { marginBottom: 20 },
  title: { fontSize: 20, fontWeight: '700', color: TEXT, marginBottom: 10, textAlign: 'center' },
  subtitle: { fontSize: 14, color: MUTED, textAlign: 'center', lineHeight: 20, marginBottom: 36 },
  optionBtn: {
    width: '100%',
    flexDirection: 'row',
    alignItems: 'center',
    backgroundColor: CARD,
    borderRadius: 14,
    paddingVertical: 18,
    paddingHorizontal: 16,
    marginBottom: 12,
    borderWidth: 1,
    borderColor: BORDER,
  },
  optionIcon: { marginRight: 14 },
  optionText: { flex: 1 },
  optionLabel: { fontSize: 15, fontWeight: '600', color: TEXT },
  optionHint: { fontSize: 12, color: MUTED, marginTop: 2 },
  statusText: { fontSize: 16, fontWeight: '600', color: TEXT, marginTop: 20 },
  statusHint: { fontSize: 13, color: MUTED, marginTop: 8, textAlign: 'center' },
  errorTitle: { fontSize: 18, fontWeight: '700', color: TEXT, marginBottom: 12, textAlign: 'center' },
  errorMessage: { fontSize: 14, color: MUTED, textAlign: 'center', lineHeight: 20, marginBottom: 28 },
  retryBtn: {
    backgroundColor: PRIMARY,
    borderRadius: 12,
    paddingHorizontal: 32,
    paddingVertical: 14,
  },
  retryText: { fontSize: 15, fontWeight: '600', color: TEXT },
});
