// AC-011: Typed Axios API client with Authorization interceptor and 401 handler (refresh + retry)
import axios, { AxiosInstance, AxiosResponse } from 'axios';
import { router } from 'expo-router';
import { useSessionStore } from '@/state/sessionStore';
import { showToastGlobal } from '@/components/Toast/ToastProvider';
import { installAuthRefresh } from './authRefresh';
import { getApiErrorCode } from './apiError';
import type {
  AuthResponse,
  RefreshResponse,
  CreateCoupleResponse,
  JoinCoupleResponse,
  GetCoupleMeResponse,
  GoalDto,
  GetGoalsResponse,
  GoalsProgressSummaryResponse,
  DashboardResponse,
  TransactionResponse,
  GetTransactionsResponse,
  GetCashFlowResponse,
  NotificationSettingsResponse,
  UpdateNotificationSettingsRequest,
  BudgetPlanResponse,
  CreateBudgetPlanRequest,
  ReplaceAllocationsRequest,
  UpdateIncomeRequest,
  UpdateIncomeResponse,
  MonthlyIncomeResponse,
  IncomeSourceResponse,
  CreateIncomeSourceRequest,
  UpdateIncomeSourceRequest,
  OcrUploadResponse,
  OcrStatusResponse,
  OcrResultsResponse,
  OcrConfirmRequest,
  OcrConfirmResponse,
  ChatHistoryItem,
  ChatResponse,
  SpendingByCategoryResponse,
  CategoriesResponse,
  MonthlyTrendsResponse,
} from '@/types/api';

// 10.0.2.2 is the Android emulator alias for the host machine's localhost.
// For physical device on the same Wi-Fi, set EXPO_PUBLIC_API_BASE_URL to your machine's IP.
const BASE_URL = process.env.EXPO_PUBLIC_API_BASE_URL ?? 'http://10.0.2.2:5000';

if (__DEV__) {
  console.log('[apiClient] BASE_URL =', BASE_URL);
}

const axiosInstance: AxiosInstance = axios.create({
  baseURL: BASE_URL,
  headers: { 'Content-Type': 'application/json' },
  timeout: 30000,
});

// Response interceptor: on 403 COUPLE_REQUIRED show toast.
// Registered BEFORE the auth-refresh interceptor so that a retried request passes through it
// exactly once (the retry runs the whole chain again from inside the refresh interceptor).
axiosInstance.interceptors.response.use(
  (response) => response,
  async (error) => {
    // Bail immediately for canceled/aborted requests — do not trigger session or toast side effects
    if (axios.isCancel(error)) {
      return Promise.reject(error);
    }
    // AC-609: Surface COUPLE_REQUIRED with dedicated message
    if (error?.response?.status === 403 && getApiErrorCode(error) === 'COUPLE_REQUIRED') {
      showToastGlobal(
        'Conecte-se com seu parceiro primeiro para usar este recurso',
        'warning',
      );
      router.replace('/(auth)/couple-setup' as any);
    }
    return Promise.reject(error);
  }
);

// Request interceptor (Bearer token) + 401 handling with refresh-token renewal.
// The access token lasts 15 min; on the first 401 the session is renewed once via
// /api/v1/auth/refresh and the original request is retried. See authRefresh.ts.
installAuthRefresh(axiosInstance, {
  getTokens: () => {
    const { accessToken, refreshToken } = useSessionStore.getState();
    return { accessToken, refreshToken };
  },
  // Plain axios (no interceptors): the refresh call must not carry the expired Bearer token
  // nor re-enter the 401 handling.
  requestRefresh: async (refreshToken) => {
    const res = await axios.post<RefreshResponse>(
      `${BASE_URL}/api/v1/auth/refresh`,
      { refreshToken },
      { headers: { 'Content-Type': 'application/json' }, timeout: 30000 },
    );
    return res.data;
  },
  saveTokens: ({ accessToken, refreshToken }) =>
    useSessionStore.getState().setTokens(accessToken, refreshToken),
  onSessionExpired: async () => {
    await useSessionStore.getState().clearSession();
    showToastGlobal('Sua sessão expirou. Entre novamente.', 'warning');
    router.replace('/login' as any);
  },
});

