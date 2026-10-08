// Wizard do Open Finance (Meu Pluggy), passos 1 a 5: o que é e aceite de privacidade, conectar os bancos no
// Meu Pluggy, credenciais do Dashboard (testadas e guardadas cifradas no servidor), um Item ID por banco e o
// período do histórico, com a primeira sincronização (que roda no servidor; a tela só acompanha).
//
// O Client ID, o Client Secret e os Item IDs ficam só na memória desta tela, que é remontada a cada visita
// (resetOnFocus). O que é guardado no aparelho é só o passo (wizardStore.ts).
import React, { useEffect, useState } from 'react';
import {
  ActivityIndicator,
  KeyboardAvoidingView,
  Linking,
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
import { useQueryClient } from '@tanstack/react-query';
import { openFinanceApiClient } from '@/services/apiClient';
import { getApiErrorCode, getApiErrorMessage } from '@/services/apiError';
import { getSessionEpoch } from '@/state/sessionStore';
import { ErrorState } from '@/components/ErrorState';
import { LoadingState } from '@/components/LoadingState';
import { TextSections } from '@/components/TextSections';
import { goToParent, resetOnFocus } from '@/navigation/resetOnFocus';
import { colors } from '@/theme';
import { isOpenFinanceAccepted } from '@/modules/privacy/consent';
import { isAiChatAllowedNow, useConsentStore } from '@/modules/privacy/consentStore';
import { AI_FEATURE_ENABLED } from '@/modules/chat/aiAvailability';
import { aiConsentForUpload } from '@/modules/ocr/uploadForm';
import { BANK_REVIEW_KEY } from '@/modules/openfinance/useBankReview';
import { useSyncRun } from '@/modules/openfinance/useSyncRun';
import { reviewDoneLabel } from '@/modules/openfinance/review';
import {
  HISTORY_OPTIONS,
  SYNC_STILL_RUNNING_TEXT,
  historyLabel,
  runProgressText,
  runResultText,
} from '@/modules/openfinance/sync';
import { OPEN_FINANCE_CONSENT_SECTIONS, OPEN_FINANCE_CONSENT_TITLE } from '@/modules/privacy/privacyContent';
import { OPEN_FINANCE_STATUS_KEY, useOpenFinanceStatus } from '@/modules/openfinance/useOpenFinanceStatus';
import { useWizardStore } from '@/modules/openfinance/wizardStore';
import {
  DASHBOARD_STEPS,
  DASHBOARD_URL,
  DEFAULT_CONNECTION_LABEL,
  ITEM_STEPS,
  MAX_LABEL_LENGTH,
  MEU_PLUGGY_STEPS,
  MEU_PLUGGY_URL,
  PERIOD_TEXT,
  UNAVAILABLE_TEXT,
  UNAVAILABLE_TITLE,
  WIZARD_STEP_TITLES,
  WIZARD_TOTAL_STEPS,
  canContinueFromBanks,
  canCreateConnection,
  canFinishWizard,
  canTestCredentials,
  canVerifyItem,
  describeItemAccounts,
  existingConnectionAfterCreateError,
  isOpenFinanceAvailable,
  myConnectionOf,
  sameCredentials,
  startingStep,
  type TestedCredentials,
  type WizardStep,
} from '@/modules/openfinance/wizard';

interface ItemField {
  readonly key: number;
  readonly itemId: string;
  /** Verificado com sucesso: as contas encontradas ("Banco · conta corrente ····1234"). */
  readonly found: string | null;
  readonly error: string | null;
}

const newItemField = (key: number): ItemField => ({ key, itemId: '', found: null, error: null });

function leave() {
  goToParent('settings/openfinance/wizard');
}

/** `Linking` é do próprio React Native (existe em todo APK já instalado). Se nenhum navegador abrir, a tela avisa. */
async function openSite(url: string, onFailure: (message: string) => void) {
  try {
    await Linking.openURL(url);
  } catch {
    onFailure(`Não foi possível abrir o navegador. Abra ${url.replace('https://', '')} manualmente.`);
  }
}

function Steps({ steps }: { steps: readonly string[] }) {
  return (
    <View style={styles.steps}>
      {steps.map((text, index) => (
        <View key={text} style={styles.stepRow}>
          <Text style={styles.stepNumber}>{index + 1}.</Text>
          <Text style={styles.stepText}>{text}</Text>
        </View>
      ))}
    </View>
  );
}

function OpenFinanceWizardScreen() {
  const queryClient = useQueryClient();
  const { data: status, isLoading, isError, refetch } = useOpenFinanceStatus();
  // Aceite: lido ao abrir o app. Se a leitura falhou a tela não fica esperando: segue como "não aceito" e o
  // passo 1 avisa se não conseguir registrar.
  const consentKnown = useConsentStore((s) => s.loaded || s.loadFailed);
  const consentAccepted = useConsentStore((s) => isOpenFinanceAccepted(s.record));
  // O progresso é lido de novo a cada visita (a tela é remontada): o que está na memória pode ser de outro grupo.
  const [progressReady, setProgressReady] = useState(false);

  // Passo em que a tela está. null até dar para decidir (status do servidor, aceite e progresso lidos).
  const [step, setStep] = useState<WizardStep | null>(null);
  /** O que está em andamento: uma ação por vez. */
  const [pending, setPending] = useState<'accept' | 'test' | 'create' | null>(null);
  const busy = pending !== null;
  const [notice, setNotice] = useState<string | null>(null);

  // Passo 2
  const [banksConnected, setBanksConnected] = useState(false);
  // Passo 3 (só em memória; nunca gravado no aparelho)
  const [label, setLabel] = useState(DEFAULT_CONNECTION_LABEL);
  const [clientId, setClientId] = useState('');
  const [clientSecret, setClientSecret] = useState('');
  const [tested, setTested] = useState<TestedCredentials | null>(null);
  const [testError, setTestError] = useState<string | null>(null);
  // Passo 4
  const [connectionId, setConnectionId] = useState<string | null>(null);
  const [items, setItems] = useState<ItemField[]>([newItemField(0)]);
  const [verifyingKey, setVerifyingKey] = useState<number | null>(null);
  // Passo 5
  const [historyMonths, setHistoryMonths] = useState<number>(3);
  const sync = useSyncRun();
  /** Quantas despesas esperam a revisão depois da sincronização (null: ainda não se sabe). */
  const [toReview, setToReview] = useState<number | null>(null);

  useEffect(() => {
    let mounted = true;
    void useWizardStore.getState().load().then(() => {
      if (mounted) setProgressReady(true);
    });
    return () => {
      mounted = false;
    };
  }, []);

  const available = isOpenFinanceAvailable(status);
  const mine = myConnectionOf(status);
  const ready = !!status && consentKnown && progressReady;

  // Decide o passo inicial uma vez, quando tudo o que ele depende já foi lido.
  useEffect(() => {
    if (!ready || step !== null) return;
    const progress = useWizardStore.getState().progress;
    const first = startingStep({ progress, consentAccepted, myConnection: mine });
    setStep(first);
    setBanksConnected(progress.banksConnected || first > 2);
    if (first >= 4 && mine) {
      setConnectionId(mine.id);
      setHistoryMonths(mine.historyMonths);
    }
  }, [ready, step, consentAccepted, mine]);

  /** Muda de passo na tela e guarda o progresso (sem nada sensível). */
  const goTo = (next: WizardStep, change: { banksConnected?: boolean; connectionId?: string | null } = {}) => {
    setNotice(null);
    setStep(next);
    void useWizardStore.getState().save({ step: next, ...change });
  };

  const handleBack = () => {
    // Com a conexão criada não há passo anterior a refazer; no primeiro passo, voltar é sair. Depois de pedir a
    // sincronização, voltar também é sair (ela segue no servidor).
    if (step === null || step === 1 || step === 4 || (step === 5 && sync.phase !== 'idle')) {
      leave();
      return;
    }
    goTo((step - 1) as WizardStep);
  };

  // ------------------------------------------------------------------ passo 1

  const handleAccept = async () => {
    if (busy) return;
    const epoch = getSessionEpoch();
    setPending('accept');
    setNotice(null);
    try {
      const saved = consentAccepted || (await useConsentStore.getState().acceptOpenFinance());
      if (getSessionEpoch() !== epoch) return;
      if (!saved) {
        setNotice('Não foi possível registrar o seu aceite. Tente novamente.');
        return;
      }
      goTo(2);
    } finally {
      if (getSessionEpoch() === epoch) setPending(null);
    }
  };

  // ------------------------------------------------------------------ passo 3

  const credentialsChanged = () => {
    setTestError(null);
    setNotice(null);
  };

  const handleTest = async () => {
    if (!canTestCredentials(clientId, clientSecret, busy)) return;
    const epoch = getSessionEpoch();
    const attempt: TestedCredentials = { clientId: clientId.trim(), clientSecret: clientSecret.trim() };
    setPending('test');
    setTested(null);
    setTestError(null);
    setNotice(null);
    try {
      await openFinanceApiClient.testCredentials(attempt.clientId, attempt.clientSecret);
      if (getSessionEpoch() !== epoch) return;
      setTested(attempt);
    } catch (err) {
      if (getSessionEpoch() !== epoch) return;
      setTestError(getApiErrorMessage(err, 'Não foi possível testar as credenciais. Tente novamente.'));
    } finally {
      if (getSessionEpoch() === epoch) setPending(null);
    }
  };

  const handleCreate = async () => {
    if (!canCreateConnection(label, clientId, clientSecret, tested, busy)) return;
    const epoch = getSessionEpoch();
    setPending('create');
    setNotice(null);
    try {
      const { data: created } = await openFinanceApiClient.createConnection({
        label: label.trim(),
        clientId: clientId.trim(),
        clientSecret: clientSecret.trim(),
        historyMonths: 3,
      });
      if (getSessionEpoch() !== epoch) return;
      // As credenciais já estão no servidor: não ficam na memória da tela.
      setClientId('');
      setClientSecret('');
      setTested(null);
      setConnectionId(created.id);
      void queryClient.invalidateQueries({ queryKey: OPEN_FINANCE_STATUS_KEY });
      goTo(4, { connectionId: created.id });
    } catch (err) {
      if (getSessionEpoch() !== epoch) return;
      // Se a conexão já existe (criada em outro aparelho, ou a resposta anterior se perdeu), segue para os bancos.
      const existingId = await existingConnectionAfterCreateError(getApiErrorCode(err), async () => (await refetch({ cancelRefetch: false })).data);
      if (getSessionEpoch() !== epoch) return;
      if (existingId) {
        setClientId('');
        setClientSecret('');
        setTested(null);
        setConnectionId(existingId);
        goTo(4, { connectionId: existingId });
        return;
      }
      setNotice(getApiErrorMessage(err, 'Não foi possível guardar a conexão. Tente novamente.'));
    } finally {
      if (getSessionEpoch() === epoch) setPending(null);
    }
  };

  // ------------------------------------------------------------------ passo 4

  const changeItem = (key: number, itemId: string) =>
    setItems((current) => current.map((item) => (item.key === key ? { ...item, itemId, found: null, error: null } : item)));

  const handleVerify = async (field: ItemField) => {
    if (!connectionId || !canVerifyItem(field.itemId, verifyingKey !== null)) return;
    const epoch = getSessionEpoch();
    setVerifyingKey(field.key);
    setNotice(null);
    const settle = (change: Partial<ItemField>) =>
      setItems((current) => current.map((item) => (item.key === field.key ? { ...item, ...change } : item)));
    try {
      const { data: found } = await openFinanceApiClient.addItem(connectionId, field.itemId.trim());
      if (getSessionEpoch() !== epoch) return;
      settle({ found: describeItemAccounts(found.connectorName, found.accounts), error: null });
      void queryClient.invalidateQueries({ queryKey: OPEN_FINANCE_STATUS_KEY });
    } catch (err) {
      if (getSessionEpoch() !== epoch) return;
      // Item sem contas, item que o Pluggy não conhece, banco que precisa de ação: o texto vem da API.
      settle({ found: null, error: getApiErrorMessage(err, 'Não foi possível verificar este Item ID. Tente novamente.') });
    } finally {
      if (getSessionEpoch() === epoch) setVerifyingKey(null);
    }
  };

  const handleToPeriod = () => {
    if (!canFinishWizard(items.map((item) => ({ verified: item.found !== null })))) return;
    goTo(5, { connectionId });
  };

  // ------------------------------------------------------------------ passo 5

  const handleSync = async () => {
    if (!connectionId || sync.phase === 'working') return;
    const epoch = getSessionEpoch();
    setNotice(null);
    setToReview(null);
    const run = await sync.start(connectionId, {
      historyMonths,
      aiConsent: aiConsentForUpload(AI_FEATURE_ENABLED, isAiChatAllowedNow()),
    });
    if (getSessionEpoch() !== epoch || !run) return;
    // Pedido aceito: o wizard terminou, não há mais o que retomar.
    await useWizardStore.getState().finish();
    if (getSessionEpoch() !== epoch) return;
    void queryClient.invalidateQueries({ queryKey: OPEN_FINANCE_STATUS_KEY });
    void queryClient.invalidateQueries({ queryKey: BANK_REVIEW_KEY });
    if (run.status !== 'Done') return;
    try {
      const { data: review } = await openFinanceApiClient.getReview();
      if (getSessionEpoch() === epoch) setToReview(review.pendingAllMonths ?? 0);
    } catch {
      // Sem a contagem, o botão leva à revisão do mesmo jeito.
      if (getSessionEpoch() === epoch) setToReview(null);
    }
  };

  const openReview = () => router.push('/(main)/openfinance/review' as any);

  // ------------------------------------------------------------------ telas de espera, erro e indisponível

  const backLink = (
    <TouchableOpacity onPress={handleBack} accessibilityRole="button" accessibilityLabel="Voltar">
      <Text style={styles.back}>← Voltar</Text>
    </TouchableOpacity>
  );

  if (isError) {
    return (
      <SafeAreaView style={styles.container}>
        <View style={styles.content}>{backLink}</View>
        <ErrorState message="Não foi possível carregar o Open Finance." onRetry={() => refetch()} />
      </SafeAreaView>
    );
  }
  if (isLoading || !status) return <LoadingState />;

  if (!available) {
    return (
      <SafeAreaView style={styles.container}>
        <View style={styles.content}>
          {backLink}
          <Text style={styles.title} accessibilityRole="header">{UNAVAILABLE_TITLE}</Text>
          <Text style={styles.paragraph}>{UNAVAILABLE_TEXT}</Text>
        </View>
      </SafeAreaView>
    );
  }
  if (step === null) return <LoadingState />;

  const canTest = canTestCredentials(clientId, clientSecret, busy);
  const canCreate = canCreateConnection(label, clientId, clientSecret, tested, busy);
  const credentialsValid = sameCredentials(tested, clientId, clientSecret);
  const canFinish = canFinishWizard(items.map((item) => ({ verified: item.found !== null })));

  return (
    <SafeAreaView style={styles.container}>
      <KeyboardAvoidingView style={styles.flex} behavior={Platform.OS === 'android' ? 'height' : 'padding'}>
        <ScrollView contentContainerStyle={styles.content} keyboardShouldPersistTaps="handled">
          {backLink}
          <Text style={styles.stepLabel}>Passo {step} de {WIZARD_TOTAL_STEPS} · {WIZARD_STEP_TITLES[step]}</Text>

          {step === 1 ? (
            <>
              <Text style={styles.title} accessibilityRole="header">{OPEN_FINANCE_CONSENT_TITLE}</Text>
              <Text style={styles.subtitle}>Leia antes de continuar. Nada é conectado sem o seu aceite.</Text>
              <TextSections sections={OPEN_FINANCE_CONSENT_SECTIONS} />
              <TouchableOpacity
                style={[styles.primaryBtn, busy && styles.disabled]}
                onPress={handleAccept}
                disabled={busy}
                accessibilityRole="button"
                accessibilityLabel={consentAccepted ? 'Continuar para o passo 2' : 'Li e aceito conectar meus bancos pelo Open Finance'}
                accessibilityState={{ disabled: busy, busy: pending === 'accept' }}
              >
                {pending === 'accept' ? <ActivityIndicator color={colors.text} /> : <Text style={styles.primaryText}>{consentAccepted ? 'Continuar' : 'Li e aceito'}</Text>}
              </TouchableOpacity>
              <TouchableOpacity style={styles.secondaryBtn} onPress={leave} disabled={busy} accessibilityRole="button" accessibilityLabel="Agora não, voltar sem conectar">
                <Text style={styles.secondaryText}>Agora não</Text>
              </TouchableOpacity>
            </>
          ) : null}

          {step === 2 ? (
            <>
              <Text style={styles.title} accessibilityRole="header">Conecte seus bancos no Meu Pluggy</Text>
              <Text style={styles.subtitle}>É no Meu Pluggy que você autoriza cada banco. O CoupleSync não vê a senha do seu banco.</Text>
              <TouchableOpacity
                style={styles.linkBtn}
                onPress={() => void openSite(MEU_PLUGGY_URL, setNotice)}
                accessibilityRole="link"
                accessibilityLabel="Abrir meu.pluggy.ai no navegador"
              >
                <Text style={styles.linkText}>Abrir meu.pluggy.ai</Text>
              </TouchableOpacity>
              <Steps steps={MEU_PLUGGY_STEPS} />
              <TouchableOpacity
                style={styles.checkRow}
                onPress={() => setBanksConnected((value) => !value)}
                accessibilityRole="checkbox"
                accessibilityLabel="Conectei meus bancos"
                accessibilityState={{ checked: banksConnected }}
              >
                <View style={[styles.checkBox, banksConnected && styles.checkBoxOn]}>
                  {banksConnected ? <Text style={styles.checkMark}>✓</Text> : null}
                </View>
                <Text style={styles.checkText}>Conectei meus bancos</Text>
              </TouchableOpacity>
              <TouchableOpacity
                style={[styles.primaryBtn, !canContinueFromBanks(banksConnected) && styles.disabled]}
                onPress={() => goTo(3, { banksConnected: true })}
                disabled={!canContinueFromBanks(banksConnected)}
                accessibilityRole="button"
                accessibilityLabel="Continuar para o passo 3"
                accessibilityState={{ disabled: !canContinueFromBanks(banksConnected) }}
              >
                <Text style={styles.primaryText}>Continuar</Text>
              </TouchableOpacity>
            </>
          ) : null}

          {step === 3 ? (
            <>
              <Text style={styles.title} accessibilityRole="header">Credenciais do Dashboard</Text>
              <Text style={styles.subtitle}>
                {mine?.status === 'Disconnected'
                  ? 'Você desconectou antes. Informe o Client ID e o Client Secret de novo para voltar a usar a mesma conexão.'
                  : 'No Dashboard do Pluggy você cria uma aplicação e copia as duas credenciais dela.'}
              </Text>
              <TouchableOpacity
                style={styles.linkBtn}
                onPress={() => void openSite(DASHBOARD_URL, setNotice)}
                accessibilityRole="link"
                accessibilityLabel="Abrir dashboard.pluggy.ai no navegador"
              >
                <Text style={styles.linkText}>Abrir dashboard.pluggy.ai</Text>
              </TouchableOpacity>
              <Steps steps={DASHBOARD_STEPS} />

              <Text style={styles.label}>Apelido da conexão</Text>
              <TextInput
                accessibilityLabel="Apelido da conexão"
                style={styles.input}
                value={label}
                onChangeText={setLabel}
                maxLength={MAX_LABEL_LENGTH}
                editable={!busy}
                placeholder={DEFAULT_CONNECTION_LABEL}
                placeholderTextColor={colors.placeholder}
              />

              <Text style={styles.label}>Client ID</Text>
              <TextInput
                accessibilityLabel="Client ID"
                style={styles.input}
                value={clientId}
                onChangeText={(value) => {
                  setClientId(value);
                  credentialsChanged();
                }}
                autoCapitalize="none"
                autoCorrect={false}
                editable={!busy}
                placeholder="Cole o Client ID"
                placeholderTextColor={colors.placeholder}
              />

              <Text style={styles.label}>Client Secret</Text>
              <TextInput
                accessibilityLabel="Client Secret"
                secureTextEntry
                style={styles.input}
                value={clientSecret}
                onChangeText={(value) => {
                  setClientSecret(value);
                  credentialsChanged();
                }}
                autoCapitalize="none"
                autoCorrect={false}
                editable={!busy}
                placeholder="Cole o Client Secret"
                placeholderTextColor={colors.placeholder}
              />
              <Text style={styles.hint}>
                As credenciais são guardadas cifradas no servidor e não ficam no seu celular. Se você sair desta tela antes de continuar, será preciso colar de novo.
              </Text>

              <TouchableOpacity
                style={[styles.secondaryBtn, !canTest && styles.disabled]}
                onPress={handleTest}
                disabled={!canTest}
                accessibilityRole="button"
                accessibilityLabel="Testar credenciais"
                accessibilityState={{ disabled: !canTest, busy: pending === 'test' }}
              >
                {pending === 'test' ? <ActivityIndicator color={colors.textSubtle} /> : <Text style={styles.secondaryText}>Testar credenciais</Text>}
              </TouchableOpacity>
              {credentialsValid ? (
                <Text style={styles.found} accessibilityRole="alert" accessibilityLiveRegion="polite">Credenciais válidas.</Text>
              ) : null}
              {testError ? (
                <Text style={styles.errorText} accessibilityRole="alert" accessibilityLiveRegion="assertive">{testError}</Text>
              ) : null}

              <TouchableOpacity
                style={[styles.primaryBtn, !canCreate && styles.disabled]}
                onPress={handleCreate}
                disabled={!canCreate}
                accessibilityRole="button"
                accessibilityLabel="Guardar as credenciais e continuar para o passo 4"
                accessibilityState={{ disabled: !canCreate, busy: pending === 'create' }}
              >
                {pending === 'create' ? <ActivityIndicator color={colors.text} /> : <Text style={styles.primaryText}>Continuar</Text>}
              </TouchableOpacity>
            </>
          ) : null}

          {step === 4 ? (
            <>
              <Text style={styles.title} accessibilityRole="header">Item ID de cada banco</Text>
              <Text style={styles.subtitle}>Cada banco conectado no Meu Pluggy tem um Item ID. Cole um por campo e verifique.</Text>
              <TouchableOpacity
                style={styles.linkBtn}
                onPress={() => void openSite(DASHBOARD_URL, setNotice)}
                accessibilityRole="link"
                accessibilityLabel="Abrir dashboard.pluggy.ai no navegador"
              >
                <Text style={styles.linkText}>Abrir dashboard.pluggy.ai</Text>
              </TouchableOpacity>
              <Steps steps={ITEM_STEPS} />

              {items.map((item, index) => {
                const verifying = verifyingKey === item.key;
                const enabled = canVerifyItem(item.itemId, verifyingKey !== null);
                return (
                  <View key={item.key} style={styles.itemBox}>
                    <Text style={styles.label}>Item ID do banco {index + 1}</Text>
                    <TextInput
                      accessibilityLabel={`Item ID do banco ${index + 1}`}
                      style={styles.input}
                      value={item.itemId}
                      onChangeText={(value) => changeItem(item.key, value)}
                      autoCapitalize="none"
                      autoCorrect={false}
                      editable={!verifying}
                      placeholder="Cole o Item ID"
                      placeholderTextColor={colors.placeholder}
                    />
                    <TouchableOpacity
                      style={[styles.secondaryBtn, !enabled && styles.disabled]}
                      onPress={() => void handleVerify(item)}
                      disabled={!enabled}
                      accessibilityRole="button"
                      accessibilityLabel={`Verificar o Item ID do banco ${index + 1}`}
                      accessibilityState={{ disabled: !enabled, busy: verifying }}
                    >
                      {verifying ? <ActivityIndicator color={colors.textSubtle} /> : <Text style={styles.secondaryText}>Verificar</Text>}
                    </TouchableOpacity>
                    {item.found ? (
                      <Text style={styles.found} accessibilityRole="alert" accessibilityLiveRegion="polite">Contas encontradas: {item.found}</Text>
                    ) : null}
                    {item.error ? (
                      <Text style={styles.errorText} accessibilityRole="alert" accessibilityLiveRegion="assertive">{item.error}</Text>
                    ) : null}
                  </View>
                );
              })}

              <TouchableOpacity
                style={styles.linkBtn}
                onPress={() => setItems((current) => [...current, newItemField(current.length === 0 ? 0 : current[current.length - 1].key + 1)])}
                accessibilityRole="button"
                accessibilityLabel="Adicionar outro banco"
              >
                <Text style={styles.linkText}>Adicionar outro banco</Text>
              </TouchableOpacity>

              <TouchableOpacity
                style={[styles.primaryBtn, !canFinish && styles.disabled]}
                onPress={handleToPeriod}
                disabled={!canFinish}
                accessibilityRole="button"
                accessibilityLabel="Continuar para o passo 5"
                accessibilityState={{ disabled: !canFinish }}
              >
                <Text style={styles.primaryText}>Continuar</Text>
              </TouchableOpacity>
            </>
          ) : null}

          {step === 5 ? (
            <>
              <Text style={styles.title} accessibilityRole="header">Quanto do passado trazer</Text>
              <Text style={styles.subtitle}>{PERIOD_TEXT}</Text>

              <View accessibilityRole="radiogroup" accessibilityLabel="Período do histórico" style={styles.options}>
                {HISTORY_OPTIONS.map((months) => {
                  const chosen = historyMonths === months;
                  const locked = sync.phase !== 'idle' && sync.phase !== 'failed';
                  return (
                    <TouchableOpacity
                      key={months}
                      style={[styles.option, chosen && styles.optionOn, locked && styles.disabled]}
                      onPress={() => setHistoryMonths(months)}
                      disabled={locked}
                      accessibilityRole="radio"
                      accessibilityLabel={`Últimos ${historyLabel(months)}`}
                      accessibilityState={{ checked: chosen, disabled: locked }}
                    >
                      <Text style={[styles.optionText, chosen && styles.optionTextOn]}>{historyLabel(months)}</Text>
                    </TouchableOpacity>
                  );
                })}
              </View>

              {sync.phase === 'idle' || sync.phase === 'failed' ? (
                <TouchableOpacity
                  style={[styles.primaryBtn, !connectionId && styles.disabled]}
                  onPress={() => void handleSync()}
                  disabled={!connectionId}
                  accessibilityRole="button"
                  accessibilityLabel={sync.phase === 'failed' ? 'Tentar sincronizar de novo' : 'Conectar e sincronizar'}
                  accessibilityState={{ disabled: !connectionId }}
                >
                  <Text style={styles.primaryText}>{sync.phase === 'failed' ? 'Tentar de novo' : 'Conectar e sincronizar'}</Text>
                </TouchableOpacity>
              ) : null}

              {sync.phase === 'working' ? (
                <View style={styles.progress} accessibilityRole="progressbar" accessibilityLiveRegion="polite">
                  <ActivityIndicator color={colors.primaryLight} />
                  <Text style={styles.paragraph}>{runProgressText(sync.run)}</Text>
                </View>
              ) : null}

              {sync.phase === 'failed' ? (
                <Text style={styles.errorText} accessibilityRole="alert" accessibilityLiveRegion="assertive">
                  {sync.run ? runResultText(sync.run) : sync.requestError}
                </Text>
              ) : null}

              {sync.phase === 'stillRunning' ? (
                <Text style={styles.paragraph} accessibilityRole="alert" accessibilityLiveRegion="polite">{SYNC_STILL_RUNNING_TEXT}</Text>
              ) : null}

              {sync.phase === 'done' && sync.run ? (
                <>
                  <Text style={styles.found} accessibilityRole="alert" accessibilityLiveRegion="polite">{runResultText(sync.run)}</Text>
                  <TouchableOpacity
                    style={styles.primaryBtn}
                    onPress={openReview}
                    accessibilityRole="button"
                    accessibilityLabel={toReview === null ? 'Ver as transações para revisar' : reviewDoneLabel(toReview)}
                  >
                    <Text style={styles.primaryText}>{toReview === null ? 'Ver transações para revisar' : reviewDoneLabel(toReview)}</Text>
                  </TouchableOpacity>
                </>
              ) : null}

              {sync.phase !== 'idle' && sync.phase !== 'working' ? (
                <TouchableOpacity style={styles.secondaryBtn} onPress={leave} accessibilityRole="button" accessibilityLabel="Ver as conexões do grupo">
                  <Text style={styles.secondaryText}>Ver conexões</Text>
                </TouchableOpacity>
              ) : null}
            </>
          ) : null}

          {notice ? (
            <Text style={styles.errorText} accessibilityRole="alert" accessibilityLiveRegion="assertive">{notice}</Text>
          ) : null}
        </ScrollView>
      </KeyboardAvoidingView>
    </SafeAreaView>
  );
}

// Cada visita começa limpa: credenciais, Item IDs e avisos de uma visita anterior não ficam na tela.
export default resetOnFocus(OpenFinanceWizardScreen);

const styles = StyleSheet.create({
  container: { flex: 1, backgroundColor: colors.background },
  flex: { flex: 1 },
  content: { paddingHorizontal: 20, paddingTop: 24, paddingBottom: 40, gap: 10 },
  back: { minHeight: 44, textAlignVertical: 'center', color: colors.primaryLight, fontSize: 15 },
  stepLabel: { fontSize: 13, color: colors.primaryLight, fontWeight: '600' },
  title: { fontSize: 24, fontWeight: '700', color: colors.text },
  subtitle: { fontSize: 14, color: colors.textMuted, lineHeight: 20, marginBottom: 8 },
  paragraph: { fontSize: 15, color: colors.textSubtle, lineHeight: 22 },
  steps: { backgroundColor: colors.surface, borderRadius: 12, borderWidth: 1, borderColor: colors.border, padding: 14, gap: 8 },
  stepRow: { flexDirection: 'row' },
  stepNumber: { width: 22, fontSize: 14, color: colors.primaryLight, fontWeight: '700' },
  stepText: { flex: 1, fontSize: 14, color: colors.textSubtle, lineHeight: 20 },
  label: { fontSize: 14, color: colors.textMuted, marginTop: 6 },
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
  hint: { fontSize: 13, color: colors.textMuted, lineHeight: 19 },
  itemBox: { gap: 8, paddingTop: 6 },
  checkRow: { flexDirection: 'row', alignItems: 'center', minHeight: 48, marginTop: 4 },
  checkBox: { width: 24, height: 24, borderRadius: 6, borderWidth: 2, borderColor: colors.border, alignItems: 'center', justifyContent: 'center', marginRight: 12 },
  checkBoxOn: { backgroundColor: colors.primary, borderColor: colors.primary },
  checkMark: { color: colors.text, fontSize: 15, fontWeight: '700' },
  checkText: { fontSize: 16, color: colors.text },
  linkBtn: { minHeight: 48, borderRadius: 12, borderWidth: 1, borderColor: colors.primary, alignItems: 'center', justifyContent: 'center' },
  linkText: { color: colors.primaryLight, fontSize: 16, fontWeight: '600' },
  primaryBtn: { minHeight: 48, borderRadius: 12, backgroundColor: colors.primary, alignItems: 'center', justifyContent: 'center', marginTop: 8 },
  primaryText: { color: colors.text, fontSize: 16, fontWeight: '700' },
  secondaryBtn: { minHeight: 48, borderRadius: 12, borderWidth: 1, borderColor: colors.border, alignItems: 'center', justifyContent: 'center' },
  secondaryText: { color: colors.textSubtle, fontSize: 16, fontWeight: '600' },
  disabled: { opacity: 0.5 },
  found: { fontSize: 14, color: colors.success, lineHeight: 20 },
  errorText: { fontSize: 14, color: colors.errorLight, lineHeight: 20 },
  options: { flexDirection: 'row', gap: 10 },
  option: { flex: 1, minHeight: 48, borderRadius: 12, borderWidth: 1, borderColor: colors.border, alignItems: 'center', justifyContent: 'center', backgroundColor: colors.surface },
  optionOn: { borderColor: colors.primary, backgroundColor: colors.primary },
  optionText: { color: colors.textSubtle, fontSize: 16, fontWeight: '600' },
  optionTextOn: { color: colors.text },
  progress: { flexDirection: 'row', alignItems: 'center', gap: 12, minHeight: 48 },
});
