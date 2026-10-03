import React from 'react';
import { ConfirmModal } from '../ui/ConfirmModal';
import { MigrationProgress } from './MigrationProgress';
import { MigrationApiError, MigrationJob, migrationRequest, useStorageMigration } from '../../hooks/useStorageMigration';

export function MigrationApplyPanel({ active, onExpired, onApplied }: { active: boolean; onExpired: () => void; onApplied: () => Promise<void> }) {
  const status = useStorageMigration(active, onExpired);
  const [confirm, setConfirm] = React.useState<MigrationJob | null>(null);
  const [message, setMessage] = React.useState('');
  const [busy, setBusy] = React.useState(false);
  const notified = React.useRef(new Set<string>());
  const applied = React.useRef(onApplied); applied.current = onApplied;
  React.useEffect(() => {
    const completed = status.jobs.filter(job => job.state === 'completed' && !notified.current.has(job.id));
    if (completed.length) { completed.forEach(job => notified.current.add(job.id)); void applied.current(); }
  }, [status.jobs]);
  async function mutate(path: string, body: unknown = {}) {
    setBusy(true); setMessage('');
    try { await migrationRequest(path, body); setConfirm(null); await status.refresh(); }
    catch (e) { if (e instanceof MigrationApiError && e.status === 403) onExpired(); else setMessage(e instanceof Error ? e.message : 'Переключение не удалось'); }
    finally { setBusy(false); }
  }
  const jobs = status.jobs.filter(job => job.canApply || job.phase === 'draining' || job.phase.startsWith('final-') || ['applying', 'restarting', 'completed'].includes(job.phase));
  const orphan = status.cutovers.filter(cutover => !status.jobs.some(job => job.id === cutover.id));
  return <div className="migration-apply-panel">
    {jobs.map(job => <div key={job.id}><MigrationProgress job={job} onRefresh={status.refresh} onExpired={onExpired} />
      {job.canApply && <button className="btn primary" disabled={busy} onClick={() => setConfirm(job)}>Применить миграцию</button>}</div>)}
    {orphan.map(cutover => <article key={cutover.id} className="migration-progress-card"><strong>Незавершённое переключение: {cutover.source.bucketName}</strong>
      <p>{cutover.source.serviceUrl} → {cutover.destination.serviceUrl} / {cutover.destination.bucketName}</p>
      <p>Барьер сохранён в Files. Запись остаётся заблокированной до безопасной отмены или подтверждения новых подключений.</p>
      {cutover.activeUploads > 0 && <p>Активных загрузок и обработок: {cutover.activeUploads}</p>}
      <div className="migration-actions">
        {cutover.canRecover && <button className="btn primary" disabled={busy} onClick={() => void mutate('/api/settings/migration/cutovers/' + cutover.id + '/recover')}>Повторить перезапуск Files</button>}
        {cutover.canCancel && <button className="btn text" disabled={busy} onClick={() => void mutate('/api/settings/migration/cutovers/' + cutover.id + '/cancel')}>Отменить переключение</button>}
      </div></article>)}
    {(message || status.error) && <p role="alert" className="migration-error">{message || status.error}</p>}
    {confirm && <ConfirmModal title="Применить миграцию?" confirmLabel="Применить миграцию" onClose={() => setConfirm(null)}
      message={<div className="settings-confirm-lines"><p>{confirm.source.serviceUrl} / {confirm.source.bucketName}</p>
        <p>Назначение: {confirm.destination.serviceUrl} / {confirm.destination.bucketName}</p>
        <p>Регион: {confirm.destination.region || 'автоматически'} · {confirm.destination.forcePathStyle ? 'Path style' : 'Virtual host'}{confirm.destination.isR2 ? ' · Cloudflare R2' : ''}</p>
        <p>Будут заменены подключения всех профилей: {confirm.source.profileIds.join(', ')}. Их ID, версии, роли и квоты сохранятся.</p>
        <p>Новые записи временно приостановятся. После завершения активных загрузок выполним финальную досинхронизацию, сохраним подключения и автоматически перезапустим Files. Чтение доступно до перезапуска.</p>
      </div>} onConfirm={() => mutate('/api/settings/server/storage/migration/apply', { jobId: confirm.id })} />}
  </div>;
}
