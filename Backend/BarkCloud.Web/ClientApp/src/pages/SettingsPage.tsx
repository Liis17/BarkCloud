import React from 'react';
import { Icon } from '../components/Icon';
import { Loading } from '../components/ui/EmptyState';
import { usePageHeader } from '../hooks/usePageHeader';
import { plural } from '../lib/format';
import { applyTheme, getTheme, type Theme } from '../lib/theme';
import type { Privacy, Session, SettingsState } from '../lib/types';

// ─── HTTP к /api/settings/* (отдельный от lib/api: возвращает {ok,status,data}, не бросает) ───
interface ApiResp<T = unknown> {
  ok: boolean;
  status: number;
  data: T | null;
}
async function apiJson<T = unknown>(method: string, path: string, body?: unknown): Promise<ApiResp<T>> {
  const r = await fetch(path, {
    method,
    credentials: 'same-origin',
    headers: body ? { 'Content-Type': 'application/json' } : undefined,
    body: body ? JSON.stringify(body) : undefined,
  });
  let data: T | null = null;
  try {
    data = (await r.json()) as T;
  } catch {
    /* пусто */
  }
  return { ok: r.ok, status: r.status, data };
}
const sGet = <T,>(p: string) => apiJson<T>('GET', p);
const sPost = <T,>(p: string, b?: unknown) => apiJson<T>('POST', p, b);
const errMsg = (res: ApiResp, fallback?: string): string => {
  const m = res.data && typeof res.data === 'object' ? (res.data as { message?: string }).message : undefined;
  return m || fallback || 'Ошибка';
};

type Flash = (kind: 'ok' | 'err', msg: string) => void;

const SVC_LABELS: Record<string, string> = {
  configuration: 'Configuration',
  identity: 'Identity',
  users: 'Users',
  files: 'Files',
  notification: 'Notification',
  web: 'Веб-клиент',
};

function Toggle({ on, onChange, disabled }: { on: boolean; onChange: (v: boolean) => void; disabled?: boolean }) {
  return (
    <div
      className={'toggle' + (on ? ' on' : '')}
      onClick={() => {
        if (!disabled) onChange(!on);
      }}
      style={disabled ? { opacity: 0.5, cursor: 'default' } : undefined}
    />
  );
}

function Field({ label, help, children, end }: { label: React.ReactNode; help?: React.ReactNode; children?: React.ReactNode; end?: React.ReactNode }) {
  return (
    <div className="field-row">
      <div className="lbl">
        {label}
        {help && <span className="help">{help}</span>}
      </div>
      <div>{children}</div>
      <div className="right-end">{end}</div>
    </div>
  );
}

function Toast({ toast }: { toast: { kind: 'ok' | 'err'; msg: string } | null }) {
  if (!toast) return null;
  return (
    <div className={'sys-toast ' + toast.kind}>
      {toast.kind === 'ok' ? <Icon.check size={18} /> : <Icon.x size={18} />}
      <span>{toast.msg}</span>
    </div>
  );
}

function SaveBtn({ saving, onClick, disabled, children, icon }: { saving?: boolean; onClick: () => void; disabled?: boolean; children: React.ReactNode; icon?: React.ReactNode }) {
  return (
    <button className="btn primary" onClick={onClick} disabled={saving || disabled}>
      {saving ? <span className="spin" /> : icon || null} {children}
    </button>
  );
}

// ─────────── Обслуживание ───────────

interface Svc {
  service: string;
  state: string;
  image?: string;
  isWeb?: boolean;
}
interface ServicesSnap {
  services: Svc[];
  dockerOk: boolean;
  error?: string;
}

function SvcStatus({ state }: { state: string }) {
  const running = state === 'running';
  const label = running
    ? 'Запущен'
    : state === 'not_found'
    ? 'Не найден'
    : state === 'exited' || state === 'dead'
    ? 'Остановлен'
    : state === 'restarting'
    ? 'Перезапуск'
    : state || '—';
  return (
    <span className={'pill-info ' + (running ? 'ok' : 'warn')}>
      {running ? <Icon.check size={12} /> : <Icon.x size={12} />} {label}
    </span>
  );
}

interface ProgressState {
  items: { svc: string; state: 'pending' | 'current' | 'done' | 'error' }[];
  done: number;
  total: number;
  finished: boolean;
}

