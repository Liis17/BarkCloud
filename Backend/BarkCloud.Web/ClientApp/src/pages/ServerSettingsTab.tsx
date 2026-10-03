import React from 'react';
import { Loading } from '../components/ui/EmptyState';
import { ConfirmModal } from '../components/ui/ConfirmModal';
import { Icon } from '../components/Icon';
import { useApiResource } from '../hooks/useApiResource';
import { plural } from '../lib/format';

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
  isActive: boolean; isLegacy: boolean; quotaBytes?: string;
  editedAt: string | null; editedBy: string; editedFrom: string;
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
type QuotaUnit = 'gb' | 'tb' | 'pb';
interface StorageDraft {
  serviceUrl: string; accessKey: string; secretKey: string; bucketName: string; isR2: boolean;
  quotaValue: string; quotaUnit: QuotaUnit;
}
interface Confirmation { title: string; lines: string[]; run: () => Promise<void> }
type Changed = (targets: string[]) => Promise<void>;

const SERVICE_LABELS: Record<number, string> = {
  0: 'Общие', 1: 'Авторизация', 2: 'Пользователи', 3: 'Уведомления',
  5: 'Файлы', 6: 'Веб-клиент', 7: 'Торренты',
};
const SERVICE_NAMES: Record<number, string> = {
  0: 'GlobalSettings', 1: 'IdentitySettings', 2: 'UsersSettings', 3: 'NotificationSettings',
  5: 'FilesSettings', 6: 'WebSettings', 7: 'TorrentSettings',
};
const STORAGE_ROLES: Record<string, string> = {
  universal: 'Универсальное', avatars: 'Аватары', images: 'Изображения', videos: 'Видео',
  audio: 'Аудио', documents: 'Документы', other: 'Прочие файлы', previews: 'Превью',
};
const STORAGE_FIELD_NAMES = ['Endpoint', 'Bucket', 'Access key Credentials', 'Secret key Credentials', 'Квота S3 бакета', 'Cloudflare R2'];
const STORAGE_FIELDS = 'S3 ' + STORAGE_FIELD_NAMES.join(' ');
const QUOTA_UNIT_BYTES: Record<QuotaUnit, bigint> = { gb: 1024n ** 3n, tb: 1024n ** 4n, pb: 1024n ** 5n };
const settingId = (setting: ServerSetting) => setting.serviceId + ':' + setting.section + ':' + setting.key;
const settingName = (setting: ServerSetting) => setting.section + (setting.key ? ':' + setting.key : '');
const matches = (value: string, query: string) => !query || value.toLocaleLowerCase('ru-RU').includes(query);
const EMPTY_DRAFT: StorageDraft = { serviceUrl: '', accessKey: '', secretKey: '', bucketName: '', isR2: false, quotaValue: '0', quotaUnit: 'gb' };

function quotaInputFromBytes(value = '0'): { value: string; unit: QuotaUnit } {
  try {
    const bytes = BigInt(value);
    for (const unit of ['pb', 'tb', 'gb'] as const)
      if (bytes !== 0n && bytes % QUOTA_UNIT_BYTES[unit] === 0n)
        return { value: (bytes / QUOTA_UNIT_BYTES[unit]).toString(), unit };
  } catch { /* invalid data */ }
  return { value: '0', unit: 'gb' };
}
function profileDraft(profile?: StorageProfile): StorageDraft {
  const quota = quotaInputFromBytes(profile?.quotaBytes);
  return { ...EMPTY_DRAFT, serviceUrl: profile?.serviceUrl || '', bucketName: profile?.bucketName || '',
    isR2: profile?.isR2 || false, quotaValue: quota.value, quotaUnit: quota.unit };
}
function quotaLabel(profile?: StorageProfile) {
  if (!profile?.quotaBytes || profile.quotaBytes === '0') return 'Безлимит';
  const quota = quotaInputFromBytes(profile.quotaBytes);
  if (BigInt(quota.value) * QUOTA_UNIT_BYTES[quota.unit] !== BigInt(profile.quotaBytes))
    return BigInt(profile.quotaBytes).toLocaleString('ru-RU') + ' Б';
  return quota.value + ' ' + ({ gb: 'ГБ', tb: 'ТБ', pb: 'ПБ' }[quota.unit]);
}
function Highlight({ value, query }: { value: string; query: string }) {
  const index = query ? value.toLocaleLowerCase('ru-RU').indexOf(query) : -1;
  if (index < 0) return <>{value}</>;
  return <>{value.slice(0, index)}<mark>{value.slice(index, index + query.length)}</mark>{value.slice(index + query.length)}</>;
}
function Confirm({ confirmation, onClose }: { confirmation: Confirmation; onClose: () => void }) {
  return <ConfirmModal title={confirmation.title} confirmLabel="Подтвердить" onClose={onClose}
    message={<div className="settings-confirm-lines">{confirmation.lines.map((line, index) => <p key={index}>{line}</p>)}</div>}
    onConfirm={confirmation.run} />;
}
async function request<T>(path: string, body?: unknown): Promise<{ ok: boolean; status: number; data: T | null }> {
  try {
    const response = await fetch(path, {
      method: body === undefined ? 'GET' : 'POST', credentials: 'same-origin',
      headers: body === undefined ? undefined : { 'Content-Type': 'application/json' },
      body: body === undefined ? undefined : JSON.stringify(body),
    });
    if (response.status === 401) window.location.href = '/login';
    let data: T | null = null;
    try { data = await response.json() as T; } catch { /* empty response */ }
    return { ok: response.ok, status: response.status, data };
  } catch { return { ok: false, status: 0, data: null }; }
}
function revisionValue(revision: SettingRevision, side: 'previous' | 'new') {
  if (!revision.isSensitive) return (side === 'previous' ? revision.previousValue : revision.newValue) || '(пусто)';
  return (side === 'previous' ? revision.previousHasValue : revision.newHasValue) ? '••••••••' : '(пусто)';
}

