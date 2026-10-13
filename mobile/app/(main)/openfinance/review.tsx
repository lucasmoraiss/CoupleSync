// Revisão do banco (Open Finance): as despesas que a sincronização trouxe esperam aqui até alguém do grupo
// confirmar. Nada entra sem confirmação; "Selecionar tudo" confirma num toque. O valor não é editável: só a
// categoria. Lançamento que o banco ainda não efetivou, sem valor ou em outra moeda aparece em cinza e sem caixa.
//
// Conciliação: a linha que parece uma transação que o app já tinha não tem caixa nem entra em "Selecionar tudo";
// ela mostra "Parece a mesma de: ..." e os botões "É a mesma" e "É outra". O servidor confere de novo ao confirmar:
// a linha que ele segurar (parece uma compra já registrada) não é contada como registrada, o aviso diz quantas
// esperam a resposta e a lista recarregada as traz marcadas. O que o servidor ignorou sozinho
// (transferência entre contas do grupo, pagamento de fatura) fica na seção Ignoradas, recolhida, com "restaurar"
// em toda linha: nas saídas e nas entradas.
//
// Entradas (salário, Pix recebido): seção própria, fora de "Selecionar tudo". Cada uma vira renda do mês só pela
// ação "Registrar como renda do mês" (o nome é editável, o valor não) ou é descartada.
//
// Aba oculta cujo pai é Transações. A tela é remontada a cada visita (resetOnFocus): seleção, categorias
// trocadas e avisos de uma visita não ficam para a seguinte, e a lista é a que o servidor tem agora.
import React, { useEffect, useMemo, useState } from 'react';
import {
  ActivityIndicator,
  Modal,
  Pressable,
  RefreshControl,
  SafeAreaView,
  ScrollView,
  SectionList,
  StyleSheet,
  Text,
  TextInput,
  TouchableOpacity,
  View,
} from 'react-native';
import { useQueryClient } from '@tanstack/react-query';
import { openFinanceApiClient } from '@/services/apiClient';
import { getApiErrorMessage } from '@/services/apiError';
import { getSessionEpoch } from '@/state/sessionStore';
import { ErrorState } from '@/components/ErrorState';
import { LoadingState } from '@/components/LoadingState';
import { goToParent, resetOnFocus } from '@/navigation/resetOnFocus';
import { colors } from '@/theme';
import { spokenBRL } from '@/utils/a11y';
import { getCategoryLabel } from '@/modules/transactions/categories';
import { useCategories } from '@/modules/transactions/useCategories';
import { BANK_REVIEW_KEY, useBankReview } from '@/modules/openfinance/useBankReview';
import {
  ANSWER_OTHER_FAILED_TEXT,
  ANSWER_SAME_FAILED_TEXT,
  INCOME_NAME_MAX,
  REGISTER_INCOME_FAILED_TEXT,
  REGISTER_INCOME_LABEL,
  allSelected,
  answerableDuplicateCount,
  answerDuplicateNotice,
  answerDuplicateRequest,
  buildConfirmBatches,
  canAnswerDuplicate,
  canRegisterIncome,
  chosenOfHeld,
  confirmInBatches,
  dayLabel,
  defaultIncomeName,
  discardedLineText,
  duplicateNoticeText,
  groupByDay,
  hasSuspectedDuplicate,
  ignoredLineText,
  incomeBlockedReason,
  incomeLineText,
  incomeNameError,
  incomeRequest,
  incomeResultMessage,
  initialReviewMonth,
  installmentText,
  isSelectable,
  lineAmountText,
  lineTitle,
  matchText,
  mergePendingByMonth,
  monthTitle,
  pruneSelection,
  restoreLabel,
  restoredNoticeText,
  selectAllIds,
  selectedTotalBrl,
  shiftMonth,
  toggleSelected,
  unselectableReason,
  type DuplicateAnswer,
} from '@/modules/openfinance/review';
import type { BankReviewLineResponse } from '@/types/api';

function leave() {
  goToParent('openfinance/review');
}

/** O dia da linha para o leitor de tela ("05/10/2026"). */
function dayOf(line: BankReviewLineResponse): string {
  return dayLabel(line.day);
}

