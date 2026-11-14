// Переход на вход с возвратом на текущую страницу (/login?returnUrl=…).
// Адрес возврата окончательно проверяет сервер (Auth/LoginReturnUrl.cs).

export const RETURN_URL_PARAM = 'returnUrl';

export function loginUrl(returnTo: string): string {
  return '/login?' + new URLSearchParams({ [RETURN_URL_PARAM]: returnTo }).toString();
}

export function redirectToLogin(): void {
  const { pathname, search, hash } = window.location;
  window.location.href = loginUrl(pathname + search + hash);
}

/**
 * Адрес возврата для страницы входа. Фрагмент (#tags) браузер не отправляет на сервер,
 * но сохраняет при 302 с /settings#tags на /login?returnUrl=%2Fsettings — дописываем его.
 */
export function returnUrlFromLocation(location: Pick<Location, 'search' | 'hash'>): string | undefined {
  const value = new URLSearchParams(location.search).get(RETURN_URL_PARAM);
  if (!value) return undefined;
  return value.includes('#') || !location.hash ? value : value + location.hash;
}