function SystemSection({ admin, system }: { admin: SettingsState['admin']; system: SettingsState['system'] }) {
  const [unlocked, setUnlocked] = React.useState(admin.unlocked);
  const [password, setPassword] = React.useState('');
  const [unlockErr, setUnlockErr] = React.useState('');
  const [unlocking, setUnlocking] = React.useState(false);
  const [services, setServices] = React.useState<Svc[] | null>(null);
  const [dockerErr, setDockerErr] = React.useState<string | null>(null);
  const [busy, setBusy] = React.useState<Record<string, boolean>>({});
  const [toast, setToast] = React.useState<{ kind: 'ok' | 'err'; msg: string } | null>(null);
  const [progress, setProgress] = React.useState<ProgressState | null>(null);
  const [overlay, setOverlay] = React.useState<{ title: string; seconds: number } | null>(null);

  const flash = (kind: 'ok' | 'err', msg: string) => {
    setToast({ kind, msg });
    setTimeout(() => setToast(null), 4200);
  };

  const loadServices = React.useCallback(async () => {
    try {
      const r = await fetch('/api/system/services', { credentials: 'same-origin', cache: 'no-cache' });
      if (r.ok) {
        const snap: ServicesSnap = await r.json();
        setServices(snap.services || []);
        setDockerErr(snap.dockerOk ? null : snap.error || 'Docker недоступен');
      } else if (r.status === 403) {
        setUnlocked(false);
      } else {
        setServices([]);
        setDockerErr(`Сервер ответил ${r.status}`);
      }
    } catch (e) {
      setServices([]);
      setDockerErr(String(e));
    }
  }, []);

  React.useEffect(() => {
    if (unlocked) {
      setServices(null);
      loadServices();
    }
  }, [unlocked, loadServices]);

  async function doUnlock() {
    if (!password) return;
    setUnlocking(true);
    setUnlockErr('');
    const { ok, data } = await sPost<{ message?: string }>('/api/system/unlock', { password });
    setUnlocking(false);
    if (ok) {
      setPassword('');
      setUnlocked(true);
    } else setUnlockErr(data?.message || 'Не удалось разблокировать');
  }
  async function doLock() {
    await sPost('/api/system/lock');
    setUnlocked(false);
    setServices(null);
  }

  async function svcAction(svc: string, kind: string) {
    setBusy((b) => ({ ...b, [svc]: true }));
    const { ok, data } = await sPost<{ message?: string }>(`/api/system/services/${svc}/${kind}`);
    flash(ok ? 'ok' : 'err', data?.message || (ok ? 'Готово' : 'Ошибка'));
    setBusy((b) => ({ ...b, [svc]: false }));
    setTimeout(loadServices, 1500);
  }

  async function updateAll() {
    const targets = (services || []).filter((s) => !s.isWeb).map((s) => s.service);
    if (!targets.length) return;
    if (!window.confirm('Обновить все сервисы приложения (кроме веб-клиента)?')) return;
    setProgress({ items: targets.map((s) => ({ svc: s, state: 'pending' })), done: 0, total: targets.length, finished: false });
    for (const svc of targets) {
      setProgress((p) => (p ? { ...p, items: p.items.map((it) => (it.svc === svc ? { ...it, state: 'current' } : it)) } : p));
      const { ok } = await sPost(`/api/system/services/${svc}/update`);
      setProgress((p) => (p ? { ...p, done: p.done + 1, items: p.items.map((it) => (it.svc === svc ? { ...it, state: ok ? 'done' : 'error' } : it)) } : p));
    }
    setProgress((p) => (p ? { ...p, finished: true } : p));
    loadServices();
  }

  function startOverlay(title: string) {
    setOverlay({ title, seconds: 0 });
    let success = 0;
    const t0 = Date.now();
    const tick = setInterval(() => setOverlay((o) => (o ? { ...o, seconds: Math.floor((Date.now() - t0) / 1000) } : o)), 1000);
    const poll = setInterval(async () => {
      try {
        const r = await fetch('/healthz', { cache: 'no-cache' });
        if (r.ok) {
          if (++success >= 2) {
            clearInterval(poll);
            clearInterval(tick);
            setTimeout(() => window.location.reload(), 1500);
          }
        } else success = 0;
      } catch {
        success = 0;
      }
    }, 3000);
  }

  function webSelf(kind: 'update' | 'restart') {
    const title = kind === 'update' ? 'Обновление веб-клиента' : 'Перезапуск веб-клиента';
    if (!window.confirm(`${title}? Страница ненадолго станет недоступна и перезагрузится автоматически.`)) return;
    const path = kind === 'update' ? '/api/system/web/update-self' : '/api/system/web/restart-self';
    sPost<{ message?: string }>(path).then(({ ok, data }) => (ok ? startOverlay(title) : flash('err', data?.message || 'Ошибка')));
  }

  let body: React.ReactNode;
  if (!admin.enabled) {
    body = (
      <div style={{ color: 'var(--md-on-surface-variant)', fontSize: 14 }}>
        Админ-доступ не настроен. Задайте <code>WEB_ADMIN_PASSWORD</code> в окружении веб-контейнера и перезапустите его.
      </div>
    );
  } else if (!unlocked) {
    body = (
      <div>
        <div style={{ color: 'var(--md-on-surface-variant)', fontSize: 14, marginBottom: 16 }}>Введите админ-пароль, чтобы управлять обновлением бэкенда.</div>
        <div className="unlock-row">
          <input type="password" placeholder="Админ-пароль" value={password} onChange={(e) => setPassword(e.target.value)} onKeyDown={(e) => { if (e.key === 'Enter') doUnlock(); }} autoFocus />
          <button className="btn primary" onClick={doUnlock} disabled={unlocking || !password}>
            {unlocking ? <span className="spin" /> : <Icon.lock size={16} />} Разблокировать
          </button>
        </div>
        {unlockErr && <div className="unlock-err">{unlockErr}</div>}
      </div>
    );
  } else if (services === null) {
    body = (
      <div style={{ display: 'flex', alignItems: 'center', gap: 12, color: 'var(--md-on-surface-variant)', fontSize: 14 }}>
        <span className="spin" /> Загрузка состояния сервисов…
      </div>
    );
  } else {
    const micros = services.filter((s) => !s.isWeb);
    const web = services.find((s) => s.isWeb);

    const renderRow = (s: Svc, actions: (running: boolean) => React.ReactNode) => (
      <div key={s.service} className="svc-row">
        <div className="svc-main">
          <div className="svc-ic">
            <Icon.server size={20} />
          </div>
          <div className="svc-info">
            <div className="svc-name">
              {SVC_LABELS[s.service] || s.service} <SvcStatus state={s.state} />
            </div>
            {s.image && (
              <div className="svc-img" title={s.image}>
                {s.image}
              </div>
            )}
            {s.service === 'notification' && !system.emailEnabled && (
              <div className="svc-note" style={{ fontSize: 12, color: 'var(--md-on-surface-variant)', marginTop: 4 }}>
                Не используется — почта на сервере не настроена. Сервис можно остановить, а чтобы убрать совсем —
                удалить <code>notification</code> из <code>docker-compose.yml</code> и его переменные из <code>.env</code>.
              </div>
            )}
          </div>
        </div>
        <div className="svc-actions">{busy[s.service] ? <span className="spin" style={{ margin: '0 11px' }} /> : actions(s.state === 'running')}</div>
      </div>
    );

    body = (
      <>
        {dockerErr && (
          <div className="sys-banner err">
            <Icon.x size={18} />
            <span>Docker недоступен: {dockerErr}</span>
          </div>
        )}

        <div className="sys-section-label">Микросервисы</div>
        <div className="svc-list">
          {micros.map((s) =>
            renderRow(s, (running) => (
              <>
                <button className="iconb" title="Обновить" onClick={() => svcAction(s.service, 'update')}>
                  <Icon.download size={20} />
                </button>
                <button className="iconb" title="Перезапустить" onClick={() => svcAction(s.service, 'restart')}>
                  <Icon.refresh size={20} />
                </button>
                {running ? (
                  <button className="iconb" title="Остановить" onClick={() => svcAction(s.service, 'stop')}>
                    <Icon.power size={20} />
                  </button>
                ) : (
                  <button className="iconb" title="Запустить" onClick={() => svcAction(s.service, 'start')}>
                    <Icon.play size={20} />
                  </button>
                )}
              </>
            )),
          )}
        </div>
        <div style={{ display: 'flex', gap: 8, flexWrap: 'wrap', alignItems: 'center', marginTop: 6 }}>
          <button className="btn primary" onClick={updateAll} disabled={!micros.length}>
            <Icon.download size={16} /> Обновить микросервисы
          </button>
          <button className="btn" onClick={() => { setServices(null); loadServices(); }}>
            <Icon.refresh size={16} /> Обновить статус
          </button>
        </div>

        <hr className="divider" />

        <div className="sys-section-label">Веб-клиент</div>
        {web && <div className="svc-list">{renderRow(web, () => <span style={{ fontSize: 12, color: 'var(--md-on-surface-variant)' }}>ниже ↓</span>)}</div>}
        <div className="sys-note">
          Обновление и перезапуск веб-клиента: страница ненадолго станет недоступна и перезагрузится сама. Веб пересоздаётся отдельным helper-контейнером, поэтому работает
          и под Linux/WSL, и на Windows Docker Desktop. При неудачном обновлении автоматически откатывается на прежний контейнер.
        </div>
        <div style={{ display: 'flex', gap: 8, flexWrap: 'wrap', alignItems: 'center' }}>
          <button className="btn primary" onClick={() => webSelf('update')}>
            <Icon.download size={16} /> Обновить веб-клиент
          </button>
          <button className="btn" onClick={() => webSelf('restart')}>
            <Icon.refresh size={16} /> Перезапустить веб-клиент
          </button>
          <button className="btn text" onClick={doLock} style={{ marginLeft: 'auto' }}>
            <Icon.lock size={16} /> Заблокировать
          </button>
        </div>
      </>
    );
  }

  return (
    <>
      <div className="set-card" id="sec-system">
        <div className="set-card-head">
          <h3>Обслуживание</h3>
          <div className="sub" style={{ display: 'flex', alignItems: 'center', gap: 10, flexWrap: 'wrap' }}>
            <span>Обновление и перезапуск сервисов бэкенда</span>
            <span className="pill-info ok">
              {system.version} · {system.edition}
            </span>
          </div>
        </div>
        <div className="set-card-body">{body}</div>
      </div>

      {progress && (
        <div className="sys-scrim">
          <div className="sys-dialog">
            <h3>Обновление сервисов</h3>
            <div className="sub">{progress.finished ? 'Готово' : 'Выполняется последовательно…'}</div>
            <div className="prog-bar">
              <span style={{ width: `${Math.round((progress.done / progress.total) * 100)}%` }} />
            </div>
            <div className="prog-list">
              {progress.items.map((it) => (
                <div key={it.svc} className={'prog-item ' + it.state}>
                  <span className="pi">
                    {it.state === 'current' ? <span className="spin" /> : it.state === 'done' ? <Icon.check size={18} /> : it.state === 'error' ? <Icon.x size={18} /> : <Icon.clock size={16} />}
                  </span>
                  <span>{SVC_LABELS[it.svc] || it.svc}</span>
                </div>
              ))}
            </div>
            {progress.finished && (
              <div className="dlg-actions">
                <button className="btn" onClick={() => setProgress(null)}>
                  Закрыть
                </button>
              </div>
            )}
          </div>
        </div>
      )}

      {overlay && (
        <div className="upd-overlay">
          <div className="big-spin" />
          <h2>{overlay.title}</h2>
          <p>Загрузка нового образа и пересоздание контейнера. Проверяем доступность каждые 3 секунды — страница перезагрузится сама.</p>
          <div className="timer">
            <Icon.clock size={16} /> {overlay.seconds} сек
          </div>
        </div>
      )}

      <Toast toast={toast} />
    </>
  );
}

