export const colors = {
  background: '#0F172A',
  surface: '#1E293B',
  primary: '#6366F1',
  primaryLight: '#818CF8',
  text: '#F8FAFC',
  textSubtle: '#CBD5E1',
  textMuted: '#94A3B8',
  textDisabled: '#64748B',
  placeholder: '#9CA3AF',
  border: '#334155',
  error: '#EF4444',
  errorLight: '#F87171',
  success: '#22C55E',
  successDark: '#14532D',
  warning: '#F59E0B',
  overlay: 'rgba(0,0,0,0.7)',
  white: '#FFFFFF',
} as const;

export const spacing = {
  xs: 4,
  sm: 8,
  md: 16,
  lg: 24,
  xl: 32,
  xxl: 48,
} as const;

export const typography = {
  fontSize: {
    xs: 11,
    sm: 12,
    md: 14,
    lg: 16,
    xl: 18,
    xxl: 22,
    title: 28,
  },
  fontWeight: {
    regular: '400' as const,
    medium: '500' as const,
    semibold: '600' as const,
    bold: '700' as const,
  },
} as const;

export const borderRadius = {
  sm: 4,
  md: 8,
  lg: 12,
  full: 999,
} as const;

export const shadows = {
  card: {
    shadowColor: '#000',
    shadowOffset: { width: 0, height: 2 },
    shadowOpacity: 0.1,
    shadowRadius: 4,
    elevation: 3,
  },
  bottomTab: {
    shadowColor: '#000',
    shadowOffset: { width: 0, height: -2 },
    shadowOpacity: 0.05,
    shadowRadius: 4,
    elevation: 5,
  },
} as const;
