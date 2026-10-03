import React from 'react';
import { Select } from '../components/ui/Select';
import { Switch } from '../components/ui/Switch';
import { Loading } from '../components/ui/EmptyState';
import { MigrationProgress } from '../components/settings/MigrationProgress';
import { MigrationApiError, MigrationConnection, MigrationSource, migrationRequest, useStorageMigration } from '../hooks/useStorageMigration';

const EMPTY: MigrationConnection = { serviceUrl: '', bucketName: '', accessKey: '', secretKey: '', region: '', forcePathStyle: true, isR2: false };
export function validMigrationConnection(value: MigrationConnection) {
  try {
    const url = new URL(value.serviceUrl);
    return ['http:', 'https:'].includes(url.protocol) && !url.username && !url.password && !url.search && !url.hash
      && !!value.bucketName.trim() && !!value.accessKey.trim() && !!value.secretKey;
  } catch { return false; }
}

export default function MigrationTab({ active = true, onExpired }: { active?: boolean; onExpired: () => void }) {
  const status = useStorageMigration(active, onExpired);
  const [sources, setSources] = React.useState<MigrationSource[]>([]);
  const [sourceId, setSourceId] = React.useState('');
  const [target, setTarget] = React.useState<MigrationConnection>(EMPTY);
  const [validationId, setValidationId] = React.useState<string | null>(null);
  const [busy, setBusy] = React.useState(false);
  const [message, setMessage] = React.useState('');
  const [error, setError] = React.useState('');
  const expired = React.useRef(onExpired); expired.current = onExpired;
  const source = sources.find(x => x.id === sourceId);
  const running = status.jobs.some(x => ['queued', 'running', 'stopping'].includes(x.state)) || status.cutovers.length > 0;
  React.useEffect(() => {
    if (!active) return;
    const controller = new AbortController();
    void migrationRequest<MigrationSource[]>('/api/settings/migration/sources', undefined, controller.signal)
      .then(value => { if (!controller.signal.aborted) setSources(value); })
      .catch(e => { if (controller.signal.aborted) return; if (e instanceof MigrationApiError && e.status === 403) expired.current(); else setError(e instanceof Error ? e.message : 'Не удалось получить бакеты'); });
    return () => controller.abort();
  }, [active]);
  function invalidate() { setValidationId(null); setMessage(''); setError(''); }
  function edit<K extends keyof MigrationConnection>(key: K, value: MigrationConnection[K]) {
    invalidate(); setTarget(current => ({ ...current, [key]: value }));
  }
  async function check() {
    setBusy(true); setError(''); setMessage('');
    try {
      const result = await migrationRequest<{ validationId: string; message: string }>('/api/settings/migration/check', { sourceId, destination: target });
      setValidationId(result.validationId); setMessage(result.message);
    } catch (e) {
      setValidationId(null);
      if (e instanceof MigrationApiError && e.status === 403) onExpired();
      else setError(e instanceof Error ? e.message : 'Проверка не удалась');
    } finally { setBusy(false); }
  }
  async function start() {
    setBusy(true); setError('');
    try {
      await migrationRequest('/api/settings/migration/start', { validationId });
      setValidationId(null); setMessage(''); setTarget(current => ({ ...current, accessKey: '', secretKey: '' }));
      await status.refresh();
    } catch (e) {
      if (e instanceof MigrationApiError && e.status === 403) onExpired();
      else { setError(e instanceof Error ? e.message : 'Запуск не удался'); setValidationId(null); }
    } finally { setBusy(false); }
  }
  return <div className="migration-tab">
    <header className="migration-heading"><h2>Миграция</h2><p>Скопируйте бакет в другой S3 с теми же ключами, содержимым и метаданными. Исходные данные сохраняются.</p></header>
    <div className="migration-layout">
      <article className="migration-connection-card" aria-label="Исходный бакет">
        <div className="migration-card-title"><span className="migration-step">1</span><div><h3>Источник</h3><small>Настроенное хранилище BarkCloud</small></div></div>
        <label className="migration-field"><span>Исходный бакет</span><Select aria-label="Исходный бакет" value={sourceId} disabled={busy || running}
          options={[{ value: '', label: 'Выберите бакет' }, ...sources.map(x => ({ value: x.id, label: x.bucketName + ' · ' + x.serviceUrl }))]}
          onChange={value => { invalidate(); setSourceId(value); }} /></label>
        {source ? <div className="migration-source-details"><span className="migration-caption">Endpoint</span><code>{source.serviceUrl}</code>
          <span className="migration-caption">Связанные роли</span><div className="migration-roles">{source.roles.map(role => <span className="pill-info" key={role}>{role}</span>)}</div>
          <details><summary>Профили и версии ({source.profileIds.length})</summary><ul>{source.profileIds.map(id => <li key={id}><code>{id}</code></li>)}</ul></details>
        </div> : <p className="migration-note">В списке собраны все настроенные бакеты, включая прошлые версии и legacy-профили. Одинаковые адрес и bucket объединены.</p>}
        <div className="migration-source-note"><strong>Копирование без удаления</strong><p>Во время переноса сервис продолжает работать. Имена объектов и полные S3-ключи сохраняются, поэтому существующие файлы останутся связаны с прежними ID профилей.</p></div>
      </article>
      <div className="migration-arrow" aria-hidden="true"><svg viewBox="0 0 24 24"><path d="M 4 12 H 20 M 13 5 L 20 12 L 13 19" /></svg></div>
      <article className="migration-connection-card" aria-label="Целевое хранилище">
        <div className="migration-card-title"><span className="migration-step">2</span><div><h3>Назначение</h3><small>Отдельный пустой бакет на другом S3</small></div></div>
        <fieldset className="migration-fields" disabled={busy || running}><legend className="sr-only">Подключение назначения</legend>
          <label className="migration-field"><span>Endpoint</span><input aria-label="Endpoint назначения" type="url" value={target.serviceUrl} placeholder="https://s3.example.com" onChange={e => edit('serviceUrl', e.target.value)} autoComplete="off" spellCheck={false} /></label>
          <label className="migration-field"><span>Bucket</span><input aria-label="Bucket назначения" value={target.bucketName} onChange={e => edit('bucketName', e.target.value)} autoComplete="off" spellCheck={false} /></label>
          <label className="migration-field"><span>Access key</span><input aria-label="Access key назначения" type="password" value={target.accessKey} onChange={e => edit('accessKey', e.target.value)} autoComplete="off" spellCheck={false} /></label>
          <label className="migration-field"><span>Secret key</span><input aria-label="Secret key назначения" type="password" value={target.secretKey} onChange={e => edit('secretKey', e.target.value)} autoComplete="new-password" spellCheck={false} /></label>
          <label className="migration-field"><span>Регион</span><input aria-label="Регион назначения" value={target.isR2 ? 'auto' : target.region} disabled={target.isR2} placeholder="Автоматически" onChange={e => edit('region', e.target.value)} autoComplete="off" /></label>
          <label className="migration-field"><span>Адресация бакета</span><Select aria-label="Адресация бакета назначения" value={target.forcePathStyle ? 'path' : 'virtual'} disabled={busy || running}
            options={[{ value: 'path', label: 'Path style · endpoint/bucket' }, { value: 'virtual', label: 'Virtual host · bucket.endpoint' }]} onChange={v => edit('forcePathStyle', v === 'path')} /></label>
          <div className="migration-r2"><span>Cloudflare R2</span><Switch label="Cloudflare R2 назначения" on={target.isR2} disabled={busy || running} onChange={v => edit('isR2', v)} /></div>
        </fieldset>
        <p className="migration-note">Создайте бакет заранее. Проверка записывает тестовый объект, читает его и удаляет. Ключи доступа остаются только в памяти текущей сессии и сервера.</p>
      </article>
    </div>
    <div className="migration-check-actions">
      {validationId ? <button className="btn primary" disabled={busy || running} onClick={() => void start()}>{busy ? 'Запуск…' : 'Перенести'}</button>
        : <button className="btn primary" disabled={busy || running || !source || !validMigrationConnection(target)} onClick={() => void check()}>{busy ? 'Проверяем запись и удаление…' : 'Проверить доступ и запись'}</button>}
      {message && <p role="status">{message}</p>}
      {error && <p className="migration-error" role="alert">{error}</p>}
    </div>
    {status.error && <p className="migration-error" role="alert">{status.error}</p>}
    {status.cutovers.some(cutover => !status.jobs.some(job => job.id === cutover.id)) && <div className="migration-progress-card"><strong>В Files сохранено незавершённое переключение.</strong><p>Откройте S3-профили в настройках сервера, чтобы безопасно отменить его или повторить перезапуск Files.</p><a className="btn primary" href="/settings#server-settings">Открыть S3-профили</a></div>}
    {status.loading && <Loading label="Получаем состояние миграции…" />}
    {status.jobs.map(job => <MigrationProgress key={job.id} job={job} onRefresh={status.refresh} onExpired={onExpired} applyLink />)}
    <p className="migration-note">Переносятся текущие объекты. История версий, политики, ACL и lifecycle бакета не копируются. Задача продолжится при закрытии страницы, но не восстанавливается после перезапуска Web.</p>
  </div>;
}
