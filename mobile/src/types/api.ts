// AC-011: Shared API response types matching backend contracts

export interface AuthUserResponse {
  readonly id: string;
  readonly email: string;
  readonly name: string;
  /** Ausente em servidor antigo. Conta não confirmada continua funcionando por inteiro. */
  readonly emailVerified?: boolean;
}

export interface ForgotPasswordResponse {
  readonly message: string;
}

export interface AuthResponse {
  readonly user: AuthUserResponse;
  readonly accessToken: string;
  readonly refreshToken: string;
}

export interface CreateCoupleResponse {
  readonly coupleId: string;
  readonly joinCode: string;
  readonly accessToken: string;
  /** Só vem quando o usuário não tinha refresh token válido (ex.: foi removido de um grupo). */
  readonly refreshToken?: string | null;
}

export interface CoupleMemberResponse {
  readonly userId: string;
  readonly name: string;
  readonly email: string;
}

export interface JoinCoupleResponse {
  readonly coupleId: string;
  readonly members: readonly CoupleMemberResponse[];
  readonly accessToken: string;
  /** Só vem quando o usuário não tinha refresh token válido (ex.: foi removido de um grupo). */
  readonly refreshToken?: string | null;
}

export interface GetCoupleMeResponse {
  readonly coupleId: string;
  readonly joinCode: string;
  readonly createdAtUtc: string;
  readonly members: readonly CoupleMemberResponse[];
  /** Quem criou (ou herdou) o grupo; só ele remove membros e gera novo código. Ausente em servidor antigo. */
  readonly ownerUserId?: string | null;
  /** Fim da validade do código de convite (UTC). Ausente em servidor antigo. */
  readonly joinCodeExpiresAtUtc?: string;
}

export interface LeaveCoupleResponse {
  readonly accessToken: string;
  readonly refreshToken: string;
  /** Grupo que ficou ativo depois da saída (outro grupo do usuário) ou null. Ausente em servidor antigo. */
  readonly activeCoupleId?: string | null;
}

export interface MyGroupMemberResponse {
  readonly userId: string;
  readonly name: string;
}

/** Um dos grupos do usuário. Grupos não têm nome próprio: `name` descreve quem mais está nele. */
export interface MyGroupResponse {
  readonly coupleId: string;
  readonly name: string;
  readonly isOwner: boolean;
  readonly isActive: boolean;
  readonly joinedAtUtc: string;
  readonly members: readonly MyGroupMemberResponse[];
}

export interface MyGroupsResponse {
  readonly activeCoupleId: string | null;
  readonly maxGroups: number;
  readonly groups: readonly MyGroupResponse[];
}

export interface SwitchCoupleResponse {
  readonly coupleId: string;
  readonly accessToken: string;
  readonly refreshToken: string;
}

export interface RegenerateJoinCodeResponse {
  readonly joinCode: string;
  readonly joinCodeExpiresAtUtc: string;
}

export interface GoalDto {
  readonly id: string;
  readonly createdByUserId: string;
  readonly title: string;
  readonly description: string | null;
  readonly targetAmount: number;
  /** Progresso unificado: valor guardado manualmente + transações vinculadas. */
  readonly currentAmount: number;
  readonly currency: string;
  readonly deadline: string;
  readonly status: string;
  readonly createdAtUtc: string;
  readonly updatedAtUtc: string;
  /** Parte guardada manualmente (ausente em servidor antigo: currentAmount já era só o manual). */
  readonly manualAmount?: number;
  /** Parte vinda das transações vinculadas. */
  readonly linkedAmount?: number;
  readonly progressPercent?: number;
  readonly isAchieved?: boolean;
}

export interface GoalProgressSummaryItem {
  readonly id: string;
  readonly title: string;
  readonly targetAmount: number;
  readonly currentAmount: number;
  readonly progressPercent: number;
  readonly isAchieved: boolean;
  readonly deadline: string;
  readonly manualAmount?: number;
  readonly linkedAmount?: number;
}

