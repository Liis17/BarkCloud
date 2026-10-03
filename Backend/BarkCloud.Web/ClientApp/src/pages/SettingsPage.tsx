import React from 'react';
import { Icon } from '../components/Icon';
import { Loading } from '../components/ui/EmptyState';
import { StorageStatsStatus } from '../components/ui/StorageStatsStatus';
import { useApiResource } from '../hooks/useApiResource';
import { usePageHeader } from '../hooks/usePageHeader';
import { plural } from '../lib/format';
import { maintenanceWaitPath } from '../lib/maintenance';
import { applyTheme, getTheme, type Theme } from '../lib/theme';
import { webauthnRegister, webauthnSupported } from '../lib/webauthn';
import { useConfirm, usePrompt } from '../hooks/useDialog';
import { Switch as Toggle } from '../components/ui/Switch';
import type { Privacy, Session, SettingsState } from '../lib/types';

const ServerSettingsTab = React.lazy(() => import('./ServerSettingsTab'));

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
async function apiJson<T = unknown>(method: string, path: string, body?: unknown, signal?: AbortSignal): Promise<ApiResp<T>> {
  const r = await fetch(path, {
    method, signal,
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
  if (r.status === 401) window.location.href = "/login";
  return { ok: r.ok, status: r.status, data };
}
const sGet = <T,>(p: string, signal?: AbortSignal) => apiJson<T>('GET', p, undefined, signal);
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

function Field({ label, help, children, end }: { label: React.ReactNode; help?: React.ReactNode; children?: React.ReactNode; end?: React.ReactNode }) {
  const action = React.isValidElement<{ label?: string }>(end) && end.type === Toggle && typeof label === 'string'
    ? React.cloneElement(end, { label }) : end;
  return (
    <div className="field-row" data-setting-label={typeof label === "string" ? label : undefined}>
      <div className="lbl">
        {label}
        {help && <span className="help">{help}</span>}
      </div>
      <div>{children}</div>
      <div className="right-end">{action}</div>
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
  if (version.state === 'checking') return <span className="pill-info"><span className="spin" /> Проверяем…</span>;
  return <span className="pill-info warn"><Icon.info size={12} /> Версия не определена</span>;
}

interface ProgressState {
  title: string;
  job: DeploymentJob;
  autoClose: boolean;
}

function SystemSection({ admin, system, onUnlockedChange, active }: {
  admin: SettingsState['admin'];
  system: SettingsState['system'];
  onUnlockedChange?: (value: boolean) => void;
  active: boolean;
}) {
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
  const [confirmNode, confirm] = useConfirm();

  const flash = (kind: 'ok' | 'err', msg: string) => {
    setToast({ kind, msg });
    setTimeout(() => setToast(null), 4200);
  };

  const servicesRequest = React.useRef<AbortController | null>(null);
  const [versionsLoading, setVersionsLoading] = React.useState(false);
  React.useEffect(() => () => servicesRequest.current?.abort(), []);
  React.useEffect(() => { setUnlocked(admin.unlocked); }, [admin.unlocked]);

  const loadServices = React.useCallback(async () => {
    servicesRequest.current?.abort();
    const controller = new AbortController();
    servicesRequest.current = controller;
    setVersionsLoading(true);
    let statusesLoaded = false;
    const expired = () => { setUnlocked(false); onUnlockedChange?.(false); };
    const branchesTask = sGet<BranchSnap>('/api/system/branches', controller.signal)
      .then((response) => {
        if (controller.signal.aborted) return;
        if (response.status === 403) { expired(); return; }
        setBranches(response.ok ? response.data : null);
      }).catch(() => { if (!controller.signal.aborted) setBranches(null); });
    try {
      const serviceRes = await sGet<ServicesSnap>('/api/system/services?includeVersions=false', controller.signal);
      if (controller.signal.aborted) return;
      if (serviceRes.status === 403) { expired(); return; }
      if (!serviceRes.ok || !serviceRes.data) {
        setServices([]); setDockerErr(errMsg(serviceRes)); return;
      }
      setServices((serviceRes.data.services || []).map((service) => ({ ...service,
        version: { ...versionOf(service), state: service.state === 'unavailable' ? 'unknown' : 'checking' } })));
      setDockerErr(serviceRes.data.dockerOk ? null : serviceRes.data.error || 'Docker недоступен');
      setLastMaintenance(serviceRes.data.lastMaintenance || null);
      statusesLoaded = true;
      if (!serviceRes.data.dockerOk) return;
      const versions = await sGet<ServicesSnap>('/api/system/services', controller.signal);
      if (controller.signal.aborted) return;
      if (versions.status === 403) { expired(); return; }
      if (versions.ok && versions.data) {
        const byService = new Map(versions.data.services.map((service) => [service.service, versionOf(service)]));
        setServices((current) => current?.map((service) => ({ ...service, version: byService.get(service.service) || versionOf(service) })) || []);
      } else {
        setServices((current) => current?.map((service) => ({ ...service, version: { ...versionOf(service),
          state: 'registry_unavailable', error: errMsg(versions, 'Не удалось проверить версии') } })) || []);
      }
    } catch (error) {
      if (!controller.signal.aborted) {
        if (statusesLoaded) {
          setServices((current) => current?.map((service) => ({ ...service, version: { ...versionOf(service),
            state: 'registry_unavailable', error: 'Не удалось проверить версии' } })) || []);
        } else {
          setServices([]);
          setDockerErr(error instanceof Error ? error.message : 'Не удалось получить состояние контейнеров');
        }
      }
    } finally {
      if (!controller.signal.aborted) setVersionsLoading(false);
      await branchesTask;
    }
  }, [onUnlockedChange]);

  React.useEffect(() => {
    if (unlocked && active) {
      setServices(null);
      void loadServices();
    }
    return () => servicesRequest.current?.abort();
  }, [unlocked, active, loadServices]);

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
      onUnlockedChange?.(true);
    } else setUnlockErr(data?.message || 'Не удалось разблокировать');
  }

  async function doLock() {
    await sPost('/api/system/lock');
    setUnlocked(false);
    onUnlockedChange?.(false);
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
    if (!(await confirm({ title: 'Обновить все сервисы?', message: 'Web будет обновлён последним, после чего страница переподключится.', confirmLabel: 'Обновить' }))) return;
    await runQueuedAction('Обновление всех сервисов', '/api/system/update-all', 'Update', true);
  }

  async function updateAvailable() {
    const count = (services || []).filter((service) => service.composeService && versionOf(service).updateAvailable === true).length;
    if (!count || dockerErr) return;
    if (!(await confirm({ title: `Обновить доступные сервисы (${count})?`, message: 'Web, если для него доступно обновление, будет последним.', confirmLabel: 'Обновить' }))) return;
    await runQueuedAction(`Обновление доступных сервисов (${count})`, '/api/system/update-available', 'Update', true);
  }

  async function restartAll() {
    if (!services?.length || dockerErr) return;
    if (!(await confirm({ title: 'Перезапустить все сервисы?', message: 'Web будет последним, затем страница переподключится.', confirmLabel: 'Перезапустить' }))) return;
    await runQueuedAction('Перезапуск всех сервисов', '/api/system/restart-all', 'Restart');
  }

  async function webSelf(kind: 'update' | 'restart') {
    const title = kind === 'update' ? 'Обновление веб-клиента' : 'Перезапуск веб-клиента';
    if (!(await confirm({ title: `${title}?`, message: 'Страница ненадолго станет недоступна и перезагрузится автоматически.', confirmLabel: kind === 'update' ? 'Обновить' : 'Перезапустить' }))) return;
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
    const availableCount = services.filter((service) => service.composeService && versionOf(service).updateAvailable === true).length;
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
              <Toggle label="Регистрация новых аккаунтов" on={registrationEnabled} onChange={toggleRegistration} disabled={registrationBusy} />
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
            <span role="columnheader">Сервис</span>
            <span role="columnheader">Состояние</span>
            <span role="columnheader">Канал</span>
            <span role="columnheader">Текущая</span>
            <span role="columnheader">Доступная</span>
            <span role="columnheader">Действия</span>
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
                <div role="cell" className="svc-cell svc-service" data-label="Сервис">
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
                <div role="cell" className="svc-cell" data-label="Состояние"><SvcStatus state={service.state} status={service.status} /></div>
                <div role="cell" className="svc-cell svc-channel" data-label="Канал">
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
                <div role="cell" className="svc-cell svc-version" data-label="Текущая">
                  <span>{version.currentVersion || 'не определена'}</span>
                  {version.tag && version.tag !== version.currentVersion && <small>{version.tag}</small>}
                </div>
                <div role="cell" className="svc-cell svc-version svc-latest" data-label="Доступная">
                  <span>{version.latestVersion || '—'}</span>
                  <VersionBadge service={service} />
                </div>
                <div role="cell" className="svc-cell svc-actions" data-label="Действия">
                  {rowBusy ? <span className="spin" /> : (
                    <>
                      <button className="iconb" title="Обновить" aria-label={`Обновить ${service.service}`} disabled={actionDisabled || versionsLoading} onClick={() => service.isWeb ? webSelf('update') : void svcAction(service.service, 'Update')}>
                        <Icon.download size={19} />
                      </button>
                      <button className="iconb" title="Перезапустить" aria-label={`Перезапустить ${service.service}`} disabled={lifecycleDisabled} onClick={() => service.isWeb ? webSelf('restart') : void svcAction(service.service, 'Restart')}>
                        <Icon.refresh size={19} />
                      </button>
                      {!service.isWeb && <details className="svc-more"><summary className="iconb" aria-label={`Другие действия ${service.service}`}>⋯</summary><div className="svc-more-menu">{service.state === 'running' ? (
                        <button className="iconb" title="Остановить" disabled={lifecycleDisabled} onClick={() => void svcAction(service.service, 'Stop')}>
                          <Icon.power size={19} /> Остановить
                        </button>
                      ) : (
                        <button className="iconb" title="Запустить" disabled={lifecycleDisabled} onClick={() => void svcAction(service.service, 'Start')}>
                          <Icon.play size={19} /> Запустить
                        </button>
                      )}</div></details>}
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
      {confirmNode}
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
  const [avatarUrl, setAvatarUrl] = React.useState(profile.avatarUrl || profile.avatarPreviewUrl || '');
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
      setAvatarUrl(data.avatarUrl || data.avatarPreviewUrl || '');
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
          <div className="avatar-edit" style={{ display: 'flex', gap: 16, alignItems: 'center' }}>
            <div className="avatar-big">{avatarUrl ? <img src={avatarUrl} alt="" /> : profile.initials}</div>
            <div style={{ display: 'flex', flexDirection: 'column', gap: 8, flex: 1 }}>
              <div style={{ fontSize: 14, color: 'var(--md-on-surface-variant)' }}>Изменить аватар. Рекомендуется не меньше 256×256.</div>
              <div style={{ display: 'flex', gap: 8, alignItems: 'center', flexWrap: 'wrap' }}>
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
  const [promptNode, prompt] = usePrompt();
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
  // Повторное подтверждение паролем: настройка Authenticator и отключение email-2FA.
  const [reauthDlg, setReauthDlg] = React.useState<'enableAuth' | 'disableEmail' | null>(null);
  const [reauthPwd, setReauthPwd] = React.useState('');
  const [reauthBusy, setReauthBusy] = React.useState(false);

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
  async function submitReauth() {
    if (!reauthDlg) return;
    setReauthBusy(true);
    if (reauthDlg === 'enableAuth') {
      const res = await sPost<{ qr: string; code: string }>('/api/settings/security/2fa/enable', { otpType: 1, password: reauthPwd });
      if (res.ok && res.data) {
        setReauthDlg(null);
        setEnableCode('');
        setEnableDlg({ otpType: 1, qr: res.data.qr, code: res.data.code });
      } else flash('err', errMsg(res));
    } else {
      const res = await sPost('/api/settings/security/2fa/disable', { otpType: 2, otpCode: '', password: reauthPwd });
      if (res.ok) {
        setReauthDlg(null);
        flash('ok', 'Email-2FA отключена');
        refresh2fa();
      } else flash('err', errMsg(res));
    }
    setReauthBusy(false);
  }
  async function startEnable(otpType: number) {
    if (otpType === 1) {
      setReauthPwd('');
      setReauthDlg('enableAuth');
      return;
    }
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
      setReauthPwd('');
      setReauthDlg('disableEmail');
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
      const name = (await prompt({ title: 'Название ключа', label: 'Название', initial: 'Ключ безопасности' })) || 'Ключ безопасности';
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
      {promptNode}
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

      {reauthDlg && (
        <div className="sys-scrim" onClick={() => !reauthBusy && setReauthDlg(null)}>
          <div className="sys-dialog" onClick={(e) => e.stopPropagation()}>
            <h3>{reauthDlg === 'enableAuth' ? 'Настройка аутентификатора' : 'Отключить email-2FA'}</h3>
            <div className="sub">Введите текущий пароль, чтобы подтвердить изменение двухфакторной защиты.</div>
            <input className="dlg-input" type="password" value={reauthPwd} onChange={(e) => setReauthPwd(e.target.value)} placeholder="Пароль" autoFocus onKeyDown={(e) => { if (e.key === 'Enter' && reauthPwd) submitReauth(); }} />
            <div className="dlg-actions two">
              <button className="btn text" onClick={() => setReauthDlg(null)} disabled={reauthBusy}>
                Отмена
              </button>
              <button className="btn primary" onClick={submitReauth} disabled={reauthBusy || !reauthPwd}>
                {reauthBusy ? <span className="spin" /> : null} Продолжить
              </button>
            </div>
          </div>
        </div>
      )}

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
const DISK_S3_COLOR = 'var(--md-primary)';

function StorageTab({ storage }: { storage: SettingsState['storage'] }) {
  const disk = storage.disk;
  const diskAvailable = disk.state !== 'loading' && !(disk.state === 'error' && !disk.updatedAt);
  const s3 = storage.s3;
  return <div className="settings-storage-grid">
    <div className="set-card" data-setting-label="Личное хранилище">
      <div className="set-card-head"><h3>Личное хранилище</h3><div className="sub">Ваши файлы и лимит аккаунта</div></div>
      <div className="set-card-body">
        <div className="storage-total">{storage.used} {storage.unit}<span>{storage.total > 0 ? `из ${storage.total} ${storage.unit}` : 'безлимит'}</span></div>
        {storage.total > 0 && <progress max="100" value={storage.percent} aria-label="Использование личного хранилища" />}
        {storage.breakdown.map((item) => <div className="storage-breakdown-row" key={item.k}><span>{item.k}</span><strong>{item.v}</strong></div>)}
        {storage.total > 0 && <small>Свободно {storage.freeLabel}</small>}
      </div>
    </div>
    <div className="set-card" data-setting-label="Диск сервера">
      <div className="set-card-head"><h3>Диск сервера</h3><div className="sub">{diskAvailable ? `${disk.usedLabel} из ${disk.totalLabel}` : disk.state === 'error' ? 'Ошибка подсчёта' : 'Подсчитываем…'}</div></div>
      <div className="set-card-body">
        {diskAvailable && <><div className="stor-bar"><span style={{ width: `${disk.otherPct}%`, background: DISK_OTHER_COLOR }} /><span style={{ width: `${disk.s3Pct}%`, background: DISK_S3_COLOR }} /></div>
          <div className="stor-legend"><div className="item"><span className="sw" style={{ background: DISK_OTHER_COLOR }} /><span className="k">Другие данные</span><span className="v">{disk.otherLabel}</span></div>
          <div className="item"><span className="sw" style={{ background: DISK_S3_COLOR }} /><span className="k">Локальный S3</span><span className="v">{disk.s3Label}</span></div>
          <div className="item"><span className="k">Свободно</span><span className="v">{disk.freeLabel}</span></div></div></>}
        <StorageStatsStatus state={disk.state} updatedAt={disk.updatedAt} />
      </div>
    </div>
    {s3 && <div className="set-card" data-setting-label="S3-бакеты">
      <div className="set-card-head"><h3>S3-бакеты</h3><div className="sub">Все активные уникальные бакеты</div></div>
      <div className="set-card-body">
        {s3.allS3StatsAvailable && <><div className="storage-total">{s3.allS3UsedLabel}<span>{s3.allS3HasFiniteQuota ? `из ${s3.allS3QuotaLabel}` : 'безлимит'}</span></div>
          {s3.allS3HasFiniteQuota && <progress max="100" value={s3.allS3Percent} aria-label="Использование S3-бакетов" />}</>}
        <StorageStatsStatus state={s3.allS3State} updatedAt={s3.allS3UpdatedAt} />
      </div>
    </div>}
  </div>;
}

// ─────────── Устройства и сессии ───────────

function SessionsTab({ sessions: initial, flash }: { sessions: Session[]; flash: Flash }) {
  const [sessions, setSessions] = React.useState<Session[]>(initial);
  const [busy, setBusy] = React.useState<Record<string, boolean>>({});
  const [renaming, setRenaming] = React.useState<string | null>(null);
  const [renameVal, setRenameVal] = React.useState('');
  const [revokingAll, setRevokingAll] = React.useState(false);
  const [confirmNode, confirm] = useConfirm();

  async function revoke(s: Session) {
    if (!(await confirm({ title: 'Завершить сессию?', message: `Устройство «${s.device}» будет отключено.`, confirmLabel: 'Отключить', danger: true }))) return;
    setBusy((b) => ({ ...b, [s.deviceId]: true }));
    const res = await sPost('/api/settings/sessions/revoke', { deviceId: s.deviceId });
    setBusy((b) => ({ ...b, [s.deviceId]: false }));
    if (res.ok) {
      setSessions((prev) => prev.filter((x) => x.deviceId !== s.deviceId));
      flash('ok', 'Сессия завершена');
    } else flash('err', errMsg(res));
  }
  async function revokeOthers() {
    if (!(await confirm({ title: 'Завершить все сессии?', message: 'Все устройства, кроме этого, будут отключены.', confirmLabel: 'Завершить', danger: true }))) return;
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
      {confirmNode}
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
            <button className={'theme-swatch light' + (theme === 'light' ? ' on' : '')} onClick={() => pick('light')} aria-label="Светлая" aria-pressed={theme === 'light'} />
            <button className={'theme-swatch dark' + (theme === 'dark' ? ' on' : '')} onClick={() => pick('dark')} aria-label="Тёмная" aria-pressed={theme === 'dark'} />
            <button className={'theme-swatch auto' + (theme === 'auto' ? ' on' : '')} onClick={() => pick('auto')} aria-label="Как в системе" aria-pressed={theme === 'auto'} />
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

const SECTIONS: NavItem[] = [
  { key: 'account', label: 'Аккаунт', icon: 'user' },
  { key: 'security', label: 'Безопасность', icon: 'lock' },
  { key: 'privacy', label: 'Приватность', icon: 'eye' },
  { key: 'storage', label: 'Хранилище', icon: 'server' },
  { key: 'sessions', label: 'Устройства и сессии', icon: 'device' },
  { key: 'appearance', label: 'Внешний вид', icon: 'palette' },
  { key: 'system', label: 'Обслуживание', icon: 'server' },
  { key: 'server-settings', label: 'Настройки сервера', icon: 'settings' },
];
const SEARCH_ITEMS = [
  ['account', 'Профиль', 'Аватар, имя и фамилия'], ['account', 'Email', 'Адрес для входа'],
  ['account', 'Имя пользователя', 'Username'], ['account', 'О себе', 'Описание профиля'],
  ['account', 'Опасная зона', 'Удаление аккаунта'], ['security', 'Пароль', 'Смена пароля'],
  ['security', 'Ключи безопасности', 'WebAuthn, FIDO2, YubiKey'],
  ['security', 'Двухфакторная аутентификация', '2FA, TOTP, коды по email'],
  ['privacy', 'Профиль', 'Видимость профиля'], ['privacy', 'Email', 'Видимость почты'],
  ['privacy', 'Был в сети', 'Время последнего посещения'],
  ['privacy', 'Поиск по имени пользователя', 'Доступность в поиске'],
  ['storage', 'Личное хранилище', 'Использование и лимит аккаунта'],
  ['storage', 'Диск сервера', 'Свободное место, локальный S3'],
  ['storage', 'S3-бакеты', 'Размер объектов и квота'],
  ['sessions', 'Устройства и сессии', 'Активные устройства, выход'],
  ['appearance', 'Тема', 'Светлая, тёмная, системная'],
  ['system', 'Обслуживание', 'Контейнеры, обновление, перезапуск, каналы'],
  ['server-settings', 'Параметры сервисов', 'Серверные конфигурации'],
  ['server-settings', 'S3-профили', 'Endpoint, bucket, credentials, квоты'],
  ['server-settings', 'Зарезервированные имена', 'Имена пользователей'],
];
const SECTION_PATHS: Record<string, string> = {
  account: '/api/settings/profile', security: '/api/settings/security/2fa', privacy: '/api/settings/privacy',
  storage: '/api/settings/storage', sessions: '/api/settings/sessions', system: '/api/settings/system',
};
const settingsStoragePending = (data: unknown) => {
  const storage = data as SettingsState['storage'];
  return ['loading', 'refreshing'].includes(storage.disk.state || '') || ['loading', 'refreshing'].includes(storage.s3?.allS3State || '');
};

function SettingsPanel({ section, active, admin, flash, onUnlockedChange }: {
  section: string; active: boolean; admin: SettingsState['admin']; flash: Flash; onUnlockedChange: (value: boolean) => void;
}) {
  const resource = useApiResource<unknown>(SECTION_PATHS[section] || null, active,
    section === 'storage' ? settingsStoragePending : undefined);
  if (section === 'appearance') return <AppearanceTab />;
  if (section === 'server-settings') return <React.Suspense fallback={<Loading label="Загрузка редактора…" />}>
    <ServerSettingsTab active={active} onAccessExpired={() => onUnlockedChange(false)} />
  </React.Suspense>;
  let content: React.ReactNode = null;
  if (resource.data) switch (section) {
    case 'account': content = <AccountTab profile={resource.data as SettingsState['profile']} flash={flash} />; break;
    case 'security': {
      const data = resource.data as { authenticator: boolean; email: boolean };
      content = <SecurityTab security={{ authenticator: data.authenticator, emailOtp: data.email, twoFa: data.authenticator || data.email }} flash={flash} />;
      break;
    }
    case 'privacy': content = <PrivacyTab privacy={resource.data as Privacy} flash={flash} />; break;
    case 'storage': content = <StorageTab storage={resource.data as SettingsState['storage']} />; break;
    case 'sessions': content = <SessionsTab sessions={(resource.data as { sessions: Session[] }).sessions} flash={flash} />; break;
    case 'system': content = <SystemSection admin={admin} system={resource.data as SettingsState['system']} active={active} onUnlockedChange={onUnlockedChange} />; break;
  }
  return <>
    {resource.error && <div className="sys-banner err" role="alert">{resource.error.message}<button className="btn text" onClick={() => void resource.reload()}>Повторить</button></div>}
    {!resource.data && !resource.error ? <Loading label="Загрузка раздела…" /> : content}
  </>;
}

export function SettingsPage() {
  const context = useApiResource<{ admin: SettingsState['admin'] }>('/api/settings/context');
  const [adminOverride, setAdminOverride] = React.useState<boolean | null>(null);
  const admin = { enabled: context.data?.admin.enabled || false, unlocked: adminOverride ?? context.data?.admin.unlocked ?? false };
  React.useEffect(() => { if (context.data) setAdminOverride(context.data.admin.unlocked); }, [context.data]);
  const [toast, setToast] = React.useState<{ kind: 'ok' | 'err'; msg: string } | null>(null);
  const flash = React.useCallback<Flash>((kind, msg) => { setToast({ kind, msg }); }, []);
  React.useEffect(() => {
    if (!toast) return;
    const timer = window.setTimeout(() => setToast(null), 4200);
    return () => window.clearTimeout(timer);
  }, [toast]);
  const onUnlockedChange = React.useCallback((value: boolean) => setAdminOverride(value), []);
  const [section, setSection] = React.useState(() => window.location.hash.slice(1) || 'account');
  const active = SECTIONS.some((item) => item.key === section) ? section : 'account';
  const [visited, setVisited] = React.useState<string[]>([active]);
  const panels = visited.includes(active) ? visited : [...visited, active];
  React.useEffect(() => { setVisited((current) => current.includes(active) ? current : [...current, active]); }, [active]);
  React.useEffect(() => {
    const onHash = () => setSection(window.location.hash.slice(1) || 'account');
    window.addEventListener('hashchange', onHash);
    return () => window.removeEventListener('hashchange', onHash);
  }, []);
  const go = (key: string) => { setSection(key); window.location.hash = key; };
  const nav = SECTIONS.filter((item) => item.key === 'system' ? admin.enabled : item.key === 'server-settings' ? admin.enabled && admin.unlocked : true);
  const activeLabel = SECTIONS.find((item) => item.key === active)!.label;
  const [search, setSearch] = React.useState('');
  const [target, setTarget] = React.useState<string | null>(null);
  const contentRef = React.useRef<HTMLDivElement>(null);
  const query = search.trim().toLocaleLowerCase('ru-RU');
  const results = SEARCH_ITEMS.filter(([key, label, help]) => nav.some((item) => item.key === key)
    && `${label} ${help} ${SECTIONS.find((item) => item.key === key)?.label}`.toLocaleLowerCase('ru-RU').includes(query));
  React.useEffect(() => {
    if (!target || !contentRef.current) return;
    const container = contentRef.current;
    let timer: number | undefined;
    const find = () => {
      const element = Array.from(container.querySelectorAll<HTMLElement>('[data-setting-label], h3, .ttl'))
        .find((item) => !item.closest('[hidden]') && (item.dataset.settingLabel || item.textContent?.trim()) === target);
      if (!element) return;
      element.tabIndex = -1;
      element.focus({ preventScroll: true });
      element.scrollIntoView?.({ block: 'nearest' });
      element.classList.add('settings-search-hit');
      timer = window.setTimeout(() => { element.classList.remove('settings-search-hit'); setTarget(null); }, 1800);
      observer.disconnect();
    };
    const observer = new MutationObserver(find);
    observer.observe(container, { childList: true, subtree: true });
    find();
    return () => { observer.disconnect(); window.clearTimeout(timer); };
  }, [target, active]);
  usePageHeader(() => ({ title: 'Настройки', documentTitle: `Настройки: ${activeLabel}`, search: false,
    kicker: <><span>Прочее</span><span className="sep">/</span><span className="cur">Настройки</span></>,
    contentClass: 'settings-content' }), [activeLabel]);

  return <>
    <div className="settings-shell">
      <div className="set-nav">
        <div className="settings-search">
          <label htmlFor="settings-search">Поиск по настройкам</label>
          <div className="settings-search-input"><Icon.search size={18} /><input id="settings-search" type="search" value={search}
            placeholder="Название пункта…" onChange={(event) => setSearch(event.target.value)} />
            {search && <button className="icon-btn" aria-label="Очистить поиск настроек" onClick={() => setSearch('')}><Icon.x size={16} /></button>}</div>
          {query && <div className="settings-search-results" aria-label="Результаты поиска настроек">
            {results.length === 0 ? <div className="server-empty">Ничего не найдено</div> : results.map(([key, label, help]) =>
              <button key={`${key}:${label}`} onClick={() => { go(key); setTarget(label); setSearch(''); }}><strong>{label}</strong><small>{SECTIONS.find((item) => item.key === key)?.label} · {help}</small></button>)}
          </div>}
        </div>
        <label className="settings-mobile-nav">Раздел<select aria-label="Раздел настроек" value={active} onChange={(event) => go(event.target.value)}>
          {!nav.some((item) => item.key === active) && <option value={active}>{activeLabel}</option>}
          {nav.map((item) => <option key={item.key} value={item.key}>{item.label}</option>)}
        </select></label>
        <nav className="settings-desktop-nav" aria-label="Разделы настроек">{nav.map((item) => {
          const Ic = Icon[item.icon];
          return <button key={item.key} className={active === item.key ? 'on' : ''} aria-current={active === item.key ? 'page' : undefined} onClick={() => go(item.key)}><Ic size={20} />{item.label}</button>;
        })}</nav>
        {context.error && <div className="settings-context-error" role="alert">Не удалось проверить доступ <button className="btn text" onClick={() => void context.reload()}>Повторить</button></div>}
      </div>
      <div className={'set-content' + (['system', 'server-settings', 'storage'].includes(active) ? ' wide' : '')} ref={contentRef}>
        {panels.map((key) => {
          const isAdmin = ['system', 'server-settings'].includes(key);
          if (isAdmin && !context.data) return key === active ? <Loading key={key} label="Проверяем доступ…" /> : null;
          if (isAdmin && !admin.enabled) return key === active ? <div key={key} className="server-empty">Администрирование не настроено</div> : null;
          if (key === 'server-settings' && !admin.unlocked && key !== active) return null;
          return <section key={key} hidden={key !== active} aria-label={SECTIONS.find((item) => item.key === key)?.label}>
            <SettingsPanel section={key === 'server-settings' && !admin.unlocked ? 'system' : key} active={key === active}
              admin={admin} flash={flash} onUnlockedChange={onUnlockedChange} />
          </section>;
        })}
      </div>
    </div>
    <Toast toast={toast} />
  </>;
}
