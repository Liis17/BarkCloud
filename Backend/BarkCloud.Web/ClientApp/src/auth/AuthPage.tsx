import { FormEvent, useEffect, useRef, useState } from 'react';
import { OtpInput } from './OtpInput';
import type { AuthPageState, AuthScreen, AuthValues } from './types';
import { webauthnAuthenticate, webauthnSupported } from '../lib/webauthn';
import './auth.css';

interface AuthPageProps {
  initialState: AuthPageState;
  request?: typeof fetch;
  authenticate?: (optionsJson: string) => Promise<string>;
  passkeyAvailable?: boolean;
  navigate?: (url: string) => void;
}

interface TextFieldProps {
  id: string;
  name: string;
  label: string;
  value: string;
  error?: string;
  disabled: boolean;
  type?: string;
  autoComplete?: string;
  formError?: string;
  required?: boolean;
  minLength?: number;
  inputMode?: 'text' | 'email';
  onChange: (value: string) => void;
}

function TextField({
  id,
  name,
  label,
  value,
  error,
  disabled,
  type = 'text',
  autoComplete,
  formError,
  required,
  minLength,
  inputMode,
  onChange,
}: TextFieldProps) {
  const errorId = id + '-error';
  return (
    <div className="auth-field">
      <label htmlFor={id}>{label}</label>
      <input
        id={id}
        name={name}
        type={type}
        value={value}
        disabled={disabled}
        autoComplete={autoComplete}
        required={required}
        minLength={minLength}
        inputMode={inputMode}
        aria-invalid={error ? 'true' : undefined}
        aria-describedby={error ? errorId : undefined}
        onChange={(event) => onChange(event.currentTarget.value)}
      />
      {error && <p className="auth-field-error" id={errorId} role={error === formError ? 'alert' : undefined}>{error}</p>}
    </div>
  );
}

function safeValuesFromForm(form: FormData, current: AuthValues): AuthValues {
  const values: AuthValues = { ...current };
  const textKeys = ['login', 'username', 'email', 'first_name', 'last_name'] as const;
  for (const key of textKeys) {
    if (form.has(key)) values[key] = String(form.get(key) ?? '');
  }
  if (form.has('remember')) values.remember = String(form.get('remember')) === 'true';
  if (form.has('revoke_sessions')) values.revoke_sessions = String(form.get('revoke_sessions')) === 'true';
  if (form.has('reset_passkeys')) values.reset_passkeys = String(form.get('reset_passkeys')) === 'true';
  return values;
}

function withDefaultChoices(values: AuthValues): AuthValues {
  return {
    ...values,
    remember: values.remember ?? true,
    revoke_sessions: values.revoke_sessions ?? true,
    reset_passkeys: values.reset_passkeys ?? true,
  };
}

function isAuthPageState(value: unknown): value is AuthPageState {
  if (!value || typeof value !== 'object') return false;
  const candidate = value as Partial<AuthPageState>;
  return (
    typeof candidate.screen === 'string' &&
    typeof candidate.serverName === 'string' &&
    typeof candidate.version === 'string' &&
    typeof candidate.csrfToken === 'string' &&
    !!candidate.values &&
    !!candidate.fieldErrors &&
    !!candidate.features
  );
}

async function responseJson(response: Response): Promise<Record<string, unknown>> {
  try {
    const value = await response.json();
    return value && typeof value === 'object' ? value as Record<string, unknown> : {};
  } catch {
    return {};
  }
}

function canonicalUrl(state: AuthPageState): string {
  let route = '/login';
  if (state.screen === 'register' || state.screen === 'register-otp' || state.screen === 'session-pending') route = '/register';
  if (state.screen === 'forgot' || state.screen === 'reset') route = '/forgot';
  const params = new URLSearchParams();
  if (state.flowId) params.set('flow', state.flowId);
  if (state.screen === 'session-pending') params.set('stage', 'account-created');
  const query = params.toString();
  return query ? route + '?' + query : route;
}

function screenTitle(screen: AuthScreen): string {
  switch (screen) {
    case 'login': return 'Войдите в своё облако';
    case 'login-otp': return 'Подтвердите вход';
    case 'register': return 'Создайте аккаунт';
    case 'register-otp': return 'Подтвердите почту';
    case 'forgot': return 'Восстановите доступ';
    case 'reset': return 'Задайте новый пароль';
    case 'session-pending': return 'Аккаунт создан';
  }
}