export interface GoalsProgressSummaryResponse {
  readonly goals: readonly GoalProgressSummaryItem[];
}

export interface GetGoalsResponse {
  readonly totalCount: number;
  readonly items: readonly GoalDto[];
}

export interface PartnerBreakdownResponse {
  readonly userId: string;
  readonly totalAmount: number;
}

// Matches backend GetDashboardResponse
export interface DashboardResponse {
  readonly totalExpenses: number;
  readonly expensesByCategory: Record<string, number>;
  readonly partnerBreakdown: readonly PartnerBreakdownResponse[];
  readonly transactionCount: number;
  readonly periodStart: string;
  readonly periodEnd: string;
  readonly generatedAtUtc: string;
}

// Matches backend TransactionResponse
export interface TransactionResponse {
  readonly id: string;
  readonly userId: string;
  readonly authorName: string;
  readonly bank: string;
  readonly amount: number;
  readonly currency: string;
  readonly eventTimestampUtc: string;
  readonly description: string | null;
  readonly merchant: string | null;
  readonly category: string;
  readonly source: string;
  readonly createdAtUtc: string;
}

// Matches backend GetTransactionsResponse
export interface GetTransactionsResponse {
  readonly totalCount: number;
  readonly page: number;
  readonly pageSize: number;
  readonly items: readonly TransactionResponse[];
}

export interface GetCashFlowResponse {
  readonly horizon: number;
  readonly historicalPeriodStart: string;
  readonly historicalPeriodEnd: string;
  readonly transactionCount: number;
  readonly totalHistoricalSpend: number;
  readonly averageDailySpend: number;
  readonly projectedSpend: number;
  readonly categoryBreakdown: Record<string, number>;
  readonly assumptions: string;
  readonly generatedAtUtc: string;
  /** Mês corrente no fuso do Brasil ("AAAA-MM"). */
  readonly month: string;
  /** Renda do mês: fontes de renda do grupo, com as recorrentes já aplicadas. */
  readonly monthIncome: number;
  readonly monthSpentToDate: number;
  /** Média diária usada na previsão (do mês atual; com menos de 3 dias, a do mês anterior). */
  readonly forecastDailyAverage: number;
  readonly remainingDays: number;
  readonly forecastRemainingSpend: number;
  /** Saldo previsto ao fim do mês = renda − gasto até hoje − gasto previsto dos dias restantes. */
  readonly projectedMonthEndBalance: number;
}

export interface NotificationSettingsResponse {
  readonly userId: string;
  readonly lowBalanceEnabled: boolean;
  readonly largeTransactionEnabled: boolean;
  readonly billReminderEnabled: boolean;
  readonly updatedAtUtc: string;
}

export interface UpdateNotificationSettingsRequest {
  readonly lowBalanceEnabled?: boolean;
  readonly largeTransactionEnabled?: boolean;
  readonly billReminderEnabled?: boolean;
}

export interface RefreshResponse {
  readonly accessToken: string;
  readonly refreshToken?: string;
}

export interface ApiError {
  readonly message: string;
  readonly statusCode: number;
}

// --- Budget ---
export interface BudgetAllocationResponse {
  readonly id: string;
  readonly category: string;
  readonly allocatedAmount: number;
  readonly currency: string;
  readonly actualSpent: number;
  readonly remaining: number;
}

export interface BudgetPlanResponse {
  readonly id: string;
  readonly month: string;
  readonly grossIncome: number;
  readonly currency: string;
  readonly allocations: readonly BudgetAllocationResponse[];
  readonly budgetGap: number;
  readonly createdAtUtc: string;
  readonly updatedAtUtc: string;
}

export interface CreateBudgetPlanRequest {
  readonly month: string;
  readonly grossIncome: number;
  readonly currency: string;
}

export interface AllocationItemRequest {
  readonly category: string;
  readonly allocatedAmount: number;
  readonly currency: string;
}

