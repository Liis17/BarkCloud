import { render, screen } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { Sidebar } from './Sidebar';
import { ShellContext } from '../../hooks/useShell';
import type { Shell } from '../../lib/types';

function renderSidebar(storage: Shell['storage']) {
  const shell: Shell = {
    user: { initials: 'BC', displayName: 'Bark Cloud', role: 'admin', avatarUrl: '' },
    storage,
    app: { version: '1', edition: 'self-host' },
    server: { host: 'cloud.example' },
    sync: { status: 'Синхронизировано', lastAt: '12:00' },
  };
  return render(<MemoryRouter><ShellContext.Provider value={shell}><Sidebar /></ShellContext.Provider></MemoryRouter>);
}

afterEach(() => vi.unstubAllGlobals());

describe('Sidebar storage', () => {
  const diskStorage = {
    usedLabel: '4 ГБ', totalLabel: '10 ГБ', percent: 40, otherPct: 20, s3Pct: 20,
  };

  it('показывает подсчёт без фиктивных процентов и нулевой статистики', () => {
    renderSidebar(null);
    expect(screen.getAllByText('Подсчитываем…').length).toBeGreaterThan(0);
    expect(screen.queryByText('0% использовано')).toBeNull();
    expect(screen.queryByText('недоступно')).toBeNull();
    expect(screen.queryByText('0 Б / 0 Б')).toBeNull();
  });

  it('оставляет последний замер при ошибке обновления', () => {
    renderSidebar({ ...diskStorage, state: 'error', updatedAt: '2026-10-03T10:00:00Z',
      allS3UsedLabel: '120 ГБ', allS3QuotaLabel: '300 ГБ', allS3Percent: 40,
      allS3HasFiniteQuota: true, allS3StatsAvailable: true,
      allS3State: 'error', allS3UpdatedAt: '2026-10-03T10:00:00Z' });
    expect(screen.getByText('4 ГБ / 10 ГБ')).toBeTruthy();
    expect(screen.getByText('120 ГБ / 300 ГБ')).toBeTruthy();
    expect(screen.getAllByText(/Не удалось обновить/).length).toBe(2);
  });

  it('первая ошибка подсчёта не выглядит как продолжающаяся загрузка', () => {
    renderSidebar({ ...diskStorage, state: 'error', updatedAt: null,
      allS3UsedLabel: '0 Б', allS3QuotaLabel: '0 Б', allS3Percent: 0,
      allS3HasFiniteQuota: false, allS3StatsAvailable: false, allS3State: 'error' });
    expect(screen.queryByText('Подсчитываем…')).toBeNull();
    expect(screen.getByText('Ошибка подсчёта')).toBeTruthy();
    expect(document.querySelector('.bar.pending')).toBeNull();
  });

  it('removes the expand action and displays a finite S3 quota', () => {
    renderSidebar({
      ...diskStorage,
      allS3UsedLabel: '120 ГБ', allS3QuotaLabel: '300 ГБ', allS3Percent: 40,
      allS3HasFiniteQuota: true, allS3StatsAvailable: true,
    });

    expect(screen.queryByText('Расширить')).toBeNull();
    expect(screen.getByText('120 ГБ / 300 ГБ')).toBeTruthy();
    expect(screen.getByRole('progressbar', { name: 'Использование S3-хранилища' }).getAttribute('aria-valuenow')).toBe('40');
    expect(screen.getAllByText('40% использовано').length).toBeGreaterThan(0);
  });

  it('fills the S3 line and labels it unlimited when any bucket has no quota', () => {
    renderSidebar({
      ...diskStorage,
      allS3UsedLabel: '120 ГБ', allS3QuotaLabel: '50 ГБ', allS3Percent: 0,
      allS3HasFiniteQuota: false, allS3StatsAvailable: true,
    });

    expect(screen.getByText('120 ГБ · безлимит')).toBeTruthy();
    expect(screen.getByText('безлимит')).toBeTruthy();
    expect(screen.getByRole('progressbar', { name: 'Использование S3-хранилища' }).getAttribute('aria-valuenow')).toBe('100');
  });

  it('shows an unavailable state instead of a zero-byte S3 reading', () => {
    renderSidebar({
      ...diskStorage,
      allS3UsedLabel: '0 Б', allS3QuotaLabel: '0 Б', allS3Percent: 0,
      allS3HasFiniteQuota: false, allS3StatsAvailable: false,
    });

    expect(screen.getAllByText('недоступно').length).toBeGreaterThan(0);
    expect(screen.queryByText('0 Б · безлимит')).toBeNull();
  });
});
