// Como uma transação aparece na lista: o título é o estabelecimento (ou a descrição, ou o banco);
// a descrição vira subtítulo quando existe estabelecimento e ela diz algo diferente.

interface TransactionLabelSource {
  readonly merchant: string | null;
  readonly description: string | null;
  readonly bank: string;
}

function clean(value: string | null | undefined): string | null {
  const trimmed = value?.trim();
  return trimmed ? trimmed : null;
}

export function getTransactionTitle(tx: TransactionLabelSource): string {
  return clean(tx.merchant) ?? clean(tx.description) ?? tx.bank;
}

export function getTransactionSubtitle(tx: TransactionLabelSource): string | null {
  const merchant = clean(tx.merchant);
  const description = clean(tx.description);
  if (!merchant || !description) return null;
  return merchant.toLowerCase() === description.toLowerCase() ? null : description;
}
