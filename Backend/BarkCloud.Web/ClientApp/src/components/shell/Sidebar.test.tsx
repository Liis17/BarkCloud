import { fireEvent, render, screen } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { Sidebar } from './Sidebar';
import { ShellContext } from '../../hooks/useShell';
import type { Shell } from '../../lib/types';

function renderSidebar(storage: Shell['storage'], collapsed = false, onToggle = vi.fn()) {
  const shell: Shell = {
    user: { initials: 'BC', displayName: 'Bark Cloud', role: 'admin', avatarUrl: '' },
    storage,
    app: { version: '1', edition: 'self-host' },
    server: { host: 'cloud.example' },
    sync: { status: 'Синхронизировано', lastAt: '12:00' },
  };
  return render(<MemoryRouter><ShellContext.Provider value={shell}><Sidebar collapsed={collapsed} onToggle={onToggle} /></ShellContext.Provider></MemoryRouter>);
}

afterEach(() => vi.unstubAllGlobals());

describe('Sidebar storage', () => {
  const diskStorage = {
    usedLabel: '4 ГБ', totalLabel: '10 ГБ', percent: 40, otherPct: 20, s3Pct: 20,
  };

  it('показывает подсчёт без фиктивных процентов и нулевой статистики', () => {
    const { container } = renderSidebar(null);
    expect(container.querySelector('.sb-brand img')?.getAttribute('src')).toBe('/barkcloud-icon.png');
    expect(screen.getAllByText('Подсчитываем…').length).toBeGreaterThan(0);
    expect(screen.queryByText('0% использовано')).toBeNull();
    expect(screen.queryByText('недоступно')).toBeNull();
    expect(screen.queryByText('0 Б / 0 Б')).toBeNull();
  });

  it('оставляет последний замер при ошибке обновления', () => {
    renderSidebar({ ...diskStorage, state: 'error', updatedAt: '2026-10-03T10:00:00Z',
      s3Groups: [{ id: 'grp-photos', name: 'Фото', usedLabel: '120 ГБ', quotaLabel: '300 ГБ', percent: 40,
        hasFiniteQuota: true, ungrouped: false, unmeasuredBuckets: 0 }],
      allS3StatsAvailable: true, allS3State: 'error', allS3UpdatedAt: '2026-10-03T10:00:00Z' });
    expect(screen.getByText('4 ГБ / 10 ГБ')).toBeTruthy();
    expect(screen.getByText('120 ГБ / 300 ГБ')).toBeTruthy();
    expect(screen.getAllByText(/Не удалось обновить/).length).toBe(2);
  });

  it('показывает несколько групп, безлимит и занятую статистику без группы с одним временем обновления', () => {
    renderSidebar({ ...diskStorage,
      s3Groups: [
        { id: 'grp-photos', name: 'Фото', usedLabel: '120 ГБ', quotaLabel: '300 ГБ', percent: 40,
          hasFiniteQuota: true, ungrouped: false, unmeasuredBuckets: 2 },
        { id: 'grp-video', name: 'Видео', usedLabel: '20 ГБ', quotaLabel: '0 Б', percent: 100,
          hasFiniteQuota: false, ungrouped: false, unmeasuredBuckets: 0 },
        { id: 'ungrouped', name: 'Без группы', usedLabel: '5 ГБ', quotaLabel: '0 Б', percent: 100,
          hasFiniteQuota: false, ungrouped: true, unmeasuredBuckets: 0 },
      ],
      allS3StatsAvailable: true, allS3State: 'ready', allS3UpdatedAt: '2026-10-03T10:00:00Z' });

    expect(screen.getByText('120 ГБ / 300 ГБ')).toBeTruthy();
    expect(screen.getByText('20 ГБ · безлимит')).toBeTruthy();
    expect(screen.getByText('5 ГБ')).toBeTruthy();
    expect(screen.getByText('Без группы')).toBeTruthy();
    expect(screen.getByText('учёт неполный')).toBeTruthy();
    expect(screen.getAllByText(/Обновлено/)).toHaveLength(1);
    expect(screen.queryByRole('progressbar', { name: 'Использование S3: Без группы' })).toBeNull();
  });

  it('показывает недоступное состояние одной строкой S3', () => {
    renderSidebar({ ...diskStorage, s3Groups: [], allS3StatsAvailable: false, allS3State: 'not_configured' });

    expect(screen.getByText('S3')).toBeTruthy();
    expect(screen.getByText('не настроен')).toBeTruthy();
    expect(screen.getAllByText('S3')).toHaveLength(1);
    expect(screen.queryAllByRole('progressbar', { name: /S3/ })).toHaveLength(0);
  });

  it('переключает панель доступной кнопкой с актуальным aria-expanded', () => {
    const onToggle = vi.fn();
    renderSidebar(null, false, onToggle);

    const button = screen.getByRole('button', { name: 'Свернуть панель' });
    expect(button.getAttribute('aria-expanded')).toBe('true');
    expect(button.getAttribute('title')).toBe('Свернуть панель');
    fireEvent.click(button);
    expect(onToggle).toHaveBeenCalledTimes(1);
  });

  it('в свёрнутом виде скрывает подписи классом, сохраняя доступные ссылки', () => {
    const { container } = renderSidebar(null, true);

    expect(container.querySelector('.sidebar')?.classList.contains('collapsed')).toBe(true);
    expect(screen.getByRole('button', { name: 'Развернуть панель' }).getAttribute('aria-expanded')).toBe('false');
    expect(screen.getByRole('link', { name: 'Фото' }).getAttribute('title')).toBe('Фото');
    expect(screen.getByRole('link', { name: 'Настройки' }).getAttribute('aria-label')).toBe('Настройки');
  });
});
