import { describe, expect, it } from 'vitest';
import { loginUrl, returnUrlFromLocation } from './loginRedirect';

describe('login return url', () => {
  it('encodes the current page into returnUrl', () => {
    expect(loginUrl('/files?dir=a&b=1#x')).toBe('/login?returnUrl=%2Ffiles%3Fdir%3Da%26b%3D1%23x');
  });

  it('restores the fragment kept by the browser across the server redirect', () => {
    expect(returnUrlFromLocation({ search: '?returnUrl=%2Fsettings', hash: '#tags' })).toBe('/settings#tags');
    expect(returnUrlFromLocation({ search: '?returnUrl=%2Fsettings%23tags', hash: '' })).toBe('/settings#tags');
    expect(returnUrlFromLocation({ search: '', hash: '#tags' })).toBeUndefined();
  });
});
