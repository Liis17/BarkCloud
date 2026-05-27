import React from 'react';
import type { Shell } from '../lib/types';

/** Данные каркаса (GET /api/me): профиль, хранилище, версия, хост. */
export const ShellContext = React.createContext<Shell | null>(null);

export function useShell(): Shell | null {
  return React.useContext(ShellContext);
}