export interface ReplaceAllocationsRequest {
  readonly allocations: readonly AllocationItemRequest[];
}

export interface UpdateIncomeRequest {
  readonly grossIncome: number;
  readonly currency?: string;
}

export interface UpdateIncomeResponse {
  readonly planId: string;
  readonly month: string;
  readonly grossIncome: number;
  readonly currency: string;
}

// --- Income Sources ---
export interface IncomeSourceResponse {
  readonly id: string;
  readonly userId: string;
  readonly name: string;
  readonly amount: number;
  readonly currency: string;
  readonly isShared: boolean;
  readonly isRecurring: boolean;
  readonly createdAtUtc: string;
  readonly updatedAtUtc: string;
}

export interface IncomeGroupResponse {
  readonly userId: string | null;
  readonly userName: string | null;
  readonly sources: readonly IncomeSourceResponse[];
  readonly total: number;
}

export interface MonthlyIncomeResponse {
  readonly month: string;
  readonly currency: string;
  readonly personalIncome: IncomeGroupResponse;
  readonly partnerIncome: IncomeGroupResponse | null;
  readonly sharedIncome: IncomeGroupResponse;
  readonly coupleTotal: number;
}

export interface CreateIncomeSourceRequest {
  readonly month: string;
  readonly name: string;
  readonly amount: number;
  readonly currency: string;
  readonly isShared: boolean;
  readonly isRecurring?: boolean;
}

export interface UpdateIncomeSourceRequest {
  readonly name?: string;
  readonly amount?: number;
  readonly isShared?: boolean;
  readonly isRecurring?: boolean;
}

// --- OCR ---
export interface OcrUploadResponse {
  readonly uploadId: string;
}

export interface OcrStatusResponse {
  readonly status: string;
  readonly errorCode?: string;
  readonly quotaResetDate?: string;
}

export interface OcrCandidateResponse {
  readonly index: number;
  readonly date: string;
  readonly description: string;
  readonly amount: number;
  readonly currency: string;
  readonly confidence: number;
  readonly duplicateSuspected: boolean;
  readonly suggestedCategory?: string;
  /** O que já aconteceu com a linha; a API antiga não envia (tudo pendente). */
  readonly lineState?: 'Pending' | 'Confirmed' | 'Discarded';
}

/** Entrada (crédito) do extrato: aparece na revisão só como informação, nunca é importada. */
export interface OcrCreditResponse {
  readonly date: string;
  readonly description: string;
  readonly amount: number;
  readonly currency: string;
}

export interface OcrResultsResponse {
  readonly candidates: readonly OcrCandidateResponse[];
  readonly credits?: readonly OcrCreditResponse[];
  readonly creditsCount?: number;
}

export interface OcrCategoryOverride {
  readonly index: number;
  readonly category: string;
}

/** Edição feita pelo usuário na revisão; só os campos alterados são enviados. */
export interface OcrCandidateEdit {
  readonly index: number;
  readonly description?: string;
  readonly amount?: number;
}

export interface OcrConfirmRequest {
  readonly selectedIndices: readonly number[];
  readonly categoryOverrides?: readonly OcrCategoryOverride[];
  readonly candidateEdits?: readonly OcrCandidateEdit[];
  /** true: as linhas não selecionadas ficam pendentes para confirmar depois; omitido, a importação fecha. */
  readonly keepJobOpen?: boolean;
  /** Linhas descartadas de vez (deixam de manter a importação aberta). */
  readonly discardedIndices?: readonly number[];
}

export interface OcrConfirmResponse {
  readonly transactionsCreated: number;
  readonly duplicatesSkipped?: number;
  /** Linhas ainda pendentes depois desta chamada (0 = importação fechada). */
  readonly remainingLines?: number;
}

/** Importação com linhas ainda por revisar, listada na tela de importar extrato. */
export interface OcrOpenImport {
  readonly uploadId: string;
  readonly fileName?: string | null;
  readonly createdAtUtc: string;
  readonly pendingLines: number;
  readonly totalLines: number;
  readonly creditsCount: number;
}

