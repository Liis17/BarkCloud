import type { StatsState, SidebarStorage } from '../../lib/types';

export const storagePending = (data: SidebarStorage) =>
  data.state === 'loading' || data.state === 'refreshing' || data.allS3State === 'loading' || data.allS3State === 'refreshing';

export function StorageStatsStatus({ state, updatedAt }: { state?: StatsState; updatedAt?: string | null }) {
  const date = updatedAt ? new Date(updatedAt) : null;
  const message = state === 'loading' ? 'Подсчитываем…' : state === 'refreshing' ? 'Обновляем…' : state === 'error'
    ? updatedAt ? 'Не удалось обновить' : 'Не удалось получить статистику' : state === 'not_configured' ? 'S3 не настроен' : null;
  return <span className={'storage-stats-status' + (state === 'error' ? ' error' : '')} role="status">
    {message}
    {date && <time dateTime={updatedAt!} title={date.toLocaleString('ru-RU')}>
      {message ? ' · ' : ''}Обновлено {date.toLocaleTimeString('ru-RU', { hour: '2-digit', minute: '2-digit' })}
    </time>}
  </span>;
}