function BankReviewScreen() {
  const queryClient = useQueryClient();
  const categories = useCategories();
  // null até a primeira resposta dizer em que mês há algo esperando.
  const [month, setMonth] = useState<string | null>(null);
  const { data, isLoading, isError, error, refetch, isRefetching } = useBankReview(month);

  const [selected, setSelected] = useState<ReadonlySet<string>>(new Set());
  /** Categoria escolhida pela pessoa, por linha (só as trocadas). */
  const [chosen, setChosen] = useState<Readonly<Record<string, string>>>({});
  const [pickingFor, setPickingFor] = useState<BankReviewLineResponse | null>(null);
  const [showDiscarded, setShowDiscarded] = useState(false);
  const [showIgnored, setShowIgnored] = useState(false);
  /** O que está em andamento: uma ação por vez. */
  const [busy, setBusy] = useState<'confirm' | 'discard' | 'restore' | 'answer' | 'income' | null>(null);
  const [notice, setNotice] = useState<{ readonly text: string; readonly error: boolean } | null>(null);
  /** A entrada cujo nome de renda está sendo digitado (a folha "Registrar como renda do mês" está aberta). */
  const [registering, setRegistering] = useState<BankReviewLineResponse | null>(null);
  const [incomeName, setIncomeName] = useState('');
  /** O erro ao registrar a renda, mostrado na própria folha (ela fica aberta: é onde a pessoa está olhando). */
  const [incomeError, setIncomeError] = useState<string | null>(null);

  // A primeira resposta (mês corrente do servidor) diz onde há pendências: a revisão abre no mês mais recente
  // que tem algo esperando, despesa ou entrada.
  useEffect(() => {
    if (month === null && data) {
      setMonth(
        initialReviewMonth(mergePendingByMonth(data.pendingByMonth, data.pendingIncomesByMonth), data.month, data.ignoredByMonth),
      );
    }
  }, [month, data]);

  const expenses = useMemo(() => data?.expenses ?? [], [data]);
  const discarded = data?.discarded ?? [];
  // Servidor anterior à conciliação não manda a lista: não há seção.
  const ignored = data?.ignored ?? [];
  // Servidor anterior às entradas não manda a lista: não há seção, e a entrada restaurada some daqui como antes.
  const incomes = data?.incomes ?? [];
  const listsIncomes = data?.incomes !== undefined;
  const sections = useMemo(
    () => groupByDay(expenses).map((group) => ({ title: group.label, day: group.day, data: [...group.lines] })),
    [expenses],
  );

  // Depois de recarregar, a seleção fica só com o que ainda está na lista e pode ser confirmado.
  useEffect(() => {
    setSelected((current) => {
      const pruned = pruneSelection(current, expenses);
      return pruned.size === current.size ? current : pruned;
    });
  }, [expenses]);

  const everything = allSelected(expenses, selected);
  const selectableCount = selectAllIds(expenses).length;
  const selectedCount = selected.size;

  const changeMonth = (next: string) => {
    if (busy) return;
    setNotice(null);
    setSelected(new Set());
    setChosen({});
    setMonth(next);
  };

  const invalidateEverything = () => {
    void queryClient.invalidateQueries({ queryKey: BANK_REVIEW_KEY });
    void queryClient.invalidateQueries({ queryKey: ['transactions'] });
    void queryClient.invalidateQueries({ queryKey: ['dashboard'] });
    void queryClient.invalidateQueries({ queryKey: ['reports'] });
    void queryClient.invalidateQueries({ queryKey: ['budget'] });
  };

  const openRegisterIncome = (line: BankReviewLineResponse) => {
    if (busy || !canRegisterIncome(line)) return;
    setIncomeName(defaultIncomeName(line));
    setIncomeError(null);
    setRegistering(line);
  };

  /** "Registrar como renda do mês": a entrada vira renda do mês do dia dela, com o nome da folha. */
  const handleRegisterIncome = async () => {
    const line = registering;
    if (busy || !line || incomeNameError(incomeName)) return;
    const epoch = getSessionEpoch();
    setBusy('income');
    setNotice(null);
    setIncomeError(null);
    try {
      const result = (await openFinanceApiClient.confirmReview(incomeRequest(line, incomeName))).data;
      if (getSessionEpoch() !== epoch) return;
      setRegistering(null);
      setNotice({ text: incomeResultMessage(line, result), error: false });
    } catch (err) {
      if (getSessionEpoch() !== epoch) return;
      // A folha fica aberta com a mensagem: o aviso do topo da lista estaria atrás dela, fora da vista.
      setIncomeError(getApiErrorMessage(err, REGISTER_INCOME_FAILED_TEXT));
    } finally {
      if (getSessionEpoch() === epoch) {
        setBusy(null);
        void queryClient.invalidateQueries({ queryKey: BANK_REVIEW_KEY });
        // A renda nova muda as Rendas, o orçamento e o fluxo de caixa.
        void queryClient.invalidateQueries({ queryKey: ['incomes'] });
        void queryClient.invalidateQueries({ queryKey: ['budget'] });
        void queryClient.invalidateQueries({ queryKey: ['cashflow'] });
      }
    }
  };

  const handleConfirm = async () => {
    if (busy || selectedCount === 0) return;
    const epoch = getSessionEpoch();
    const batches = buildConfirmBatches(expenses, selected, chosen);
    if (batches.length === 0) return;
    setBusy('confirm');
    setNotice(null);
    try {
      // Um lote por vez; para no que falhar, e o que já entrou fica (regra em review.ts).
      const outcome = await confirmInBatches(batches, {
        confirm: async (batch) => (await openFinanceApiClient.confirmReview(batch)).data,
        isCurrent: () => getSessionEpoch() === epoch,
      });
      if (outcome.kind === 'stale') return;
      if (outcome.kind === 'done') {
        setSelected(new Set());
        setChosen((current) => chosenOfHeld(current, outcome.heldIds));
      }
      setNotice({ text: outcome.text, error: outcome.kind === 'failed' });
    } finally {
      if (getSessionEpoch() === epoch) {
        setBusy(null);
        invalidateEverything();
      }
    }
  };

  const handleDiscard = async (line: BankReviewLineResponse) => {
    if (busy) return;
    const epoch = getSessionEpoch();
    setBusy('discard');
    setNotice(null);
    try {
      await openFinanceApiClient.confirmReview({ discard: [line.id] });
      if (getSessionEpoch() !== epoch) return;
      setSelected((current) => {
        const next = new Set(current);
        next.delete(line.id);
        return next;
      });
    } catch (err) {
      if (getSessionEpoch() !== epoch) return;
      setNotice({ text: getApiErrorMessage(err, 'Não foi possível descartar. Tente novamente.'), error: true });
    } finally {
      if (getSessionEpoch() === epoch) {
        setBusy(null);
        void queryClient.invalidateQueries({ queryKey: BANK_REVIEW_KEY });
      }
    }
  };

  /** "É a mesma" (nada é criado) ou "É outra" (despesa nova) para uma linha com possível duplicada. */
  const handleAnswer = async (answer: DuplicateAnswer, line: BankReviewLineResponse) => {
    if (busy || !canAnswerDuplicate(line)) return;
    const epoch = getSessionEpoch();
    setBusy('answer');
    setNotice(null);
    try {
      const result = (await openFinanceApiClient.confirmReview(answerDuplicateRequest(answer, line, chosen))).data;
      if (getSessionEpoch() !== epoch) return;
      setNotice({ text: answerDuplicateNotice(answer, line, chosen, result), error: false });
    } catch (err) {
      if (getSessionEpoch() !== epoch) return;
      setNotice({
        text: getApiErrorMessage(err, answer === 'same' ? ANSWER_SAME_FAILED_TEXT : ANSWER_OTHER_FAILED_TEXT),
        error: true,
      });
    } finally {
      if (getSessionEpoch() === epoch) {
        setBusy(null);
        // As duas respostas mexem nas transações: uma cria, a outra completa a que já existia.
        invalidateEverything();
      }
    }
  };

  const handleRestore = async (line: BankReviewLineResponse) => {
    if (busy) return;
    const epoch = getSessionEpoch();
    setBusy('restore');
    setNotice(null);
    try {
      await openFinanceApiClient.restoreReview([line.id]);
      if (getSessionEpoch() !== epoch) return;
      const restoredText = restoredNoticeText(line, listsIncomes);
      if (restoredText) setNotice({ text: restoredText, error: false });
    } catch (err) {
      if (getSessionEpoch() !== epoch) return;
      setNotice({ text: getApiErrorMessage(err, 'Não foi possível restaurar. Tente novamente.'), error: true });
    } finally {
      if (getSessionEpoch() === epoch) {
        setBusy(null);
        void queryClient.invalidateQueries({ queryKey: BANK_REVIEW_KEY });
      }
    }
  };

  const backLink = (
    <TouchableOpacity onPress={leave} accessibilityRole="button" accessibilityLabel="Voltar para as transações">
      <Text style={styles.back}>← Voltar</Text>
    </TouchableOpacity>
  );

  if (isError && !data) {
    return (
      <SafeAreaView style={styles.container}>
        <View style={styles.padded}>{backLink}</View>
        <ErrorState message={getApiErrorMessage(error, 'Não foi possível carregar a revisão do banco.')} onRetry={() => refetch()} />
      </SafeAreaView>
    );
  }
  if (isLoading || !data || month === null) return <LoadingState />;

  const otherMonths = mergePendingByMonth(data.pendingByMonth, data.pendingIncomesByMonth).filter((m) => m.month !== month && m.pending > 0);
  const nameError = incomeNameError(incomeName);
  const total = selectedTotalBrl(expenses, selected);
  const duplicatesNotice = duplicateNoticeText(answerableDuplicateCount(expenses));

  const header = (
    <View style={styles.header}>
      {backLink}
      <Text style={styles.title} accessibilityRole="header">Revisão do banco</Text>
      <Text style={styles.subtitle}>
        O que veio do banco pelo Open Finance espera aqui. Só vira despesa ou renda o que você confirmar.
      </Text>

      <View style={styles.monthRow}>
        <TouchableOpacity
          style={styles.monthBtn}
          onPress={() => changeMonth(shiftMonth(month, -1))}
          disabled={busy !== null}
          accessibilityRole="button"
          accessibilityLabel="Mês anterior"
        >
          <Text style={styles.monthArrow}>‹</Text>
        </TouchableOpacity>
        <Text style={styles.monthTitle} accessibilityRole="header" accessibilityLiveRegion="polite">{monthTitle(month)}</Text>
        <TouchableOpacity
          style={styles.monthBtn}
          onPress={() => changeMonth(shiftMonth(month, 1))}
          disabled={busy !== null}
          accessibilityRole="button"
          accessibilityLabel="Próximo mês"
        >
          <Text style={styles.monthArrow}>›</Text>
        </TouchableOpacity>
      </View>

      {otherMonths.length > 0 ? (
        <View style={styles.otherMonths}>
          <Text style={styles.muted}>Também há o que revisar em:</Text>
          <ScrollView horizontal showsHorizontalScrollIndicator={false}>
            <View style={styles.chipRow}>
              {otherMonths.map((m) => (
                <TouchableOpacity
                  key={m.month}
                  style={styles.chip}
                  onPress={() => changeMonth(m.month)}
                  disabled={busy !== null}
                  accessibilityRole="button"
                  accessibilityLabel={`Ir para ${monthTitle(m.month)}, ${m.pending} para revisar`}
                >
                  <Text style={styles.chipText}>{monthTitle(m.month)} ({m.pending})</Text>
                </TouchableOpacity>
              ))}
            </View>
          </ScrollView>
        </View>
      ) : null}

      {notice ? (
        <Text
          style={notice.error ? styles.errorText : styles.successText}
          accessibilityRole="alert"
          accessibilityLiveRegion={notice.error ? 'assertive' : 'polite'}
        >
          {notice.text}
        </Text>
      ) : null}

      {duplicatesNotice ? (
        <View style={styles.duplicatesNotice} accessible accessibilityRole="alert">
          <Text style={styles.duplicatesNoticeText}>{duplicatesNotice}</Text>
          <Text style={styles.muted}>
            Elas parecem compras que já estão no app. Diga em cada uma se é a mesma ou se é outra.
          </Text>
        </View>
      ) : null}

      <View style={styles.sectionHeader}>
        <Text style={styles.sectionTitle} accessibilityRole="header">Despesas ({expenses.length})</Text>
        {expenses.length > 0 ? (
          <Text style={styles.muted} accessibilityLabel={`Total esperando: ${spokenBRL(data.pendingTotalBrl)}`}>
            {lineAmountText(data.pendingTotalBrl, 'BRL')}
          </Text>
        ) : null}
      </View>

      {selectableCount > 0 ? (
        <TouchableOpacity
          style={styles.selectAll}
          onPress={() => setSelected(everything ? new Set() : new Set(selectAllIds(expenses)))}
          disabled={busy !== null}
          accessibilityRole="checkbox"
          accessibilityLabel="Selecionar tudo"
          accessibilityState={{ checked: everything, disabled: busy !== null }}
        >
          <View style={[styles.checkBox, everything && styles.checkBoxOn]}>
            {everything ? <Text style={styles.checkMark}>✓</Text> : null}
          </View>
          <Text style={styles.selectAllText}>Selecionar tudo</Text>
        </TouchableOpacity>
      ) : null}

      {expenses.length === 0 ? (
        <Text style={styles.empty}>Nenhuma despesa do banco esperando revisão neste mês.</Text>
      ) : null}
    </View>
  );

  const footer = (
    <View style={styles.footer}>
      {incomes.length > 0 ? (
        <>
          <Text style={styles.sectionTitle} accessibilityRole="header">Entradas ({incomes.length})</Text>
          <Text style={styles.muted}>
            Dinheiro que entrou na conta. Registre como renda do mês o que for renda (salário, um Pix recebido) e descarte o resto.
          </Text>
          {incomes.map((line) => {
            const blocked = incomeBlockedReason(line);
            const title = lineTitle(line);
            return (
              <View key={line.id} style={[styles.line, blocked !== null && styles.lineMuted]}>
                <View style={styles.lineBody}>
                  <Text style={blocked === null ? styles.lineTitle : styles.lineTitleMuted} numberOfLines={1}>{title}</Text>
                  <Text
                    style={styles.muted}
                    accessibilityLabel={`${dayOf(line)}, entrada de ${line.currency === 'BRL' ? spokenBRL(line.amount) : lineAmountText(line.amount, line.currency)}`}
                  >
                    {incomeLineText(line)}
                  </Text>
                  <Text style={styles.muted} numberOfLines={1}>
                    {[line.bankName, line.accountName].filter((part) => !!part).join(' · ')}
                  </Text>
                  {blocked ? <Text style={styles.pendingText}>{blocked}</Text> : null}
                  <View style={styles.incomeActions}>
                    {blocked === null ? (
                      <TouchableOpacity
                        style={[styles.answerBtn, busy !== null && styles.disabled]}
                        onPress={() => openRegisterIncome(line)}
                        disabled={busy !== null}
                        accessibilityRole="button"
                        accessibilityLabel={`${REGISTER_INCOME_LABEL}: ${title}`}
                        accessibilityState={{ disabled: busy !== null }}
                      >
                        <Text style={styles.answerText}>{REGISTER_INCOME_LABEL}</Text>
                      </TouchableOpacity>
                    ) : (
                      <View style={styles.lineBody} />
                    )}
                    <TouchableOpacity
                      style={styles.smallBtn}
                      onPress={() => void handleDiscard(line)}
                      disabled={busy !== null}
                      accessibilityRole="button"
                      accessibilityLabel={`Descartar entrada ${title}`}
                    >
                      <Text style={styles.smallBtnText}>descartar</Text>
                    </TouchableOpacity>
                  </View>
                </View>
              </View>
            );
          })}
        </>
      ) : null}
      {ignored.length > 0 ? (
        <>
          <TouchableOpacity
            style={styles.collapse}
            onPress={() => setShowIgnored((value) => !value)}
            accessibilityRole="button"
            accessibilityLabel={`Ignoradas, ${ignored.length}`}
            accessibilityState={{ expanded: showIgnored }}
          >
            <Text style={styles.sectionTitle}>Ignoradas ({ignored.length})</Text>
            <Text style={styles.monthArrow}>{showIgnored ? '▾' : '▸'}</Text>
          </TouchableOpacity>
          {showIgnored ? (
            <Text style={styles.muted}>
              Não viram despesa: são dinheiro passando entre contas do grupo ou o pagamento da fatura (as despesas são as compras do cartão).
            </Text>
          ) : null}
          {showIgnored
            ? ignored.map((line) => (
                <View key={line.id} style={[styles.line, styles.lineMuted]}>
                  <View style={styles.lineBody}>
                    <Text style={styles.lineTitleMuted} numberOfLines={1}>{lineTitle(line)}</Text>
                    <Text style={styles.muted}>{ignoredLineText(line)}</Text>
                    <Text style={styles.muted} numberOfLines={1}>
                      {[line.bankName, line.accountName].filter((part) => !!part).join(' · ')}
                    </Text>
                  </View>
                  <TouchableOpacity
                    style={styles.smallBtn}
                    onPress={() => void handleRestore(line)}
                    disabled={busy !== null}
                    accessibilityRole="button"
                    accessibilityLabel={restoreLabel(line)}
                  >
                    <Text style={styles.smallBtnText}>restaurar</Text>
                  </TouchableOpacity>
                </View>
              ))
            : null}
        </>
      ) : null}
      {discarded.length > 0 ? (
        <>
          <TouchableOpacity
            style={styles.collapse}
            onPress={() => setShowDiscarded((value) => !value)}
            accessibilityRole="button"
            accessibilityLabel={`Descartadas, ${discarded.length}`}
            accessibilityState={{ expanded: showDiscarded }}
          >
            <Text style={styles.sectionTitle}>Descartadas ({discarded.length})</Text>
            <Text style={styles.monthArrow}>{showDiscarded ? '▾' : '▸'}</Text>
          </TouchableOpacity>
          {showDiscarded
            ? discarded.map((line) => (
                <View key={line.id} style={[styles.line, styles.lineMuted]}>
                  <View style={styles.lineBody}>
                    <Text style={styles.lineTitleMuted} numberOfLines={1}>{lineTitle(line)}</Text>
                    <Text style={styles.muted}>{discardedLineText(line)}</Text>
                  </View>
                  <TouchableOpacity
                    style={styles.smallBtn}
                    onPress={() => void handleRestore(line)}
                    disabled={busy !== null}
                    accessibilityRole="button"
                    accessibilityLabel={restoreLabel(line)}
                  >
                    <Text style={styles.smallBtnText}>restaurar</Text>
                  </TouchableOpacity>
                </View>
              ))
            : null}
        </>
      ) : null}
    </View>
  );

  return (
    <SafeAreaView style={styles.container}>
      <SectionList
        sections={sections}
        keyExtractor={(line) => line.id}
        ListHeaderComponent={header}
        ListFooterComponent={footer}
        stickySectionHeadersEnabled={false}
        contentContainerStyle={styles.listContent}
        refreshControl={<RefreshControl refreshing={isRefetching} onRefresh={() => void refetch()} tintColor={colors.primaryLight} />}
        renderSectionHeader={({ section }) => (
          <Text style={styles.dayHeader} accessibilityRole="header">{section.title}</Text>
        )}
        renderItem={({ item: line }) => {
          const selectable = isSelectable(line);
          const whyNot = unselectableReason(line);
          const duplicate = hasSuspectedDuplicate(line);
          const answerable = canAnswerDuplicate(line);
          const checked = selected.has(line.id);
          const category = chosen[line.id] ?? line.suggestedCategory;
          const installment = installmentText(line);
          const title = lineTitle(line);
          return (
            <View style={[styles.line, !selectable && !answerable && styles.lineMuted, duplicate && styles.lineDuplicate]}>
              {selectable ? (
                <TouchableOpacity
                  style={styles.checkTouch}
                  onPress={() => setSelected((current) => toggleSelected(current, line))}
                  disabled={busy !== null}
                  accessibilityRole="checkbox"
                  accessibilityLabel={`Selecionar ${title}, ${spokenBRL(line.amount)}`}
                  accessibilityState={{ checked, disabled: busy !== null }}
                >
                  <View style={[styles.checkBox, checked && styles.checkBoxOn]}>
                    {checked ? <Text style={styles.checkMark}>✓</Text> : null}
                  </View>
                </TouchableOpacity>
              ) : (
                // Pendente no banco, sem valor, em outra moeda ou com possível duplicada (essa se responde pelos
                // botões): sem caixa. O espaço fica, para as linhas alinharem.
                <View style={styles.checkTouch} />
              )}
              <View style={styles.lineBody}>
                <View style={styles.lineTop}>
                  <Text style={selectable || answerable ? styles.lineTitle : styles.lineTitleMuted} numberOfLines={1}>{title}</Text>
                  <Text
                    style={selectable || answerable ? styles.amount : styles.amountMuted}
                    accessibilityLabel={line.currency === 'BRL' ? spokenBRL(line.amount) : lineAmountText(line.amount, line.currency)}
                  >
                    {lineAmountText(line.amount, line.currency)}
                  </Text>
                </View>
                {line.description && line.description !== title ? (
                  <Text style={styles.muted} numberOfLines={2}>{line.description}</Text>
                ) : null}
                <Text style={styles.muted} numberOfLines={1}>
                  {[line.bankName, line.accountName, installment].filter((part) => !!part).join(' · ')}
                </Text>
                {whyNot ? <Text style={styles.pendingText}>{whyNot}</Text> : null}
                {line.match ? (
                  <>
                    <Text style={styles.matchText}>{matchText(line.match)}</Text>
                    <View style={styles.answerRow}>
                      <TouchableOpacity
                        style={[styles.answerBtn, (busy !== null || !answerable) && styles.disabled]}
                        onPress={() => void handleAnswer('same', line)}
                        disabled={busy !== null || !answerable}
                        accessibilityRole="button"
                        accessibilityLabel={`É a mesma: ${title} já está no app, não registrar de novo`}
                        accessibilityState={{ disabled: busy !== null || !answerable }}
                      >
                        <Text style={styles.answerText}>É a mesma</Text>
                      </TouchableOpacity>
                      <TouchableOpacity
                        style={[styles.answerBtn, (busy !== null || !answerable) && styles.disabled]}
                        onPress={() => void handleAnswer('other', line)}
                        disabled={busy !== null || !answerable}
                        accessibilityRole="button"
                        accessibilityLabel={`É outra: registrar ${title} como uma despesa nova`}
                        accessibilityState={{ disabled: busy !== null || !answerable }}
                      >
                        <Text style={styles.answerText}>É outra</Text>
                      </TouchableOpacity>
                    </View>
                  </>
                ) : null}
                <View style={styles.lineActions}>
                  <TouchableOpacity
                    style={styles.chip}
                    onPress={() => setPickingFor(line)}
                    disabled={busy !== null || !(selectable || answerable)}
                    accessibilityRole="button"
                    accessibilityLabel={`Categoria: ${getCategoryLabel(category)}. Toque para trocar`}
                    accessibilityState={{ disabled: busy !== null || !(selectable || answerable) }}
                  >
                    <Text style={styles.chipText}>{getCategoryLabel(category)}</Text>
                  </TouchableOpacity>
                  <TouchableOpacity
                    style={styles.smallBtn}
                    onPress={() => void handleDiscard(line)}
                    disabled={busy !== null}
                    accessibilityRole="button"
                    accessibilityLabel={`Descartar ${title}`}
                  >
                    <Text style={styles.smallBtnText}>descartar</Text>
                  </TouchableOpacity>
                </View>
              </View>
            </View>
          );
        }}
      />

      {selectableCount > 0 ? (
        <View style={styles.bar}>
          <Text style={styles.muted} accessibilityLiveRegion="polite">
            {selectedCount === 0 ? 'Nenhuma selecionada' : `${selectedCount} selecionada${selectedCount === 1 ? '' : 's'} · ${lineAmountText(total, 'BRL')}`}
          </Text>
          <TouchableOpacity
            style={[styles.primaryBtn, (selectedCount === 0 || busy !== null) && styles.disabled]}
            onPress={() => void handleConfirm()}
            disabled={selectedCount === 0 || busy !== null}
            accessibilityRole="button"
            accessibilityLabel="Confirmar selecionadas"
            accessibilityState={{ disabled: selectedCount === 0 || busy !== null, busy: busy === 'confirm' }}
          >
            {busy === 'confirm' ? <ActivityIndicator color={colors.text} /> : <Text style={styles.primaryText}>Confirmar selecionadas</Text>}
          </TouchableOpacity>
        </View>
      ) : null}

      <Modal
        visible={registering !== null}
        transparent
        animationType="slide"
        onRequestClose={() => (busy === null ? setRegistering(null) : undefined)}
      >
        <Pressable
          accessibilityRole="button"
          accessibilityLabel="Fechar"
          style={styles.overlay}
          onPress={() => (busy === null ? setRegistering(null) : undefined)}
        >
          <Pressable accessible={false} style={styles.sheet} onPress={() => undefined}>
            <Text style={styles.sectionTitle} accessibilityRole="header">{REGISTER_INCOME_LABEL}</Text>
            {registering ? (
              <Text style={styles.muted}>
                {incomeLineText(registering)} · entra nas rendas de {monthTitle(registering.day.slice(0, 7))}, compartilhada com o grupo. O valor não muda.
              </Text>
            ) : null}
            <Text style={styles.inputLabel}>Nome da renda</Text>
            <TextInput
              style={styles.input}
              value={incomeName}
              onChangeText={setIncomeName}
              editable={busy === null}
              maxLength={INCOME_NAME_MAX}
              placeholder={registering ? defaultIncomeName(registering) : undefined}
              placeholderTextColor={colors.placeholder}
              accessibilityLabel="Nome da renda"
              returnKeyType="done"
            />
            {nameError ? <Text style={styles.errorText} accessibilityRole="alert">{nameError}</Text> : null}
            {incomeError ? (
              <Text style={styles.errorText} accessibilityRole="alert" accessibilityLiveRegion="assertive">
                {incomeError}
              </Text>
            ) : null}
            <TouchableOpacity
              style={[styles.primaryBtn, (busy !== null || nameError !== null) && styles.disabled]}
              onPress={() => void handleRegisterIncome()}
              disabled={busy !== null || nameError !== null}
              accessibilityRole="button"
              accessibilityLabel="Registrar renda"
              accessibilityState={{ disabled: busy !== null || nameError !== null, busy: busy === 'income' }}
            >
              {busy === 'income' ? <ActivityIndicator color={colors.text} /> : <Text style={styles.primaryText}>Registrar renda</Text>}
            </TouchableOpacity>
            <TouchableOpacity
              style={styles.smallBtn}
              onPress={() => setRegistering(null)}
              disabled={busy !== null}
              accessibilityRole="button"
              accessibilityLabel="Cancelar"
            >
              <Text style={styles.smallBtnText}>cancelar</Text>
            </TouchableOpacity>
          </Pressable>
        </Pressable>
      </Modal>

      <Modal visible={pickingFor !== null} transparent animationType="slide" onRequestClose={() => setPickingFor(null)}>
        <Pressable accessibilityRole="button" accessibilityLabel="Fechar" style={styles.overlay} onPress={() => setPickingFor(null)}>
          <Pressable accessible={false} style={styles.sheet} onPress={() => undefined}>
            <Text style={styles.sectionTitle} accessibilityRole="header">Categoria</Text>
            {pickingFor ? <Text style={styles.muted} numberOfLines={1}>{lineTitle(pickingFor)}</Text> : null}
            <ScrollView style={styles.sheetList}>
              {categories.map((category) => {
                const current = pickingFor ? (chosen[pickingFor.id] ?? pickingFor.suggestedCategory) === category.value : false;
                return (
                  <TouchableOpacity
                    key={category.value}
                    style={[styles.sheetItem, current && styles.sheetItemOn]}
                    onPress={() => {
                      if (pickingFor) setChosen((all) => ({ ...all, [pickingFor.id]: category.value }));
                      setPickingFor(null);
                    }}
                    accessibilityRole="radio"
                    accessibilityLabel={`Categoria: ${category.label}`}
                    accessibilityState={{ selected: current }}
                  >
                    <Text style={styles.sheetItemText}>{category.label}</Text>
                  </TouchableOpacity>
                );
              })}
            </ScrollView>
          </Pressable>
        </Pressable>
      </Modal>
    </SafeAreaView>
  );
}

