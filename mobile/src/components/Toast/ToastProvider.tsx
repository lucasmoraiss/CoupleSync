// AC-608: ToastProvider — context, state management, overlay rendering
// Module-level ref allows axios interceptors to trigger toasts outside React tree
import React, { createContext, useCallback, useEffect, useRef, useState } from 'react';
import { Animated } from 'react-native';
import { Toast } from './Toast';

export type ToastVariant = 'success' | 'error' | 'warning' | 'info';

interface ToastState {
  message: string;
  variant: ToastVariant;
  visible: boolean;
}

export interface ToastContextValue {
  showToast: (message: string, variant?: ToastVariant, duration?: number) => void;
}

export const ToastContext = createContext<ToastContextValue>({
  showToast: () => {},
});

// Module-level ref — allows axios interceptors to trigger toasts before React renders
let _showToastRef: ToastContextValue['showToast'] | null = null;

/** Call from outside React (e.g., axios response interceptor). No-op if provider not mounted. */
export function showToastGlobal(
  message: string,
  variant: ToastVariant = 'error',
  duration?: number,
): void {
  _showToastRef?.(message, variant, duration);
}

// Fixed bottom offset: 60dp tab bar + 16dp margin
const TOAST_BOTTOM_OFFSET = 76;

export function ToastProvider({ children }: { children: React.ReactNode }) {
  const [toast, setToast] = useState<ToastState>({
    message: '',
    variant: 'info',
    visible: false,
  });
  const timerRef = useRef<ReturnType<typeof setTimeout> | null>(null);
  const slideAnim = useRef(new Animated.Value(120)).current;

  const dismiss = useCallback(() => {
    if (timerRef.current) {
      clearTimeout(timerRef.current);
      timerRef.current = null;
    }
    Animated.timing(slideAnim, {
      toValue: 120,
      duration: 200,
      useNativeDriver: true,
    }).start(() => {
      setToast((prev) => ({ ...prev, visible: false }));
    });
  }, [slideAnim]);

  const showToast = useCallback(
    (message: string, variant: ToastVariant = 'info', duration = 4000) => {
      if (timerRef.current) {
        clearTimeout(timerRef.current);
        timerRef.current = null;
      }
      // Reset anim before showing new toast
      slideAnim.setValue(120);
      setToast({ message, variant, visible: true });
      Animated.spring(slideAnim, {
        toValue: 0,
        useNativeDriver: true,
        friction: 7,
        tension: 80,
      }).start();
      timerRef.current = setTimeout(dismiss, duration);
    },
    [slideAnim, dismiss],
  );

  // Register globally for use in axios interceptors
  useEffect(() => {
    _showToastRef = showToast;
    return () => {
      _showToastRef = null;
    };
  }, [showToast]);

  // Cleanup pending dismiss timer on unmount
  useEffect(() => {
    return () => {
      if (timerRef.current) clearTimeout(timerRef.current);
    };
  }, []);

  return (
    <ToastContext.Provider value={{ showToast }}>
      {children}
      {toast.visible && (
        <Toast
          message={toast.message}
          variant={toast.variant}
          translateY={slideAnim}
          onDismiss={dismiss}
          bottomOffset={TOAST_BOTTOM_OFFSET}
        />
      )}
    </ToastContext.Provider>
  );
}