/** Returns true when the error is a 403 with code COUPLE_REQUIRED (toast already shown globally). */
export function isCoupleRequiredError(error: unknown): boolean {
  return (
    axios.isAxiosError(error) &&
    error.response?.status === 403 &&
    getApiErrorCode(error) === 'COUPLE_REQUIRED'
  );
}

// --- Auth API ---
interface LoginRequest {
  email: string;
  password: string;
}

interface RegisterRequest {
  email: string;
  password: string;
  name: string;
}

export const authApiClient = {
  login: (data: LoginRequest): Promise<AxiosResponse<AuthResponse>> =>
    axiosInstance.post<AuthResponse>('/api/v1/auth/login', data),

  register: (data: RegisterRequest): Promise<AxiosResponse<AuthResponse>> =>
    axiosInstance.post<AuthResponse>('/api/v1/auth/register', data),
  // O refresh não é exposto aqui: é chamado só pelo tratamento de 401 (installAuthRefresh acima).
};

// --- Couple API ---
interface JoinCoupleRequestBody {
  joinCode: string;
}

export const coupleApiClient = {
  create: (): Promise<AxiosResponse<CreateCoupleResponse>> =>
    axiosInstance.post<CreateCoupleResponse>('/api/v1/couples'),

  join: (data: JoinCoupleRequestBody): Promise<AxiosResponse<JoinCoupleResponse>> =>
    axiosInstance.post<JoinCoupleResponse>('/api/v1/couples/join', data),

  getMyCouple: (): Promise<AxiosResponse<GetCoupleMeResponse>> =>
    axiosInstance.get<GetCoupleMeResponse>('/api/v1/couples/me'),
};

// --- Dashboard API ---
interface GetDashboardParams {
  startDate?: string;
  endDate?: string;
}

export const dashboardApiClient = {
  get: (params?: GetDashboardParams): Promise<AxiosResponse<DashboardResponse>> => {
    const qs = new URLSearchParams();
    if (params?.startDate) qs.append('startDate', params.startDate);
    if (params?.endDate) qs.append('endDate', params.endDate);
    const query = qs.toString();
    return axiosInstance.get<DashboardResponse>(`/api/v1/dashboard${query ? `?${query}` : ''}`);
  },
};

// --- Transactions API ---
interface GetTransactionsParams {
  page?: number;
  pageSize?: number;
  category?: string;
  startDate?: string;
  endDate?: string;
}

export const transactionsApiClient = {
  list: (params?: GetTransactionsParams): Promise<AxiosResponse<GetTransactionsResponse>> => {
    const qs = new URLSearchParams();
    if (params?.page != null) qs.append('page', String(params.page));
    if (params?.pageSize != null) qs.append('pageSize', String(params.pageSize));
    if (params?.category) qs.append('category', params.category);
    if (params?.startDate) qs.append('startDate', params.startDate);
    if (params?.endDate) qs.append('endDate', params.endDate);
    const query = qs.toString();
    return axiosInstance.get<GetTransactionsResponse>(`/api/v1/transactions${query ? `?${query}` : ''}`);
  },

  getById: (id: string): Promise<AxiosResponse<TransactionResponse>> =>
    axiosInstance.get<TransactionResponse>(`/api/v1/transactions/${id}`),

  updateCategory: (id: string, category: string): Promise<AxiosResponse<TransactionResponse>> =>
    axiosInstance.patch<TransactionResponse>(`/api/v1/transactions/${id}/category`, { category }),

  /** Edição parcial: só os campos enviados mudam; descrição vazia limpa a descrição. */
  update: (id: string, data: UpdateTransactionBody): Promise<AxiosResponse<TransactionResponse>> =>
    axiosInstance.patch<TransactionResponse>(`/api/v1/transactions/${id}`, data),

  /** Creates a transaction manually (without relying on OCR or push notifications). */
  createManual: (data: CreateManualTransactionBody): Promise<AxiosResponse<TransactionResponse>> =>
    axiosInstance.post<TransactionResponse>('/api/v1/transactions', data),

  delete: (id: string): Promise<AxiosResponse<void>> =>
    axiosInstance.delete<void>(`/api/v1/transactions/${id}`),
};

