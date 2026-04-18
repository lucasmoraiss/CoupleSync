// AC-608: useToast hook — exposes typed toast API from any component in the tree
import { useContext, useMemo } from 'react';
import { ToastContext, type ToastVariant } from './ToastProvider';

export interface ToastAPI {
  show: (message: string, variant?: ToastVariant, duration?: number) => void;
  error: (message: string, duration?: number) => void;
  success: (message: string, duration?: number) => void;
  warning: (message: string, duration?: number) => void;
  info: (message: string, duration?: number) => void;
}

export function useToast(): { toast: ToastAPI } {
  const { showToast } = useContext(ToastContext);

  const toast = useMemo<ToastAPI>(
    () => ({
      show: showToast,
      error: (message, duration) => showToast(message, 'error', duration),
      success: (message, duration) => showToast(message, 'success', duration),
      warning: (message, duration) => showToast(message, 'warning', duration),
      info: (message, duration) => showToast(message, 'info', duration),
    }),
    [showToast],
  );

  return { toast };
}
