import React from 'react';
import { Loading } from '../components/ui/EmptyState';

interface ServerSetting {
  serviceId: number; section: string; key: string; value: string;
  isSensitive: boolean; hasValue: boolean; isReadOnly: boolean; valueKind: string;
  restartTargets: string[]; editedAt: string | null; editedBy: string; editedFrom: string;
}

interface SettingRevision {
  id: number; previousValue: string | null; newValue: string | null; isSensitive: boolean;
  previousHasValue: boolean; newHasValue: boolean; changedAt: string | null;
  changedBy: string; changedFrom: string; changeKind: string; sourceRevisionId: number | null;
}

interface StorageProfile {
  profileId: string; role: string; version: number; serviceUrl: string;
  hasAccessKey: boolean; hasSecretKey: boolean; bucketName: string; isR2: boolean;
  isActive: boolean; isLegacy: boolean; editedAt: string | null; editedBy: string; editedFrom: string;
}

interface StorageRevision {
  id: number; profileId: string; changedAt: string | null; changedBy: string;
  changedFrom: string; changeKind: string;
}

interface ServerSettings {
  settings: ServerSetting[]; reservedNames: string[];
  storageProfiles: StorageProfile[]; storageRevisions: StorageRevision[];
}

interface MutationResult { success: boolean; message: string; restartTargets: string[] }
interface StorageDraft { serviceUrl: string; accessKey: string; secretKey: string; bucketName: string; isR2: boolean }

const SERVICE_LABELS: Record<number, string> = {
  0: 'GlobalSettings', 1: 'IdentitySettings', 2: 'UsersSettings', 3: 'NotificationSettings',
  5: 'FilesSettings', 6: 'WebSettings', 7: 'TorrentSettings',
};
const STORAGE_ROLES = ['universal', 'avatars', 'images', 'videos', 'audio', 'documents', 'other', 'previews'];
const isTokenSetting = (setting: ServerSetting) => setting.key.toLowerCase() === 'token';

