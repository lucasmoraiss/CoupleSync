// AC-011: Shared API response types matching backend contracts

export interface AuthUserResponse {
  readonly id: string;
  readonly email: string;
  readonly name: string;
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
}

export interface GetCoupleMeResponse {
  readonly coupleId: string;
  readonly joinCode: string;
  readonly createdAtUtc: string;
  readonly members: readonly CoupleMemberResponse[];
}

export interface GoalDto {
  readonly id: string;
  readonly createdByUserId: string;
  readonly title: string;
  readonly description: string | null;
  readonly targetAmount: number;
  readonly currency: string;
  readonly deadline: string;
  readonly status: string;
  readonly createdAtUtc: string;
  readonly updatedAtUtc: string;
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
  readonly bank: string;
  readonly amount: number;
  readonly currency: string;
  readonly eventTimestampUtc: string;
  readonly description: string | null;
  readonly merchant: string | null;
  readonly category: string;
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
}

export interface UpdateIncomeSourceRequest {
  readonly name?: string;
  readonly amount?: number;
  readonly isShared?: boolean;
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
}

export interface OcrResultsResponse {
  readonly candidates: readonly OcrCandidateResponse[];
}

export interface OcrCategoryOverride {
  readonly index: number;
  readonly category: string;
}

export interface OcrConfirmRequest {
  readonly selectedIndices: readonly number[];
  readonly categoryOverrides?: readonly OcrCategoryOverride[];
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

