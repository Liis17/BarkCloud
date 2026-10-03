import React from 'react';
import { MigrationJob, migrationRequest, MigrationApiError } from '../../hooks/useStorageMigration';
import '../../styles/migration.css';

const PHASES: Record<string, string> = {
  counting: 'Подсчёт объектов', copying: 'Копирование', verifying: 'Проверка содержимого',
  copied: 'Копирование завершено', draining: 'Ожидание активных загрузок',
  'final-counting': 'Финальный подсчёт', 'final-copying': 'Финальная досинхронизация',
  'final-verifying': 'Финальная проверка содержимого', applying: 'Сохранение подключений',
  restarting: 'Перезапуск и проверка Files', completed: 'Миграция применена',
};
export function migrationPercent(job: MigrationJob) {
  if (['copied', 'completed'].includes(job.state)) return 100;
  if (job.phase.includes('counting')) return 0;
  const total = BigInt(job.totalBytes || '0');
  const copied = BigInt(job.copiedBytes || '0') + BigInt(job.currentBytes || '0');
  if (total === 0n) return job.totalFiles > 0 ? Math.min(99, Math.floor(job.copiedFiles * 100 / job.totalFiles)) : 0;
  return Math.max(0, Math.min(99, Number(copied * 10000n / total) / 100));
}
export function migrationBytes(value: string) {
  const bytes = BigInt(value || '0');
  const units = ['Б', 'КиБ', 'МиБ', 'ГиБ', 'ТиБ', 'ПиБ'];
  let divisor = 1n, unit = 0;
  while (bytes >= divisor * 1024n && unit < units.length - 1) { divisor *= 1024n; unit++; }
  return (unit ? (Number(bytes * 10n / divisor) / 10).toLocaleString('ru-RU') : bytes.toLocaleString('ru-RU')) + ' ' + units[unit];
}
export function WavyProgress({ value, animated, label }: { value: number; animated: boolean; label: string }) {
  const clip = 'migration-wave-' + React.useId().replace(/:/g, '');
  const wave = 'M -32 12 ' + Array.from({ length: 34 }, () => 'q 8 -8 16 0 q 8 8 16 0').join(' ');
  return <div className={'migration-wave' + (animated ? ' animated' : '')} role="progressbar" aria-label={label}
    aria-valuemin={0} aria-valuemax={100} aria-valuenow={value}>
    <svg viewBox="0 0 1000 24" preserveAspectRatio="none" aria-hidden="true">
      <defs><clipPath id={clip}><rect x="0" y="0" width={value * 10} height="24" /></clipPath></defs>
      <path className="migration-wave-track" d={`M ${Math.min(1000, value * 10 + 12)} 12 H 996`} />
      <g clipPath={`url(#${clip})`}><path className="migration-wave-line" d={wave} /></g>
      {value < 100 && <circle className="migration-wave-stop" cx="996" cy="12" r="2" />}
    </svg>
  </div>;
}

export function MigrationProgress({ job, onRefresh, onExpired, applyLink = false }: {
  job: MigrationJob; onRefresh: () => Promise<void>; onExpired: () => void; applyLink?: boolean;
}) {
  const [busy, setBusy] = React.useState(false);
  const [message, setMessage] = React.useState('');
  const percent = migrationPercent(job);
  async function act(action: string) {
    setBusy(true); setMessage('');
    try { await migrationRequest('/api/settings/migration/jobs/' + job.id + '/' + action, {}); await onRefresh(); }
    catch (e) {
      if (e instanceof MigrationApiError && e.status === 403) onExpired();
      else setMessage(e instanceof Error ? e.message : 'Не удалось выполнить операцию');
    } finally { setBusy(false); }
  }
  return <article className="migration-progress-card" aria-label={'Перенос ' + job.source.bucketName}>
    <div className="migration-progress-head"><div><strong>{job.source.bucketName} → {job.destination.bucketName}</strong>
      <small>{job.destination.serviceUrl}</small></div><span className="migration-percent">{percent.toLocaleString('ru-RU', { maximumFractionDigits: 1 })}%</span></div>
    <div className="migration-phase" role="status">{job.state === 'queued' ? 'В очереди' : job.state === 'stopping' ? 'Остановка…' : job.state === 'cancelled' ? 'Задача остановлена' : PHASES[job.phase] || job.phase}</div>
    <WavyProgress value={percent} animated={job.state === 'running'} label="Прогресс переноса" />
    <div className="migration-stats"><span>{migrationBytes(job.copiedBytes)} / {migrationBytes(job.totalBytes)}<small>Подтверждено после чтения копий</small></span>
      <span>{job.copiedFiles.toLocaleString('ru-RU')} / {job.totalFiles.toLocaleString('ru-RU')}<small>Проверенных файлов</small></span></div>
    {job.phase === 'draining' && <p>Активных загрузок и обработок: {job.activeUploads}. Они завершатся перед переключением.</p>}
    {job.currentKey !== null && <div className="migration-current">{job.currentName && <strong>{job.currentName}</strong>}<small>Полный S3-ключ</small><code>{job.currentKey}</code></div>}
    {job.error && <p className="migration-error" role="alert">{job.error}</p>}
    {message && <p className="migration-error" role="alert">{message}</p>}
    <div className="migration-actions">
      {job.canRetry && <button className="btn primary" disabled={busy} onClick={() => void act('retry')}>Продолжить</button>}
      {job.canCancel && <button className="btn text" disabled={busy || job.state === 'stopping'} onClick={() => void act('cancel')}>{job.phase.startsWith('final-') || job.phase === 'draining' || job.phase === 'applying' ? 'Отменить переключение' : 'Остановить'}</button>}
      {applyLink && job.canApply && <a className="btn primary" href="/settings#server-settings">К применению миграции</a>}
    </div>
    {job.state === 'copied' && <p className="migration-note">Источник остаётся доступным. Примените миграцию в основных настройках S3: перед сменой подключения новые и изменённые объекты будут скопированы ещё раз.</p>}
    {job.state === 'cancelled' && <p className="migration-note">Частичные копии остаются в назначении. Новая задача требует пустого бакета.</p>}
  </article>;
}