// ─────────── Аккаунт ───────────

function AccountTab({ profile, flash }: { profile: SettingsState['profile']; flash: Flash }) {
  const [firstName, setFirstName] = React.useState(profile.firstName || '');
  const [lastName, setLastName] = React.useState(profile.lastName || '');
  const [savingName, setSavingName] = React.useState(false);
  const [bio, setBio] = React.useState(profile.bio || '');
  const [savingBio, setSavingBio] = React.useState(false);
  const [username, setUsername] = React.useState(profile.username || '');
  const [baseUsername, setBaseUsername] = React.useState(profile.username || '');
  const [uStatus, setUStatus] = React.useState<'idle' | 'checking' | 'ok' | 'taken' | 'invalid'>('idle');
  const [savingUser, setSavingUser] = React.useState(false);
  const [avatarUrl, setAvatarUrl] = React.useState(profile.avatarPreviewUrl || profile.avatarUrl || '');
  const [avatarBusy, setAvatarBusy] = React.useState(false);
  const fileRef = React.useRef<HTMLInputElement>(null);
  const [delOpen, setDelOpen] = React.useState(false);
  const [delText, setDelText] = React.useState('');
  const [deleting, setDeleting] = React.useState(false);

  React.useEffect(() => {
    const u = username.trim();
    if (u === baseUsername) {
      setUStatus('idle');
      return;
    }
    if (u.length < 3) {
      setUStatus('invalid');
      return;
    }
    setUStatus('checking');
    const id = setTimeout(async () => {
      const res = await sGet<{ available: boolean }>('/api/settings/profile/username-available?u=' + encodeURIComponent(u));
      if (res.ok && res.data) setUStatus(res.data.available ? 'ok' : 'taken');
      else setUStatus('idle');
    }, 400);
    return () => clearTimeout(id);
  }, [username, baseUsername]);

  async function saveName() {
    setSavingName(true);
    const res = await sPost('/api/settings/profile/name', { firstName: firstName.trim(), lastName: lastName.trim() });
    setSavingName(false);
    flash(res.ok ? 'ok' : 'err', res.ok ? 'Имя сохранено' : errMsg(res));
  }
  async function saveBio() {
    setSavingBio(true);
    const res = await sPost('/api/settings/profile/bio', { bio });
    setSavingBio(false);
    flash(res.ok ? 'ok' : 'err', res.ok ? 'Описание сохранено' : errMsg(res));
  }
  async function saveUsername() {
    setSavingUser(true);
    const res = await sPost('/api/settings/profile/username', { username: username.trim() });
    setSavingUser(false);
    if (res.ok) {
      setBaseUsername(username.trim());
      setUStatus('idle');
      flash('ok', 'Имя пользователя изменено');
    } else flash('err', errMsg(res));
  }
  async function onPickFile(e: React.ChangeEvent<HTMLInputElement>) {
    const file = e.target.files && e.target.files[0];
    e.target.value = '';
    if (!file) return;
    setAvatarBusy(true);
    const fd = new FormData();
    fd.append('file', file);
    const r = await fetch('/api/settings/avatar', { method: 'POST', credentials: 'same-origin', body: fd });
    let data: { avatarUrl?: string; avatarPreviewUrl?: string; message?: string } | null = null;
    try {
      data = await r.json();
    } catch {
      /* пусто */
    }
    setAvatarBusy(false);
    if (r.ok && data) {
      setAvatarUrl(data.avatarPreviewUrl || data.avatarUrl || '');
      flash('ok', 'Аватар обновлён');
    } else flash('err', (data && data.message) || 'Не удалось загрузить аватар');
  }
  async function removeAvatar() {
    setAvatarBusy(true);
    const res = await sPost('/api/settings/avatar/remove');
    setAvatarBusy(false);
    if (res.ok) {
      setAvatarUrl('');
      flash('ok', 'Аватар удалён');
    } else flash('err', errMsg(res));
  }
  async function doDelete() {
    setDeleting(true);
    const res = await sPost('/api/settings/account/delete');
    if (res.ok) {
      window.location.href = '/login';
      return;
    }
    setDeleting(false);
    flash('err', errMsg(res));
  }

  const uPill =
    uStatus === 'checking' ? (
      <span className="pill-info warn">Проверка…</span>
    ) : uStatus === 'ok' ? (
      <span className="pill-info ok">
        <Icon.check size={12} /> Свободно
      </span>
    ) : uStatus === 'taken' ? (
      <span className="pill-info err">
        <Icon.x size={12} /> Занято
      </span>
    ) : uStatus === 'invalid' ? (
      <span className="pill-info err">Минимум 3 символа</span>
    ) : null;

  return (
    <>
      <div className="set-card">
        <div className="set-card-head">
          <h3>Профиль</h3>
          <div className="sub">Имя, фото и описание, видимые другим</div>
        </div>
        <div className="set-card-body">
          <div style={{ display: 'flex', gap: 20, alignItems: 'center' }}>
            <div className="avatar-big">{avatarUrl ? <img src={avatarUrl} alt="" /> : profile.initials}</div>
            <div style={{ display: 'flex', flexDirection: 'column', gap: 8, flex: 1 }}>
              <div style={{ fontSize: 14, color: 'var(--md-on-surface-variant)' }}>Изменить аватар. Рекомендуется не меньше 256×256.</div>
              <div style={{ display: 'flex', gap: 8, alignItems: 'center' }}>
                <input ref={fileRef} type="file" accept="image/*" style={{ display: 'none' }} onChange={onPickFile} />
                <button className="btn" onClick={() => fileRef.current && fileRef.current.click()} disabled={avatarBusy}>
                  {avatarBusy ? <span className="spin" /> : <Icon.upload size={16} />} Загрузить
                </button>
                {avatarUrl && (
                  <button className="btn text" onClick={removeAvatar} disabled={avatarBusy}>
                    Удалить
                  </button>
                )}
              </div>
            </div>
          </div>
          <hr className="divider" />
          <div className="form-stack">
            <label>Имя</label>
            <input type="text" value={firstName} onChange={(e) => setFirstName(e.target.value)} placeholder="Имя" />
            <label>Фамилия</label>
            <input type="text" value={lastName} onChange={(e) => setLastName(e.target.value)} placeholder="Фамилия" />
            <div>
              <SaveBtn saving={savingName} onClick={saveName} icon={<Icon.check size={16} />}>
                Сохранить имя
              </SaveBtn>
            </div>
          </div>
          <hr className="divider" />
          <Field label="Email" help="Используется для входа, изменить нельзя">
            <input type="email" value={profile.email || ''} disabled placeholder="—" style={{ opacity: 0.6, cursor: 'not-allowed' }} />
          </Field>
        </div>
      </div>

      <div className="set-card">
        <div className="set-card-head">
          <h3>Имя пользователя</h3>
          <div className="sub">Уникальный @username для поиска и ссылок</div>
        </div>
        <div className="set-card-body">
          <div className="uname-wrap">
            <div className="row">
              <input type="text" value={username} onChange={(e) => setUsername(e.target.value.replace(/\s/g, ''))} placeholder="username" />
              {uPill}
            </div>
            <div>
              <SaveBtn saving={savingUser} onClick={saveUsername} disabled={uStatus !== 'ok'} icon={<Icon.check size={16} />}>
                Сменить имя пользователя
              </SaveBtn>
            </div>
          </div>
        </div>
      </div>

      <div className="set-card">
        <div className="set-card-head">
          <h3>О себе</h3>
          <div className="sub">Короткое описание профиля (до 200 символов)</div>
        </div>
        <div className="set-card-body">
          <textarea value={bio} maxLength={200} onChange={(e) => setBio(e.target.value)} placeholder="Расскажите о себе…" />
          <div className="char-counter">{bio.length}/200</div>
          <div>
            <SaveBtn saving={savingBio} onClick={saveBio} icon={<Icon.check size={16} />}>
              Сохранить описание
            </SaveBtn>
          </div>
        </div>
      </div>

      <div className="set-card danger">
        <div className="set-card-head">
          <h3>Опасная зона</h3>
          <div className="sub">Действие нельзя отменить</div>
        </div>
        <div className="set-card-body">
          <div className="danger-row">
            <div className="info">
              <div className="t">Удалить аккаунт безвозвратно</div>
              <div className="d">Профиль, устройства, файлы и история удаляются. Восстановление невозможно.</div>
            </div>
            <button className="danger-btn" onClick={() => { setDelText(''); setDelOpen(true); }}>
              Удалить
            </button>
          </div>
        </div>
      </div>

      {delOpen && (
        <div className="sys-scrim" onClick={() => !deleting && setDelOpen(false)}>
          <div className="sys-dialog" onClick={(e) => e.stopPropagation()}>
            <h3>Удалить аккаунт?</h3>
            <div className="sub">
              Это необратимо. Введите <b>УДАЛИТЬ</b> для подтверждения.
            </div>
            <input className="dlg-input" value={delText} onChange={(e) => setDelText(e.target.value)} placeholder="УДАЛИТЬ" autoFocus />
            <div className="dlg-actions two">
              <button className="btn text" onClick={() => setDelOpen(false)} disabled={deleting}>
                Отмена
              </button>
              <button className="danger-btn" onClick={doDelete} disabled={deleting || delText.trim() !== 'УДАЛИТЬ'}>
                {deleting ? <span className="spin" /> : null} Удалить навсегда
              </button>
            </div>
          </div>
        </div>
      )}
    </>
  );
}