function SettingEditor({ setting, query, onSaved, onDirty, onExpired }: {
  setting: ServerSetting; query: string; onSaved: Changed; onDirty: (id: string, dirty: boolean) => void; onExpired: () => void;
}) {
  const initial = setting.isSensitive ? '' : setting.value;
  const base = React.useRef(initial);
  const [value, setValue] = React.useState(initial);
  const [busy, setBusy] = React.useState(false);
  const [message, setMessage] = React.useState('');
  const [history, setHistory] = React.useState<SettingRevision[] | null>(null);
  const [historyLoading, setHistoryLoading] = React.useState(false);
  const [confirmation, setConfirmation] = React.useState<Confirmation | null>(null);
  const id = settingId(setting);
  const dirty = !setting.isReadOnly && value !== initial;
  React.useEffect(() => {
    setValue((current) => current === base.current ? initial : current);
    base.current = initial;
  }, [initial]);
  React.useEffect(() => { onDirty(id, dirty); }, [id, dirty, onDirty]);

  async function mutate(path: string, body: unknown, resetValue = false) {
    setBusy(true);
    try {
      const response = await request<MutationResult>(path, body);
      if (response.status === 403) { onExpired(); return; }
      setMessage(response.data?.message || (response.ok ? 'Сохранено' : 'Не удалось сохранить'));
      setConfirmation(null);
      if (response.ok) {
        if (resetValue && setting.isSensitive) setValue('');
        await onSaved(response.data?.restartTargets || []);
      }
    } finally { setBusy(false); }
  }
  function save() {
    const before = setting.isSensitive ? setting.hasValue ? '••••••••' : '(пусто)' : setting.value || '(пусто)';
    setConfirmation({ title: 'Сохранить параметр?', lines: [settingName(setting), 'Было: ' + before,
      'Станет: ' + (setting.isSensitive ? 'новое секретное значение' : value || '(пусто)')],
      run: () => mutate('/api/settings/server/value', { serviceId: setting.serviceId, section: setting.section, key: setting.key, value }, true) });
  }
  async function toggleHistory() {
    if (history !== null) { setHistory(null); return; }
    setHistoryLoading(true);
    const params = new URLSearchParams({ serviceId: String(setting.serviceId), section: setting.section, key: setting.key, count: '50' });
    const response = await request<SettingRevision[]>('/api/settings/server/history?' + params);
    setHistoryLoading(false);
    if (response.status === 403) { onExpired(); return; }
    if (response.ok) setHistory(response.data || []);
    else setMessage('Не удалось загрузить историю');
  }
  const type = setting.valueKind === 'password' ? 'password' : setting.valueKind === 'integer' ? 'number' : setting.valueKind === 'url' ? 'url' : 'text';
  return <div className={'server-setting-row' + (dirty ? ' dirty' : '')} data-setting-id={id}>
    <div className="server-setting-title"><strong><Highlight value={settingName(setting)} query={query} /></strong>
      <small>{setting.isReadOnly ? setting.editedFrom === 'computed' ? 'Вычисляемое · только чтение' : '.env / compose · только чтение' : 'БД · ' + (setting.editedFrom || 'seed')}
        {!setting.isReadOnly && setting.restartTargets.length > 0 && <span title="После сохранения нужен перезапуск"> · ↻ {setting.restartTargets.join(', ')}</span>}</small>
    </div>
    <div className="server-setting-control">
      {setting.valueKind === 'boolean' ? <select aria-label={settingName(setting)} value={value} disabled={setting.isReadOnly || busy} onChange={(event) => setValue(event.target.value)}>
        <option value="true">Включено</option><option value="false">Выключено</option>
      </select> : <input aria-label={settingName(setting)} type={type} value={value} disabled={setting.isReadOnly || busy}
        placeholder={!setting.isReadOnly && setting.isSensitive && setting.hasValue ? 'Задано — новое значение для замены' : ''}
        onChange={(event) => setValue(event.target.value)} />}
      {!setting.isReadOnly && <button className="btn primary" disabled={!dirty || busy} onClick={save}>{busy ? 'Сохраняем…' : 'Сохранить'}</button>}
      <button className="icon-btn" aria-label={'История ' + settingName(setting)} title="История изменений" onClick={() => void toggleHistory()} disabled={historyLoading}><Icon.clock size={18} /></button>
    </div>
    {message && <div className="server-setting-message" role="status">{message}</div>}
    {historyLoading && <small>Загрузка истории…</small>}
    {history !== null && <div className="server-history">
      {history.length === 0 ? <span>История пуста</span> : history.map((revision) => <div key={revision.id} className="server-history-row">
        <span>{revision.changedAt ? new Date(revision.changedAt).toLocaleString('ru-RU') : '—'} · {revision.changedBy} · {revision.changeKind}</span>
        <code>{revisionValue(revision, 'previous')} → {revisionValue(revision, 'new')}</code>
        {!setting.isReadOnly && <button className="btn" disabled={busy} onClick={() => setConfirmation({
          title: 'Откатить параметр?', lines: [settingName(setting), 'Вернуть: ' + revisionValue(revision, 'previous')],
          run: () => mutate('/api/settings/server/rollback', { revisionId: revision.id, serviceId: setting.serviceId, section: setting.section, key: setting.key }),
        })}>Откатить</button>}
      </div>)}
    </div>}
    {confirmation && <Confirm confirmation={confirmation} onClose={() => setConfirmation(null)} />}
  </div>;
}