function cooldownSeconds(retryAt?: string): number {
  if (!retryAt) return 0;
  const retryTime = Date.parse(retryAt);
  if (!Number.isFinite(retryTime)) return 0;
  return Math.max(0, Math.ceil((retryTime - Date.now()) / 1000));
}

function useCooldown(retryAt?: string): number {
  const [seconds, setSeconds] = useState(() => cooldownSeconds(retryAt));
  useEffect(() => {
    setSeconds(cooldownSeconds(retryAt));
    if (!retryAt || cooldownSeconds(retryAt) <= 0) return;
    const timer = window.setInterval(() => {
      const remaining = cooldownSeconds(retryAt);
      setSeconds(remaining);
      if (remaining <= 0) window.clearInterval(timer);
    }, 1000);
    return () => window.clearInterval(timer);
  }, [retryAt]);
  return seconds;
}

function formatSeconds(total: number): string {
  const minutes = Math.floor(total / 60);
  const seconds = total % 60;
  return String(minutes).padStart(2, '0') + ':' + String(seconds).padStart(2, '0');
}

function FileIllustration() {
  return (
    <svg className="auth-illustration" viewBox="0 0 430 300" role="img" aria-label="Файлы и фотографии в облаке">
      <path className="auth-illustration-cloud" d="M112 213h207a49 49 0 0 0 2-98 77 77 0 0 0-146-15 59 59 0 0 0-63 113Z" />
      <rect className="auth-illustration-back" x="120" y="139" width="118" height="144" rx="17" transform="rotate(-9 120 139)" />
      <rect className="auth-illustration-front" x="205" y="128" width="119" height="153" rx="17" transform="rotate(7 205 128)" />
      <rect className="auth-illustration-photo" x="224" y="151" width="78" height="66" rx="8" transform="rotate(7 224 151)" />
      <circle className="auth-illustration-sun" cx="280" cy="171" r="9" />
      <path className="auth-illustration-hill" d="m229 207 22-26 18 17 14-14 18 28Z" />
      <path className="auth-illustration-line" d="m141 227 66-10M144 246l54-9M226 236l76 9" />
    </svg>
  );
}

