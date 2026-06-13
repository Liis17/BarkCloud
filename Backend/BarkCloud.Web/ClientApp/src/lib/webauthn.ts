// Хелперы WebAuthn: конвертация между Fido2NetLib JSON (base64url) и нативными
// объектами navigator.credentials. Результат — JSON в формате
// AuthenticatorAttestation/AssertionRawResponse, который ждёт сервер.

function b64urlToBuf(b64url: string): ArrayBuffer {
  const pad = b64url.length % 4 === 0 ? '' : '='.repeat(4 - (b64url.length % 4));
  const b64 = b64url.replace(/-/g, '+').replace(/_/g, '/') + pad;
  const bin = atob(b64);
  const bytes = new Uint8Array(bin.length);
  for (let i = 0; i < bin.length; i++) bytes[i] = bin.charCodeAt(i);
  return bytes.buffer;
}

function bufToB64url(buf: ArrayBuffer): string {
  const bytes = new Uint8Array(buf);
  let bin = '';
  for (let i = 0; i < bytes.length; i++) bin += String.fromCharCode(bytes[i]);
  return btoa(bin).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
}

export function webauthnSupported(): boolean {
  return typeof window !== 'undefined' && !!window.PublicKeyCredential;
}

// Привязка ключа: optionsJson (CredentialCreateOptions) → attestation JSON для сервера.
export async function webauthnRegister(optionsJson: string): Promise<string> {
  const o = JSON.parse(optionsJson);
  const publicKey: PublicKeyCredentialCreationOptions = {
    ...o,
    challenge: b64urlToBuf(o.challenge),
    user: { ...o.user, id: b64urlToBuf(o.user.id) },
    excludeCredentials: (o.excludeCredentials || []).map((c: { id: string; type: string; transports?: string[] }) => ({
      ...c,
      id: b64urlToBuf(c.id),
    })),
  };

  const cred = (await navigator.credentials.create({ publicKey })) as PublicKeyCredential | null;
  if (!cred) throw new Error('Ключ не вернул учётные данные');
  const resp = cred.response as AuthenticatorAttestationResponse;

  return JSON.stringify({
    id: cred.id,
    rawId: bufToB64url(cred.rawId),
    type: cred.type,
    extensions: cred.getClientExtensionResults(),
    response: {
      attestationObject: bufToB64url(resp.attestationObject),
      clientDataJSON: bufToB64url(resp.clientDataJSON),
    },
  });
}

// Вход по ключу: optionsJson (AssertionOptions) → assertion JSON для сервера.
export async function webauthnAuthenticate(optionsJson: string): Promise<string> {
  const o = JSON.parse(optionsJson);
  const publicKey: PublicKeyCredentialRequestOptions = {
    ...o,
    challenge: b64urlToBuf(o.challenge),
    allowCredentials: (o.allowCredentials || []).map((c: { id: string; type: string; transports?: string[] }) => ({
      ...c,
      id: b64urlToBuf(c.id),
    })),
  };

  const cred = (await navigator.credentials.get({ publicKey })) as PublicKeyCredential | null;
  if (!cred) throw new Error('Ключ не вернул подпись');
  const resp = cred.response as AuthenticatorAssertionResponse;

  return JSON.stringify({
    id: cred.id,
    rawId: bufToB64url(cred.rawId),
    type: cred.type,
    extensions: cred.getClientExtensionResults(),
    response: {
      authenticatorData: bufToB64url(resp.authenticatorData),
      clientDataJSON: bufToB64url(resp.clientDataJSON),
      signature: bufToB64url(resp.signature),
      userHandle: resp.userHandle ? bufToB64url(resp.userHandle) : null,
    },
  });
}