function StorageEditor({ role, selected, profiles, revisions, draft, setDraft, onChanged, onSelect, onExpired, query }: {
  role: string; selected?: StorageProfile; profiles: StorageProfile[]; revisions: StorageRevision[];
  draft: StorageDraft; setDraft: (draft: StorageDraft) => void; onChanged: (targets: string[], saved: boolean) => Promise<void>; onSelect: (id: string) => void; onExpired: () => void; query: string;
}) {
  const [message, setMessage] = React.useState('');
  const [errors, setErrors] = React.useState<Record<string, string>>({});
  const [busy, setBusy] = React.useState(false);
  const [showSecret, setShowSecret] = React.useState(false);
  const [showAccess, setShowAccess] = React.useState(false);
  const [confirmation, setConfirmation] = React.useState<Confirmation | null>(null);
  const legacy = !!selected?.isLegacy || role.endsWith('-old');
  const initial = profileDraft(selected);
  const dirty = Object.keys(initial).some((key) => draft[key as keyof StorageDraft] !== initial[key as keyof StorageDraft]);
  const ordered = profiles.filter((profile) => profile.role === role).sort((a, b) => b.version - a.version);
  const active = ordered.find((profile) => profile.isActive);
  function edit<K extends keyof StorageDraft>(key: K, value: StorageDraft[K]) { setDraft({ ...draft, [key]: value }); setErrors((current) => ({ ...current, [key]: '' })); }
  function validate() {
    const next: Record<string, string> = {};
    try { const url = new URL(draft.serviceUrl); if (!['https:', 'http:'].includes(url.protocol)) throw new Error(); }
    catch { next.serviceUrl = 'Укажите HTTP(S) endpoint'; }
    if (!draft.bucketName.trim()) next.bucketName = 'Укажите bucket';
    if (!draft.accessKey && !selected?.hasAccessKey) next.accessKey = 'Укажите access key';
    if (!draft.secretKey && !selected?.hasSecretKey) next.secretKey = 'Укажите secret key';
    if (!/^\d+$/.test(draft.quotaValue)) next.quotaValue = 'Введите целое число от 0';
    else if (BigInt(draft.quotaValue) * QUOTA_UNIT_BYTES[draft.quotaUnit] > 9223372036854775807n)
      next.quotaValue = 'Квота превышает допустимый размер';
    setErrors(next);
    return Object.keys(next).length === 0;
  }
  const quotaChanged = draft.quotaValue !== initial.quotaValue || draft.quotaUnit !== initial.quotaUnit;
  const body = { role, ...draft, quotaValue: selected && !quotaChanged ? '' : draft.quotaValue,
    quotaUnit: selected && !quotaChanged ? '' : draft.quotaUnit,
    isLegacy: legacy, profileId: selected?.profileId || null, confirmLegacyMutation: legacy };
  async function mutate(path: string, payload: unknown, save = true) {
    setBusy(true);
    try {
      const response = await request<MutationResult>(path, payload);
      if (response.status === 403) { onExpired(); return; }
      setMessage(response.data?.message || (response.ok ? 'Выполнено' : 'Не удалось выполнить действие'));
      setConfirmation(null);
      if (response.ok && save) {
        const saved = path === '/api/settings/server/storage/profile';
        if (saved) setDraft({ ...draft, accessKey: '', secretKey: '' });
        await onChanged(response.data?.restartTargets || ['files'], saved);
      }
    } finally { setBusy(false); }
  }
  function save() {
    if (!validate()) return;
    const lines = [
      'Роль: ' + (STORAGE_ROLES[role] || role),
      !selected || selected.serviceUrl !== draft.serviceUrl ? 'Endpoint: ' + (selected?.serviceUrl ? selected.serviceUrl + ' → ' : '') + draft.serviceUrl : '',
      !selected || selected.bucketName !== draft.bucketName ? 'Bucket: ' + (selected?.bucketName ? selected.bucketName + ' → ' : '') + draft.bucketName : '',
      !selected || selected.isR2 !== draft.isR2 ? 'Cloudflare R2: ' + (draft.isR2 ? 'да' : 'нет') : '',
      initial.quotaValue !== draft.quotaValue || initial.quotaUnit !== draft.quotaUnit || !selected ? 'Квота: ' + draft.quotaValue + ' ' + draft.quotaUnit.toUpperCase() + (draft.quotaValue === '0' ? ' (безлимит)' : '') : '',
      draft.accessKey ? 'Access key: заменить' : '', draft.secretKey ? 'Secret key: заменить' : '',
      legacy ? 'Legacy-профиль используется существующими файлами. Изменение требует подтверждения.' : '',
    ].filter(Boolean);
    setConfirmation({ title: selected ? 'Сохранить S3-профиль?' : 'Создать S3-профиль?', lines,
      run: () => mutate('/api/settings/server/storage/profile', body) });
  }
  function fieldError(key: string) { return errors[key] ? <small className="field-error" role="alert">{errors[key]}</small> : null; }
  return <article className="storage-profile-card">
    <div className="storage-profile-head"><div className="storage-profile-identity"><h3>{STORAGE_ROLES[role] || role}</h3>
      <small>{selected?.profileId || 'Новый профиль роли ' + role}</small></div>
      <span className={'pill-info ' + (selected?.isActive ? 'ok' : 'warn')}>{selected?.isActive ? 'Активен' : legacy ? 'Legacy' : selected ? 'Прошлая версия' : 'Создание'}</span>
    </div>
    {!selected && role !== 'universal' && <p className="sys-note">До создания профиля новые файлы этой роли используют universal.</p>}
    <fieldset disabled={busy} className="storage-profile-grid"><legend>Подключение</legend>
      <label><span><Highlight value="Endpoint" query={query} /></span><input aria-label="Endpoint" type="url" value={draft.serviceUrl} aria-invalid={!!errors.serviceUrl} onChange={(event) => edit('serviceUrl', event.target.value)} placeholder="https://s3.example.com" />{fieldError('serviceUrl')}</label>
      <label><span><Highlight value="Bucket" query={query} /></span><input aria-label="Bucket" value={draft.bucketName} aria-invalid={!!errors.bucketName} onChange={(event) => edit('bucketName', event.target.value)} />{fieldError('bucketName')}</label>
      <label><span><Highlight value="Access key" query={query} />{selected?.hasAccessKey && <small className="credential-set"> · задан</small>}</span>
        <div className="credential-input"><input aria-label="Access key" type={showAccess ? 'text' : 'password'} value={draft.accessKey} placeholder={selected?.hasAccessKey ? 'Задан' : ''} aria-invalid={!!errors.accessKey} onChange={(event) => edit('accessKey', event.target.value)} autoComplete="off" />
          <button type="button" className="icon-btn" aria-label={showAccess ? 'Скрыть введённый access key' : 'Показать введённый access key'} disabled={!draft.accessKey} onClick={() => setShowAccess(!showAccess)}><Icon.eye size={18} /></button></div>{fieldError('accessKey')}
      </label>
      <label><span><Highlight value="Secret key" query={query} />{selected?.hasSecretKey && <small className="credential-set"> · задан</small>}</span>
        <div className="credential-input"><input aria-label="Secret key" type={showSecret ? 'text' : 'password'} value={draft.secretKey} placeholder={selected?.hasSecretKey ? 'Оставьте пустым, чтобы не менять' : ''} aria-invalid={!!errors.secretKey} onChange={(event) => edit('secretKey', event.target.value)} autoComplete="new-password" />
          <button type="button" className="icon-btn" aria-label={showSecret ? 'Скрыть введённый secret key' : 'Показать введённый secret key'} disabled={!draft.secretKey} onClick={() => setShowSecret(!showSecret)}><Icon.eye size={18} /></button></div>{fieldError('secretKey')}
      </label>
      <p className="storage-credentials-help"><Highlight value="Оставьте поля ключей пустыми, чтобы сохранить текущие credentials." query={query} /></p>
      <label><span><Highlight value="Квота S3 бакета" query={query} /></span><div className="storage-quota-control">
        <input aria-label="Квота S3 бакета" type="number" min="0" step="1" inputMode="numeric" value={draft.quotaValue} aria-invalid={!!errors.quotaValue} onChange={(event) => edit('quotaValue', event.target.value)} />
        <select aria-label="Единица квоты" value={draft.quotaUnit} onChange={(event) => edit('quotaUnit', event.target.value as QuotaUnit)}><option value="gb">ГБ</option><option value="tb">ТБ</option><option value="pb">ПБ</option></select>
      </div><small>0 = безлимит. Квота общая для одинаковых endpoint и bucket.</small>
        {selected?.quotaBytes && selected.quotaBytes !== '0' && initial.quotaValue === '0' && <small>Текущая квота: {quotaLabel(selected)}. Введите целое значение для замены.</small>}{fieldError('quotaValue')}</label>
      <label className="server-check"><input type="checkbox" checked={draft.isR2} onChange={(event) => edit('isR2', event.target.checked)} /><span><Highlight value="Cloudflare R2" query={query} /></span></label>
    </fieldset>
    <div className="storage-profile-footer"><div className="storage-profile-actions">
      <button className="btn" disabled={busy} onClick={() => { if (validate()) void mutate('/api/settings/server/storage/check', body, false); }}>{busy ? 'Выполняем…' : 'Проверить доступ'}</button>
      <button className="btn primary" disabled={busy || !dirty} onClick={save}>{selected ? 'Сохранить профиль' : 'Создать профиль'}</button>
      {dirty && <button className="btn text" disabled={busy} onClick={() => { setDraft(initial); setErrors({}); }}>Сбросить</button>}
    </div></div>
    {message && <div className="server-setting-message" role="status">{message}</div>}
    <details className="storage-history"><summary>Версии и история · {ordered.length}</summary>
      {ordered.map((profile) => <div className="storage-version-row" key={profile.profileId}>
        <div><strong>{profile.profileId}</strong><small>{profile.serviceUrl} · {profile.bucketName} · {quotaLabel(profile)}</small></div>
        <div className="storage-version-actions"><button className="btn" disabled={busy} onClick={() => onSelect(profile.profileId)}>Редактировать</button>
          {!profile.isLegacy && !profile.isActive && <button className="btn" disabled={busy} onClick={() => setConfirmation({ title: 'Активировать версию?', lines: [profile.profileId, 'Новые файлы роли будут использовать эту версию.'],
            run: () => mutate('/api/settings/server/storage/activate', { profileId: profile.profileId }) })}>Активировать</button>}</div>
      </div>)}
      {revisions.filter((revision) => ordered.some((profile) => profile.profileId === revision.profileId)).map((revision) =>
        <div className="storage-revision-row" key={revision.id}>{revision.changeKind} · {revision.profileId} · {revision.changedAt ? new Date(revision.changedAt).toLocaleString('ru-RU') : '—'}</div>)}
      {!legacy && role !== 'universal' && active && <button className="btn text" disabled={busy} onClick={() => setConfirmation({ title: 'Отключить специализированную роль?',
        lines: ['Новые файлы роли ' + role + ' будут использовать universal. Старые версии останутся доступны.'],
        run: () => mutate('/api/settings/server/storage/disable', { role }) })}>Отключить роль</button>}
    </details>
    {confirmation && <Confirm confirmation={confirmation} onClose={() => setConfirmation(null)} />}
  </article>;
}