export interface OcrOpenImportsResponse {
  readonly imports: readonly OcrOpenImport[];
}

// --- AI Chat ---
export interface ChatHistoryItem {
  readonly role: 'user' | 'model';
  readonly content: string;
}

export interface ChatResponse {
  readonly reply: string;
}

// --- Categories ---
export interface CategoryItemResponse {
  readonly key: string;
  readonly label: string;
}

export interface CategoriesResponse {
  readonly categories: readonly CategoryItemResponse[];
}

// --- Reports ---
export interface CategorySpending {
  readonly name: string;
  readonly total: number;
  readonly percentage: number;
  readonly color: string;
}

export interface SpendingByCategoryResponse {
  readonly categories: readonly CategorySpending[];
}

export interface MonthlyTrend {
  readonly month: string;
  readonly income: number;
  readonly expense: number;
  readonly net: number;
}

export interface MonthlyTrendsResponse {
  readonly months: readonly MonthlyTrend[];
}

// --- Open Finance (Meu Pluggy) ---
// A API nunca devolve o Client Secret nem o Client ID inteiro: só o `clientIdHint` (4 últimos caracteres).
export type BankConnectionStatus = 'Active' | 'Error' | 'Disconnected';

export interface BankAccountResponse {
  readonly id: string;
  /** 'BANK' | 'CREDIT', como o Pluggy envia. */
  readonly type: string;
  /** 'CHECKING_ACCOUNT' | 'SAVINGS_ACCOUNT' | 'CREDIT_CARD', como o Pluggy envia. */
  readonly subtype: string | null;
  readonly name: string;
  readonly marketingName: string | null;
  /** Só os últimos caracteres do número da conta/cartão. */
  readonly numberMasked: string | null;
  readonly currency: string;
  readonly balance: number;
  readonly balanceAtUtc: string;
  readonly creditLimit: number | null;
  readonly availableCreditLimit: number | null;
  /** 'AAAA-MM-DD'. */
  readonly balanceCloseDate: string | null;
  readonly balanceDueDate: string | null;
  readonly minimumPayment: number | null;
  readonly brand: string | null;
  readonly syncEnabled: boolean;
}

export interface BankItemResponse {
  readonly id: string;
  /** Nome do banco. */
  readonly connectorName: string;
  readonly status: string;
  readonly executionStatus: string | null;
  readonly lastUpdatedAtUtc: string | null;
  readonly lastErrorMessage: string | null;
  readonly accounts: readonly BankAccountResponse[];
}

export interface BankConnectionResponse {
  readonly id: string;
  readonly label: string;
  readonly userId: string;
  readonly userName: string;
  /** A conexão é de quem está logado: só ele adiciona bancos, liga/desliga contas e desconecta. */
  readonly isMine: boolean;
  readonly status: BankConnectionStatus | string;
  readonly clientIdHint: string | null;
  readonly historyMonths: number;
  readonly lastSyncAtUtc: string | null;
  readonly lastErrorCode: string | null;
  readonly lastErrorMessage: string | null;
  readonly createdAtUtc: string;
  readonly items: readonly BankItemResponse[];
}

export interface OpenFinanceStatusResponse {
  /** false: o servidor não tem a configuração do Open Finance; nenhuma escrita funciona. */
  readonly available: boolean;
  readonly connections: readonly BankConnectionResponse[];
}

export interface TestCredentialsResponse {
  readonly valid: boolean;
}

export interface CreateBankConnectionRequest {
  readonly label: string;
  readonly clientId: string;
  readonly clientSecret: string;
  readonly historyMonths?: number;
}

// --- Análise com IA: ativação do grupo e consumo (GET /api/v1/ai/status, /ai/usage) ---
export interface AiAcceptedByResponse {
  readonly userId: string;
  readonly name: string;
  readonly acceptedAtUtc: string;
}