async function request<T>(path: string, method = 'GET', body?: unknown): Promise<{ ok: boolean; data: T | null }> {
  const response = await fetch(path, {
    method, credentials: 'same-origin',
    headers: body === undefined ? undefined : { 'Content-Type': 'application/json' },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  let data: T | null = null;
  try { data = await response.json() as T; } catch { /* empty response */ }
  return { ok: response.ok, data };
}

const settingId = (setting: ServerSetting) => `${setting.serviceId}:${setting.section}:${setting.key}`;

function countLabel(count: number, one: string, few: string, many: string) {
  const lastTwo = count % 100;
  const last = count % 10;
  return `${count} ${last === 1 && lastTwo !== 11 ? one : last >= 2 && last <= 4 && (lastTwo < 10 || lastTwo >= 20) ? few : many}`;
}

function displayValue(revision: SettingRevision, side: 'previous' | 'new') {
  if (!revision.isSensitive) return side === 'previous' ? revision.previousValue : revision.newValue;
  const hasValue = side === 'previous' ? revision.previousHasValue : revision.newHasValue;
  return hasValue ? '••••••••' : '(пусто)';
}

function SettingEditor({ setting, onSaved }: { setting: ServerSetting; onSaved: (targets: string[]) => void }) {
  const [value, setValue] = React.useState(setting.isSensitive ? '' : setting.value);
  const [busy, setBusy] = React.useState(false);
  const [message, setMessage] = React.useState('');
  const [history, setHistory] = React.useState<SettingRevision[] | null>(null);
  React.useEffect(() => setValue(setting.isSensitive ? '' : setting.value), [setting]);

  async function save() {
    const before = setting.isSensitive ? (setting.hasValue ? '••••••••' : '(пусто)') : (setting.value || '(пусто)');
    const after = setting.isSensitive ? 'новое секретное значение' : (value || '(пусто)');
    if (!window.confirm(`${setting.section}:${setting.key}\n\nБыло: ${before}\nСтанет: ${after}`)) return;
    setBusy(true);
    const response = await request<MutationResult>('/api/settings/server/value', 'POST', {
      serviceId: setting.serviceId, section: setting.section, key: setting.key, value,
    });
    setBusy(false);
    setMessage(response.data?.message || (response.ok ? 'Сохранено' : 'Ошибка'));
    if (response.ok) onSaved(response.data?.restartTargets || []);
  }

  async function toggleHistory() {
    if (history !== null) { setHistory(null); return; }
    const query = new URLSearchParams({
      serviceId: String(setting.serviceId), section: setting.section, key: setting.key, count: '50',
    });
    const response = await request<SettingRevision[]>(`/api/settings/server/history?${query}`);
    setHistory(response.data || []);
  }

  async function rollback(revision: SettingRevision) {
    if (!window.confirm(`Откатить ${setting.section}:${setting.key} к предыдущему значению этой ревизии?`)) return;
    const response = await request<MutationResult>('/api/settings/server/rollback', 'POST', {
      revisionId: revision.id, serviceId: setting.serviceId, section: setting.section, key: setting.key,
    });
    setMessage(response.data?.message || (response.ok ? 'Выполнено' : 'Ошибка'));
    if (response.ok) onSaved(response.data?.restartTargets || []);
  }

  const inputType = setting.valueKind === 'password' ? 'password'
    : setting.valueKind === 'integer' ? 'number'
      : setting.valueKind === 'url' ? 'url' : 'text';
  return (
    <div className="server-setting-row" data-setting-id={settingId(setting)}>
      <div className="server-setting-title">
        <strong>{setting.section}{setting.key ? `:${setting.key}` : ''}</strong>
        <small>{setting.isReadOnly
          ? setting.editedFrom === 'computed' ? 'Источник: вычисляемое значение · только чтение' : 'Источник: .env / compose · только чтение'
          : `Источник: БД · ${setting.editedFrom || 'seed'}`}</small>
      </div>
      <div className="server-setting-control">
        {setting.valueKind === 'boolean' ? (
          <select value={value} disabled={setting.isReadOnly || busy} onChange={(event) => setValue(event.target.value)}>
            <option value="true">Включено</option><option value="false">Выключено</option>
          </select>
        ) : (
          <input type={inputType} value={value} disabled={setting.isReadOnly || busy}
            placeholder={!setting.isReadOnly && setting.isSensitive && setting.hasValue ? 'Значение задано — введите новое для замены' : ''}
            onChange={(event) => setValue(event.target.value)} />
        )}
        <button className="btn primary" disabled={setting.isReadOnly || busy || (setting.isSensitive && !value)} onClick={save}>Сохранить</button>
        <button className="btn" onClick={toggleHistory}>{history === null ? 'История' : 'Скрыть'}</button>
      </div>
      {message && <div className="server-setting-message">{message}</div>}
      {history !== null && <div className="server-history">
        {history.length === 0 ? <span>История пуста</span> : history.map((revision) => <div key={revision.id} className="server-history-row">
          <span>{revision.changeKind} · {revision.changedAt ? new Date(revision.changedAt).toLocaleString('ru-RU') : '—'} · {revision.changedBy}</span>
          <code>{displayValue(revision, 'previous')} → {displayValue(revision, 'new')}</code>
          {!setting.isReadOnly && <button className="btn" onClick={() => rollback(revision)}>Откатить</button>}
        </div>)}
      </div>}
    </div>
  );
}

function StorageCard({ role, profiles, revisions, onChanged }: {
  role: string; profiles: StorageProfile[]; revisions: StorageRevision[]; onChanged: (targets: string[]) => void;
}) {
  const ordered = profiles.filter((profile) => profile.role === role).sort((a, b) => b.version - a.version);
  const active = ordered.find((profile) => profile.isActive);
  const current = active || ordered[0];
  const [selectedProfileId, setSelectedProfileId] = React.useState<string | null>(null);
  const selected = ordered.find((profile) => profile.profileId === selectedProfileId) || current;
  const legacy = selected?.isLegacy || role.endsWith('-old');
  const [draft, setDraft] = React.useState<StorageDraft>({ serviceUrl: '', accessKey: '', secretKey: '', bucketName: '', isR2: false });
  const [message, setMessage] = React.useState('');
  React.useEffect(() => setDraft({ serviceUrl: selected?.serviceUrl || '', accessKey: '', secretKey: '', bucketName: selected?.bucketName || '', isR2: selected?.isR2 || false }), [selected?.profileId]);

  async function save() {
    const diff = selected ? [
      selected.serviceUrl !== draft.serviceUrl ? `Endpoint: ${selected.serviceUrl} → ${draft.serviceUrl || '(пусто)'}` : '',
      selected.bucketName !== draft.bucketName ? `Bucket: ${selected.bucketName} → ${draft.bucketName || '(пусто)'}` : '',
      selected.isR2 !== draft.isR2 ? `R2: ${selected.isR2 ? 'да' : 'нет'} → ${draft.isR2 ? 'да' : 'нет'}` : '',
      draft.accessKey ? 'Access key: заменить' : 'Access key: оставить текущий',
      draft.secretKey ? 'Secret key: заменить' : 'Secret key: оставить текущий',
    ].filter(Boolean) : [
      `Endpoint: ${draft.serviceUrl || '(пусто)'}`,
      `Bucket: ${draft.bucketName || '(пусто)'}`,
      `R2: ${draft.isR2 ? 'да' : 'нет'}`,
      `Access key: ${draft.accessKey ? 'задан' : '(пусто)'}`,
      `Secret key: ${draft.secretKey ? 'задан' : '(пусто)'}`,
    ];
    const warning = legacy ? '\n\nЭто legacy-профиль, на него ссылаются существующие файлы.' : '';
    if (!window.confirm(`${selected ? 'Изменить' : 'Создать'} S3-профиль роли «${role}»?\n\n${diff.join('\n')}${warning}`)) return;
    const response = await request<MutationResult>('/api/settings/server/storage/profile', 'POST', {
      role, ...draft, isLegacy: legacy, profileId: selected?.profileId || null, confirmLegacyMutation: legacy,
    });
    setMessage(response.data?.message || (response.ok ? 'Сохранено' : 'Ошибка'));
    if (response.ok) {
      setDraft((currentDraft) => ({ ...currentDraft, accessKey: '', secretKey: '' }));
      setSelectedProfileId(null);
      onChanged(response.data?.restartTargets || ['files']);
    }
  }

  async function activate(profileId: string) {
    if (!window.confirm(`Активировать ${profileId} для новых файлов?`)) return;
    const response = await request<MutationResult>('/api/settings/server/storage/activate', 'POST', { profileId });
    setMessage(response.data?.message || '');
    if (response.ok) {
      setSelectedProfileId(null);
      onChanged(response.data?.restartTargets || ['files']);
    }
  }

  async function disable() {
    if (!window.confirm(`Отключить специализированную роль «${role}» для новых файлов? Старые версии останутся доступными.`)) return;
    const response = await request<MutationResult>('/api/settings/server/storage/disable', 'POST', { role });
    setMessage(response.data?.message || '');
    if (response.ok) onChanged(response.data?.restartTargets || ['files']);
  }

  const statusLabel = selected?.isActive ? 'Активен' : legacy ? 'Legacy' : selected ? 'Прошлая версия' : 'Fallback universal';
  return <article className={'storage-profile-card' + (legacy ? ' legacy' : '')}>
    <div className="storage-profile-head">
      <div className="storage-profile-identity"><strong>{role}</strong><small>{selected?.profileId || 'Не настроен'}</small></div>
      <div className="storage-profile-status">
        <span className={'pill-info ' + (selected?.isActive ? 'ok' : 'warn')}>{statusLabel}</span>
        {selected?.isActive && <small>Для новых файлов</small>}
      </div>
    </div>
    <div className="storage-profile-grid">
      <label><span>Endpoint</span><input type="url" value={draft.serviceUrl} onChange={(event) => setDraft({ ...draft, serviceUrl: event.target.value })} /></label>
      <label><span>Bucket</span><input type="text" value={draft.bucketName} onChange={(event) => setDraft({ ...draft, bucketName: event.target.value })} /></label>
      <label><span>Access key</span><input type="text" value={draft.accessKey} placeholder={selected?.hasAccessKey ? 'Задан' : ''} onChange={(event) => setDraft({ ...draft, accessKey: event.target.value })} /></label>
      <label><span>Secret key</span><input type="password" value={draft.secretKey} placeholder={selected?.hasSecretKey ? 'Оставьте пустым, чтобы не менять' : ''} onChange={(event) => setDraft({ ...draft, secretKey: event.target.value })} /></label>
    </div>
    <div className="storage-profile-footer">
      <label className="server-check"><input type="checkbox" checked={draft.isR2} onChange={(event) => setDraft({ ...draft, isR2: event.target.checked })} /><span>Cloudflare R2</span></label>
      <div className="storage-profile-actions"><button type="button" className="btn primary" onClick={save}>Сохранить</button>
        {!legacy && role !== 'universal' && active && <button type="button" className="btn" onClick={disable}>Отключить роль</button>}</div>
    </div>
    {message && <div className="server-setting-message" role="status">{message}</div>}
    {ordered.length > 0 && <details><summary>Версии и история</summary>
      {ordered.map((profile) => <div className="storage-version-row" key={profile.profileId}>
        <span>{profile.profileId} · {profile.serviceUrl}/{profile.bucketName}</span>
        <span className="storage-version-actions">
          <button type="button" className="btn" onClick={() => setSelectedProfileId(profile.profileId)}>Редактировать</button>
          {!profile.isLegacy && !profile.isActive && <button type="button" className="btn" onClick={() => activate(profile.profileId)}>Активировать</button>}
        </span>
      </div>)}
      {revisions.filter((revision) => ordered.some((profile) => profile.profileId === revision.profileId)).map((revision) =>
        <div className="storage-revision-row" key={revision.id}>{revision.changeKind} · {revision.profileId} · {revision.changedAt ? new Date(revision.changedAt).toLocaleString('ru-RU') : '—'}</div>)}
    </details>}
    </article>;
  }

export default function ServerSettingsTab() {
  const [data, setData] = React.useState<ServerSettings | null>(null);
  const [error, setError] = React.useState('');
  const [search, setSearch] = React.useState('');
  const [restartTargets, setRestartTargets] = React.useState<string[]>([]);
  const [newReserved, setNewReserved] = React.useState('');
  const load = React.useCallback(async () => {
    const response = await request<ServerSettings>('/api/settings/server');
    if (!response.ok || !response.data) { setError('Не удалось загрузить настройки сервера'); return; }
    setData(response.data); setError('');
  }, []);
  React.useEffect(() => { void load(); }, [load]);
  function changed(targets: string[]) { setRestartTargets((current) => Array.from(new Set([...current, ...targets]))); void load(); }
  async function addReserved() {
    if (!newReserved.trim()) return;
    const response = await request<MutationResult>('/api/settings/server/reserved/add', 'POST', { name: newReserved });
    if (response.ok) { setNewReserved(''); changed(response.data?.restartTargets || ['users']); }
  }
  async function deleteReserved(name: string) {
    if (!window.confirm(`Удалить зарезервированное имя «${name}»?`)) return;
    const response = await request<MutationResult>('/api/settings/server/reserved/delete', 'POST', { name });
    if (response.ok) changed(response.data?.restartTargets || ['users']);
  }
  async function renameReserved(oldName: string) {
    const newName = window.prompt('Новое зарезервированное имя', oldName);
    if (!newName || newName === oldName) return;
    const response = await request<MutationResult>('/api/settings/server/reserved/update', 'POST', { oldName, newName });
    if (response.ok) changed(response.data?.restartTargets || ['users']);
  }
  if (error) return <div className="sys-banner err">{error}</div>;
  if (!data) return <Loading label="Загрузка настроек сервера…" />;
  const query = search.trim().toLowerCase();
  const filtered = data.settings
    .filter((setting) => !isTokenSetting(setting))
    .filter((setting) => !query || `${setting.section}:${setting.key}`.toLowerCase().includes(query));
  const serviceIds = Array.from(new Set(filtered.map((setting) => setting.serviceId)));
  const legacyRoles = Array.from(new Set(data.storageProfiles.filter((profile) => profile.isLegacy).map((profile) => profile.role)));
  return <div className="server-settings-tab">
    {restartTargets.length > 0 && <div className="sys-banner warn">Изменения сохранены. Требуется ручной перезапуск: {restartTargets.join(', ')}. Перейдите в «Обслуживание».</div>}
    <header className="server-settings-intro">
      <div>
        <span className="server-settings-kicker">Администрирование</span>
        <h2>Конфигурация сервера</h2>
        <p>Параметры сервисов, зарезервированные имена и маршрутизация файлов.</p>
      </div>
      <div className="server-settings-summary" aria-label="Сводка конфигурации">
        <div><strong>{serviceIds.length}</strong><span>{countLabel(serviceIds.length, 'группа сервиса', 'группы сервисов', 'групп сервисов')}</span></div>
        <div><strong>{data.storageProfiles.length}</strong><span>{countLabel(data.storageProfiles.length, 'профиль S3', 'профиля S3', 'профилей S3')}</span></div>
      </div>
    </header>
    <section className="set-card server-settings-toolbar">
      <div className="server-section-head">
        <div><div className="ttl">Параметры сервисов</div><div className="sub">Секреты не загружаются в браузер</div></div>
        <span className="server-section-count">{countLabel(filtered.length, 'параметр', 'параметра', 'параметров')}</span>
      </div>
      <label className="server-search-field"><span>Поиск по ключу</span><input className="server-settings-search" placeholder="Например, JwtSettings:Issuer" value={search} onChange={(event) => setSearch(event.target.value)} /></label>
      {filtered.length === 0 && <div className="server-empty">{query ? 'По этому запросу параметры не найдены.' : 'Параметры сервисов не найдены.'}</div>}
    </section>
    {serviceIds.map((serviceId, index) => {
      const serviceSettings = filtered.filter((setting) => setting.serviceId === serviceId);
      return <section className="set-card server-service-card" key={serviceId}>
        <div className="set-card-head server-section-head">
          <div className="server-service-title"><span className="server-service-number">{String(index + 1).padStart(2, '0')}</span><div><div className="ttl">{SERVICE_LABELS[serviceId] || `Service ${serviceId}`}</div><div className="sub">{countLabel(serviceSettings.length, 'параметр', 'параметра', 'параметров')}</div></div></div>
        </div>
        <div className="set-card-body server-settings-list">{serviceSettings.map((setting) =>
          <SettingEditor key={settingId(setting)} setting={setting} onSaved={changed} />)}</div>
      </section>;
    })}
    <section className="set-card server-reserved-card">
      <div className="set-card-head server-section-head"><div><div className="ttl">Зарезервированные имена</div><div className="sub">Хранятся нормализованно в lowercase</div></div><span className="server-section-count">{countLabel(data.reservedNames.length, 'имя', 'имени', 'имён')}</span></div>
      <div className="set-card-body server-reserved-body">
        <div className="reserved-add"><label><span>Добавить имя</span><input type="text" aria-label="Новое зарезервированное имя" value={newReserved} onChange={(event) => setNewReserved(event.target.value)} placeholder="Например, admin" /></label><button type="button" className="btn primary" onClick={addReserved}>Добавить</button></div>
        {data.reservedNames.length > 0 ? <div className="reserved-list">{data.reservedNames.map((name) => <span key={name}><b>{name}</b><button type="button" aria-label={`Переименовать ${name}`} title="Переименовать" onClick={() => renameReserved(name)}>✎</button><button type="button" aria-label={`Удалить ${name}`} title="Удалить" onClick={() => deleteReserved(name)}>×</button></span>)}</div>
          : <div className="server-empty">Зарезервированных имён пока нет.</div>}
      </div>
    </section>
    <section className="set-card server-storage-section">
      <div className="set-card-head server-section-head"><div><div className="ttl">S3-профили</div><div className="sub">Роль определяет, где хранятся оригиналы. Превью используют роль previews.</div></div><span className="server-section-count">{countLabel(data.storageProfiles.length, 'профиль сохранён', 'профиля сохранено', 'профилей сохранено')}</span></div>
      <div className="set-card-body storage-profile-list">{[...STORAGE_ROLES, ...legacyRoles].map((role) =>
        <StorageCard key={role} role={role} profiles={data.storageProfiles} revisions={data.storageRevisions} onChanged={changed} />)}</div>
    </section>
  </div>;
}