export interface UpdateTransactionBody {
  amount?: number;
  description?: string;
  merchant?: string;
  /** Instante UTC (ISO) da data/hora de Brasília escolhida. */
  eventTimestampUtc?: string;
  category?: string;
}

export interface CreateManualTransactionBody {
  amount: number;
  currency?: string;
  eventTimestampUtc?: string;
  description?: string;
  merchant?: string;
  category: string;
}

// --- Goals API ---
interface CreateGoalRequest {
  title: string;
  description?: string;
  targetAmount: number;
  currency?: string;
  deadline: string;
}

interface UpdateGoalRequest {
  title?: string;
  description?: string;
  targetAmount?: number;
  /** Valor guardado manualmente (o `currentAmount` legado é o total e não deve mais ser enviado). */
  manualAmount?: number;
  deadline?: string;
}

export const goalsApiClient = {
  list: (includeArchived = false): Promise<AxiosResponse<GetGoalsResponse>> =>
    axiosInstance.get<GetGoalsResponse>(`/api/v1/goals${includeArchived ? '?includeArchived=true' : ''}`),

  getById: (id: string): Promise<AxiosResponse<GoalDto>> =>
    axiosInstance.get<GoalDto>(`/api/v1/goals/${id}`),

  create: (data: CreateGoalRequest): Promise<AxiosResponse<GoalDto>> =>
    axiosInstance.post<GoalDto>('/api/v1/goals', data),

  update: (id: string, data: UpdateGoalRequest): Promise<AxiosResponse<GoalDto>> =>
    axiosInstance.patch<GoalDto>(`/api/v1/goals/${id}`, data),

  delete: (id: string): Promise<AxiosResponse<void>> =>
    axiosInstance.delete<void>(`/api/v1/goals/${id}`),

  progressSummary: (): Promise<AxiosResponse<GoalsProgressSummaryResponse>> =>
    axiosInstance.get<GoalsProgressSummaryResponse>('/api/v1/goals/progress-summary'),
};

// --- Cash Flow API ---
export const cashFlowApiClient = {
  get: (horizon: 30 | 90 = 30): Promise<AxiosResponse<GetCashFlowResponse>> =>
    axiosInstance.get<GetCashFlowResponse>(`/api/v1/cashflow?horizon=${horizon}`),
};

// --- Budget API ---
export const budgetApiClient = {
  upsertPlan: (data: CreateBudgetPlanRequest): Promise<AxiosResponse<BudgetPlanResponse>> =>
    axiosInstance.post<BudgetPlanResponse>('/api/v1/budgets', data),

  getCurrent: (): Promise<AxiosResponse<BudgetPlanResponse>> =>
    axiosInstance.get<BudgetPlanResponse>('/api/v1/budgets/current'),

  getByMonth: (month: string): Promise<AxiosResponse<BudgetPlanResponse>> =>
    axiosInstance.get<BudgetPlanResponse>(`/api/v1/budgets/${month}`),

  replaceAllocations: (planId: string, data: ReplaceAllocationsRequest): Promise<AxiosResponse<BudgetPlanResponse>> =>
    axiosInstance.put<BudgetPlanResponse>(`/api/v1/budgets/${planId}/allocations`, data),

  updateIncome: (data: UpdateIncomeRequest): Promise<AxiosResponse<UpdateIncomeResponse>> =>
    axiosInstance.patch<UpdateIncomeResponse>('/api/v1/budgets/income', data),
};