// ─────────── Безопасность ───────────

function SecurityTab({ security, flash }: { security: SettingsState['security']; flash: Flash }) {
  const [auth2fa, setAuth2fa] = React.useState(security.authenticator);
  const [email2fa, setEmail2fa] = React.useState(security.emailOtp);
  const [pwdOpen, setPwdOpen] = React.useState(false);
  const [oldPwd, setOldPwd] = React.useState('');
  const [newPwd, setNewPwd] = React.useState('');
  const [confirmPwd, setConfirmPwd] = React.useState('');
  const [savingPwd, setSavingPwd] = React.useState(false);
  const [enableDlg, setEnableDlg] = React.useState<{ otpType: number; qr: string; code: string } | null>(null);
  const [enableCode, setEnableCode] = React.useState('');
  const [enableBusy, setEnableBusy] = React.useState(false);
  const [disableDlg, setDisableDlg] = React.useState<{ otpType: number } | null>(null);
  const [disableCode, setDisableCode] = React.useState('');
  const [disableBusy, setDisableBusy] = React.useState(false);

  async function refresh2fa() {
    const res = await sGet<{ authenticator: boolean; email: boolean }>('/api/settings/security/2fa');
    if (res.ok && res.data) {
      setAuth2fa(res.data.authenticator);
      setEmail2fa(res.data.email);
    }
  }
  async function savePassword() {
    if (newPwd.length < 6) {
      flash('err', 'Пароль слишком короткий (мин. 6)');
      return;
    }
    if (newPwd !== confirmPwd) {
      flash('err', 'Пароли не совпадают');
      return;
    }
    setSavingPwd(true);
    const res = await sPost('/api/settings/security/password', { oldPassword: oldPwd, newPassword: newPwd });
    setSavingPwd(false);
    if (res.ok) {
      setPwdOpen(false);
      setOldPwd('');
      setNewPwd('');
      setConfirmPwd('');
      flash('ok', 'Пароль изменён');
    } else flash('err', errMsg(res));
  }
  async function startEnable(otpType: number) {
    setEnableBusy(true);
    const res = await sPost<{ qr: string; code: string }>('/api/settings/security/2fa/enable', { otpType });
    setEnableBusy(false);
    if (res.ok && res.data) {
      setEnableCode('');
      setEnableDlg({ otpType, qr: res.data.qr, code: res.data.code });
    } else flash('err', errMsg(res));
  }
  async function confirmEnable() {
    setEnableBusy(true);
    const res = await sPost('/api/settings/security/2fa/confirm', { otpCode: enableCode.trim() });
    setEnableBusy(false);
    if (res.ok) {
      setEnableDlg(null);
      flash('ok', 'Двухфакторная включена');
      refresh2fa();
    } else flash('err', errMsg(res));
  }
  async function startDisable(otpType: number) {
    if (otpType === 2) {
      const res = await sPost('/api/settings/security/2fa/disable', { otpType, otpCode: '' });
      if (res.ok) {
        flash('ok', 'Email-2FA отключена');
        refresh2fa();
      } else flash('err', errMsg(res));
      return;
    }
    setDisableCode('');
    setDisableDlg({ otpType });
  }
  async function confirmDisable() {
    if (!disableDlg) return;
    setDisableBusy(true);
    const res = await sPost('/api/settings/security/2fa/disable', { otpType: disableDlg.otpType, otpCode: disableCode.trim() });
    setDisableBusy(false);
    if (res.ok) {
      setDisableDlg(null);
      flash('ok', 'Двухфакторная отключена');
      refresh2fa();
    } else flash('err', errMsg(res));
  }

  return (
    <>
      <div className="set-card">
        <div className="set-card-head">
          <h3>Пароль</h3>
          <div className="sub">Смена пароля для входа</div>
        </div>
        <div className="set-card-body">
          {!pwdOpen ? (
            <div>
              <button className="btn" onClick={() => setPwdOpen(true)}>
                <Icon.key size={16} /> Сменить пароль
              </button>
            </div>
          ) : (
            <div className="form-stack">
              <label>Текущий пароль</label>
              <input type="password" value={oldPwd} onChange={(e) => setOldPwd(e.target.value)} autoFocus />
              <label>Новый пароль</label>
              <input type="password" value={newPwd} onChange={(e) => setNewPwd(e.target.value)} />
              <label>Повторите новый пароль</label>
              <input type="password" value={confirmPwd} onChange={(e) => setConfirmPwd(e.target.value)} />
              <div style={{ display: 'flex', gap: 8 }}>
                <SaveBtn saving={savingPwd} onClick={savePassword} icon={<Icon.check size={16} />}>
                  Сохранить
                </SaveBtn>
                <button className="btn text" onClick={() => { setPwdOpen(false); setOldPwd(''); setNewPwd(''); setConfirmPwd(''); }} disabled={savingPwd}>
                  Отмена
                </button>
              </div>
            </div>
          )}
        </div>
      </div>

      <div className="set-card">
        <div className="set-card-head">
          <h3>Двухфакторная аутентификация</h3>
          <div className="sub">Дополнительный код при входе</div>
        </div>
        <div className="set-card-body">
          <Field label="Приложение-аутентификатор" help="Google Authenticator · TOTP" end={<Toggle on={auth2fa} onChange={(v) => (v ? startEnable(1) : startDisable(1))} disabled={enableBusy} />}>
            {auth2fa && (
              <span className="pill-info ok">
                <Icon.check size={12} /> Включено
              </span>
            )}
          </Field>
          <Field label="Коды по email" help="Одноразовый код на почту" end={<Toggle on={email2fa} onChange={(v) => (v ? startEnable(2) : startDisable(2))} disabled={enableBusy} />}>
            {email2fa && (
              <span className="pill-info ok">
                <Icon.check size={12} /> Включено
              </span>
            )}
          </Field>
        </div>
      </div>

      {enableDlg && (
        <div className="sys-scrim" onClick={() => !enableBusy && setEnableDlg(null)}>
          <div className="sys-dialog" onClick={(e) => e.stopPropagation()}>
            <h3>Включение 2FA</h3>
            <div className="sub">{enableDlg.qr ? 'Отсканируйте QR в приложении-аутентификаторе и введите код из него.' : 'Введите код, отправленный на вашу почту.'}</div>
            <div className="qr-wrap">
              {enableDlg.qr && <img src={'data:image/png;base64,' + enableDlg.qr} alt="QR" />}
              {enableDlg.code && <div className="qr-code">Ключ: {enableDlg.code}</div>}
            </div>
            <input className="dlg-input" value={enableCode} onChange={(e) => setEnableCode(e.target.value)} placeholder="Код подтверждения" autoFocus onKeyDown={(e) => { if (e.key === 'Enter') confirmEnable(); }} />
            <div className="dlg-actions two">
              <button className="btn text" onClick={() => setEnableDlg(null)} disabled={enableBusy}>
                Отмена
              </button>
              <button className="btn primary" onClick={confirmEnable} disabled={enableBusy || !enableCode.trim()}>
                {enableBusy ? <span className="spin" /> : null} Подтвердить
              </button>
            </div>
          </div>
        </div>
      )}

      {disableDlg && (
        <div className="sys-scrim" onClick={() => !disableBusy && setDisableDlg(null)}>
          <div className="sys-dialog" onClick={(e) => e.stopPropagation()}>
            <h3>Отключить 2FA</h3>
            <div className="sub">Введите текущий код из приложения-аутентификатора.</div>
            <input className="dlg-input" value={disableCode} onChange={(e) => setDisableCode(e.target.value)} placeholder="Код" autoFocus onKeyDown={(e) => { if (e.key === 'Enter') confirmDisable(); }} />
            <div className="dlg-actions two">
              <button className="btn text" onClick={() => setDisableDlg(null)} disabled={disableBusy}>
                Отмена
              </button>
              <button className="btn primary" onClick={confirmDisable} disabled={disableBusy || !disableCode.trim()}>
                {disableBusy ? <span className="spin" /> : null} Отключить
              </button>
            </div>
          </div>
        </div>
      )}
    </>
  );
}

