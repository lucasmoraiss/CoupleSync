// AC-011: Typed Axios API client with Authorization interceptor and 401 handler
import axios, { AxiosInstance, AxiosResponse } from 'axios';
import { router } from 'expo-router';
import { useSessionStore } from '@/state/sessionStore';
import { showToastGlobal } from '@/components/Toast/ToastProvider';
import type {
  AuthResponse,
  RefreshResponse,
  CreateCoupleResponse,
  JoinCoupleResponse,
  GetCoupleMeResponse,
  GoalDto,
  GetGoalsResponse,
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
  OcrUploadResponse,
  OcrStatusResponse,
  OcrResultsResponse,
  OcrConfirmRequest,
  OcrConfirmResponse,
  ChatHistoryItem,
  ChatResponse,
  SpendingByCategoryResponse,
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

// Guard against concurrent 401 handling (multiple parallel requests expiring at same time)
let isHandling401 = false;

// Request interceptor: attach Bearer token from session store
axiosInstance.interceptors.request.use((config) => {
  const { accessToken } = useSessionStore.getState();
  if (accessToken) {
    config.headers['Authorization'] = `Bearer ${accessToken}`;
  }
  return config;
});

// Response interceptor: on 401 clear session and redirect to login; on 403 COUPLE_REQUIRED show toast
axiosInstance.interceptors.response.use(
  (response) => response,
  async (error) => {
    if (error?.response?.status === 401 && !isHandling401) {
      isHandling401 = true;
      try {
        await useSessionStore.getState().clearSession();
        router.replace('/login' as any);
      } finally {
        isHandling401 = false;
      }
    }
    // AC-609: Surface COUPLE_REQUIRED with dedicated message
    if (
      error?.response?.status === 403 &&
      error?.response?.data?.code === 'COUPLE_REQUIRED'
    ) {
      showToastGlobal(
        'Conecte-se com seu parceiro primeiro para usar este recurso',
        'warning',
      );
      router.replace('/(auth)/couple-setup' as any);
    }
    return Promise.reject(error);
  }
);

/** Returns true when the error is a 403 with code COUPLE_REQUIRED (toast already shown globally). */
export function isCoupleRequiredError(error: unknown): boolean {
  return (
    axios.isAxiosError(error) &&
    error.response?.status === 403 &&
    error.response?.data?.code === 'COUPLE_REQUIRED'
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

  refresh: (refreshToken: string): Promise<AxiosResponse<RefreshResponse>> =>
    axiosInstance.post<RefreshResponse>('/api/v1/auth/refresh', { refreshToken }),
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

  /** Creates a transaction manually (without relying on OCR or push notifications). */
  createManual: (data: CreateManualTransactionBody): Promise<AxiosResponse<TransactionResponse>> =>
    axiosInstance.post<TransactionResponse>('/api/v1/transactions', data),
};

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

  archive: (id: string): Promise<AxiosResponse<void>> =>
    axiosInstance.delete<void>(`/api/v1/goals/${id}/archive`),
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
  upload: (file: FormData): Promise<AxiosResponse<OcrUploadResponse>> =>
    axiosInstance.post<OcrUploadResponse>('/api/v1/ocr/upload', file),

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

// --- Reports API ---
export const reportsApiClient = {
  spendingByCategory: (months = 6): Promise<AxiosResponse<SpendingByCategoryResponse>> =>
    axiosInstance.get<SpendingByCategoryResponse>(`/api/v1/reports/spending-by-category?months=${months}`),

  monthlyTrends: (months = 12): Promise<AxiosResponse<MonthlyTrendsResponse>> =>
    axiosInstance.get<MonthlyTrendsResponse>(`/api/v1/reports/monthly-trends?months=${months}`),
};
