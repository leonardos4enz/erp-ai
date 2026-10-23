import { useCallback, useEffect, useState } from 'react';
import { safeStorageGet, safeStorageSet } from './hooks';

export type Theme = 'light' | 'dark';
const KEY = 'erp.theme';

function initialTheme(): Theme {
  const saved = safeStorageGet(KEY);
  if (saved === 'light' || saved === 'dark') return saved;
  return window.matchMedia?.('(prefers-color-scheme: dark)').matches ? 'dark' : 'light';
}

export function applyInitialTheme() {
  document.documentElement.dataset.theme = initialTheme();
}

export function useTheme() {
  const [theme, setTheme] = useState<Theme>(() => (document.documentElement.dataset.theme as Theme) || initialTheme());
  useEffect(() => {
    document.documentElement.dataset.theme = theme;
  }, [theme]);
  const toggle = useCallback(() => {
    setTheme((t) => {
      const next = t === 'dark' ? 'light' : 'dark';
      safeStorageSet(KEY, next);
      return next;
    });
  }, []);
  return { theme, toggle };
}