// ─────────── Приватность ───────────

const VIS_OPTS = [
  { v: 0, l: 'Всем' },
  { v: 1, l: 'Контактам' },
  { v: 2, l: 'Никому' },
];

function PrivacyTab({ privacy, flash }: { privacy: Privacy; flash: Flash }) {
  const [p, setP] = React.useState<Privacy>(privacy);
  const [saving, setSaving] = React.useState(false);
  const set = <K extends keyof Privacy>(k: K, v: Privacy[K]) => setP((prev) => ({ ...prev, [k]: v }));

  async function save() {
    setSaving(true);
    const res = await sPost<Privacy>('/api/settings/privacy', {
      profileVisibility: p.profileVisibility,
      emailVisibility: p.emailVisibility,
      lastSeenVisibility: p.lastSeenVisibility,
      searchableByUsername: p.searchableByUsername,
    });
    setSaving(false);
    if (res.ok && res.data) setP(res.data);
    flash(res.ok ? 'ok' : 'err', res.ok ? 'Настройки приватности сохранены' : errMsg(res));
  }

  const sel = (k: 'profileVisibility' | 'emailVisibility' | 'lastSeenVisibility') => (
    <select value={p[k]} onChange={(e) => set(k, parseInt(e.target.value, 10))}>
      {VIS_OPTS.map((o) => (
        <option key={o.v} value={o.v}>
          {o.l}
        </option>
      ))}
    </select>
  );

  return (
    <div className="set-card">
      <div className="set-card-head">
        <h3>Приватность</h3>
        <div className="sub">Кто видит ваши данные</div>
      </div>
      <div className="set-card-body">
        <Field label="Профиль" help="Аватар, имя и описание">
          {sel('profileVisibility')}
        </Field>
        <Field label="Email" help="Видимость адреса почты">
          {sel('emailVisibility')}
        </Field>
        <Field label="Был в сети" help="Время последнего захода">
          {sel('lastSeenVisibility')}
        </Field>
        <Field label="Поиск по имени пользователя" help="Можно ли найти вас через поиск" end={<Toggle on={p.searchableByUsername} onChange={(v) => set('searchableByUsername', v)} />}>
          <span />
        </Field>
        <hr className="divider" />
        <div>
          <SaveBtn saving={saving} onClick={save} icon={<Icon.check size={16} />}>
            Сохранить
          </SaveBtn>
        </div>
      </div>
    </div>
  );
}

