// Contas (Open Finance): por pessoa do grupo, as contas com o saldo e há quanto tempo ele foi atualizado, e os
// cartões com a fatura atual, as próximas e as anteriores. O que uma pessoa conecta é do grupo: todos veem tudo.
// Nada aqui converte moeda: cada valor aparece na moeda dele, e a soma por pessoa é só das contas em reais.
//
// Aba oculta cujo pai é o Painel (abre pelo cartão "Faturas a vencer"). A tela é remontada a cada visita
// (resetOnFocus): o que estava aberto numa visita não fica para a seguinte, e os saldos são os de agora.
import React, { useState } from 'react';
import { RefreshControl, SafeAreaView, ScrollView, StyleSheet, Text, TouchableOpacity, View } from 'react-native';
import { getApiErrorMessage } from '@/services/apiError';
import { ErrorState } from '@/components/ErrorState';
import { LoadingState } from '@/components/LoadingState';
import { goToParent, resetOnFocus } from '@/navigation/resetOnFocus';
import { colors } from '@/theme';
import { spokenBRL } from '@/utils/a11y';
import { useBankAccounts } from '@/modules/openfinance/useBankAccounts';
import { accountKindLabel, billDatesText, cardLimitText, peopleSections, updatedAgoText } from '@/modules/openfinance/accounts';
import { lineAmountText } from '@/modules/openfinance/review';
import type { BankCardResponse, CardBillResponse } from '@/types/api';

function leave() {
  goToParent('accounts/index');
}

/** O valor para o leitor de tela: por extenso quando é em reais; na moeda dele quando não é. */
function spoken(amount: number, currency: string): string {
  return currency === 'BRL' ? spokenBRL(amount) : lineAmountText(amount, currency);
}

function BillRow({ bill, label }: { bill: CardBillResponse; label?: string }) {
  return (
    <View style={styles.billRow}>
      <View style={styles.flex}>
        {label ? <Text style={styles.billLabel}>{label}</Text> : null}
        <Text style={styles.muted}>{billDatesText(bill)}</Text>
        {typeof bill.minimumPayment === 'number' ? (
          <Text style={styles.muted}>Pagamento mínimo {lineAmountText(bill.minimumPayment, bill.currency)}</Text>
        ) : null}
      </View>
      <Text style={label ? styles.amount : styles.amountSmall} accessibilityLabel={spoken(bill.totalAmount, bill.currency)}>
        {lineAmountText(bill.totalAmount, bill.currency)}
      </Text>
    </View>
  );
}

function CardBlock({ card, now }: { card: BankCardResponse; now: number }) {
  const [showPrevious, setShowPrevious] = useState(false);
  const limit = cardLimitText(card);
  const updated = updatedAgoText(card.balanceAtUtc, now);
  const title = [card.bankName, card.name].filter((part) => !!part).join(' · ');
  return (
    <View style={styles.item}>
      <Text style={styles.itemTitle}>{title}</Text>
      <Text style={styles.muted}>
        {[card.brand, card.numberMasked ? `final ${card.numberMasked}` : null, updated].filter((part) => !!part).join(' · ')}
      </Text>

      {card.currentBill ? (
        <BillRow bill={card.currentBill} label="Fatura atual" />
      ) : (
        <Text style={styles.muted}>Nenhuma fatura a vencer.</Text>
      )}
      {card.nextBills.map((bill) => (
        <BillRow key={bill.id} bill={bill} label="Próxima fatura" />
      ))}

      {/* O "saldo" que o banco informa para o cartão não é a fatura: fica com o nome dele. */}
      <View style={styles.rowBetween}>
        <Text style={styles.muted}>Saldo do cartão</Text>
        <Text style={styles.muted} accessibilityLabel={spoken(card.balance, card.currency)}>{lineAmountText(card.balance, card.currency)}</Text>
      </View>
      {limit ? <Text style={styles.muted}>{limit}</Text> : null}

      {card.previousBills.length > 0 ? (
        <>
          <TouchableOpacity
            style={styles.collapse}
            onPress={() => setShowPrevious((value) => !value)}
            accessibilityRole="button"
            accessibilityLabel={`Faturas anteriores de ${card.name}, ${card.previousBills.length}`}
            accessibilityState={{ expanded: showPrevious }}
          >
            <Text style={styles.link}>Faturas anteriores ({card.previousBills.length})</Text>
            <Text style={styles.link}>{showPrevious ? '▾' : '▸'}</Text>
          </TouchableOpacity>
          {showPrevious ? card.previousBills.map((bill) => <BillRow key={bill.id} bill={bill} />) : null}
        </>
      ) : null}
    </View>
  );
}