export interface AiStatusResponse {
  /** A IA existe no servidor agora (não está desligada e há provedor configurado). */
  readonly available: boolean;
  /** O grupo ativou: há ao menos um aceite em vigor de quem ainda é membro. */
  readonly enabled: boolean;
  readonly consentVersion: number;
  readonly acceptedBy: readonly AiAcceptedByResponse[];
  readonly myAcceptance: { readonly acceptedAtUtc: string } | null;
  /** A tela de boas-vindas da IA ainda é devida a esta pessoa neste grupo. */
  readonly onboardingPending: boolean;
  readonly weeklyEmailEnabled: boolean;
  readonly emailVerified: boolean;
  readonly emailConfigured: boolean;
  readonly providers: readonly { readonly name: string; readonly country: string; readonly trainsOnData: boolean }[];
  readonly features: { readonly assistant: boolean; readonly insights: boolean; readonly education: boolean; readonly weeklyEmail: boolean };
  readonly budget: { readonly callsToday: number; readonly callLimit: number; readonly resetsAtLocal: string };
}

export interface AiUsageDayResponse {
  /** Dia de Brasília, yyyy-MM-dd. */
  readonly day: string;
  readonly calls: number;
  readonly inputTokens: number;
  readonly outputTokens: number;
  readonly failures: number;
}

export interface AiUsageProviderResponse {
  readonly name: string;
  readonly model: string;
  readonly calls: number;
  /** Cota diária do modelo; null enquanto ela não é conhecida. */
  readonly limit: number | null;
  readonly percentUsed: number | null;
  readonly exhaustedToday: boolean;
}

export interface AiUsageResponse {
  readonly days: readonly AiUsageDayResponse[];
  readonly byFeature: readonly { readonly feature: string; readonly calls: number; readonly inputTokens: number; readonly outputTokens: number }[];
  readonly providersToday: readonly AiUsageProviderResponse[];
  readonly groupBudget: {
    readonly callsToday: number;
    readonly callLimit: number;
    readonly tokensToday: number;
    readonly tokenLimit: number;
    readonly resetsAtLocal: string;
  };
}

// --- Open Finance: sincronização e revisão do banco ---

export interface SyncRunResponse {
  readonly id: string;
  readonly connectionId: string;
  /** 'Pending' | 'Running' | 'Done' | 'Failed' */
  readonly status: string;
  /** 'User' | 'AppOpen' | 'Scheduler' */
  readonly triggeredBy: string;
  readonly createdAtUtc: string;
  readonly startedAtUtc: string | null;
  readonly finishedAtUtc: string | null;
  readonly transactionsNew: number;
  readonly transactionsUpdated: number;
  readonly errorCode: string | null;
  /** Em português, pronto para exibir. */
  readonly errorMessage: string | null;
}

export interface BankReviewLineResponse {
  readonly id: string;
  /** "AAAA-MM-DD": o dia do lançamento no Brasil. */
  readonly day: string;
  readonly merchant: string | null;
  readonly description: string | null;
  /** O valor da despesa, sempre positivo. Não é editável. */
  readonly amount: number;
  readonly currency: string;
  /** Chave de categoria do app (ex.: 'ALIMENTACAO'). */
  readonly suggestedCategory: string;
  /** 'Posted' | 'Pending' (ainda não efetivado no banco: não pode ser confirmado). */
  readonly bankStatus: string;
  readonly bankName: string;
  readonly accountName: string;
  readonly installmentNumber: number | null;
  readonly installmentTotal: number | null;
}

export interface BankReviewMonthResponse {
  /** "AAAA-MM" */
  readonly month: string;
  readonly pending: number;
}

