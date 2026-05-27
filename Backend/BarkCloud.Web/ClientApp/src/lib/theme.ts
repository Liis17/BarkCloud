// Тема (light/dark/auto): хранится в localStorage.bark_theme, применяется через data-theme.
// Bootstrap-скрипт в index.html ставит тему до рендера (без мигания); здесь — переключение в рантайме.

export type Theme = 'light' | 'dark' | 'auto';

export const THEME_KEY = 'bark_theme';

export function getTheme(): Theme {
  try {
    return (localStorage.getItem(THEME_KEY) as Theme) || 'auto';
  } catch {
    return 'auto';
  }
}

export function applyTheme(t: Theme): void {
  const dark =
    t === 'dark' ||
    (t === 'auto' && window.matchMedia && window.matchMedia('(prefers-color-scheme: dark)').matches);
  if (dark) document.documentElement.setAttribute('data-theme', 'dark');
  else document.documentElement.removeAttribute('data-theme');
  try {
    localStorage.setItem(THEME_KEY, t);
  } catch {
    /* ignore */
  }
}
