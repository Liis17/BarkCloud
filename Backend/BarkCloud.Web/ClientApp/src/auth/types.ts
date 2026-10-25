export type AuthScreen =
  | 'login'
  | 'login-otp'
  | 'register'
  | 'register-otp'
  | 'forgot'
  | 'reset'
  | 'session-pending';

export interface AuthValues {
  login?: string;
  username?: string;
  email?: string;
  first_name?: string;
  last_name?: string;
  remember?: boolean;
  revoke_sessions?: boolean;
  reset_passkeys?: boolean;
}

export interface AuthPageState {
  screen: AuthScreen;
  serverName: string;
  version: string;
  csrfToken: string;
  flowId?: string;
  flowExpiresAt?: string;
  codeExpiresAt?: string;
  retryAt?: string;
  otpMethod?: 'email' | 'authenticator';
  otpMailStatus?: 'sent' | 'existing';
  authenticatorRequired?: boolean;
  values: AuthValues;
  fieldErrors: Record<string, string>;
  formError?: string;
  formMessage?: string;
  features: {
    registrationEnabled: boolean;
    emailEnabled: boolean;
  };
  clearOtp?: boolean;
}

export interface AuthSuccess {
  redirectTo?: string;
}