export function AuthPage({
  initialState,
  request = fetch,
  authenticate = webauthnAuthenticate,
  passkeyAvailable = webauthnSupported(),
  navigate = (url) => window.location.assign(url),
}: AuthPageProps) {
  const [page, setPage] = useState(() => ({ ...initialState, values: withDefaultChoices(initialState.values ?? {}) }));
  const pageRef = useRef(page);
  const [values, setValues] = useState<AuthValues>(() => withDefaultChoices(initialState.values ?? {}));
  const [password, setPassword] = useState('');
  const [newPassword, setNewPassword] = useState('');
  const [passwordVisible, setPasswordVisible] = useState(false);
  const [newPasswordVisible, setNewPasswordVisible] = useState(false);
  const [otp, setOtp] = useState('');
  const [otpError, setOtpError] = useState<string>();
  const [authenticatorCode, setAuthenticatorCode] = useState('');
  const [authenticatorError, setAuthenticatorError] = useState<string>();
  const [networkError, setNetworkError] = useState<string>();
  const [busy, setBusy] = useState(false);
  const [otpFocusRequest, setOtpFocusRequest] = useState(0);
  const [authenticatorFocusRequest, setAuthenticatorFocusRequest] = useState(0);
  const busyRef = useRef(false);
  const otpRef = useRef<HTMLInputElement>(null);
  const authenticatorRef = useRef<HTMLInputElement>(null);
  const retrySeconds = useCooldown(page.retryAt);

  useEffect(() => {
    document.documentElement.lang = 'ru';
    document.title = 'BarkCloud — ' + screenTitle(page.screen);
  }, [page.screen]);

  useEffect(() => {
    if (otpFocusRequest > 0) otpRef.current?.focus();
  }, [otpFocusRequest]);

  useEffect(() => {
    if (authenticatorFocusRequest > 0) authenticatorRef.current?.focus();
  }, [authenticatorFocusRequest]);

  useEffect(() => {
    const root = document.documentElement;
    const media = window.matchMedia?.('(prefers-color-scheme: dark)');
    const applyTheme = () => {
      let theme = 'auto';
      try {
        theme = localStorage.getItem('bark_theme') || 'auto';
      } catch {
        theme = 'auto';
      }
      const dark = theme === 'dark' || (theme === 'auto' && !!media?.matches);
      root.setAttribute('data-theme', dark ? 'dark' : 'light');
    };
    applyTheme();
    media?.addEventListener?.('change', applyTheme);
    window.addEventListener('storage', applyTheme);
    return () => {
      media?.removeEventListener?.('change', applyTheme);
      window.removeEventListener('storage', applyTheme);
    };
  }, []);

  function updateValue(name: keyof AuthValues, value: string | boolean) {
    setValues((previous) => ({ ...previous, [name]: value }));
  }

  function applyPageState(next: AuthPageState, submittedValues: AuthValues = values) {
    const previous = pageRef.current;
    const merged: AuthPageState = {
      ...next,
      values: { ...submittedValues, ...(next.values ?? {}) },
    };
    const screenChanged = previous.screen !== merged.screen;
    pageRef.current = merged;
    setPage(merged);
    setValues(merged.values);
    setNetworkError(undefined);
    if (screenChanged || next.clearOtp) {
      setOtp('');
      setOtpError(undefined);
      if (merged.screen === 'login-otp' || merged.screen === 'register-otp' || merged.screen === 'reset') {
        setOtpFocusRequest((previousRequest) => previousRequest + 1);
      }
    }
    // Повторная отправка письма (или переход) снимает требование TOTP — очищаем введённый код.
    if (screenChanged || next.clearOtp || (previous.authenticatorRequired && !merged.authenticatorRequired)) {
      setAuthenticatorCode('');
      setAuthenticatorError(undefined);
    }
    if (!previous.authenticatorRequired && merged.authenticatorRequired && merged.screen === 'reset') {
      setAuthenticatorFocusRequest((previousRequest) => previousRequest + 1);
    }
    if (screenChanged) {
      setPassword('');
      setNewPassword('');
      setPasswordVisible(false);
      setNewPasswordVisible(false);
    }
    window.history.replaceState(null, '', canonicalUrl(merged));
  }

  async function consumeResponse(
    response: Response,
    submittedValues: AuthValues,
    preserveValues: ('remember' | 'revoke_sessions' | 'reset_passkeys')[] = [],
  ) {
    const body = await responseJson(response);
    if (typeof body.redirectTo === 'string') {
      navigate(body.redirectTo);
      return;
    }
    if (isAuthPageState(body)) {
      const nextState: AuthPageState = { ...body, values: { ...body.values } };
      for (const key of preserveValues) {
        if (key === 'remember' && submittedValues.remember !== undefined) {
          nextState.values.remember = submittedValues.remember;
        }
        if (key === 'revoke_sessions' && submittedValues.revoke_sessions !== undefined) {
          nextState.values.revoke_sessions = submittedValues.revoke_sessions;
        }
        if (key === 'reset_passkeys' && submittedValues.reset_passkeys !== undefined) {
          nextState.values.reset_passkeys = submittedValues.reset_passkeys;
        }
      }
      applyPageState(nextState, submittedValues);
      return;
    }
    if (response.ok && body.ok === true) {
      navigate('/photos');
      return;
    }
    const message = typeof body.message === 'string' ? body.message : 'Не удалось выполнить запрос. Попробуйте ещё раз.';
    throw new Error(message);
  }

  async function runMutation(action: () => Promise<void>) {
    if (busyRef.current) return;
    busyRef.current = true;
    setBusy(true);
    setNetworkError(undefined);
    try {
      await action();
    } catch (error) {
      setNetworkError(error instanceof Error ? error.message : 'Не удалось связаться с сервером.');
    } finally {
      busyRef.current = false;
      setBusy(false);
    }
  }

  function routeForSubmit(screen: AuthScreen): string {
    switch (screen) {
      case 'login':
      case 'login-otp': return '/login';
      case 'register': return '/register';
      case 'register-otp':
      case 'session-pending': return '/register/confirm';
      case 'forgot': return '/forgot';
      case 'reset': return '/forgot/confirm';
    }
  }

  async function submitForm(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (busyRef.current) return;
    if ((page.screen === 'login-otp' || page.screen === 'register-otp' || page.screen === 'reset') && !/^[0-9]{6}$/.test(otp)) {
      setOtpError('Введите все шесть цифр кода.');
      otpRef.current?.focus();
      return;
    }

    if (page.screen === 'reset' && page.authenticatorRequired && !/^[0-9]{6}$/.test(authenticatorCode)) {
      setAuthenticatorError('Введите все шесть цифр кода из приложения.');
      authenticatorRef.current?.focus();
      return;
    }

    const form = event.currentTarget;
    const formData = new FormData(form);
    formData.set('__RequestVerificationToken', page.csrfToken);
    if (page.flowId) formData.set('flow_id', page.flowId);
    else formData.delete('flow_id');
    const submittedValues = safeValuesFromForm(formData, values);
    await runMutation(async () => {
      const response = await request(routeForSubmit(page.screen), {
        method: 'POST',
        credentials: 'same-origin',
        headers: { Accept: 'application/json' },
        body: formData,
      });
      await consumeResponse(response, submittedValues);
    });
  }

  async function resendCode() {
    let route: string | undefined;
    if (page.screen === 'login-otp' && page.otpMethod === 'email') route = '/login/resend';
    if (page.screen === 'register-otp') route = '/register/resend';
    if (page.screen === 'reset') route = '/forgot/resend';
    if (!route) return;
    const formData = new FormData();
    formData.set('__RequestVerificationToken', page.csrfToken);
    if (page.flowId) formData.set('flow_id', page.flowId);
    await runMutation(async () => {
      const response = await request(route!, {
        method: 'POST',
        credentials: 'same-origin',
        headers: { Accept: 'application/json' },
        body: formData,
      });
      await consumeResponse(response, values, page.screen === 'reset' ? ['revoke_sessions', 'reset_passkeys'] : []);
    });
  }

  async function usePasskey() {
    if (!passkeyAvailable) return;
    await runMutation(async () => {
      const headers = { Accept: 'application/json', 'X-CSRF-TOKEN': page.csrfToken };
      const beginResponse = await request('/login/webauthn/begin', {
        method: 'POST',
        credentials: 'same-origin',
        headers,
      });
      const begin = await responseJson(beginResponse);
      if (!beginResponse.ok || typeof begin.optionsJson !== 'string' || typeof begin.challengeId !== 'string') {
        throw new Error(typeof begin.message === 'string' ? begin.message : 'Не удалось начать вход по ключу.');
      }
      let assertion: string;
      try {
        assertion = await authenticate(begin.optionsJson);
      } catch (error) {
        const name = error && typeof error === 'object' && 'name' in error ? String(error.name) : '';
        if (name === 'NotAllowedError' || name === 'AbortError') return;
        throw error;
      }
      const completeResponse = await request('/login/webauthn/complete', {
        method: 'POST',
        credentials: 'same-origin',
        headers: { ...headers, 'Content-Type': 'application/json' },
        body: JSON.stringify({
          challengeId: begin.challengeId,
          assertion: JSON.parse(assertion),
          remember: values.remember ?? true,
        }),
      });
      const complete = await responseJson(completeResponse);
      if (completeResponse.ok && complete.ok === true) {
        navigate(typeof complete.redirectTo === 'string' ? complete.redirectTo : '/photos');
        return;
      }
      if (isAuthPageState(complete)) {
        applyPageState(complete, values);
        return;
      }
      throw new Error(typeof complete.message === 'string' ? complete.message : 'Не удалось войти по ключу.');
    });
  }

  const title = screenTitle(page.screen);
  const canResend = page.screen === 'register-otp' || page.screen === 'reset' ||
    (page.screen === 'login-otp' && page.otpMethod === 'email');
  const fieldError = (name: string) => page.fieldErrors[name];
  const csrfAndFlow = (
    <>
      <input type="hidden" name="__RequestVerificationToken" value={page.csrfToken} />
      {page.flowId && <input type="hidden" name="flow_id" value={page.flowId} />}
    </>
  );
  const formErrorIsFieldError = Boolean(page.formError && Object.values(page.fieldErrors).includes(page.formError));
  const formFeedback = (
    <>
      {page.formError && !formErrorIsFieldError && <p className="auth-alert auth-alert-error" role="alert">{page.formError}</p>}
      {networkError && <p className="auth-alert auth-alert-error" role="alert">{networkError}</p>}
      {page.formMessage && <p className="auth-alert auth-alert-message" role="status">{page.formMessage}</p>}
    </>
  );
  const setTextValue = (key: keyof AuthValues) => (value: string) => updateValue(key, value);
  const link = (href: string, text: string, className = 'auth-text-link') => <a className={className} href={href}>{text}</a>;

  return (
    <main className="auth-page">
      <div className="auth-layout">
        <section className="auth-story" aria-label="BarkCloud">
          <a className="auth-brand" href="/login" aria-label="BarkCloud — вход">
            <img src="/barkcloud-icon.png" alt="" width="48" height="48" />
            <span>BarkCloud</span>
          </a>
          <div className="auth-story-copy">
            <p className="auth-eyebrow">Личное файловое облако</p>
            <h1>Ваши файлы<br />всегда рядом.</h1>
            <p className="auth-story-description">Фото, документы и видео собраны в одном месте.</p>
          </div>
          <p className="auth-host"><span>Сервер</span><strong>{page.serverName}</strong></p>
          <FileIllustration />
        </section>

        <section className="auth-card" aria-labelledby="auth-title">
          <header className="auth-card-heading">
            <p className="auth-eyebrow">Доступ к облаку</p>
            <h2 id="auth-title">{title}</h2>
            {page.screen === 'login' && <p>Войдите, чтобы открыть свои файлы.</p>}
            {page.screen === 'login-otp' && (
              <p>
                {page.otpMethod === 'authenticator'
                  ? 'Введите код из приложения-аутентификатора.'
                  : page.otpMethod === 'email'
                    ? 'Введите код подтверждения из письма.'
                    : 'Введите код подтверждения для входа.'}
              </p>
            )}
            {page.screen === 'register' && <p>Несколько деталей — и облако готово.</p>}
            {page.screen === 'register-otp' && <p>Введите код из письма, чтобы завершить регистрацию.</p>}
            {page.screen === 'forgot' && <p>Мы отправим инструкции, если аккаунт существует.</p>}
            {page.screen === 'reset' && <p>Введите код из письма и придумайте новый пароль.</p>}
            {page.screen === 'session-pending' && <p>Повторите проверку, чтобы продолжить вход.</p>}
          </header>

          {formFeedback}

          {page.screen === 'login' && (
            <form className="auth-form" onSubmit={submitForm} aria-busy={busy}>
              {csrfAndFlow}
              <TextField
                id="auth-login"
                name="login"
                label="Логин или почта"
                value={values.login ?? ''}
                error={fieldError('login')}
                formError={page.formError}
                disabled={busy}
                autoComplete="username"
                required
                onChange={setTextValue('login')}
              />
              <div className="auth-field">
                <label htmlFor="auth-password">Пароль</label>
                <div className="auth-password-wrap">
                  <input
                    id="auth-password"
                    name="password"
                    type={passwordVisible ? 'text' : 'password'}
                    value={password}
                    disabled={busy}
                    autoComplete="current-password"
                    required
                    aria-invalid={fieldError('password') ? 'true' : undefined}
                    aria-describedby={fieldError('password') ? 'auth-password-error' : undefined}
                    onChange={(event) => setPassword(event.currentTarget.value)}
                  />
                  <button type="button" className="auth-password-toggle" aria-label={passwordVisible ? 'Скрыть пароль' : 'Показать пароль'} disabled={busy} onClick={() => setPasswordVisible((visible) => !visible)}>
                    {passwordVisible ? 'Скрыть' : 'Показать'}
                  </button>
                </div>
                {fieldError('password') && <p className="auth-field-error" id="auth-password-error">{fieldError('password')}</p>}
                <div className="auth-field-meta">
                  <label className="auth-checkbox">
                    <input type="checkbox" name="remember" value="true" checked={values.remember ?? false} disabled={busy} onChange={(event) => updateValue('remember', event.currentTarget.checked)} />
                    <span>Оставаться в системе</span>
                  </label>
                  {page.features.emailEnabled && link('/forgot', 'Забыли пароль?')}
                </div>
              </div>
              <button className="auth-primary-button" type="submit" disabled={busy}>{busy ? 'Входим…' : 'Войти'}</button>
              {passkeyAvailable && <button className="auth-secondary-button" type="button" disabled={busy} onClick={usePasskey}>Войти с ключом</button>}
              {page.features.registrationEnabled && (
                <p className="auth-form-footer">Впервые здесь? {link('/register', 'Создать аккаунт')}</p>
              )}
            </form>
          )}

          {page.screen === 'login-otp' && (
            <form className="auth-form" onSubmit={submitForm} aria-busy={busy}>
              {csrfAndFlow}
              {values.login && <input type="hidden" name="login" value={values.login} />}
              {values.remember && <input type="hidden" name="remember" value="true" />}
              <div className="auth-field">
                <label htmlFor="auth-otp">Код подтверждения</label>
                <OtpInput ref={otpRef} value={otp} disabled={busy} error={otpError || fieldError('otp')} onChange={setOtp} onInvalid={setOtpError} />
                {(otpError || fieldError('otp')) && <p className="auth-field-error" id="auth-otp-error" role={(otpError || fieldError('otp')) === page.formError ? 'alert' : undefined}>{otpError || fieldError('otp')}</p>}
              </div>
              {page.otpMethod === 'authenticator' ? (
                <p className="auth-inline-note">Откройте приложение-аутентификатор и введите текущий код.</p>
              ) : page.otpMethod === 'email' ? (
                <p className="auth-inline-note">
                  {page.otpMailStatus === 'existing' ? 'Код уже отправлен. Проверьте почту.' : 'Если письмо не появилось, проверьте папку «Спам».'}
                </p>
              ) : (
                <p className="auth-inline-note">Введите код подтверждения, который вы получили.</p>
              )}
              <button className="auth-primary-button" type="submit" disabled={busy}>{busy ? 'Проверяем…' : 'Продолжить'}</button>
              {canResend && (
                <button className="auth-resend-button" type="button" disabled={busy || retrySeconds > 0} onClick={resendCode}>
                  {busy ? 'Отправляем…' : retrySeconds > 0 ? 'Отправить повторно через ' + formatSeconds(retrySeconds) : 'Отправить код повторно'}
                </button>
              )}
              <p className="auth-form-footer">{link('/login', 'Назад ко входу')}</p>
            </form>
          )}

          {page.screen === 'register' && (
            page.features.registrationEnabled ? (
              <form className="auth-form" onSubmit={submitForm} aria-busy={busy}>
                {csrfAndFlow}
                <div className="auth-name-fields">
                  <TextField id="auth-first-name" name="first_name" label="Имя (необязательно)" value={values.first_name ?? ''} error={fieldError('first_name')} formError={page.formError} disabled={busy} autoComplete="given-name" onChange={setTextValue('first_name')} />
                  <TextField id="auth-last-name" name="last_name" label="Фамилия (необязательно)" value={values.last_name ?? ''} error={fieldError('last_name')} formError={page.formError} disabled={busy} autoComplete="family-name" onChange={setTextValue('last_name')} />
                </div>
                <TextField id="auth-username" name="username" label="Логин" value={values.username ?? ''} error={fieldError('username')} formError={page.formError} disabled={busy} autoComplete="username" required onChange={setTextValue('username')} />
                <TextField id="auth-email" name="email" label="Электронная почта" value={values.email ?? ''} error={fieldError('email')} formError={page.formError} disabled={busy} type="email" autoComplete="email" inputMode="email" required onChange={setTextValue('email')} />
                <div className="auth-field">
                  <label htmlFor="auth-password">Пароль</label>
                  <div className="auth-password-wrap">
                    <input
                      id="auth-password"
                      name="password"
                      type={passwordVisible ? 'text' : 'password'}
                      value={password}
                      disabled={busy}
                      autoComplete="new-password"
                      minLength={6}
                      required
                      aria-invalid={fieldError('password') ? 'true' : undefined}
                      aria-describedby={fieldError('password') ? 'auth-password-error' : undefined}
                      onChange={(event) => setPassword(event.currentTarget.value)}
                    />
                    <button type="button" className="auth-password-toggle" aria-label={passwordVisible ? 'Скрыть пароль' : 'Показать пароль'} disabled={busy} onClick={() => setPasswordVisible((visible) => !visible)}>
                      {passwordVisible ? 'Скрыть' : 'Показать'}
                    </button>
                  </div>
                  <p className="auth-helper">Не менее 6 символов.</p>
                  {fieldError('password') && <p className="auth-field-error" id="auth-password-error" role={fieldError('password') === page.formError ? 'alert' : undefined}>{fieldError('password')}</p>}
                </div>
                <button className="auth-primary-button" type="submit" disabled={busy}>{busy ? 'Создаём аккаунт…' : 'Создать аккаунт'}</button>
                <p className="auth-form-footer">Уже есть аккаунт? {link('/login', 'Войти')}</p>
              </form>
            ) : (
              <div className="auth-unavailable">
                <p role="status">Регистрация на этом сервере отключена.</p>
                {link('/login', 'Вернуться ко входу', 'auth-primary-button auth-button-link')}
              </div>
            )
          )}

          {page.screen === 'register-otp' && (
            <form className="auth-form" onSubmit={submitForm} aria-busy={busy}>
              {csrfAndFlow}
              <div className="auth-field">
                <label htmlFor="auth-otp">Код из письма</label>
                <OtpInput ref={otpRef} value={otp} disabled={busy} error={otpError || fieldError('otp')} onChange={setOtp} onInvalid={setOtpError} />
                {(otpError || fieldError('otp')) && <p className="auth-field-error" id="auth-otp-error" role={(otpError || fieldError('otp')) === page.formError ? 'alert' : undefined}>{otpError || fieldError('otp')}</p>}
              </div>
              <p className="auth-inline-note">{page.otpMailStatus === 'existing' ? 'Код уже отправлен. Проверьте почту.' : 'Если письма нет, проверьте папку «Спам».'}</p>
              <button className="auth-primary-button" type="submit" disabled={busy}>{busy ? 'Проверяем…' : 'Подтвердить почту'}</button>
              <button className="auth-resend-button" type="button" disabled={busy || retrySeconds > 0} onClick={resendCode}>
                {busy ? 'Отправляем…' : retrySeconds > 0 ? 'Отправить повторно через ' + formatSeconds(retrySeconds) : 'Отправить код повторно'}
              </button>
            </form>
          )}

          {page.screen === 'forgot' && (
            page.features.emailEnabled ? (
              <form className="auth-form" onSubmit={submitForm} aria-busy={busy}>
                {csrfAndFlow}
                <TextField id="auth-login" name="login" label="Логин или почта" value={values.login ?? ''} error={fieldError('login')} formError={page.formError} disabled={busy} autoComplete="username" required onChange={setTextValue('login')} />
                <p className="auth-inline-note">Если аккаунт существует, мы отправим письмо с кодом для восстановления.</p>
                <button className="auth-primary-button" type="submit" disabled={busy}>{busy ? 'Отправляем…' : 'Продолжить'}</button>
                <p className="auth-form-footer">{link('/login', 'Назад ко входу')}</p>
              </form>
            ) : (
              <div className="auth-unavailable">
                <p role="status">Восстановление по почте сейчас недоступно.</p>
                {link('/login', 'Вернуться ко входу', 'auth-primary-button auth-button-link')}
              </div>
            )
          )}

          {page.screen === 'reset' && (
            <form className="auth-form" onSubmit={submitForm} aria-busy={busy}>
              {csrfAndFlow}
              <div className="auth-field">
                <label htmlFor="auth-otp">Код из письма</label>
                <OtpInput ref={otpRef} value={otp} disabled={busy} error={otpError || fieldError('otp')} onChange={setOtp} onInvalid={setOtpError} />
                {(otpError || fieldError('otp')) && <p className="auth-field-error" id="auth-otp-error" role={(otpError || fieldError('otp')) === page.formError ? 'alert' : undefined}>{otpError || fieldError('otp')}</p>}
              </div>
              {page.authenticatorRequired && (
                <div className="auth-field">
                  <label htmlFor="auth-authenticator">Код из приложения-аутентификатора</label>
                  <OtpInput
                    ref={authenticatorRef}
                    id="auth-authenticator"
                    name="authenticator_code"
                    ariaLabel="Код приложения-аутентификатора"
                    value={authenticatorCode}
                    disabled={busy}
                    error={authenticatorError || fieldError('authenticator')}
                    onChange={setAuthenticatorCode}
                    onInvalid={setAuthenticatorError}
                  />
                  {(authenticatorError || fieldError('authenticator')) && (
                    <p className="auth-field-error" id="auth-authenticator-error" role={(authenticatorError || fieldError('authenticator')) === page.formError ? 'alert' : undefined}>
                      {authenticatorError || fieldError('authenticator')}
                    </p>
                  )}
                  <p className="auth-inline-note">Аккаунт защищён двухфакторной аутентификацией: введите текущий код из приложения.</p>
                </div>
              )}
              <div className="auth-field">
                <label htmlFor="auth-new-password">Новый пароль</label>
                <div className="auth-password-wrap">
                  <input
                    id="auth-new-password"
                    name="password"
                    type={newPasswordVisible ? 'text' : 'password'}
                    value={newPassword}
                    disabled={busy}
                    autoComplete="new-password"
                    minLength={6}
                    required
                    aria-invalid={fieldError('password') ? 'true' : undefined}
                    aria-describedby={fieldError('password') ? 'auth-new-password-error' : undefined}
                    onChange={(event) => setNewPassword(event.currentTarget.value)}
                  />
                  <button type="button" className="auth-password-toggle" aria-label={newPasswordVisible ? 'Скрыть пароль' : 'Показать пароль'} disabled={busy} onClick={() => setNewPasswordVisible((visible) => !visible)}>
                    {newPasswordVisible ? 'Скрыть' : 'Показать'}
                  </button>
                </div>
                <p className="auth-helper">Не менее 6 символов.</p>
                {fieldError('password') && <p className="auth-field-error" id="auth-new-password-error" role={fieldError('password') === page.formError ? 'alert' : undefined}>{fieldError('password')}</p>}
              </div>
              <label className="auth-checkbox">
                <input type="checkbox" name="reset_passkeys" value="true" checked={values.reset_passkeys ?? true} disabled={busy} onChange={(event) => updateValue('reset_passkeys', event.currentTarget.checked)} />
                <span>Сбросить все passkey</span>
              </label>
              {values.reset_passkeys !== false ? (
                <p className="auth-inline-note">При сбросе пароля будут удалены все ключи доступа (passkey). Их потребуется добавить заново.</p>
              ) : (
                <p className="auth-inline-note">Passkey сохранятся и продолжат работать после смены пароля.</p>
              )}
              <label className="auth-checkbox">
                <input type="checkbox" name="revoke_sessions" value="true" checked={values.revoke_sessions ?? true} disabled={busy} onChange={(event) => updateValue('revoke_sessions', event.currentTarget.checked)} />
                <span>Выйти с других устройств</span>
              </label>
              <p className="auth-inline-note">Выход с устройств не удаляет passkey.</p>
              <button className="auth-primary-button" type="submit" disabled={busy}>{busy ? 'Сохраняем…' : 'Сменить пароль'}</button>
              <button className="auth-resend-button" type="button" disabled={busy || retrySeconds > 0} onClick={resendCode}>
                {busy ? 'Отправляем…' : retrySeconds > 0 ? 'Отправить код повторно через ' + formatSeconds(retrySeconds) : 'Отправить код повторно'}
              </button>
            </form>
          )}

          {page.screen === 'session-pending' && (
            <form className="auth-form" onSubmit={submitForm} aria-busy={busy}>
              {csrfAndFlow}
              <button className="auth-primary-button" type="submit" disabled={busy}>{busy ? 'Проверяем…' : 'Продолжить'}</button>
            </form>
          )}
          <p className="auth-version">{page.version}</p>
        </section>
      </div>
    </main>
  );
}