// Cada visita começa limpa e com a lista que o servidor tem agora.
export default resetOnFocus(BankReviewScreen);

const styles = StyleSheet.create({
  container: { flex: 1, backgroundColor: colors.background },
  padded: { paddingHorizontal: 20, paddingTop: 24 },
  listContent: { paddingHorizontal: 20, paddingTop: 24, paddingBottom: 24 },
  header: { gap: 10 },
  footer: { marginTop: 16, gap: 8 },
  back: { minHeight: 44, textAlignVertical: 'center', color: colors.primaryLight, fontSize: 15 },
  title: { fontSize: 24, fontWeight: '700', color: colors.text },
  subtitle: { fontSize: 14, color: colors.textMuted, lineHeight: 20 },
  muted: { fontSize: 13, color: colors.textMuted, lineHeight: 19 },
  empty: { fontSize: 14, color: colors.textSubtle, lineHeight: 20, paddingVertical: 12 },
  errorText: { fontSize: 14, color: colors.errorLight, lineHeight: 20 },
  successText: { fontSize: 14, color: colors.success, lineHeight: 20 },
  monthRow: { flexDirection: 'row', alignItems: 'center', justifyContent: 'space-between', marginTop: 4 },
  monthBtn: { minWidth: 48, minHeight: 48, alignItems: 'center', justifyContent: 'center' },
  monthArrow: { fontSize: 24, color: colors.primaryLight },
  monthTitle: { fontSize: 18, fontWeight: '700', color: colors.text },
  otherMonths: { gap: 6 },
  chipRow: { flexDirection: 'row', gap: 8 },
  chip: { minHeight: 36, paddingHorizontal: 14, borderRadius: 18, borderWidth: 1, borderColor: colors.primary, alignItems: 'center', justifyContent: 'center' },
  chipText: { color: colors.primaryLight, fontSize: 13, fontWeight: '600' },
  sectionHeader: { flexDirection: 'row', alignItems: 'baseline', justifyContent: 'space-between', marginTop: 8 },
  sectionTitle: { fontSize: 17, fontWeight: '700', color: colors.text },
  selectAll: { flexDirection: 'row', alignItems: 'center', minHeight: 48 },
  selectAllText: { fontSize: 16, color: colors.text },
  checkTouch: { width: 44, minHeight: 44, alignItems: 'flex-start', justifyContent: 'center' },
  checkBox: { width: 24, height: 24, borderRadius: 6, borderWidth: 2, borderColor: colors.border, alignItems: 'center', justifyContent: 'center', marginRight: 12 },
  checkBoxOn: { backgroundColor: colors.primary, borderColor: colors.primary },
  checkMark: { color: colors.text, fontSize: 15, fontWeight: '700' },
  dayHeader: { fontSize: 13, fontWeight: '700', color: colors.primaryLight, marginTop: 16, marginBottom: 6 },
  line: { flexDirection: 'row', backgroundColor: colors.surface, borderRadius: 12, borderWidth: 1, borderColor: colors.border, padding: 12, marginBottom: 8 },
  lineMuted: { opacity: 0.6 },
  lineDuplicate: { borderColor: colors.warning },
  duplicatesNotice: { borderRadius: 12, borderWidth: 1, borderColor: colors.warning, padding: 12, gap: 4 },
  duplicatesNoticeText: { fontSize: 15, fontWeight: '700', color: colors.warning },
  matchText: { fontSize: 13, color: colors.warning, lineHeight: 19 },
  answerRow: { flexDirection: 'row', gap: 8, marginTop: 4 },
  answerBtn: { flex: 1, minHeight: 44, borderRadius: 10, borderWidth: 1, borderColor: colors.primary, alignItems: 'center', justifyContent: 'center', paddingHorizontal: 8 },
  answerText: { color: colors.primaryLight, fontSize: 14, fontWeight: '700' },
  lineBody: { flex: 1, gap: 4 },
  lineTop: { flexDirection: 'row', alignItems: 'baseline', justifyContent: 'space-between', gap: 8 },
  lineTitle: { flex: 1, fontSize: 15, fontWeight: '600', color: colors.text },
  lineTitleMuted: { flex: 1, fontSize: 15, fontWeight: '600', color: colors.textMuted },
  amount: { fontSize: 15, fontWeight: '700', color: colors.text },
  amountMuted: { fontSize: 15, fontWeight: '700', color: colors.textMuted },
  pendingText: { fontSize: 12, color: colors.warning, lineHeight: 17 },
  lineActions: { flexDirection: 'row', alignItems: 'center', justifyContent: 'space-between', marginTop: 4 },
  incomeActions: { flexDirection: 'row', alignItems: 'center', justifyContent: 'space-between', gap: 8, marginTop: 4 },
  smallBtn: { minHeight: 44, minWidth: 44, paddingHorizontal: 8, alignItems: 'center', justifyContent: 'center' },
  smallBtnText: { color: colors.textSubtle, fontSize: 13, fontWeight: '600', textDecorationLine: 'underline' },
  collapse: { flexDirection: 'row', alignItems: 'center', justifyContent: 'space-between', minHeight: 48 },
  bar: { paddingHorizontal: 20, paddingTop: 10, paddingBottom: 12, borderTopWidth: 1, borderTopColor: colors.border, backgroundColor: colors.background, gap: 8 },
  primaryBtn: { minHeight: 48, borderRadius: 12, backgroundColor: colors.primary, alignItems: 'center', justifyContent: 'center' },
  primaryText: { color: colors.text, fontSize: 16, fontWeight: '700' },
  disabled: { opacity: 0.5 },
  overlay: { flex: 1, backgroundColor: colors.overlay, justifyContent: 'flex-end' },
  sheet: { backgroundColor: colors.surface, borderTopLeftRadius: 20, borderTopRightRadius: 20, padding: 20, gap: 8, maxHeight: '70%' },
  sheetList: { marginTop: 8 },
  sheetItem: { minHeight: 48, justifyContent: 'center', paddingHorizontal: 12, borderRadius: 10 },
  sheetItemOn: { backgroundColor: colors.background },
  sheetItemText: { fontSize: 16, color: colors.text },
  inputLabel: { fontSize: 13, color: colors.textMuted, marginTop: 8 },
  input: { minHeight: 48, borderRadius: 10, borderWidth: 1, borderColor: colors.border, backgroundColor: colors.background, color: colors.text, fontSize: 16, paddingHorizontal: 12 },
});