// ─────────── Хранилище ───────────

const DISK_OTHER_COLOR = 'var(--md-on-surface-variant)';
const DISK_S3_COLOR = '#9A4F1E';

function StorageTab({ storage }: { storage: SettingsState['storage'] }) {
  const disk = storage.disk;
  return (
    <div className="set-card">
      <div className="set-card-head">
        <h3>Хранилище</h3>
        <div className="sub" style={{ display: 'flex', alignItems: 'center', gap: 10, flexWrap: 'wrap' }}>
          <span>
            {disk.usedLabel} из {disk.totalLabel} использовано на диске
          </span>
          <span className="pill-info warn">{disk.usedPct}%</span>
        </div>
      </div>
      <div className="set-card-body">
        <div className="stor-bar">
          <span style={{ width: `${disk.otherPct}%`, background: DISK_OTHER_COLOR }} />
          <span style={{ width: `${disk.s3Pct}%`, background: DISK_S3_COLOR }} />
        </div>
        <div className="stor-legend">
          <div className="item">
            <span className="sw" style={{ background: DISK_OTHER_COLOR }} />
            <span className="k">Другие данные</span>
            <span className="v">{disk.otherLabel}</span>
          </div>
          <div className="item">
            <span className="sw" style={{ background: DISK_S3_COLOR }} />
            <span className="k">Облако (S3)</span>
            <span className="v">{disk.s3Label}</span>
          </div>
          <div className="item">
            <span className="sw" style={{ background: 'var(--md-surface-container-high)', border: '1px solid var(--md-outline-variant)' }} />
            <span className="k">Свободно</span>
            <span className="v">{disk.freeLabel}</span>
          </div>
        </div>
      </div>
    </div>
  );
}