function StorageSection({ profiles, revisions, query, onChanged, onExpired, open, onToggle }: {
  profiles: StorageProfile[]; revisions: StorageRevision[]; query: string; onChanged: Changed; onExpired: () => void;
  open: boolean; onToggle: (event: React.SyntheticEvent<HTMLDetailsElement>) => void;
}) {
  const legacyRoles = Array.from(new Set(profiles.filter((profile) => profile.isLegacy).map((profile) => profile.role)));
  const roles = [...Object.keys(STORAGE_ROLES), ...legacyRoles];
  const [role, setRole] = React.useState('universal');
  const [selectedId, setSelectedId] = React.useState<string | null>(null);
  const [creating, setCreating] = React.useState(false);
  const [mobileDetail, setMobileDetail] = React.useState(false);
  const [drafts, setDrafts] = React.useState<Record<string, StorageDraft>>({});
  const ordered = profiles.filter((profile) => profile.role === role).sort((a, b) => b.version - a.version);
  const selected = creating ? undefined : ordered.find((profile) => profile.profileId === selectedId) || ordered.find((profile) => profile.isActive) || ordered[0];
  const draftKey = selected?.profileId || 'new:' + role;
  const draft = drafts[draftKey] || profileDraft(selected);
  const universal = profiles.find((profile) => profile.role === 'universal' && profile.isActive);
  const roleMatches = (item: string) => matches(STORAGE_FIELDS + ' ' + item + ' ' + (STORAGE_ROLES[item] || item), query);
  const visible = roles.some(roleMatches);
  const dirtyFields = Object.entries(drafts).reduce((count, [key, value]) => {
    const initial = profileDraft(profiles.find((profile) => profile.profileId === key));
    return count + Object.keys(value).filter((field) => value[field as keyof StorageDraft] !== initial[field as keyof StorageDraft]).length;
  }, 0);
  async function changed(targets: string[], saved: boolean) {
    if (saved) {
      setDrafts((current) => { const next = { ...current }; delete next[draftKey]; return next; });
      setSelectedId(null); setCreating(false);
    }
    await onChanged(targets);
  }
  return <details className="set-card server-storage-section" hidden={!visible} open={open} onToggle={onToggle}>
    <summary className="set-card-head server-group-summary"><div><strong className="ttl">S3-профили</strong><small>Роли для новых файлов, подключение и квоты бакетов</small></div><span className="server-section-count">{profiles.length} профилей{dirtyFields ? ' · ' + dirtyFields + ' изменено' : ''}</span></summary>
    <div className={'storage-workspace' + (mobileDetail ? ' show-detail' : '')}>
      <div className="storage-role-list">
        {roles.map((item, index) => {
          const current = profiles.filter((profile) => profile.role === item).sort((a, b) => b.version - a.version);
          const active = current.find((profile) => profile.isActive);
          const legacy = legacyRoles.includes(item);
          const effective = active || (legacy ? current[0] : universal);
          return <React.Fragment key={item}>
            {index === Object.keys(STORAGE_ROLES).length && <div className="storage-role-group">Legacy · существующие файлы</div>}
            <button className={'storage-role-row' + (role === item ? ' selected' : '')} hidden={!roleMatches(item)} aria-pressed={role === item}
              onClick={() => { setRole(item); setSelectedId(null); setCreating(false); setMobileDetail(true); }}>
              <div className="storage-role-name"><strong><Highlight value={STORAGE_ROLES[item] || item} query={query} /></strong><small>{item}</small></div>
              <span className={'pill-info ' + (active ? 'ok' : 'warn')}>{active ? 'v' + active.version : legacy ? 'Legacy' : item === 'universal' ? 'Не настроен' : 'Universal'}</span>
              <span className="storage-role-location">{effective ? effective.bucketName : 'Нет подключения'}<small>{effective?.serviceUrl || 'Создайте профиль'}</small></span>
              <small className="storage-role-quota">{effective ? quotaLabel(effective) : '—'}</small>
            </button>
          </React.Fragment>;
        })}
      </div>
      <div className="storage-role-detail">
        <button className="btn text storage-back" onClick={() => setMobileDetail(false)}>← К списку ролей</button>
        {selected && !selected.isLegacy && <button className="btn text storage-create" onClick={() => { setCreating(true); setSelectedId(null); }}>Новая версия профиля</button>}
        <StorageEditor key={draftKey} role={role} selected={selected} profiles={profiles} revisions={revisions} draft={draft}
          setDraft={(value) => setDrafts((current) => ({ ...current, [draftKey]: value }))}
          onChanged={changed} onSelect={(id) => { setSelectedId(id); setCreating(false); }} onExpired={onExpired} query={query} />
      </div>
    </div>
  </details>;
}

