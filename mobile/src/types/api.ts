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
}

export interface OcrConfirmResponse {
  readonly transactionsCreated: number;
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