export interface BankReviewResponse {
  readonly month: string;
  readonly expenses: readonly BankReviewLineResponse[];
  readonly discarded: readonly BankReviewLineResponse[];
  /** Soma das despesas esperando neste mês, só as em reais. */
  readonly pendingTotalBrl: number;
  /** Quantas despesas podem ser confirmadas, em todos os meses (sem as pendentes no banco, sem valor ou em outra moeda). */
  readonly pendingAllMonths: number;
  /** Por mês, tudo o que espera na revisão (também o que só pode ser descartado). */
  readonly pendingByMonth: readonly BankReviewMonthResponse[];
}

export interface ConfirmBankReviewRequest {
  readonly expenses?: ReadonlyArray<{ readonly id: string; readonly category?: string; readonly description?: string }>;
  readonly discard?: readonly string[];
}

export interface ConfirmBankReviewResponse {
  readonly created: ReadonlyArray<{ readonly id: string; readonly transactionId: string }>;
  readonly discarded: readonly string[];
  readonly alreadyConfirmed: number;
  /** Linhas enviadas para confirmar que não têm valor ou estão em outra moeda: o servidor não as confirma e elas continuam na revisão. */
  readonly skipped?: readonly string[];
  /** As de `skipped` que foram puladas por estarem em outra moeda. */
  readonly skippedOtherCurrency?: readonly string[];
}

export interface RestoreBankReviewResponse {
  readonly restored: readonly string[];
}

// ─── Assinaturas e recorrências (GET /api/v1/ai/recurring) ────────────────────
export type RecurringKind = 'Subscription' | 'FixedBill' | 'Installment' | 'Habit';
/** `Irregular`: pequeno gasto frequente (4 ou mais compras em 30 dias), sem intervalo fixo. */
export type RecurringCadence = 'Weekly' | 'Monthly' | 'Yearly' | 'Irregular';
export type RecurringStatus = 'Active' | 'SuspectedDormant' | 'Stopped';
export type RecurringFlag = 'Forgotten' | 'PriceIncrease' | 'New' | 'ChargedAfterCancel';
export type RecurringOverride = 'NotRecurring' | 'Cancelled' | 'Subscription' | 'FixedBill';

export interface RecurringItemResponse {
  readonly id: string;
  readonly name: string;
  readonly kind: RecurringKind;
  readonly variableAmount: boolean;
  readonly cadence: RecurringCadence;
  /** O valor típico de hoje (a mediana das 3 últimas cobranças quando o valor varia). */
  readonly amount: number;
  readonly lastAmount: number;
  readonly previousAmount?: number | null;
  readonly annualCost: number;
  readonly occurrences: number;
  readonly missedCount?: number;
  /** Datas no calendário de Brasília, "AAAA-MM-DD". */
  readonly firstSeen: string;
  readonly lastSeen: string;
  readonly nextExpected?: string | null;
  readonly status: RecurringStatus;
  readonly flags: readonly RecurringFlag[];
  /** `Low`: parcela provável (uma marca "n/N" só). */
  readonly confidence?: 'High' | 'Medium' | 'Low';
  readonly category: string;
  readonly person?: { readonly userId: string; readonly name: string } | null;
  readonly installment?: {
    readonly number: number;
    readonly total: number;
    readonly remainingAmount: number;
    /** "AAAA-MM" da última parcela. */
    readonly endMonth: string;
  } | null;
  readonly override?: RecurringOverride | null;
}

export interface RecurringListResponse {
  readonly monthlyTotal: number;
  readonly annualTotal: number;
  readonly detectedAtUtc: string;
  readonly subscriptions: readonly RecurringItemResponse[];
  readonly fixedBills: readonly RecurringItemResponse[];
  readonly installments: readonly RecurringItemResponse[];
  readonly habits: readonly RecurringItemResponse[];
  readonly hidden: readonly RecurringItemResponse[];
  readonly installmentsByMonth?: readonly { readonly month: string; readonly amount: number }[];
}

export interface RecurringChargeResponse {
  readonly transactionId: string;
  readonly date: string;
  readonly amount: number;
  readonly merchant: string;
}

export interface RecurringChargesResponse {
  readonly transactions: readonly RecurringChargeResponse[];
}