// ─────────── Устройства и сессии ───────────

function SessionsTab({ sessions: initial, flash }: { sessions: Session[]; flash: Flash }) {
  const [sessions, setSessions] = React.useState<Session[]>(initial);
  const [busy, setBusy] = React.useState<Record<string, boolean>>({});
  const [renaming, setRenaming] = React.useState<string | null>(null);
  const [renameVal, setRenameVal] = React.useState('');
  const [revokingAll, setRevokingAll] = React.useState(false);

  async function revoke(s: Session) {
    if (!window.confirm(`Завершить сессию «${s.device}»?`)) return;
    setBusy((b) => ({ ...b, [s.deviceId]: true }));
    const res = await sPost('/api/settings/sessions/revoke', { deviceId: s.deviceId });
    setBusy((b) => ({ ...b, [s.deviceId]: false }));
    if (res.ok) {
      setSessions((prev) => prev.filter((x) => x.deviceId !== s.deviceId));
      flash('ok', 'Сессия завершена');
    } else flash('err', errMsg(res));
  }
  async function revokeOthers() {
    if (!window.confirm('Завершить все сессии, кроме текущей?')) return;
    setRevokingAll(true);
    const res = await sPost<{ revoked: number }>('/api/settings/sessions/revoke-others');
    if (res.ok) {
      const fresh = await sGet<{ sessions: Session[] }>('/api/settings/sessions');
      if (fresh.ok && fresh.data) setSessions(fresh.data.sessions || []);
      flash('ok', `Завершено сессий: ${res.data ? res.data.revoked : 0}`);
    } else flash('err', errMsg(res));
    setRevokingAll(false);
  }
  async function saveRename(s: Session) {
    const name = renameVal.trim();
    setBusy((b) => ({ ...b, [s.deviceId]: true }));
    const res = await sPost('/api/settings/devices/rename', { deviceId: s.deviceId, customName: name });
    setBusy((b) => ({ ...b, [s.deviceId]: false }));
    if (res.ok) {
      setSessions((prev) => prev.map((x) => (x.deviceId === s.deviceId ? { ...x, device: name || x.device } : x)));
      setRenaming(null);
      flash('ok', 'Устройство переименовано');
    } else flash('err', errMsg(res));
  }

  const header = sessions.length
    ? `${sessions.length} ${plural(sessions.length, 'устройство', 'устройства', 'устройств')} с активным доступом`
    : 'Нет активных сессий';

  return (
    <div className="set-card">
      <div className="set-card-head">
        <h3>Устройства и сессии</h3>
        <div className="sub">{header}</div>
      </div>
      <div className="set-card-body" style={{ paddingTop: 6 }}>
        {sessions.length === 0 && <div style={{ color: 'var(--md-on-surface-variant)', fontSize: 14 }}>Список пуст или сервис недоступен.</div>}
        {sessions.map((s) => (
          <div key={s.deviceId || s.device} className={'session-row' + (s.current ? ' curr' : '')}>
            <div className="si">
              <Icon.device size={20} />
            </div>
            <div style={{ minWidth: 0 }}>
              {renaming === s.deviceId ? (
                <input
                  className="dlg-input"
                  style={{ marginTop: 0 }}
                  value={renameVal}
                  autoFocus
                  onChange={(e) => setRenameVal(e.target.value)}
                  onKeyDown={(e) => { if (e.key === 'Enter') saveRename(s); if (e.key === 'Escape') setRenaming(null); }}
                />
              ) : (
                <div className="who">{s.device}</div>
              )}
              <div className="meta">{[s.os, s.location, s.when].filter(Boolean).join(' · ')}</div>
            </div>
            {s.current ? <span className="badge-curr">Текущая</span> : <span style={{ width: 80 }} />}
            <div style={{ display: 'flex', gap: 6, justifyContent: 'flex-end' }}>
              {busy[s.deviceId] ? (
                <span className="spin" style={{ margin: '0 8px' }} />
              ) : renaming === s.deviceId ? (
                <button className="btn text" onClick={() => saveRename(s)}>
                  Сохранить
                </button>
              ) : (
                <>
                  {s.deviceId && (
                    <button className="btn text" title="Переименовать" onClick={() => { setRenameVal(s.device); setRenaming(s.deviceId); }}>
                      <Icon.pencil size={16} />
                    </button>
                  )}
                  {!s.current && s.deviceId && (
                    <button className="disc" onClick={() => revoke(s)}>
                      Отключить
                    </button>
                  )}
                </>
              )}
            </div>
          </div>
        ))}
        <div style={{ marginTop: 12 }}>
          <button className="btn" onClick={revokeOthers} disabled={revokingAll || sessions.length <= 1}>
            {revokingAll ? <span className="spin" /> : null} Выйти со всех устройств, кроме этого
          </button>
        </div>
      </div>
    </div>
  );
}

