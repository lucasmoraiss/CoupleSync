// AC-121, AC-129, AC-130: OCR upload screen — PDF file picker + status polling.
// Only PDF is offered: the server's default parser handles digital PDF statements only and
// rejects images (IMAGE_NOT_SUPPORTED), so camera and image selection are not exposed.
import { getApiErrorMessage } from '@/services/apiError';
import React, { useState, useCallback, useRef } from 'react';
import {
  View,
  Text,
  StyleSheet,
  SafeAreaView,
  TouchableOpacity,
  ScrollView,
  Alert,
} from 'react-native';
import { router, useFocusEffect } from 'expo-router';
import { Ionicons } from '@expo/vector-icons';
import * as DocumentPicker from 'expo-document-picker';
import * as FileSystem from 'expo-file-system';
import axios from 'axios';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { ocrApiClient } from '@/services/apiClient';
import { colors } from '@/theme';
import { LoadingState } from '@/components/LoadingState';
import { ErrorState } from '@/components/ErrorState';
import { ocrFailureMessage } from '@/modules/ocr/failureMessage';
import { buildDiscardRestRequest, openImportSummary } from '@/modules/ocr/confirmRequest';

// ─── Design tokens ────────────────────────────────────────────────────────────
const BG = colors.background;
const CARD = colors.surface;
const PRIMARY = colors.primary;
const ACCENT = colors.primaryLight;
const TEXT = colors.text;
const MUTED = colors.textMuted;
const BORDER = colors.border;
const ERROR = colors.error;
const PDF_MIME_TYPE = 'application/pdf';
const NOT_PDF_ERROR = 'Só é possível importar arquivos PDF. Selecione o extrato bancário em PDF.';
const PDF_READABILITY_ERROR = 'O PDF selecionado parece estar protegido por senha ou danificado. Exporte sem senha e tente novamente.';

// ─── Polling config ───────────────────────────────────────────────────────────
const POLL_DELAYS_MS = [1000, 2000, 4000, 8000, 15000];

type ScreenState =
  | { phase: 'idle' }
  | { phase: 'uploading' }
  | { phase: 'polling'; uploadId: string; jobStatus?: string }
  | { phase: 'error'; message: string };