export default function ServerSettingsTab({ active = true, onAccessExpired = () => {} }: { active?: boolean; onAccessExpired?: () => void }) {
  const resource = useApiResource<ServerSettings>('/api/settings/server', active);
  const [search, setSearch] = React.useState('');
  const [expanded, setExpanded] = React.useState<Set<string>>(new Set());
  const [dirty, setDirty] = React.useState<Record<string, boolean>>({});
  const [restartTargets, setRestartTargets] = React.useState<string[]>([]);
  const [newReserved, setNewReserved] = React.useState('');
  const [renaming, setRenaming] = React.useState<string | null>(null);
  const [reservedMessage, setReservedMessage] = React.useState('');
  const [reservedBusy, setReservedBusy] = React.useState(false);
  const [confirmation, setConfirmation] = React.useState<Confirmation | null>(null);
  React.useEffect(() => { if (resource.error?.status === 403) onAccessExpired(); }, [resource.error, onAccessExpired]);
  const onDirty = React.useCallback((id: string, changed: boolean) => setDirty((current) => current[id] === changed ? current : { ...current, [id]: changed }), []);
  const changed: Changed = async (targets) => { setRestartTargets((current) => Array.from(new Set([...current, ...targets]))); await resource.reload(); };
  async function reservedAction(path: string, body: unknown) {
    setReservedBusy(true);
    try {
      const response = await request<MutationResult>('/api/settings/server/reserved/' + path, body);
      if (response.status === 403) { onAccessExpired(); return; }
      setReservedMessage(response.data?.message || (response.ok ? 'Сохранено' : 'Не удалось сохранить имя'));
      setConfirmation(null);
      if (response.ok) { setNewReserved(''); setRenaming(null); await changed(response.data?.restartTargets || ['users']); }
    } finally { setReservedBusy(false); }
  }
  if (!resource.data) return resource.error ? <div className="sys-banner err" role="alert">{resource.error.message}<button className="btn" onClick={() => void resource.reload()}>Повторить</button></div> : <Loading label="Загрузка настроек сервера…" />;
  const data = resource.data;
  const settings = data.settings.filter((setting) => setting.key.toLowerCase() !== 'token');
  const query = search.trim().toLocaleLowerCase('ru-RU');
  const parameterMatches = (setting: ServerSetting) => matches((SERVICE_LABELS[setting.serviceId] || '') + ' ' + SERVICE_NAMES[setting.serviceId] + ' ' + settingName(setting), query);
  const count = settings.filter(parameterMatches).length;
  const serviceIds = Array.from(new Set(settings.map((setting) => setting.serviceId))).sort((a, b) => a - b);
  const allKeys = ['s3', 'reserved', ...serviceIds.flatMap((id) => ['service:' + id, ...Array.from(new Set(settings.filter((setting) => setting.serviceId === id).map((setting) => 'section:' + id + ':' + setting.section)))])];
  const hasS3 = matches(STORAGE_FIELDS, query) || [...Object.keys(STORAGE_ROLES), ...data.storageProfiles.map((profile) => profile.role)].some((role) => matches(role + ' ' + (STORAGE_ROLES[role] || ''), query));
  const s3FieldCount = STORAGE_FIELD_NAMES.filter((field) => matches(field, query)).length;
  const s3RoleCount = Array.from(new Set([...Object.keys(STORAGE_ROLES), ...data.storageProfiles.map((profile) => profile.role)]))
    .filter((role) => matches(role + ' ' + (STORAGE_ROLES[role] || ''), query)).length;
  const s3MatchesLabel = s3FieldCount ? s3FieldCount + ' ' + plural(s3FieldCount, 'поле', 'поля', 'полей') + ' S3'
    : s3RoleCount ? s3RoleCount + ' ' + plural(s3RoleCount, 'роль', 'роли', 'ролей') + ' S3' : '1 группа S3';
  const hasReserved = matches('Зарезервированные имена ReservedNames Usernames', query);
  const toggle = (key: string, event: React.SyntheticEvent<HTMLDetailsElement>) => {
    if (query || event.target !== event.currentTarget) return;
    const open = event.currentTarget.open;
    setExpanded((current) => { if (current.has(key) === open) return current; const next = new Set(current); if (open) next.add(key); else next.delete(key); return next; });
  };
  return <div className="server-settings-tab">
    {restartTargets.length > 0 && <div className="sys-banner warn">Сохранено. Требуется перезапуск: {restartTargets.join(', ')}. <a href="#system">Открыть обслуживание</a></div>}
    {resource.error && <div className="sys-banner err" role="alert">{resource.error.message}<button className="btn text" onClick={() => void resource.reload()}>Повторить</button></div>}
    <header className="server-settings-intro"><div><h2>Конфигурация сервера</h2><p>{serviceIds.length} сервисов · {settings.length} параметров · {data.storageProfiles.length} S3-профилей</p></div>
      <button className="btn text" disabled={resource.loading} onClick={() => void resource.reload()}><Icon.refresh size={16} /> Обновить</button></header>
    <section className="server-settings-toolbar">
      <div className="server-section-head"><div><div className="ttl">Параметры сервисов</div><div className="sub">Изменения применяются после перезапуска соответствующих сервисов</div></div><span className="server-section-count">{count} параметров{query && hasS3 ? ' · ' + s3MatchesLabel : ''}{Object.values(dirty).filter(Boolean).length > 0 ? ' · ' + Object.values(dirty).filter(Boolean).length + ' изменено' : ''}</span></div>
      <label className="server-search-field"><span>Поиск по параметрам конфигурации</span><input type="search" value={search} placeholder="JwtSettings:Issuer или Endpoint" onChange={(event) => setSearch(event.target.value)} /></label>
      <div className="server-collapse-actions"><button className="btn text" onClick={() => setExpanded(new Set(allKeys))}>Развернуть всё</button><button className="btn text" onClick={() => setExpanded(new Set())}>Свернуть всё</button>
        {search && <button className="btn text" onClick={() => setSearch('')}>Очистить поиск конфигураций</button>}</div>
      {query && count === 0 && !hasS3 && !hasReserved && <div className="server-empty" role="status">По этому запросу параметры не найдены.</div>}
    </section>
    {serviceIds.map((serviceId) => {
      const items = settings.filter((setting) => setting.serviceId === serviceId);
      const groupKey = 'service:' + serviceId;
      const groupDirty = items.filter((setting) => dirty[settingId(setting)]).length;
      const sections = Array.from(new Set(items.map((setting) => setting.section)));
      return <details className="set-card server-service-card" key={serviceId} hidden={!items.some(parameterMatches)} open={!!query || expanded.has(groupKey)} onToggle={(event) => toggle(groupKey, event)}>
        <summary className="server-group-summary"><div><strong><Highlight value={SERVICE_LABELS[serviceId] || 'Сервис ' + serviceId} query={query} /></strong><small><Highlight value={SERVICE_NAMES[serviceId] || ''} query={query} /></small></div><span>{items.filter(parameterMatches).length} параметров{groupDirty > 0 ? ' · ' + groupDirty + ' изменено' : ''}</span></summary>
        <div className="server-settings-list">{sections.map((section) => {
          const group = items.filter((setting) => setting.section === section);
          const sectionKey = 'section:' + serviceId + ':' + section;
          return <details className="server-config-section" key={section} hidden={!group.some(parameterMatches)} open={!!query || expanded.has(sectionKey)} onToggle={(event) => toggle(sectionKey, event)}>
            <summary><strong><Highlight value={section} query={query} /></strong><span>{group.filter(parameterMatches).length}</span></summary>
            {group.map((setting) => <div key={settingId(setting)} hidden={!parameterMatches(setting)}>
              <SettingEditor setting={setting} query={query} onSaved={changed} onDirty={onDirty} onExpired={onAccessExpired} /></div>)}
          </details>;
        })}</div>
      </details>;
    })}
    <details className="set-card server-reserved-card" hidden={!hasReserved} open={!!query || expanded.has('reserved')} onToggle={(event) => toggle('reserved', event)}>
      <summary className="set-card-head server-group-summary"><strong className="ttl">Зарезервированные имена</strong><span className="server-section-count">{data.reservedNames.length} имён</span></summary>
      <div className="set-card-body server-reserved-body">
        <form className="reserved-add" onSubmit={(event) => { event.preventDefault(); if (newReserved.trim()) void reservedAction(renaming ? 'update' : 'add', renaming ? { oldName: renaming, newName: newReserved } : { name: newReserved }); }}>
          <label><span>{renaming ? 'Переименовать ' + renaming : 'Добавить имя'}</span><input aria-label="Новое зарезервированное имя" value={newReserved} onChange={(event) => setNewReserved(event.target.value)} /></label><button className="btn primary" disabled={reservedBusy || !newReserved.trim()}>{renaming ? 'Сохранить' : 'Добавить'}</button>
          {renaming && <button type="button" className="btn text" onClick={() => { setRenaming(null); setNewReserved(''); }}>Отмена</button>}
        </form>
        <div className="reserved-list">{data.reservedNames.map((name) => <span key={name}><b>{name}</b><button type="button" aria-label={'Переименовать ' + name} onClick={() => { setRenaming(name); setNewReserved(name); }}><Icon.pencil size={14} /></button>
          <button type="button" aria-label={'Удалить ' + name} onClick={() => setConfirmation({ title: 'Удалить зарезервированное имя?', lines: [name], run: () => reservedAction('delete', { name }) })}><Icon.x size={14} /></button></span>)}</div>
        {reservedMessage && <div role="status">{reservedMessage}</div>}
      </div>
    </details>
    <StorageSection profiles={data.storageProfiles} revisions={data.storageRevisions} query={query} onChanged={changed} onExpired={onAccessExpired}
      open={!!query || expanded.has('s3')} onToggle={(event) => toggle('s3', event)} />
    {confirmation && <Confirm confirmation={confirmation} onClose={() => setConfirmation(null)} />}
  </div>;
}