// ─────────── Внешний вид ───────────

function AppearanceTab() {
  const [theme, setTheme] = React.useState<Theme>(getTheme());
  function pick(t: Theme) {
    setTheme(t);
    applyTheme(t);
  }
  return (
    <div className="set-card">
      <div className="set-card-head">
        <h3>Внешний вид</h3>
        <div className="sub">Тема оформления (сохраняется в этом браузере)</div>
      </div>
      <div className="set-card-body">
        <Field label="Тема">
          <div className="theme-row">
            <div className={'theme-swatch light' + (theme === 'light' ? ' on' : '')} onClick={() => pick('light')} title="Светлая" />
            <div className={'theme-swatch dark' + (theme === 'dark' ? ' on' : '')} onClick={() => pick('dark')} title="Тёмная" />
            <div className={'theme-swatch auto' + (theme === 'auto' ? ' on' : '')} onClick={() => pick('auto')} title="Как в системе" />
          </div>
        </Field>
        <div style={{ fontSize: 13, color: 'var(--md-on-surface-variant)' }}>
          {theme === 'light' ? 'Светлая тема.' : theme === 'dark' ? 'Тёмная тема.' : 'Тема следует настройкам системы.'}
        </div>
      </div>
    </div>
  );
}

// ─────────── Корневой компонент страницы ───────────

interface NavItem {
  key: string;
  label: string;
  icon: string;
}

export function SettingsPage() {
  const [data, setData] = React.useState<SettingsState | null>(null);
  const [err, setErr] = React.useState<string | null>(null);
  const [toast, setToast] = React.useState<{ kind: 'ok' | 'err'; msg: string } | null>(null);
  const flash = React.useCallback<Flash>((kind, msg) => {
    setToast({ kind, msg });
    setTimeout(() => setToast(null), 4200);
  }, []);

  React.useEffect(() => {
    sGet<SettingsState>('/api/settings/full')
      .then((res) => {
        if (res.status === 401) {
          window.location.href = '/login';
          return;
        }
        if (res.ok && res.data) setData(res.data);
        else setErr('Не удалось загрузить настройки');
      })
      .catch(() => setErr('Не удалось загрузить настройки'));
  }, []);

  const nav: NavItem[] = React.useMemo(
    () => [
      { key: 'account', label: 'Аккаунт', icon: 'user' },
      { key: 'security', label: 'Безопасность', icon: 'lock' },
      { key: 'privacy', label: 'Приватность', icon: 'eye' },
      { key: 'storage', label: 'Хранилище', icon: 'server' },
      { key: 'sessions', label: 'Устройства и сессии', icon: 'device' },
      { key: 'appearance', label: 'Внешний вид', icon: 'palette' },
      ...(data?.admin.enabled ? [{ key: 'system', label: 'Обслуживание', icon: 'server' }] : []),
    ],
    [data],
  );
  const navKeys = nav.map((n) => n.key);

  const [section, setSection] = React.useState(() => {
    const h = (window.location.hash || '').replace('#', '');
    return h || 'account';
  });

  React.useEffect(() => {
    const onHash = () => {
      const h = (window.location.hash || '').replace('#', '');
      if (h) setSection(h);
    };
    window.addEventListener('hashchange', onHash);
    return () => window.removeEventListener('hashchange', onHash);
  }, []);

  const go = (key: string) => {
    setSection(key);
    window.location.hash = key;
  };

  const active = navKeys.includes(section) ? section : 'account';
  const activeLabel = nav.find((n) => n.key === active)?.label || 'Аккаунт';

  usePageHeader(
    () => ({
      title: 'Настройки',
      documentTitle: `Настройки: ${activeLabel}`,
      kicker: (
        <>
          <span>Прочее</span>
          <span className="sep">/</span>
          <span className="cur">Настройки</span>
        </>
      ),
      search: false,
    }),
    [activeLabel],
  );

  if (err) return <div style={{ color: 'var(--md-error)', padding: 24 }}>{err}</div>;
  if (!data) return <Loading label="Загрузка настроек…" />;

  let content: React.ReactNode;
  switch (active) {
    case 'security':
      content = <SecurityTab security={data.security} flash={flash} />;
      break;
    case 'privacy':
      content = <PrivacyTab privacy={data.privacy} flash={flash} />;
      break;
    case 'storage':
      content = <StorageTab storage={data.storage} />;
      break;
    case 'sessions':
      content = <SessionsTab sessions={data.sessions} flash={flash} />;
      break;
    case 'appearance':
      content = <AppearanceTab />;
      break;
    case 'system':
      content = <SystemSection admin={data.admin} system={data.system} />;
      break;
    default:
      content = <AccountTab profile={data.profile} flash={flash} />;
  }

  return (
    <>
      <div className="settings-shell">
        <div className="set-nav">
          <div className="set-nav-label">Разделы</div>
          {nav.map((n) => {
            const Ic = Icon[n.icon];
            return (
              <button key={n.key} className={active === n.key ? 'on' : ''} onClick={() => go(n.key)}>
                <Ic size={20} />
                {n.label}
              </button>
            );
          })}
        </div>
        <div className="set-content">{content}</div>
      </div>
      <Toast toast={toast} />
    </>
  );
}
