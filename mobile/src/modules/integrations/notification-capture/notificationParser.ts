// AC-003: Bank notification parser — applies bank-specific regex patterns
// to extract structured transaction data from raw notification text.
// Never stores or logs credentials; only processes bank transaction notifications.
//
// Pattern maintenance: update notification-patterns.json and ship via app release.
// The JSON must stay strict JSON (no comments) so it can also be loaded by Jest.
//
// Only EXPENSES are turned into events (the backend records expenses only):
//   1. text that looks like money coming in (Pix recebido, estorno, depósito…) → ignored as credit;
//   2. declined purchases and advertising → ignored;
//   3. a bank-specific purchase / payment / Pix-sent pattern must match — there is no
//      "any text with R$" fallback; everything else is discarded.
import patterns from './notification-patterns.json';

export interface ParsedTransactionEvent {
  /** Resolved bank name, e.g. "Nubank" */
  readonly bank: string;
  /** Android package name of the source app */
  readonly packageName: string;
  /** Extracted amount in numeric form (e.g. 123.45) */
  readonly amount: number;
  /** Merchant or counter-party name, if extractable */
  readonly merchant: string | null;
  /** Pattern ID that matched, for diagnostics */
  readonly matchedPatternId: string;
  /** ISO 8601 timestamp from the notification */
  readonly receivedAt: string;
  /** Sanitised raw text, capped at 512 chars */
  readonly rawText: string;
}

export type IgnoreReason = 'unsupported-bank' | 'credit' | 'not-a-transaction' | 'no-match';

export type NotificationDecision =
  | { readonly action: 'upload'; readonly event: ParsedTransactionEvent }
  | { readonly action: 'ignore'; readonly reason: IgnoreReason };

interface BankPattern {
  id: string;
  regex: string;
  amountGroup: number;
  merchantGroup: number | null;
}

interface BankEntry {
  name: string;
  packageNames: string[];
  patterns: BankPattern[];
}

// ── Placeholders usable inside the JSON regexes (each one is a single capture group) ──
/** Brazilian amount: "R$ 45,90", "R$ 1.234,56". */
const AMOUNT_FRAGMENT = 'R\\$\\s*(\\d{1,3}(?:\\.\\d{3})+,\\d{2}|\\d+,\\d{2})(?!\\d)';
/**
 * Merchant / counter-party: stops at a separator ("|", " - "), at "aprovada", at the card
 * reference ("no cartão final 1234"), at the end of the sentence or at the end of the text.
 */
const MERCHANT_FRAGMENT =
  '(\\S.*?)(?=\\s*\\||\\s+[-–]\\s|\\s+(?:foi\\s+)?aprovad[ao]\\b|\\s+(?:para\\s+o|no|do|com(?:\\s+o)?)\\s+cart[aã]o\\b|[.!;]\\s|[.!;]?\\s*$)';

function compile(source: string): RegExp {
  return new RegExp(
    source.replace(/\{AMOUNT\}/g, AMOUNT_FRAGMENT).replace(/\{MERCHANT\}/g, MERCHANT_FRAGMENT),
    'i',
  );
}

interface CompiledBank {
  readonly name: string;
  readonly patterns: ReadonlyArray<BankPattern & { readonly compiled: RegExp }>;
}

const creditMarkers: RegExp[] = (patterns.creditMarkers as string[]).map(compile);
const notTransactionMarkers: RegExp[] = (patterns.notTransactionMarkers as string[]).map(compile);

/** Build a reverse lookup: packageName → bank (with pre-compiled patterns) */
const packageToBankMap = new Map<string, CompiledBank>();
for (const bank of patterns.banks as BankEntry[]) {
  const compiledBank: CompiledBank = {
    name: bank.name,
    patterns: bank.patterns.map((p) => ({ ...p, compiled: compile(p.regex) })),
  };
  for (const pkg of bank.packageNames) {
    packageToBankMap.set(pkg, compiledBank);
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
 * Decide whether a notification must become an expense on the server.
 * Credits, declined purchases, advertising and anything that does not match a
 * bank-specific expense pattern are ignored (with the reason, for diagnostics/tests).
 */
export function classifyNotification(
  packageName: string,
  rawTitle: string,
  rawBody: string,
  timestampMs: number,
): NotificationDecision {
  const bank = packageToBankMap.get(packageName);
  if (!bank) {
    return { action: 'ignore', reason: 'unsupported-bank' };
  }

  // Combine title + body for matching; body is the primary carrier of transaction data
  const combinedText = sanitise(`${rawTitle} ${rawBody}`);

  if (creditMarkers.some((re) => re.test(combinedText))) {
    return { action: 'ignore', reason: 'credit' };
  }
  if (notTransactionMarkers.some((re) => re.test(combinedText))) {
    return { action: 'ignore', reason: 'not-a-transaction' };
  }

  const safeTimestampMs = timestampMs > 0 ? timestampMs : Date.now();
  const receivedAt = new Date(safeTimestampMs).toISOString();

  for (const pattern of bank.patterns) {
    const match = pattern.compiled.exec(combinedText);
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
      action: 'upload',
      event: {
        bank: bank.name,
        packageName,
        amount,
        merchant: merchant ? sanitise(merchant) : null,
        matchedPatternId: pattern.id,
        receivedAt,
        rawText: combinedText,
      },
    };
  }

  // No expense pattern matched — potentially unsupported notification format
  return { action: 'ignore', reason: 'no-match' };
}

/**
 * Attempt to parse a notification for a known bank.
 * Returns null when the notification must not be uploaded (see classifyNotification).
 */
export function parseNotification(
  packageName: string,
  rawTitle: string,
  rawBody: string,
  timestampMs: number,
): ParsedTransactionEvent | null {
  const decision = classifyNotification(packageName, rawTitle, rawBody, timestampMs);
  return decision.action === 'upload' ? decision.event : null;
}

/**
 * Returns whether the package name belongs to a supported bank.
 * Used by the bridge to pre-filter notifications before parsing.
 */
export function isSupportedBank(packageName: string): boolean {
  return packageToBankMap.has(packageName);
}
