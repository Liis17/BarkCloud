import { act, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { AuthPage } from './AuthPage';
import type { AuthPageState } from './types';

function state(overrides: Partial<AuthPageState> = {}): AuthPageState {
  return {
    screen: 'login',
    serverName: 'cloud.example.test',
    version: 'v1.10.0',
    csrfToken: 'csrf-value',
    values: {},
    fieldErrors: {},
    features: { registrationEnabled: true, emailEnabled: true },
    ...overrides,
  };
}

function jsonResponse(value: unknown, status = 200): Response {
  return new Response(JSON.stringify(value), {
    status,
    headers: { 'Content-Type': 'application/json' },
  });
}

function requestReturning(value: unknown) {
  return vi.fn<typeof fetch>().mockResolvedValue(jsonResponse(value));
}

describe('auth screen states', () => {
  it('keeps password in React state when the server returns errors for the same screen', async () => {
    const request = requestReturning(state({ fieldErrors: { password: 'Пароль не подходит.' } }));
    render(<AuthPage initialState={state()} request={request} />);
    fireEvent.change(screen.getByLabelText('Логин или почта'), { target: { value: 'liis' } });
    fireEvent.change(screen.getByLabelText('Пароль'), { target: { value: 'secret1' } });

    await act(async () => fireEvent.submit(screen.getByRole('button', { name: 'Войти' }).closest('form')!));

    expect((screen.getByLabelText('Пароль') as HTMLInputElement).value).toBe('secret1');
    expect(screen.getByLabelText('Пароль').getAttribute('aria-invalid')).toBe('true');
    const formData = request.mock.calls[0][1]?.body as FormData;
    expect(formData.get('password')).toBe('secret1');
    expect(formData.has('flow_id')).toBe(false);
  });

  it('clears the old password when the server moves login to the code screen', async () => {
    const request = requestReturning(state({
      screen: 'login-otp',
      flowId: 'flow-1',
      values: { login: 'liis', remember: true },
      otpMethod: 'email',
    }));
    render(<AuthPage initialState={state()} request={request} />);
    fireEvent.change(screen.getByLabelText('Логин или почта'), { target: { value: 'liis' } });
    fireEvent.change(screen.getByLabelText('Пароль'), { target: { value: 'secret1' } });
    fireEvent.click(screen.getByLabelText('Оставаться в системе'));

    await act(async () => fireEvent.submit(screen.getByRole('button', { name: 'Войти' }).closest('form')!));

    expect(screen.getByRole('heading', { name: 'Подтвердите вход' })).not.toBeNull();
    expect(document.querySelector('input[name=\"password\"]')).toBeNull();
    expect((screen.getByRole('textbox', { name: 'Шестизначный код' }) as HTMLInputElement).value).toBe('');
  });

  it.each([
    ['login-otp', 'email'],
    ['register-otp', undefined],
    ['reset', undefined],
  ] as const)('shows a matching %s OTP error only once and keeps it associated with the input', (screenName, otpMethod) => {
    const message = 'Код недействителен или истёк.';
    render(<AuthPage initialState={state({
      screen: screenName,
      flowId: 'otp-flow',
      otpMethod,
      formError: message,
      fieldErrors: { otp: message },
    })} />);

    const input = screen.getByRole('textbox', { name: 'Шестизначный код' });
    expect(screen.getAllByText(message)).toHaveLength(1);
    expect(input.getAttribute('aria-invalid')).toBe('true');
    expect(input.getAttribute('aria-describedby')).toBe('auth-otp-error');
    expect(document.getElementById('auth-otp-error')?.getAttribute('role')).toBe('alert');
  });

  it('keeps distinct form, network, and status messages alongside field errors', async () => {
    const request = vi.fn<typeof fetch>().mockRejectedValue(new Error('Сеть недоступна.'));
    render(<AuthPage initialState={state({
      screen: 'login-otp',
      flowId: 'otp-flow',
      otpMethod: 'email',
      formError: 'Проверьте данные формы.',
      formMessage: 'Код запрошен повторно.',
      fieldErrors: { otp: 'Код неверен.' },
    })} request={request} />);
    fireEvent.change(screen.getByRole('textbox', { name: 'Шестизначный код' }), { target: { value: '123456' } });

    await act(async () => fireEvent.submit(screen.getByRole('button', { name: 'Продолжить' }).closest('form')!));

    expect(screen.getByText('Проверьте данные формы.')).not.toBeNull();
    expect(screen.getByText('Код неверен.')).not.toBeNull();
    expect(screen.getByText('Код запрошен повторно.')).not.toBeNull();
    expect(screen.getByText('Сеть недоступна.')).not.toBeNull();
    expect(screen.getByRole('status').textContent).toBe('Код запрошен повторно.');
    expect(screen.getAllByRole('alert')).toHaveLength(2);
  });

  it('keeps the registration spam hint to one visible copy', () => {
    render(<AuthPage initialState={state({ screen: 'register-otp', flowId: 'reg-flow' })} />);
    expect(screen.getAllByText('Если письма нет, проверьте папку «Спам».')).toHaveLength(1);
  });

  it('shows optional registration names and does not require them', async () => {
    const request = requestReturning(state({ screen: 'register-otp', flowId: 'reg-flow' }));
    render(<AuthPage initialState={state({ screen: 'register' })} request={request} />);
    const firstName = screen.getByLabelText('Имя (необязательно)') as HTMLInputElement;
    const lastName = screen.getByLabelText('Фамилия (необязательно)') as HTMLInputElement;
    expect(firstName.required).toBe(false);
    expect(lastName.required).toBe(false);
    fireEvent.change(screen.getByLabelText('Логин'), { target: { value: 'liis' } });
    fireEvent.change(screen.getByLabelText('Электронная почта'), { target: { value: 'liis@example.test' } });
    fireEvent.change(screen.getByLabelText('Пароль'), { target: { value: 'secret1' } });

    await act(async () => fireEvent.submit(screen.getByRole('button', { name: 'Создать аккаунт' }).closest('form')!));

    const formData = request.mock.calls[0][1]?.body as FormData;
    expect(formData.get('first_name')).toBe('');
    expect(formData.get('last_name')).toBe('');
    expect(screen.getByRole('heading', { name: 'Подтвердите почту' })).not.toBeNull();
  });

  it('keeps registration available when email delivery is disabled', async () => {
    const request = requestReturning(state({ screen: 'session-pending', flowId: 'pending-flow' }));
    render(<AuthPage initialState={state({
      screen: 'register',
      features: { registrationEnabled: true, emailEnabled: false },
    })} request={request} />);
    const email = screen.getByLabelText('Электронная почта') as HTMLInputElement;
    expect(email.required).toBe(true);
    fireEvent.change(screen.getByLabelText('Логин'), { target: { value: 'liis' } });
    fireEvent.change(email, { target: { value: 'liis@example.test' } });
    fireEvent.change(screen.getByLabelText('Пароль'), { target: { value: 'secret1' } });

    await act(async () => fireEvent.submit(screen.getByRole('button', { name: 'Создать аккаунт' }).closest('form')!));

    expect(request.mock.calls[0][0]).toBe('/register');
    expect(screen.getByRole('heading', { name: 'Аккаунт создан' })).not.toBeNull();
  });

  it('keeps the reset password and revoke choice after same-screen validation errors', async () => {
    const request = requestReturning(state({
      screen: 'reset',
      flowId: 'reset-flow',
      values: { revoke_sessions: true },
      fieldErrors: { otp: 'Код устарел.' },
    }));
    render(<AuthPage initialState={state({
      screen: 'reset',
      flowId: 'reset-flow',
      values: { revoke_sessions: true },
    })} request={request} />);
    fireEvent.change(screen.getByRole('textbox', { name: 'Шестизначный код' }), { target: { value: '123456' } });
    fireEvent.change(screen.getByLabelText('Новый пароль'), { target: { value: 'another1' } });
    fireEvent.click(screen.getByLabelText('Сбросить все passkey'));

    await act(async () => fireEvent.submit(screen.getByRole('button', { name: 'Сменить пароль' }).closest('form')!));

    expect((screen.getByLabelText('Новый пароль') as HTMLInputElement).value).toBe('another1');
    expect((screen.getByLabelText('Выйти с других устройств') as HTMLInputElement).checked).toBe(true);
    expect((screen.getByLabelText('Сбросить все passkey') as HTMLInputElement).checked).toBe(false);
    expect(screen.getByText('Passkey сохранятся и продолжат работать после смены пароля.')).not.toBeNull();
    expect((screen.getByRole('textbox', { name: 'Шестизначный код' }) as HTMLInputElement).value).toBe('123456');
  });

  it('shows the authenticator field on the reset screen only when the server requires it', () => {
    render(<AuthPage initialState={state({ screen: 'reset', flowId: 'reset-flow' })} />);
    expect(screen.queryByRole('textbox', { name: 'Код приложения-аутентификатора' })).toBeNull();
  });

  it('resubmits the email code together with the authenticator code', async () => {
    const request = requestReturning(state({
      screen: 'reset',
      flowId: 'reset-flow',
      authenticatorRequired: true,
      fieldErrors: { authenticator: 'Введите код из приложения-аутентификатора, чтобы завершить восстановление.' },
    }));
    render(<AuthPage initialState={state({
      screen: 'reset',
      flowId: 'reset-flow',
      authenticatorRequired: true,
    })} request={request} />);
    fireEvent.change(screen.getByRole('textbox', { name: 'Шестизначный код' }), { target: { value: '123456' } });
    fireEvent.change(screen.getByRole('textbox', { name: 'Код приложения-аутентификатора' }), { target: { value: '654321' } });
    fireEvent.change(screen.getByLabelText('Новый пароль'), { target: { value: 'another1' } });

    await act(async () => fireEvent.submit(screen.getByRole('button', { name: 'Сменить пароль' }).closest('form')!));

    const formData = request.mock.calls[0][1]?.body as FormData;
    expect(formData.get('otp')).toBe('123456');
    expect(formData.get('authenticator_code')).toBe('654321');
    expect(formData.get('password')).toBe('another1');
  });

  it('requires six digits of the authenticator code before submitting', async () => {
    const request = requestReturning(state({ screen: 'reset', flowId: 'reset-flow', authenticatorRequired: true }));
    render(<AuthPage initialState={state({
      screen: 'reset',
      flowId: 'reset-flow',
      authenticatorRequired: true,
    })} request={request} />);
    fireEvent.change(screen.getByRole('textbox', { name: 'Шестизначный код' }), { target: { value: '123456' } });
    fireEvent.change(screen.getByRole('textbox', { name: 'Код приложения-аутентификатора' }), { target: { value: '654' } });
    fireEvent.change(screen.getByLabelText('Новый пароль'), { target: { value: 'another1' } });

    await act(async () => fireEvent.submit(screen.getByRole('button', { name: 'Сменить пароль' }).closest('form')!));

    expect(request).not.toHaveBeenCalled();
    expect(screen.getByText('Введите все шесть цифр кода из приложения.')).not.toBeNull();
  });

  it('clears the authenticator code when a resend restarts the email step', async () => {
    const request = requestReturning(state({ screen: 'reset', flowId: 'reset-flow' }));
    render(<AuthPage initialState={state({
      screen: 'reset',
      flowId: 'reset-flow',
      authenticatorRequired: true,
    })} request={request} />);

    await act(async () => fireEvent.click(screen.getByRole('button', { name: 'Отправить код повторно' })));

    expect(request.mock.calls[0][0]).toBe('/forgot/resend');
    expect(screen.queryByRole('textbox', { name: 'Код приложения-аутентификатора' })).toBeNull();
  });

  it('submits a pending registration retry with only flow and CSRF fields', async () => {
    const request = requestReturning(state({ screen: 'session-pending', flowId: 'pending-flow' }));
    render(<AuthPage initialState={state({ screen: 'session-pending', flowId: 'pending-flow' })} request={request} />);

    await act(async () => fireEvent.submit(screen.getByRole('button', { name: 'Продолжить' }).closest('form')!));

    expect(request.mock.calls[0][0]).toBe('/register/confirm');
    const formData = request.mock.calls[0][1]?.body as FormData;
    expect([...formData.keys()].sort()).toEqual(['__RequestVerificationToken', 'flow_id']);
    expect(formData.get('flow_id')).toBe('pending-flow');
  });

  it('hides registration and password recovery links when the server disables them', () => {
    render(<AuthPage initialState={state({ features: { registrationEnabled: false, emailEnabled: false } })} />);
    expect(screen.queryByRole('link', { name: 'Создать аккаунт' })).toBeNull();
    expect(screen.queryByRole('link', { name: 'Забыли пароль?' })).toBeNull();
  });

  it('uses neutral recovery wording without echoing a login or email', () => {
    render(<AuthPage initialState={state({ screen: 'forgot', values: { login: 'liis' } })} />);
    expect(screen.getByText('Если аккаунт существует, мы отправим письмо с кодом для восстановления.')).not.toBeNull();
    expect(screen.queryByText(/liis/)).toBeNull();
  });
});

describe('one-time code input', () => {
  const otpState = (overrides: Partial<AuthPageState> = {}) => state({
    screen: 'login-otp',
    flowId: 'otp-flow',
    otpMethod: 'email',
    ...overrides,
  });

  it.each([
    ['six digits', '123456', '123456'],
    ['leading zeroes', '001234', '001234'],
    ['spaces', '123 456', '123456'],
    ['hyphens', '123-456', '123456'],
  ])('accepts %s as the visible six digit value', (_label, input, expected) => {
    render(<AuthPage initialState={otpState()} />);
    const code = screen.getByRole('textbox', { name: 'Шестизначный код' }) as HTMLInputElement;
    fireEvent.change(code, { target: { value: input } });
    expect(code.value).toBe(expected);
    expect(code.maxLength).toBe(-1);
    expect(document.querySelectorAll('.auth-otp-cell')).toHaveLength(6);
  });

  it('rejects overflow and non-digit characters while preserving the previous value', () => {
    render(<AuthPage initialState={otpState()} />);
    const code = screen.getByRole('textbox', { name: 'Шестизначный код' }) as HTMLInputElement;
    fireEvent.change(code, { target: { value: '123456' } });
    fireEvent.change(code, { target: { value: '1234567' } });
    expect(code.value).toBe('123456');
    expect(screen.getByText(/Введите только цифры/)).not.toBeNull();
    fireEvent.change(code, { target: { value: '12a456' } });
    expect(code.value).toBe('123456');
  });

  it('accepts middle replacement, range selection edits, and backspace edits', () => {
    render(<AuthPage initialState={otpState()} />);
    const code = screen.getByRole('textbox', { name: 'Шестизначный код' }) as HTMLInputElement;
    fireEvent.change(code, { target: { value: '123456', selectionStart: 6, selectionEnd: 6 } });
    code.setSelectionRange(2, 4);
    fireEvent.change(code, { target: { value: '12956', selectionStart: 3, selectionEnd: 3 } });
    expect(code.value).toBe('12956');
    code.setSelectionRange(2, 2);
    fireEvent.change(code, { target: { value: '1256', selectionStart: 2, selectionEnd: 2 } });
    expect(code.value).toBe('1256');
    code.setSelectionRange(2, 3);
    fireEvent.change(code, { target: { value: '126', selectionStart: 2, selectionEnd: 2 } });
    expect(code.value).toBe('126');
  });

  it('resets trailing input scroll before pointer mapping and after selection without losing the caret', () => {
    render(<AuthPage initialState={otpState()} />);
    const code = screen.getByRole('textbox', { name: 'Шестизначный код' }) as HTMLInputElement;
    code.scrollLeft = 21;
    fireEvent.change(code, { target: { value: '123456', selectionStart: 6, selectionEnd: 6 } });
    expect(code.scrollLeft).toBe(0);

    code.scrollLeft = 21;
    fireEvent.pointerDown(code, { clientX: 110.7 });
    expect(code.scrollLeft).toBe(0);
    code.setSelectionRange(1, 1);
    code.scrollLeft = 21;
    fireEvent.select(code);

    expect(code.selectionStart).toBe(1);
    expect(code.scrollLeft).toBe(0);
    expect(document.querySelectorAll('.auth-otp-cell')[1].querySelector('.auth-otp-caret')).not.toBeNull();
  });

  it('handles browser autofill input without automatically submitting', () => {
    const request = requestReturning(otpState());
    render(<AuthPage initialState={otpState()} request={request} />);
    const code = screen.getByRole('textbox', { name: 'Шестизначный код' }) as HTMLInputElement;
    fireEvent.input(code, { target: { value: '001234' } });
    expect(code.value).toBe('001234');
    expect(request).not.toHaveBeenCalled();
  });

  it('requires six ASCII digits on explicit submit and sends the active flow', async () => {
    const request = requestReturning(otpState());
    render(<AuthPage initialState={otpState()} request={request} />);
    const form = screen.getByRole('button', { name: 'Продолжить' }).closest('form')!;
    fireEvent.change(screen.getByRole('textbox', { name: 'Шестизначный код' }), { target: { value: '123' } });
    await act(async () => fireEvent.submit(form));
    expect(request).not.toHaveBeenCalled();
    expect(screen.getByText('Введите все шесть цифр кода.')).not.toBeNull();

    fireEvent.change(screen.getByRole('textbox', { name: 'Шестизначный код' }), { target: { value: '001234' } });
    await act(async () => fireEvent.submit(form));

    expect(request.mock.calls[0][0]).toBe('/login');
    const formData = request.mock.calls[0][1]?.body as FormData;
    expect(formData.get('otp')).toBe('001234');
    expect(formData.get('flow_id')).toBe('otp-flow');
  });

  it('clears the code and restores focus when the server requests a fresh code', async () => {
    const request = requestReturning(otpState({ clearOtp: true, fieldErrors: { otp: 'Повторите ввод.' } }));
    render(<AuthPage initialState={otpState()} request={request} />);
    const code = screen.getByRole('textbox', { name: 'Шестизначный код' }) as HTMLInputElement;
    fireEvent.change(code, { target: { value: '123456' } });
    await act(async () => fireEvent.submit(code.closest('form')!));

    await waitFor(() => {
      expect(code.value).toBe('');
      expect(document.activeElement).toBe(code);
    });
  });

  it('shows authenticator instructions without offering email resend', () => {
    render(<AuthPage initialState={otpState({ otpMethod: 'authenticator' })} />);
    expect(screen.getByText('Откройте приложение-аутентификатор и введите текущий код.')).not.toBeNull();
    expect(screen.queryByRole('button', { name: /Отправить код повторно/ })).toBeNull();
  });

  it('does not offer email resend when the OTP method is unknown', () => {
    render(<AuthPage initialState={otpState({ otpMethod: undefined })} />);
    expect(screen.getByText('Введите код подтверждения, который вы получили.')).not.toBeNull();
    expect(screen.queryByRole('button', { name: /Отправить код повторно/ })).toBeNull();
  });

  it('shows email state and enforces the server-provided resend cooldown', () => {
    render(<AuthPage initialState={otpState({
      otpMailStatus: 'existing',
      retryAt: new Date(Date.now() + 60_000).toISOString(),
    })} />);
    expect(screen.getByText('Код уже отправлен. Проверьте почту.')).not.toBeNull();
    const resend = screen.getByRole('button', { name: /Отправить повторно через/ }) as HTMLButtonElement;
    expect(resend.disabled).toBe(true);
  });

  it('keeps the locally unchecked session-revocation choice after resend returns stale flow values', async () => {
    const request = requestReturning(state({
      screen: 'reset',
      flowId: 'reset-flow',
      values: { revoke_sessions: true },
      formMessage: 'Повторная отправка запрошена.',
    }));
    render(<AuthPage initialState={state({
      screen: 'reset',
      flowId: 'reset-flow',
      values: { revoke_sessions: true },
    })} request={request} />);
    fireEvent.click(screen.getByLabelText('Выйти с других устройств'));
    fireEvent.click(screen.getByLabelText('Сбросить все passkey'));

    await act(async () => fireEvent.click(screen.getByRole('button', { name: 'Отправить код повторно' })));

    expect((screen.getByLabelText('Выйти с других устройств') as HTMLInputElement).checked).toBe(false);
    expect((screen.getByLabelText('Сбросить все passkey') as HTMLInputElement).checked).toBe(false);
    const formData = request.mock.calls[0][1]?.body as FormData;
    expect([...formData.keys()].sort()).toEqual(['__RequestVerificationToken', 'flow_id']);
  });

  it('disables mutations synchronously and sends only one request for repeated submit', async () => {
    let finishRequest: ((response: Response) => void) | undefined;
    const request = vi.fn<typeof fetch>(() => new Promise<Response>((resolve) => { finishRequest = resolve; }));
    render(<AuthPage initialState={state()} request={request} />);
    fireEvent.change(screen.getByLabelText('Логин или почта'), { target: { value: 'liis' } });
    fireEvent.change(screen.getByLabelText('Пароль'), { target: { value: 'secret1' } });
    const form = screen.getByRole('button', { name: 'Войти' }).closest('form')!;

    await act(async () => {
      fireEvent.submit(form);
      fireEvent.submit(form);
    });
    expect(request).toHaveBeenCalledTimes(1);
    expect((screen.getByRole('button', { name: 'Входим…' }) as HTMLButtonElement).disabled).toBe(true);

    await act(async () => finishRequest?.(jsonResponse(state())));
  });
});

describe('WebAuthn passwordless login', () => {
  it('sends CSRF and preserves the remember choice through the assertion', async () => {
    const request = vi.fn<typeof fetch>()
      .mockResolvedValueOnce(jsonResponse({ optionsJson: '{\"challenge\":\"AA\"}', challengeId: 'challenge-1' }))
      .mockResolvedValueOnce(jsonResponse({ ok: true }));
    const authenticate = vi.fn().mockResolvedValue('{\"id\":\"assertion\"}');
    const navigate = vi.fn();
    render(<AuthPage
      initialState={state({ values: { remember: true } })}
      request={request}
      authenticate={authenticate}
      passkeyAvailable
      navigate={navigate}
    />);

    await act(async () => fireEvent.click(screen.getByRole('button', { name: 'Войти с ключом' })));

    expect(request).toHaveBeenCalledTimes(2);
    expect((request.mock.calls[0][1]?.headers as Record<string, string>)['X-CSRF-TOKEN']).toBe('csrf-value');
    const complete = JSON.parse(String(request.mock.calls[1][1]?.body)) as Record<string, unknown>;
    expect(complete).toEqual({ challengeId: 'challenge-1', assertion: { id: 'assertion' }, remember: true });
    expect(navigate).toHaveBeenCalledWith('/photos');
  });

  it('quietly returns when the user cancels the platform key prompt', async () => {
    const request = requestReturning({ optionsJson: '{}', challengeId: 'challenge-1' });
    const cancel = Object.assign(new Error('cancelled'), { name: 'NotAllowedError' });
    const authenticate = vi.fn().mockRejectedValue(cancel);
    render(<AuthPage initialState={state()} request={request} authenticate={authenticate} passkeyAvailable />);

    await act(async () => fireEvent.click(screen.getByRole('button', { name: 'Войти с ключом' })));

    expect(screen.queryByRole('alert')).toBeNull();
    expect(request).toHaveBeenCalledTimes(1);
  });

  it('defaults reset choices to true while preserving explicit false', () => {
    const { unmount } = render(<AuthPage initialState={state()} />);
    expect((screen.getByLabelText('Оставаться в системе') as HTMLInputElement).checked).toBe(true);
    unmount();
    const resetPage = state({ screen: 'reset' });
    const resetView = render(<AuthPage initialState={resetPage} />);
    expect((screen.getByLabelText('Выйти с других устройств') as HTMLInputElement).checked).toBe(true);
    expect((screen.getByLabelText('Сбросить все passkey') as HTMLInputElement).checked).toBe(true);
    expect(screen.getByText('При сбросе пароля будут удалены все ключи доступа (passkey). Их потребуется добавить заново.')).not.toBeNull();
    expect(screen.getByText('Выход с устройств не удаляет passkey.')).not.toBeNull();
    resetView.unmount();
    render(<AuthPage initialState={state({ values: { remember: false } })} />);
    expect((screen.getByLabelText('Оставаться в системе') as HTMLInputElement).checked).toBe(false);
    const falseReset = render(<AuthPage initialState={state({ screen: 'reset', values: { revoke_sessions: false, reset_passkeys: false } })} />);
    expect((screen.getByLabelText('Выйти с других устройств') as HTMLInputElement).checked).toBe(false);
    expect((screen.getByLabelText('Сбросить все passkey') as HTMLInputElement).checked).toBe(false);
    falseReset.unmount();
  });
});