// --- Income Sources API ---
export const incomeApiClient = {
  getCurrent: (): Promise<AxiosResponse<MonthlyIncomeResponse>> =>
    axiosInstance.get<MonthlyIncomeResponse>('/api/v1/incomes/current'),

  getByMonth: (month: string): Promise<AxiosResponse<MonthlyIncomeResponse>> =>
    axiosInstance.get<MonthlyIncomeResponse>(`/api/v1/incomes/${month}`),

  create: (data: CreateIncomeSourceRequest): Promise<AxiosResponse<IncomeSourceResponse>> =>
    axiosInstance.post<IncomeSourceResponse>('/api/v1/incomes', data),

  update: (id: string, data: UpdateIncomeSourceRequest): Promise<AxiosResponse<IncomeSourceResponse>> =>
    axiosInstance.put<IncomeSourceResponse>(`/api/v1/incomes/${id}`, data),

  delete: (id: string): Promise<AxiosResponse<void>> =>
    axiosInstance.delete<void>(`/api/v1/incomes/${id}`),
};

// --- Notifications API ---
export const notificationsApiClient = {
  getSettings: (): Promise<AxiosResponse<NotificationSettingsResponse>> =>
    axiosInstance.get<NotificationSettingsResponse>('/api/v1/notifications/settings'),

  updateSettings: (
    data: UpdateNotificationSettingsRequest
  ): Promise<AxiosResponse<void>> =>
    axiosInstance.put<void>('/api/v1/notifications/settings', data),

  registerDeviceToken: (token: string, platform: string = 'android'): Promise<AxiosResponse<void>> =>
    axiosInstance.post<void>('/api/v1/devices/token', { token, platform }),
};

export default axiosInstance;

// --- OCR API ---
export const ocrApiClient = {
  upload: (file: FormData, signal?: AbortSignal): Promise<AxiosResponse<OcrUploadResponse>> =>
    axiosInstance.post<OcrUploadResponse>('/api/v1/ocr/upload', file, {
      signal,
      headers: { 'Content-Type': 'multipart/form-data' },
      timeout: 60000,
    }),

  getStatus: (uploadId: string): Promise<AxiosResponse<OcrStatusResponse>> =>
    axiosInstance.get<OcrStatusResponse>(`/api/v1/ocr/${uploadId}/status`),

  getResults: (uploadId: string): Promise<AxiosResponse<OcrResultsResponse>> =>
    axiosInstance.get<OcrResultsResponse>(`/api/v1/ocr/${uploadId}/results`),

  confirm: (uploadId: string, data: OcrConfirmRequest): Promise<AxiosResponse<OcrConfirmResponse>> =>
    axiosInstance.post<OcrConfirmResponse>(`/api/v1/ocr/${uploadId}/confirm`, data),
};

// --- AI Chat API ---
export const chatApiClient = {
  send: (
    message: string,
    history: ChatHistoryItem[]
  ): Promise<AxiosResponse<ChatResponse>> =>
    axiosInstance.post<ChatResponse>('/api/v1/ai/chat', { message, history }),
};

// --- Categories API (lista canônica; o app mantém uma cópia embutida como reserva) ---
export const categoriesApiClient = {
  list: (): Promise<AxiosResponse<CategoriesResponse>> =>
    axiosInstance.get<CategoriesResponse>('/api/v1/categories'),
};

// --- Reports API ---
export const reportsApiClient = {
  spendingByCategory: (months = 6): Promise<AxiosResponse<SpendingByCategoryResponse>> =>
    axiosInstance.get<SpendingByCategoryResponse>(`/api/v1/reports/spending-by-category?months=${months}`),

  monthlyTrends: (months = 12): Promise<AxiosResponse<MonthlyTrendsResponse>> =>
    axiosInstance.get<MonthlyTrendsResponse>(`/api/v1/reports/monthly-trends?months=${months}`),
};
