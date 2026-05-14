import { useCallback, useEffect, useState } from 'react';

const STORAGE_KEY = 'tamp-beacon-theme';

export type Theme = 'dark' | 'light';

/**
 * Reads the current theme from the <html> element. The initial value is set by
 * an inline pre-paint script in index.html so there's no flash of wrong theme.
 * The hook stays in sync via a small mutation observer on documentElement.
 */
export function useTheme() {
  const [theme, setTheme] = useState<Theme>(() =>
    document.documentElement.classList.contains('dark') ? 'dark' : 'light'
  );

  useEffect(() => {
    const observer = new MutationObserver(() => {
      setTheme(document.documentElement.classList.contains('dark') ? 'dark' : 'light');
    });
    observer.observe(document.documentElement, { attributes: true, attributeFilter: ['class'] });
    return () => observer.disconnect();
  }, []);

  const toggle = useCallback(() => {
    const next: Theme = document.documentElement.classList.contains('dark') ? 'light' : 'dark';
    if (next === 'dark') document.documentElement.classList.add('dark');
    else document.documentElement.classList.remove('dark');
    try { localStorage.setItem(STORAGE_KEY, next); } catch { /* private mode */ }
  }, []);

  return { theme, toggle };
}