export default function OcrUploadScreen() {
  const [state, setState] = useState<ScreenState>({ phase: 'idle' });
  const queryClient = useQueryClient();

  // Imports with lines still waiting for review (confirmed "to continue later", or left unfinished).
  const openImports = useQuery({
    queryKey: ['ocr-open-imports'],
    queryFn: () => ocrApiClient.getOpenImports().then((r) => r.data.imports),
    staleTime: 0,
  });

  // The tab stays mounted: look again every time the screen comes back into focus.
  const refetchOpenImports = openImports.refetch;
  useFocusEffect(
    useCallback(() => {
      refetchOpenImports();
    }, [refetchOpenImports]),
  );

  const handleReopen = useCallback((uploadId: string) => {
    router.push(`/(main)/ocr-review?uploadId=${uploadId}` as any);
  }, []);

  const handleDiscardRest = useCallback(
    (uploadId: string) => {
      Alert.alert(
        'Descartar o restante',
        'As transações que ainda não foram importadas serão descartadas e a importação será encerrada.',
        [
          { text: 'Cancelar', style: 'cancel' },
          {
            text: 'Descartar',
            style: 'destructive',
            onPress: async () => {
              try {
                const results = await ocrApiClient.getResults(uploadId);
                const pending = results.data.candidates
                  .filter((c) => (c.lineState ?? 'Pending') === 'Pending')
                  .map((c) => c.index);
                await ocrApiClient.confirm(uploadId, buildDiscardRestRequest(pending));
              } catch (err) {
                Alert.alert('Importação', getApiErrorMessage(err, 'Não foi possível descartar. Tente novamente.'));
              }
              queryClient.invalidateQueries({ queryKey: ['ocr-open-imports'] });
            },
          },
        ],
      );
    },
    [queryClient],
  );
  const isMounted = useRef(true);
  const abortControllerRef = useRef<AbortController | null>(null);
  // Incremented on every new upload attempt; lets async callbacks discard stale results
  const uploadGenerationRef = useRef(0);

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
            // This screen is a hidden tab and stays mounted: go back to the initial state
            // so the next import does not open on a stale "Processando extrato..." spinner.
            setState({ phase: 'idle' });
            router.replace(`/(main)/ocr-review?uploadId=${uploadId}` as any);
          }
          return;
        }

        if (status === 'Failed') {
          if (!isMounted.current) return;
          if (__DEV__) console.log('[OCR] Failed:', { errorCode, status });
          const errorMessage = ocrFailureMessage(errorCode, quotaResetDate);
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

  const ensureFileReadable = useCallback(async (uri: string) => {
    if (!uri) {
      throw new Error(PDF_READABILITY_ERROR);
    }

    const info = await FileSystem.getInfoAsync(uri);
    if (!info.exists) {
      throw new Error(PDF_READABILITY_ERROR);
    }

    await FileSystem.readAsStringAsync(uri, { encoding: FileSystem.EncodingType.Base64 });
  }, []);

  // ─── Upload flow ─────────────────────────────────────────────────────────────
  const uploadFile = useCallback(
    async (uri: string, fileName: string) => {
      try {
        await ensureFileReadable(uri);
      } catch {
        if (isMounted.current) {
          setState({ phase: 'error', message: PDF_READABILITY_ERROR });
        }
        return;
      }

      // Abort any in-flight upload and capture the generation for this attempt
      abortControllerRef.current?.abort();
      const controller = new AbortController();
      abortControllerRef.current = controller;
      const generation = ++uploadGenerationRef.current;

      setState({ phase: 'uploading' });

      const formData = new FormData();
      formData.append('file', {
        uri,
        type: PDF_MIME_TYPE,
        name: fileName,
      } as any);

      try {
        const res = await ocrApiClient.upload(formData, controller.signal);
        const { uploadId } = res.data;
        // Discard result if a newer upload has already started
        if (generation !== uploadGenerationRef.current || !isMounted.current) return;
        abortControllerRef.current = null;
        setState({ phase: 'polling', uploadId });
        pollStatus(uploadId);
      } catch (err: any) {
        // Discard error if a newer upload has already started or request was intentionally aborted
        if (generation !== uploadGenerationRef.current || !isMounted.current) return;
        abortControllerRef.current = null;
        if (axios.isCancel(err) || err?.name === 'AbortError') {
          setState({ phase: 'idle' });
          return;
        }
        const message = getApiErrorMessage(err, 'Falha ao enviar o arquivo. Tente novamente.');
        setState({ phase: 'error', message });
      }
    },
    [ensureFileReadable, pollStatus]
  );

  // ─── File picker handler (PDF only) ───────────────────────────────────────────
  const handleFilePicker = useCallback(async () => {
    const result = await DocumentPicker.getDocumentAsync({
      type: PDF_MIME_TYPE,
      copyToCacheDirectory: true,
    });
    if (result.canceled || result.assets.length === 0) return;
    const asset = result.assets[0];
    // Some file managers ignore the type filter — refuse anything that is not a PDF
    const isPdf =
      asset.mimeType === PDF_MIME_TYPE || asset.name.toLowerCase().endsWith('.pdf');
    if (!isPdf) {
      setState({ phase: 'error', message: NOT_PDF_ERROR });
      return;
    }
    await uploadFile(asset.uri, asset.name);
  }, [uploadFile]);

  const handleRetry = useCallback(() => setState({ phase: 'idle' }), []);

  const handleCancelUpload = useCallback(() => {
    if (abortControllerRef.current) {
      abortControllerRef.current.abort();
      abortControllerRef.current = null;
    }
  }, []);

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
          <Text style={styles.title}>Importar extrato em PDF</Text>
          <Text style={styles.subtitle}>
            Selecione o extrato bancário em PDF para importar as transações automaticamente.
          </Text>

          <TouchableOpacity
            style={styles.optionBtn}
            onPress={handleFilePicker}
            accessibilityLabel="Selecionar arquivo PDF"
            activeOpacity={0.8}
          >
            <Ionicons name="document-outline" size={24} color={PRIMARY} style={styles.optionIcon} />
            <View style={styles.optionText}>
              <Text style={styles.optionLabel}>Arquivo PDF</Text>
              <Text style={styles.optionHint}>Extrato em PDF salvo no dispositivo</Text>
            </View>
            <Ionicons name="chevron-forward" size={18} color={MUTED} />
          </TouchableOpacity>

          {(openImports.data ?? []).length > 0 ? (
            <View style={styles.openSection}>
              <Text style={styles.openTitle}>Importações para continuar</Text>
              <ScrollView style={styles.openList} showsVerticalScrollIndicator={false}>
                {(openImports.data ?? []).map((item) => {
                  const summary = openImportSummary(item);
                  return (
                    <View key={item.uploadId} style={styles.openCard}>
                      <Text style={styles.openName} numberOfLines={1}>{summary.title}</Text>
                      <Text style={styles.openMeta}>
                        {new Date(item.createdAtUtc).toLocaleDateString('pt-BR')} · {summary.pending}
                      </Text>
                      <View style={styles.openActions}>
                        <TouchableOpacity
                          style={styles.openPrimary}
                          onPress={() => handleReopen(item.uploadId)}
                          accessibilityLabel={`Revisar ${summary.title}`}
                        >
                          <Text style={styles.openPrimaryText}>Revisar</Text>
                        </TouchableOpacity>
                        <TouchableOpacity
                          style={styles.openSecondary}
                          onPress={() => handleDiscardRest(item.uploadId)}
                          accessibilityLabel={`Descartar o restante de ${summary.title}`}
                        >
                          <Text style={styles.openSecondaryText}>Descartar o restante</Text>
                        </TouchableOpacity>
                      </View>
                    </View>
                  );
                })}
              </ScrollView>
            </View>
          ) : null}
        </View>
      )}

      {/* Uploading */}
      {state.phase === 'uploading' && (
        <View style={styles.body}>
          <LoadingState message="Enviando arquivo..." />
          <TouchableOpacity
            style={styles.cancelBtn}
            onPress={handleCancelUpload}
            accessibilityLabel="Cancelar envio"
          >
            <Text style={styles.cancelBtnText}>Cancelar</Text>
          </TouchableOpacity>
        </View>
      )}

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
  openSection: { width: '100%', marginTop: 20, maxHeight: 260 },
  openTitle: { fontSize: 14, fontWeight: '700', color: TEXT, marginBottom: 8 },
  openList: { flexGrow: 0 },
  openCard: {
    backgroundColor: CARD,
    borderRadius: 12,
    borderWidth: 1,
    borderColor: BORDER,
    padding: 12,
    marginBottom: 8,
  },
  openName: { fontSize: 14, fontWeight: '600', color: TEXT },
  openMeta: { fontSize: 12, color: MUTED, marginTop: 2 },
  openActions: { flexDirection: 'row', gap: 8, marginTop: 10 },
  openPrimary: { backgroundColor: PRIMARY, borderRadius: 8, paddingHorizontal: 14, paddingVertical: 8 },
  openPrimaryText: { color: TEXT, fontSize: 13, fontWeight: '600' },
  openSecondary: { borderRadius: 8, borderWidth: 1, borderColor: BORDER, paddingHorizontal: 14, paddingVertical: 8 },
  openSecondaryText: { color: MUTED, fontSize: 13 },
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
  cancelBtn: {
    backgroundColor: ERROR,
    borderRadius: 12,
    paddingHorizontal: 32,
    paddingVertical: 14,
    marginTop: 24,
  },
  cancelBtnText: { fontSize: 15, fontWeight: '600', color: 'white' },
});
