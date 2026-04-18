// AC-003: Bank notification parser — applies bank-specific regex patterns
// to extract structured transaction data from raw notification text.
// Never stores or logs credentials; only processes bank transaction notifications.
import patterns from './notification-patterns.json';

export type TransactionType = 'debit' | 'credit';

export interface ParsedTransactionEvent {
  /** Resolved bank name, e.g. "Nubank" */
  readonly bank: string;
  /** Android package name of the source app */
  readonly packageName: string;
  /** Extracted amount in numeric form (e.g. 123.45) */
  readonly amount: number;
  /** Merchant or counter-party name, if extractable */
  readonly merchant: string | null;
  /** Whether this is a spend (debit) or receipt (credit) */
  readonly transactionType: TransactionType;
  /** Pattern ID that matched, for diagnostics */
  readonly matchedPatternId: string;
  /** ISO 8601 timestamp from the notification */
  readonly receivedAt: string;
  /** Sanitised raw text, capped at 512 chars */
  readonly rawText: string;
}

interface BankPattern {
  id: string;
  transactionType: TransactionType;
  regex: string;
  amountGroup: number;
  merchantGroup: number | null;
}

interface BankEntry {
  name: string;
  packageNames: string[];
  patterns: BankPattern[];
}

const bankEntries: BankEntry[] = patterns.banks as BankEntry[];

/** Build a reverse lookup: packageName → BankEntry */
const packageToBankMap = new Map<string, BankEntry>();
for (const bank of bankEntries) {
  for (const pkg of bank.packageNames) {
    packageToBankMap.set(pkg, bank);
  }
}

/**
 * Convert Brazilian number format to a JS number.
 * "1.234,56" → 1234.56
 */
function parseBrazilianAmount(raw: string): number | null {
  const cleaned = raw.replace(/\./g, '').replace(',', '.');
  const value = parseFloat(cleaned);
  return isFinite(value) && value > 0 ? value : null;
}

/**
 * Sanitise text: trim, collapse whitespace, strip any control characters.
 * Returns at most 512 characters.
 */
function sanitise(text: string): string {
  return text
    .replace(/[\x00-\x1F\x7F]+/g, ' ')
    .replace(/\s+/g, ' ')
    .trim()
    .slice(0, 512);
}

/**
 * Attempt to parse a notification for a known bank.
 * Returns null if the package is unknown or no pattern matches.
 */
export function parseNotification(
  packageName: string,
  rawTitle: string,
  rawBody: string,
  timestampMs: number,
): ParsedTransactionEvent | null {
  const bank = packageToBankMap.get(packageName);
  if (!bank) {
    return null; // Not a supported bank — do not upload
  }

  // Combine title + body for matching; body is the primary carrier of transaction data
  const combinedText = sanitise(`${rawTitle} ${rawBody}`);
  const safeTimestampMs = timestampMs > 0 ? timestampMs : Date.now();
  const receivedAt = new Date(safeTimestampMs).toISOString();

  for (const pattern of bank.patterns) {
    const regex = new RegExp(pattern.regex, 'i');
    const match = regex.exec(combinedText);
    if (!match) continue;

    const rawAmount = match[pattern.amountGroup] ?? null;
    if (!rawAmount) continue;

    const amount = parseBrazilianAmount(rawAmount);
    if (amount === null) continue;

    const merchant =
      pattern.merchantGroup !== null
        ? (match[pattern.merchantGroup]?.trim() ?? null)
        : null;

    return {
      bank: bank.name,
      packageName,
      amount,
      merchant: merchant ? sanitise(merchant) : null,
      transactionType: pattern.transactionType,
      matchedPatternId: pattern.id,
      receivedAt,
      rawText: combinedText,
    };
  }

  return null; // No pattern matched — potentially unsupported notification format
}

/**
 * Returns whether the package name belongs to a supported bank.
 * Used by the bridge to pre-filter notifications before parsing.
 */
export function isSupportedBank(packageName: string): boolean {
  return packageToBankMap.has(packageName);
}