function AccountsScreen() {
  const { data, isLoading, isError, error, refetch, isRefetching } = useBankAccounts();

  const backLink = (
    <TouchableOpacity onPress={leave} accessibilityRole="button" accessibilityLabel="Voltar para o Painel">
      <Text style={styles.back}>← Voltar</Text>
    </TouchableOpacity>
  );

  if (isError && !data) {
    return (
      <SafeAreaView style={styles.container}>
        <View style={styles.padded}>{backLink}</View>
        <ErrorState message={getApiErrorMessage(error, 'Não foi possível carregar as contas.')} onRetry={() => refetch()} />
      </SafeAreaView>
    );
  }
  if (isLoading || !data) return <LoadingState />;

  const sections = peopleSections(data);
  const now = Date.now();

  return (
    <SafeAreaView style={styles.container}>
      <ScrollView
        contentContainerStyle={styles.content}
        refreshControl={<RefreshControl refreshing={isRefetching} onRefresh={() => void refetch()} tintColor={colors.primaryLight} />}
      >
        {backLink}
        <Text style={styles.title} accessibilityRole="header">Contas</Text>
        <Text style={styles.subtitle}>
          Saldos e faturas que o grupo trouxe do banco pelo Open Finance. Todos do grupo veem as contas de todos.
        </Text>

        {sections.length === 0 ? (
          <Text style={styles.empty}>
            Nenhuma conta para mostrar. Quando alguém do grupo conectar um banco em Configurações, Open Finance, as contas aparecem aqui.
          </Text>
        ) : null}

        {sections.map((person) => (
          <View key={person.userId} style={styles.person}>
            <View style={styles.rowBetween}>
              <Text style={styles.personName} accessibilityRole="header">{person.title}</Text>
              {person.accounts.length > 0 ? (
                <Text style={styles.personTotal} accessibilityLabel={`Saldo em contas: ${spokenBRL(person.balanceBrl)}`}>
                  {lineAmountText(person.balanceBrl, 'BRL')}
                </Text>
              ) : null}
            </View>
            {person.hasOtherCurrency ? (
              <Text style={styles.muted}>A soma é só das contas em reais. Conta em outra moeda aparece abaixo, fora da soma.</Text>
            ) : null}

            {person.accounts.map((account) => (
              <View key={account.id} style={styles.item}>
                <View style={styles.rowBetween}>
                  <Text style={[styles.itemTitle, styles.flex]} numberOfLines={2}>
                    {[account.bankName, account.name].filter((part) => !!part).join(' · ')}
                  </Text>
                  <Text style={styles.amount} accessibilityLabel={spoken(account.balance, account.currency)}>
                    {lineAmountText(account.balance, account.currency)}
                  </Text>
                </View>
                <Text style={styles.muted}>
                  {[accountKindLabel(account), account.numberMasked ? `final ${account.numberMasked}` : null, updatedAgoText(account.balanceAtUtc, now)]
                    .filter((part) => !!part)
                    .join(' · ')}
                </Text>
              </View>
            ))}

            {person.cards.map((card) => (
              <CardBlock key={card.id} card={card} now={now} />
            ))}
          </View>
        ))}
      </ScrollView>
    </SafeAreaView>
  );
}

// Cada visita começa limpa e com os saldos que o servidor tem agora.
export default resetOnFocus(AccountsScreen);

const styles = StyleSheet.create({
  container: { flex: 1, backgroundColor: colors.background },
  padded: { paddingHorizontal: 20, paddingTop: 24 },
  content: { paddingHorizontal: 20, paddingTop: 24, paddingBottom: 32, gap: 10 },
  flex: { flex: 1 },
  back: { minHeight: 44, textAlignVertical: 'center', color: colors.primaryLight, fontSize: 15 },
  title: { fontSize: 24, fontWeight: '700', color: colors.text },
  subtitle: { fontSize: 14, color: colors.textMuted, lineHeight: 20 },
  muted: { fontSize: 13, color: colors.textMuted, lineHeight: 19 },
  empty: { fontSize: 14, color: colors.textSubtle, lineHeight: 20, paddingVertical: 12 },
  person: { marginTop: 12, gap: 8 },
  personName: { fontSize: 18, fontWeight: '700', color: colors.text },
  personTotal: { fontSize: 16, fontWeight: '700', color: colors.text },
  item: { backgroundColor: colors.surface, borderRadius: 12, borderWidth: 1, borderColor: colors.border, padding: 12, gap: 4 },
  itemTitle: { fontSize: 15, fontWeight: '600', color: colors.text },
  rowBetween: { flexDirection: 'row', alignItems: 'baseline', justifyContent: 'space-between', gap: 8 },
  amount: { fontSize: 15, fontWeight: '700', color: colors.text },
  amountSmall: { fontSize: 14, fontWeight: '600', color: colors.textSubtle },
  billRow: { flexDirection: 'row', alignItems: 'flex-start', justifyContent: 'space-between', gap: 8, paddingVertical: 6, borderTopWidth: 1, borderTopColor: colors.border },
  billLabel: { fontSize: 14, fontWeight: '600', color: colors.text },
  collapse: { flexDirection: 'row', alignItems: 'center', justifyContent: 'space-between', minHeight: 44 },
  link: { color: colors.primaryLight, fontSize: 14, fontWeight: '600' },
});
