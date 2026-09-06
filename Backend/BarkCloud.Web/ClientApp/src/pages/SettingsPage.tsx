import React from 'react';
import { Icon } from '../components/Icon';
import { Loading } from '../components/ui/EmptyState';
import { usePageHeader } from '../hooks/usePageHeader';
import { plural } from '../lib/format';
import { maintenanceWaitPath } from '../lib/maintenance';
import { applyTheme, getTheme, type Theme } from '../lib/theme';
import { webauthnRegister, webauthnSupported } from '../lib/webauthn';
import type { Privacy, Session, SettingsState } from '../lib/types';

interface WebAuthnKey {
  id: string;
  name: string;
  createdAt: string | null;
  lastUsedAt: string | null;
}

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

async function readServerStartedAt(): Promise<string | null> {
  try {
    const response = await fetch('/healthz', {
      cache: 'no-store',
      credentials: 'same-origin',
      redirect: 'manual',
    });
    return response.status === 200 ? response.headers.get('X-BarkCloud-Started-At') : null;
  } catch {
    return null;
  }
}

type Flash = (kind: 'ok' | 'err', msg: string) => void;

const SVC_LABELS: Record<string, string> = {
  configuration: 'Configuration',
  identity: 'Identity',
  users: 'Users',
  files: 'Files',
  notification: 'Notification',
  torrent: 'Torrent',
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

type VersionState = 'ready' | 'unknown' | 'registry_unavailable' | string;

interface VersionInfo {
  repository?: string | null;
  tag?: string | null;
  branch?: string | null;
  currentVersion?: string | null;
  latestVersion?: string | null;
  updateAvailable?: boolean | null;
  state?: VersionState;
  error?: string | null;
}

interface Svc {
  service: string;
  container: string;
  composeService?: string;
  state: string;
  status?: string;
  image?: string;
  imageDigest?: string | null;
  isWeb?: boolean;
  branch?: string | null;
  currentVersion?: string | null;
  latestVersion?: string | null;
  updateAvailable?: boolean | null;
  versionState?: VersionState;
  versionError?: string | null;
  version?: VersionInfo;
}

interface MaintenanceStatus {
  operationId: string;
  kind: string;
  state: string;
  message?: string | null;
  diagnostic?: string | null;
  updatedAtUtc: string;
}

interface ServicesSnap {
  services: Svc[];
  dockerOk: boolean;
  error?: string | null;
  lastMaintenance?: MaintenanceStatus | null;
}

interface BranchInfo {
  service: string;
  composeService: string;
  branch: string;
  runningBranch?: string | null;
  branches: string[];
}

interface BranchSnap {
  currentBranch?: string | null;
  branches: string[];
  services: BranchInfo[];
}

type DeploymentKind = 'Update' | 'Restart' | 'Start' | 'Stop' | 'SwitchBranch';
type DeploymentJobState = 'Queued' | 'Running' | 'AwaitingReconnect' | 'Completed' | 'Failed';
type DeploymentStepState = 'Pending' | 'InProgress' | 'Completed' | 'Failed' | 'Skipped';

interface DeploymentStep {
  service: string;
  branch?: string | null;
  state: DeploymentStepState;
  message?: string | null;
  diagnostic?: string | null;
  rolledBack?: boolean;
}

interface DeploymentJob {
  id: string;
  kind: DeploymentKind;
  state: DeploymentJobState;
  steps: DeploymentStep[];
  error?: string | null;
  diagnostic?: string | null;
  requiresReconnect?: boolean;
  createdAtUtc: string;
  startedAtUtc?: string | null;
  finishedAtUtc?: string | null;
}

interface JobStart {
  jobId?: string | null;
  message?: string;
  updated?: number;
}

function versionOf(service: Svc): VersionInfo {
  return service.version || {
    branch: service.branch,
    currentVersion: service.currentVersion,
    latestVersion: service.latestVersion,
    updateAvailable: service.updateAvailable,
    state: service.versionState,
    error: service.versionError,
  };
}

function SvcStatus({ state, status }: { state: string; status?: string }) {
  const running = state === 'running';
  const unavailable = state === 'unavailable';
  const label = running
    ? 'Запущен'
    : state === 'not_found'
    ? 'Не найден'
    : state === 'exited' || state === 'dead'
    ? 'Остановлен'
    : state === 'restarting'
    ? 'Перезапуск'
    : state === 'starting'
    ? 'Запускается'
    : state === 'created'
    ? 'Создан'
    : unavailable
    ? 'Docker недоступен'
    : state || '—';
  return (
    <span className={'pill-info ' + (running ? 'ok' : unavailable ? 'err' : 'warn')} title={status || undefined}>
      {running ? <Icon.check size={12} /> : <Icon.x size={12} />} {label}
    </span>
  );
}

function VersionBadge({ service }: { service: Svc }) {
  const version = versionOf(service);
  if (version.state === 'registry_unavailable') {
    return <span className="pill-info err"><Icon.x size={12} /> Реестр недоступен</span>;
  }
  if (version.updateAvailable === true) {
    return <span className="pill-info warn"><Icon.download size={12} /> Доступно обновление</span>;
  }
  if (version.state === 'ready' && version.updateAvailable === false) {
    return <span className="pill-info ok"><Icon.check size={12} /> Актуально</span>;
  }
  return <span className="pill-info warn"><Icon.info size={12} /> Версия не определена</span>;
}

interface ProgressState {
  title: string;
  job: DeploymentJob;
  autoClose: boolean;
}

function SystemSection({ admin, system }: { admin: SettingsState['admin']; system: SettingsState['system'] }) {
  const [unlocked, setUnlocked] = React.useState(admin.unlocked);
  const [password, setPassword] = React.useState('');
  const [unlockErr, setUnlockErr] = React.useState('');
  const [unlocking, setUnlocking] = React.useState(false);
  const [services, setServices] = React.useState<Svc[] | null>(null);
  const [branches, setBranches] = React.useState<BranchSnap | null>(null);
  const [dockerErr, setDockerErr] = React.useState<string | null>(null);
  const [lastMaintenance, setLastMaintenance] = React.useState<MaintenanceStatus | null>(null);
  const [busy, setBusy] = React.useState<Record<string, boolean>>({});
  const [toast, setToast] = React.useState<{ kind: 'ok' | 'err'; msg: string } | null>(null);
  const [progress, setProgress] = React.useState<ProgressState | null>(null);
  const [registrationEnabled, setRegistrationEnabled] = React.useState(system.registrationEnabled);
  const [registrationBusy, setRegistrationBusy] = React.useState(false);
  const trackedJobs = React.useRef(new Set<string>());
  const resumedJobs = React.useRef(false);

  const flash = (kind: 'ok' | 'err', msg: string) => {
    setToast({ kind, msg });
    setTimeout(() => setToast(null), 4200);
  };

  const loadServices = React.useCallback(async () => {
    try {
      const serviceRes = await sGet<ServicesSnap>('/api/system/services');
      if (serviceRes.status === 403) {
        setUnlocked(false);
        return;
      }
      if (!serviceRes.ok || !serviceRes.data) {
        setServices([]);
        setDockerErr(errMsg(serviceRes, `Сервер ответил ${serviceRes.status}`));
        return;
      }

      setServices(serviceRes.data.services || []);
      setDockerErr(serviceRes.data.dockerOk ? null : serviceRes.data.error || 'Docker недоступен');
      setLastMaintenance(serviceRes.data.lastMaintenance || null);

      const branchRes = await sGet<BranchSnap>('/api/system/branches');
      if (branchRes.ok && branchRes.data) setBranches(branchRes.data);
      else setBranches(null);
    } catch (e) {
      setServices([]);
      setDockerErr(String(e));
    }
  }, []);

  React.useEffect(() => {
    if (unlocked) {
      setServices(null);
      void loadServices();
    }
  }, [unlocked, loadServices]);

  React.useEffect(() => {
    if (!unlocked || resumedJobs.current) return;
    resumedJobs.current = true;

    let cancelled = false;
    (async () => {
      const res = await sGet<DeploymentJob[]>('/api/system/deploy/jobs');
      if (cancelled || !res.ok || !res.data) return;
      // AwaitingReconnect уже был передан странице ожидания. Если helper упал до
      // перезапуска web, ссылка с /updating должна вернуть сюда, а не запускать
      // бесконечный цикл переходов обратно на страницу ожидания.
      const active = res.data.find((job) => job.state === 'Queued' || job.state === 'Running');
      if (active) {
        const previousStartedAt = await readServerStartedAt();
        await waitForJob('Продолжение операции обслуживания', active.id, false, active, previousStartedAt);
      }
    })().catch(() => {
      /* состояние сервисов всё равно доступно через ручное обновление */
    });

    return () => {
      cancelled = true;
    };
  }, [unlocked]);

  React.useEffect(() => {
    if (!progress || progress.job.state !== 'Completed' || !progress.autoClose) return;
    const timer = window.setTimeout(() => setProgress(null), 3000);
    return () => window.clearTimeout(timer);
  }, [progress?.job.state, progress?.autoClose]);

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
    setBranches(null);
    setProgress(null);
    resumedJobs.current = false;
  }

  async function toggleRegistration(next: boolean) {
    setRegistrationBusy(true);
    const { ok, data } = await sPost<{ enabled?: boolean; message?: string }>('/api/settings/system/registration', { enabled: next });
    setRegistrationBusy(false);

    if (ok) {
      setRegistrationEnabled(data?.enabled ?? next);
      flash('ok', (data?.enabled ?? next) ? 'Регистрация включена' : 'Регистрация отключена');
    } else {
      flash('err', data?.message || 'Не удалось изменить регистрацию');
    }
  }

  async function waitForJob(
    title: string,
    jobId: string,
    autoClose = false,
    initial?: DeploymentJob,
    previousStartedAt?: string | null,
  ): Promise<DeploymentJob | null> {
    if (trackedJobs.current.has(jobId)) return null;
    trackedJobs.current.add(jobId);
    let lastJob = initial || {
      id: jobId,
      kind: 'Update' as DeploymentKind,
      state: 'Queued' as DeploymentJobState,
      steps: [],
      createdAtUtc: new Date().toISOString(),
    };
    let misses = 0;
    setProgress({ title, job: lastJob, autoClose });

    try {
      while (true) {
        try {
          const res = await sGet<DeploymentJob>(`/api/system/deploy/jobs/${jobId}`);
          if (res.ok && res.data) {
            misses = 0;
            lastJob = res.data;
            setProgress({ title, job: lastJob, autoClose });
            if (lastJob.state === 'AwaitingReconnect') {
              window.location.replace(maintenanceWaitPath(
                lastJob.kind === 'Restart' ? 'restart' : 'update',
                lastJob.id,
                previousStartedAt,
              ));
              return lastJob;
            }
            if (lastJob.state === 'Completed' || lastJob.state === 'Failed') return lastJob;
          } else if (++misses >= 5) {
            lastJob = {
              ...lastJob,
              state: 'Failed',
              error: res.status === 404
                ? 'Задача исчезла — возможно, веб-сервис был перезапущен до её завершения'
                : errMsg(res, `Не удалось получить состояние задачи (HTTP ${res.status})`),
              finishedAtUtc: new Date().toISOString(),
            };
            setProgress({ title, job: lastJob, autoClose: false });
            return lastJob;
          }
        } catch {
          if (++misses >= 5) {
            lastJob = {
              ...lastJob,
              state: 'Failed',
              error: 'Соединение с веб-сервисом потеряно, состояние задачи неизвестно',
              finishedAtUtc: new Date().toISOString(),
            };
            setProgress({ title, job: lastJob, autoClose: false });
            return lastJob;
          }
        }
        await new Promise((resolve) => window.setTimeout(resolve, 2000));
      }
    } finally {
      trackedJobs.current.delete(jobId);
    }
  }

  async function runQueuedAction(title: string, path: string, kind: DeploymentKind, autoClose = false) {
    try {
      const previousStartedAt = await readServerStartedAt();
      const res = await sPost<JobStart>(path);
      if (!res.ok) {
        flash('err', errMsg(res, 'Не удалось поставить операцию в очередь'));
        return;
      }
      if (!res.data?.jobId) {
        flash('ok', res.data?.message || 'Доступных операций нет');
        void loadServices();
        return;
      }

      const job = await waitForJob(title, res.data.jobId, autoClose, {
        id: res.data.jobId,
        kind,
        state: 'Queued',
        steps: [],
        createdAtUtc: new Date().toISOString(),
      }, previousStartedAt);
      if (job && job.state !== 'AwaitingReconnect') {
        flash(job.state === 'Completed' ? 'ok' : 'err', job.state === 'Completed' ? (res.data.message || 'Готово') : (job.error || 'Операция завершилась с ошибкой'));
      }
      void loadServices();
    } catch {
      flash('err', 'Не удалось связаться с веб-сервисом');
    }
  }

  async function svcAction(svc: string, kind: Exclude<DeploymentKind, 'SwitchBranch'>) {
    setBusy((b) => ({ ...b, [svc]: true }));
    try {
      const labels: Record<Exclude<DeploymentKind, 'SwitchBranch'>, string> = {
        Update: 'Обновление',
        Restart: 'Перезапуск',
        Start: 'Запуск',
        Stop: 'Остановка',
      };
      await runQueuedAction(`${labels[kind]}: ${SVC_LABELS[svc] || svc}`, `/api/system/services/${encodeURIComponent(svc)}/${kind.toLowerCase()}`, kind);
    } finally {
      setBusy((b) => ({ ...b, [svc]: false }));
    }
  }

  async function changeBranch(svc: string, branch: string) {
    setBusy((b) => ({ ...b, [`branch:${svc}`]: true }));
    try {
      const previousStartedAt = await readServerStartedAt();
      const res = await sPost<JobStart>(`/api/system/services/${encodeURIComponent(svc)}/branch`, { branch });
      if (!res.ok || !res.data?.jobId) {
        flash(res.ok ? 'ok' : 'err', errMsg(res, res.ok ? 'Канал уже выбран' : 'Не удалось переключить канал'));
        if (res.ok) void loadServices();
        return;
      }
      const job = await waitForJob(`Переключение канала: ${SVC_LABELS[svc] || svc}`, res.data.jobId, true, {
        id: res.data.jobId,
        kind: 'SwitchBranch',
        state: 'Queued',
        steps: [],
        createdAtUtc: new Date().toISOString(),
      }, previousStartedAt);
      if (job && job.state !== 'AwaitingReconnect') {
        flash(job.state === 'Completed' ? 'ok' : 'err', job.state === 'Completed' ? (res.data.message || 'Канал применён') : (job.error || 'Переключение канала завершилось с ошибкой'));
      }
      void loadServices();
    } catch {
      flash('err', 'Не удалось связаться с веб-сервисом');
    } finally {
      setBusy((b) => ({ ...b, [`branch:${svc}`]: false }));
    }
  }

  async function updateAll() {
    if (!services?.length || dockerErr) return;
    if (!window.confirm('Обновить все application-сервисы? Web будет обновлён последним, после чего страница переподключится.')) return;
    await runQueuedAction('Обновление всех сервисов', '/api/system/update-all', 'Update', true);
  }

  async function updateAvailable() {
    const count = (services || []).filter((service) => service.composeService && service.updateAvailable === true).length;
    if (!count || dockerErr) return;
    if (!window.confirm(`Обновить доступные сервисы (${count})? Web, если доступно обновление, будет последним.`)) return;
    await runQueuedAction(`Обновление доступных сервисов (${count})`, '/api/system/update-available', 'Update', true);
  }

  async function restartAll() {
    if (!services?.length || dockerErr) return;
    if (!window.confirm('Перезапустить все application-сервисы? Web будет последним, затем страница переподключится.')) return;
    await runQueuedAction('Перезапуск всех сервисов', '/api/system/restart-all', 'Restart');
  }

  async function webSelf(kind: 'update' | 'restart') {
    const title = kind === 'update' ? 'Обновление веб-клиента' : 'Перезапуск веб-клиента';
    if (!window.confirm(`${title}? Страница ненадолго станет недоступна и перезагрузится автоматически.`)) return;
    const path = kind === 'update' ? '/api/system/web/update-self' : '/api/system/web/restart-self';
    try {
      const previousStartedAt = await readServerStartedAt();
      const { ok, data } = await sPost<{ message?: string; operationId?: string | null }>(path);
      if (ok) {
        window.location.assign(maintenanceWaitPath(
          kind,
          data?.operationId,
          previousStartedAt,
        ));
      } else flash('err', data?.message || 'Ошибка');
    } catch {
      flash('err', 'Не удалось связаться с веб-сервисом');
    }
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
    const branchByService = new Map((branches?.services || []).map((branch) => [branch.service, branch]));
    const availableCount = services.filter((service) => service.composeService && service.updateAvailable === true).length;
    const hasActiveJob = progress?.job.state === 'Queued' || progress?.job.state === 'Running';
    const hasConfiguredServices = services.some((service) => service.composeService);

    body = (
      <>
        <div className="sys-section-label">Доступ</div>
        <Field
          label="Регистрация новых аккаунтов"
          help="Влияет на все клиенты облака"
          end={
            <div style={{ display: 'inline-flex', alignItems: 'center', gap: 10 }}>
              {registrationBusy && <span className="spin" />}
              <Toggle on={registrationEnabled} onChange={toggleRegistration} disabled={registrationBusy} />
            </div>
          }
        >
          <span className={'pill-info ' + (registrationEnabled ? 'ok' : 'warn')}>
            {registrationEnabled ? 'Разрешена' : 'Запрещена'}
          </span>
        </Field>

        <hr className="divider" />
        {dockerErr && (
          <div className="sys-banner err">
            <Icon.x size={18} />
            <span>Docker недоступен: {dockerErr}</span>
          </div>
        )}
        {lastMaintenance && lastMaintenance.state.toLowerCase() === 'failed' && (
          <div className="sys-banner err sys-maintenance-error">
            <Icon.x size={18} />
            <span>
              Последняя операция web завершилась ошибкой: {lastMaintenance.message || 'выполнен откат'}
              {lastMaintenance.diagnostic && (
                <details className="svc-inline-details">
                  <summary>Техническая диагностика</summary>
                  <pre>{lastMaintenance.diagnostic}</pre>
                </details>
              )}
            </span>
          </div>
        )}

        <div className="svc-toolbar">
          <div>
            <div className="sys-section-label">Сервисы приложения</div>
            <div className="sys-note">PostgreSQL, MinIO, RabbitMQ, Seq и nginx не входят в массовое обновление.</div>
          </div>
          <div className="svc-toolbar-actions">
            <button className="btn primary" onClick={updateAvailable} disabled={!availableCount || !!dockerErr || !!hasActiveJob}>
              <Icon.download size={16} /> Обновить доступные ({availableCount})
            </button>
            <button className="btn" onClick={updateAll} disabled={!hasConfiguredServices || !!dockerErr || !!hasActiveJob}>
              <Icon.download size={16} /> Обновить все
            </button>
            <button className="btn" onClick={restartAll} disabled={!hasConfiguredServices || !!dockerErr || !!hasActiveJob}>
              <Icon.refresh size={16} /> Перезапустить все
            </button>
            <button className="btn" onClick={() => { setServices(null); void loadServices(); }} disabled={!!hasActiveJob}>
              <Icon.refresh size={16} /> Обновить статус
            </button>
          </div>
        </div>

        <div className="svc-table" role="table" aria-label="Сервисы BarkCloud">
          <div className="svc-head" role="row">
            <span>Сервис</span>
            <span>Состояние</span>
            <span>Канал</span>
            <span>Текущая</span>
            <span>Последняя</span>
            <span>Обновление</span>
            <span>Действия</span>
          </div>
          {services.map((service) => {
            const version = versionOf(service);
            const branch = branchByService.get(service.service);
            const configured = !!service.composeService;
            const containerUnavailable = service.state === 'unavailable';
            const rowBusy = !!busy[service.service] || !!busy[`branch:${service.service}`];
            const actionDisabled = !configured || !!dockerErr || !!hasActiveJob || rowBusy;
            // not_found остаётся управляемым: очередь создаст контейнер из Compose.
            const lifecycleDisabled = actionDisabled || containerUnavailable;
            const selectedBranch = branch?.branch || version.branch || 'master';
            return (
              <div key={service.service} className={'svc-row' + (service.isWeb ? ' svc-row-web' : '')} role="row">
                <div className="svc-cell svc-service" data-label="Сервис">
                  <div className="svc-main">
                    <div className="svc-ic"><Icon.server size={20} /></div>
                    <div className="svc-info">
                      <div className="svc-name">
                        {SVC_LABELS[service.service] || service.service}
                        {service.isWeb && <span className="svc-web-label">web</span>}
                      </div>
                      <div className="svc-img" title={service.image || service.container}>
                        {service.image || service.container}
                      </div>
                      {service.service === 'notification' && !system.emailEnabled && (
                        <div className="svc-note">Не используется — почта на сервере не настроена.</div>
                      )}
                    </div>
                  </div>
                </div>
                <div className="svc-cell" data-label="Состояние"><SvcStatus state={service.state} status={service.status} /></div>
                <div className="svc-cell svc-channel" data-label="Канал">
                  {branch ? (
                    <>
                      <select
                        value={selectedBranch}
                        disabled={actionDisabled}
                        onChange={(event) => { if (event.target.value !== selectedBranch) void changeBranch(service.service, event.target.value); }}
                        aria-label={`Канал ${SVC_LABELS[service.service] || service.service}`}
                      >
                        {branch.branches.map((item) => <option key={item} value={item}>{item}</option>)}
                      </select>
                      {branch.runningBranch && branch.runningBranch !== branch.branch && (
                        <small className="svc-channel-drift">запущен: {branch.runningBranch}</small>
                      )}
                    </>
                  ) : <span className="svc-muted">—</span>}
                </div>
                <div className="svc-cell svc-version" data-label="Текущая">
                  <span>{version.currentVersion || 'не определена'}</span>
                  {version.tag && version.tag !== version.currentVersion && <small>{version.tag}</small>}
                </div>
                <div className="svc-cell svc-version" data-label="Последняя">
                  <span>{version.latestVersion || '—'}</span>
                </div>
                <div className="svc-cell svc-update" data-label="Обновление"><VersionBadge service={service} /></div>
                <div className="svc-cell svc-actions" data-label="Действия">
                  {rowBusy ? <span className="spin" /> : (
                    <>
                      <button className="iconb" title="Обновить" disabled={actionDisabled} onClick={() => service.isWeb ? webSelf('update') : void svcAction(service.service, 'Update')}>
                        <Icon.download size={19} />
                      </button>
                      <button className="iconb" title="Перезапустить" disabled={lifecycleDisabled} onClick={() => service.isWeb ? webSelf('restart') : void svcAction(service.service, 'Restart')}>
                        <Icon.refresh size={19} />
                      </button>
                      {!service.isWeb && (service.state === 'running' ? (
                        <button className="iconb" title="Остановить" disabled={lifecycleDisabled} onClick={() => void svcAction(service.service, 'Stop')}>
                          <Icon.power size={19} />
                        </button>
                      ) : (
                        <button className="iconb" title="Запустить" disabled={lifecycleDisabled} onClick={() => void svcAction(service.service, 'Start')}>
                          <Icon.play size={19} />
                        </button>
                      ))}
                    </>
                  )}
                </div>
                {(version.error || service.versionError) && (
                  <details className="svc-error">
                    <summary>Техническая ошибка</summary>
                    <pre>{version.error || service.versionError}</pre>
                  </details>
                )}
              </div>
            );
          })}
        </div>

        <div className="sys-note svc-web-note">
          Web обновляется последним через detached helper. После запуска нового контейнера страница ожидания проверит новый процесс и вернёт вас в настройки; при сбое helper восстановит предыдущий контейнер.
        </div>
        <div className="svc-footer-actions">
          <button className="btn text" onClick={doLock}><Icon.lock size={16} /> Заблокировать</button>
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
            <span className="pill-info ok">{system.version} · {system.edition}</span>
          </div>
        </div>
        <div className="set-card-body">{body}</div>
      </div>

      {progress && (
        <div className="sys-scrim">
          <div className="sys-dialog">
            <h3>{progress.title}</h3>
            <div className="sub">
              {progress.job.state === 'Queued'
                ? 'Задача поставлена в очередь…'
                : progress.job.state === 'Running'
                ? 'Выполняется последовательно…'
                : progress.job.state === 'AwaitingReconnect'
                ? 'Новый web запущен, переподключаем страницу…'
                : progress.job.state === 'Completed'
                ? 'Готово'
                : 'Завершено с ошибкой'}
            </div>
            {(() => {
              const total = progress.job.steps.length;
              const done = progress.job.steps.filter((step) => step.state === 'Completed' || step.state === 'Failed' || step.state === 'Skipped').length;
              const width = total ? Math.round((done / total) * 100) : 0;
              return <div className="prog-bar"><span style={{ width: `${width}%` }} /></div>;
            })()}
            <div className="prog-list">
              {progress.job.steps.map((step) => {
                const stateClass = step.state === 'InProgress' ? 'current' : step.state === 'Completed' ? 'done' : step.state === 'Failed' ? 'error' : step.state === 'Skipped' ? 'skipped' : 'pending';
                return (
                  <div key={step.service} className={'prog-item ' + stateClass}>
                    <span className="pi">
                      {step.state === 'InProgress' ? <span className="spin" /> : step.state === 'Completed' ? <Icon.check size={18} /> : step.state === 'Failed' ? <Icon.x size={18} /> : step.state === 'Skipped' ? <Icon.chev size={16} /> : <Icon.clock size={16} />}
                    </span>
                    <span>
                      {SVC_LABELS[step.service] || step.service}
                      {step.message && <small className="prog-message">{step.message}</small>}
                      {step.rolledBack && <small className="prog-rollback">Выполнен откат</small>}
                      {step.diagnostic && <details className="prog-diagnostic"><summary>Диагностика</summary><pre>{step.diagnostic}</pre></details>}
                    </span>
                  </div>
                );
              })}
            </div>
            {progress.job.error && <div className="prog-error">{progress.job.error}</div>}
            {progress.job.diagnostic && <details className="prog-diagnostic prog-job-diagnostic"><summary>Техническая диагностика задачи</summary><pre>{progress.job.diagnostic}</pre></details>}
            {(progress.job.state === 'Completed' || progress.job.state === 'Failed') && (
              <div className="dlg-actions"><button className="btn" onClick={() => setProgress(null)}>Закрыть</button></div>
            )}
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

  const [keys, setKeys] = React.useState<WebAuthnKey[]>([]);
  const [keyBusy, setKeyBusy] = React.useState(false);
  const canWebAuthn = webauthnSupported();

  async function loadKeys() {
    const res = await sGet<{ keys: WebAuthnKey[] }>('/api/settings/security/webauthn');
    if (res.ok && res.data) setKeys(res.data.keys);
  }
  React.useEffect(() => {
    loadKeys();
  }, []);

  async function addKey() {
    setKeyBusy(true);
    try {
      const begin = await sPost<{ optionsJson: string; challengeId: string }>('/api/settings/security/webauthn/register/begin');
      if (!begin.ok || !begin.data) {
        flash('err', errMsg(begin));
        return;
      }
      const attestationJson = await webauthnRegister(begin.data.optionsJson);
      const name = (window.prompt('Название ключа', 'Ключ безопасности') || 'Ключ безопасности').trim();
      const complete = await sPost('/api/settings/security/webauthn/register/complete', {
        challengeId: begin.data.challengeId,
        attestation: JSON.parse(attestationJson),
        name,
      });
      if (complete.ok) {
        flash('ok', 'Ключ привязан');
        loadKeys();
      } else flash('err', errMsg(complete));
    } catch (e) {
      flash('err', e instanceof Error && e.name === 'NotAllowedError' ? 'Отменено' : 'Не удалось привязать ключ');
    } finally {
      setKeyBusy(false);
    }
  }

  async function removeKey(id: string) {
    const res = await sPost('/api/settings/security/webauthn/remove', { credentialId: id });
    if (res.ok) {
      flash('ok', 'Ключ удалён');
      loadKeys();
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
          <h3>Ключи безопасности</h3>
          <div className="sub">Вход по аппаратному ключу (FIDO2/WebAuthn) без пароля</div>
        </div>
        <div className="set-card-body">
          {keys.length === 0 && <div className="sub">Нет привязанных ключей</div>}
          {keys.map((k) => (
            <Field
              key={k.id}
              label={k.name}
              help={k.lastUsedAt ? 'использован ' + new Date(k.lastUsedAt).toLocaleDateString() : 'не использовался'}
              end={
                <button className="btn text" onClick={() => removeKey(k.id)} disabled={keyBusy}>
                  Удалить
                </button>
              }
            />
          ))}
          {canWebAuthn ? (
            <button className="btn" onClick={addKey} disabled={keyBusy} style={{ marginTop: 8 }}>
              {keyBusy ? <span className="spin" /> : <Icon.key size={16} />} Добавить ключ
            </button>
          ) : (
            <div className="sub">Браузер не поддерживает ключи безопасности</div>
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
