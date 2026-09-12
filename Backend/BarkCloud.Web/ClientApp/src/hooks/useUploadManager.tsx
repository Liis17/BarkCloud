import React from 'react';
import { ApiError, apiPost, checkDuplicateHash, pickFiles } from '../lib/api';
import { hashFile } from '../lib/fileHasher';
import {
  cancelUploadSession,
  completeUploadSession,
  createUploadSession,
  getUploadSession,
  resumeUploadSession,
  uploadMissingParts,
  UploadSessionTerminalError,
  waitForUploadReady,
  type UploadSession,
} from '../lib/uploadSessions';
import {
  deleteUploadTask,
  loadUploadTasks,
  saveUploadTask,
  type PersistedUploadTask,
} from '../lib/uploadQueueStore';
import type { DuplicateLocation } from '../lib/api';
import type { DuplicateDecision } from './useDuplicatePrompt';

const MAX_CONCURRENT = 4;
const FILE_ALREADY_ATTACHED_CODE = 'F1A2B3C4-5D6E-47F8-9A0B-1C2D3E4F5A6B';

export interface AttachOptions {
  dir?: string;
  routeByMediaKind?: boolean;
}

export type TaskStatus =
  | 'hashing'
  | 'checking'
  | 'needs_file'
  | 'uploading'
  | 'processing'
  | 'attaching'
  | 'uploaded_not_attached'
  | 'done'
  | 'failed'
  | 'skipped';

export interface UploadTask {
  id: string;
  idempotencyKey: string;
  file: File | null;
  fileName: string;
  fileSize: number;
  fileType: string;
  lastModified: number;
  sha256: string | null;
  sessionId: string | null;
  fileId: string | null;
  status: TaskStatus;
  progress: number;
  error: string | null;
  attachOptions: AttachOptions;
  batchId: string;
  createdAt: number;
  updatedAt: number;
  startedAt: number | null;
  eta: number | null;
  abortCtrl: AbortController | null;
  hashValidated: boolean;
}

export interface UploadSummary {
  total: number;
  done: number;
  skipped: number;
  error: number;
  active: number;
  overallProgress: number;
  eta: number | null;
}

interface DupPromptReq {
  fileName: string;
  locations: DuplicateLocation[];
  resolve: (d: DuplicateDecision) => void;
}

interface ActionsValue {
  enqueue: (files: File[], attachOptions: AttachOptions) => void;
  attachVersion: number;
}

interface StateValue {
  tasks: UploadTask[];
  summary: UploadSummary;
  hasActive: boolean;
  dupPrompt: DupPromptReq | null;
  retry: (id: string) => void;
  reselect: (id: string) => void;
  dismiss: (id: string) => void;
  clearCompleted: () => void;
  cancel: (id: string) => void;
  answerDuplicate: (d: DuplicateDecision) => void;
}

const ActionsCtx = React.createContext<ActionsValue | null>(null);
const StateCtx = React.createContext<StateValue | null>(null);

export function UploadManagerProvider({ children }: { children: React.ReactNode }) {
  const tasksRef = React.useRef<UploadTask[]>([]);
  const runningIdsRef = React.useRef(new Set<string>());
  const [rev, setRev] = React.useState(0);
  const [attachVersion, setAttachVersion] = React.useState(0);
  const [dupPrompt, setDupPrompt] = React.useState<DupPromptReq | null>(null);
  const dupQueueRef = React.useRef<DupPromptReq[]>([]);
  const batchDecisionsRef = React.useRef<Map<string, DuplicateDecision>>(new Map());
  const tryStartMoreRef = React.useRef<() => void>(() => undefined);

  const bump = React.useCallback(() => setRev((value) => value + 1), []);
  const touch = React.useCallback((task: UploadTask, persist = true) => {
    task.updatedAt = Math.max(Date.now(), task.updatedAt + 1);
    bump();
    if (persist) void saveUploadTask(toPersisted(task)).catch(() => undefined);
  }, [bump]);
  const touchRef = React.useRef(touch);
  touchRef.current = touch;

  const showNextDup = React.useCallback(() => {
    if (dupQueueRef.current.length > 0) setDupPrompt(dupQueueRef.current.shift()!);
  }, []);

  const answerDuplicate = React.useCallback((decision: DuplicateDecision) => {
    setDupPrompt((current) => {
      current?.resolve(decision);
      return null;
    });
  }, []);

  React.useEffect(() => {
    if (!dupPrompt) showNextDup();
  }, [dupPrompt, showNextDup]);

  const askDuplicateRef = React.useRef(
    (_fileName: string, _locations: DuplicateLocation[]): Promise<DuplicateDecision> => Promise.resolve('upload'),
  );
  askDuplicateRef.current = (fileName, locations) => new Promise((resolve) => {
    const request: DupPromptReq = { fileName, locations, resolve };
    setDupPrompt((current) => {
      if (current) {
        dupQueueRef.current.push(request);
        return current;
      }
      return request;
    });
  });

  const attachTask = React.useCallback(async (
    task: UploadTask,
    isUploadRetry = false,
  ): Promise<void> => {
    if (!task.fileId) throw new Error('Сервер не вернул идентификатор файла');
    task.status = 'attaching';
    task.progress = 1;
    task.error = null;
    touchRef.current(task);
    try {
      const body: Record<string, unknown> = { fileId: task.fileId, name: task.fileName };
      if (task.attachOptions.dir) body.dir = task.attachOptions.dir;
      if (task.attachOptions.routeByMediaKind) body.routeByMediaKind = true;
      body.uploadSessionId = task.sessionId;
      body.isUploadRetry = isUploadRetry;
      await apiPost('/api/cloud/attach', body);
    } catch (error) {
      if (!(error instanceof ApiError)
          || error.code?.toUpperCase() !== FILE_ALREADY_ATTACHED_CODE) {
        task.status = 'uploaded_not_attached';
        task.error = (error as Error).message || 'Не удалось привязать файл';
        touchRef.current(task);
        return;
      }
    }

    task.status = 'done';
    task.progress = 1;
    task.error = null;
    touchRef.current(task);
    setAttachVersion((value) => value + 1);
  }, []);

  const createSession = React.useCallback(async (task: UploadTask): Promise<UploadSession> => {
    if (!task.sha256) throw new Error('SHA-256 файла не рассчитан');
    const session = await createUploadSession({
      idempotencyKey: task.idempotencyKey,
      fileName: task.fileName,
      fileSize: task.fileSize,
      contentType: task.fileType || 'application/octet-stream',
      sha256: task.sha256,
    });
    task.sessionId = session.sessionId;
    task.fileId = session.fileId;
    touchRef.current(task);
    return session;
  }, []);

  const processTask = React.useCallback(async (task: UploadTask) => {
    const controller = new AbortController();
    task.abortCtrl = controller;
    task.startedAt ??= Date.now();

    try {
      if (task.status === 'attaching' || task.status === 'uploaded_not_attached') {
        await attachTask(task, true);
        return;
      }

      if (task.status === 'processing' && task.sessionId) {
        const current = await getUploadSession(task.sessionId);
        if (current.status === 'uploading') {
          task.status = 'needs_file';
          task.error = 'Выберите файл повторно, чтобы продолжить загрузку';
          touchRef.current(task);
          return;
        }
        if (current.status === 'ready') {
          task.fileId = current.fileId;
          await attachTask(task);
          return;
        }
        if (current.status === 'failed' || current.status === 'expired' || current.status === 'cancelled')
          throw new UploadSessionTerminalError(current);

        const ready = await waitForUploadReady(task.sessionId, controller.signal);
        task.fileId = ready.fileId;
        await attachTask(task);
        return;
      }

      if (!task.file) {
        task.status = 'needs_file';
        task.error = 'Выберите файл повторно, чтобы продолжить загрузку';
        touchRef.current(task);
        return;
      }

      if (!task.hashValidated) {
        task.status = 'hashing';
        task.progress = 0;
        task.error = null;
        touchRef.current(task);
        const calculated = await hashFile(task.file, (fraction) => {
          task.progress = fraction;
          touchRef.current(task, false);
        }, controller.signal);
        if (task.sha256 && task.sha256 !== calculated) {
          task.file = null;
          task.status = 'needs_file';
          task.error = 'Выбран другой файл: SHA-256 не совпадает';
          touchRef.current(task);
          return;
        }
        task.sha256 = calculated;
        task.hashValidated = true;
        touchRef.current(task);
      }

      let session: UploadSession;
      if (task.sessionId) {
        session = await resumeUploadSession(task.sessionId);
        if (session.status === 'failed' || session.status === 'expired' || session.status === 'cancelled') {
          task.idempotencyKey = randomId();
          task.sessionId = null;
          task.fileId = null;
          session = await createSession(task);
        }
      } else {
        task.status = 'checking';
        touchRef.current(task);
        const duplicate = await checkDuplicateHash(task.sha256!);
        if (duplicate.exists) {
          const batchDecision = batchDecisionsRef.current.get(task.batchId);
          let decision: DuplicateDecision;
          if (batchDecision === 'skip-all' || batchDecision === 'upload-all') {
            decision = batchDecision;
          } else {
            decision = await askDuplicateRef.current(task.fileName, duplicate.locations);
            if (decision === 'skip-all' || decision === 'upload-all')
              batchDecisionsRef.current.set(task.batchId, decision);
          }
          if (decision === 'skip' || decision === 'skip-all') {
            task.status = 'skipped';
            task.progress = 1;
            touchRef.current(task);
            return;
          }
        }
        session = await createSession(task);
      }

      task.fileId = session.fileId;
      if (session.status === 'uploading') {
        task.status = 'uploading';
        touchRef.current(task);
        await uploadMissingParts(task.file, session, (fraction) => {
          task.progress = fraction;
          if (fraction > 0.01 && task.startedAt) {
            const elapsed = (Date.now() - task.startedAt) / 1000;
            task.eta = Math.max(0, elapsed / fraction - elapsed);
          }
          touchRef.current(task, false);
        }, controller.signal);
        session = await completeUploadSession(session.sessionId);
      }

      if (session.status !== 'ready') {
        task.status = 'processing';
        task.progress = 1;
        touchRef.current(task);
        session = await waitForUploadReady(session.sessionId, controller.signal);
      }
      task.fileId = session.fileId;
      await attachTask(task);
    } catch (error) {
      const terminalNeedsFile = !task.file
        && error instanceof UploadSessionTerminalError
        && (error.status === 'failed' || error.status === 'expired' || error.status === 'cancelled');
      task.status = terminalNeedsFile ? 'needs_file' : 'failed';
      task.error = controller.signal.aborted
        ? 'Отменено'
        : terminalNeedsFile
          ? `${error.message}. Выберите файл повторно, чтобы начать новую сессию`
          : (error as Error).message || 'Ошибка загрузки';
      if (error instanceof UploadSessionTerminalError
          && (error.status === 'failed' || error.status === 'expired')) {
        // Идентификатор оставляем: Retry сначала подтвердит терминальное состояние,
        // затем создаст новую сессию с новым idempotency key.
      }
      touchRef.current(task);
    } finally {
      task.abortCtrl = null;
    }
  }, [attachTask, createSession]);

  const tryStartMore = React.useCallback(() => {
    while (runningIdsRef.current.size < MAX_CONCURRENT) {
      const task = tasksRef.current.find((item) =>
        !runningIdsRef.current.has(item.id)
        && (item.status === 'hashing' || item.status === 'processing' || item.status === 'attaching'));
      if (!task) break;
      runningIdsRef.current.add(task.id);
      void processTask(task).finally(() => {
        runningIdsRef.current.delete(task.id);
        tryStartMoreRef.current();
      });
    }
  }, [processTask]);
  tryStartMoreRef.current = tryStartMore;

  React.useEffect(() => {
    let disposed = false;
    void loadUploadTasks().then((stored) => {
      if (disposed) return;
      const existing = new Set(tasksRef.current.map((task) => task.id));
      const restored = stored.filter((task) => !existing.has(task.id)).map(fromPersisted);
      tasksRef.current = [...tasksRef.current, ...restored];
      if (restored.length > 0) {
        bump();
        window.setTimeout(() => tryStartMoreRef.current(), 0);
      }
    }).catch(() => undefined);
    return () => { disposed = true; };
  }, [bump]);

  const enqueue = React.useCallback((files: File[], attachOptions: AttachOptions) => {
    const now = Date.now();
    const batchId = randomId();
    const newTasks = files.map<UploadTask>((file) => ({
      id: randomId(),
      idempotencyKey: randomId(),
      file,
      fileName: file.name,
      fileSize: file.size,
      fileType: file.type,
      lastModified: file.lastModified,
      sha256: null,
      sessionId: null,
      fileId: null,
      status: 'hashing',
      progress: 0,
      error: null,
      attachOptions: { ...attachOptions },
      batchId,
      createdAt: now,
      updatedAt: now,
      startedAt: null,
      eta: null,
      abortCtrl: null,
      hashValidated: false,
    }));
    tasksRef.current = [...tasksRef.current, ...newTasks];
    for (const task of newTasks) void saveUploadTask(toPersisted(task)).catch(() => undefined);
    bump();
    window.setTimeout(() => tryStartMoreRef.current(), 0);
  }, [bump]);

  const retry = React.useCallback((id: string) => {
    const task = tasksRef.current.find((item) => item.id === id);
    if (!task) return;
    if (task.status === 'needs_file') {
      void reselectFile(task);
      return;
    }
    if (task.status === 'uploaded_not_attached') {
      task.status = 'attaching';
    } else if (task.status === 'failed') {
      task.status = task.file ? 'hashing' : task.sessionId ? 'processing' : 'needs_file';
    } else {
      return;
    }
    task.error = null;
    touchRef.current(task);
    window.setTimeout(() => tryStartMoreRef.current(), 0);
  }, []);

  const reselect = React.useCallback((id: string) => {
    const task = tasksRef.current.find((item) => item.id === id);
    if (task) void reselectFile(task);
  }, []);

  async function reselectFile(task: UploadTask): Promise<void> {
    const [file] = await pickFiles({ multiple: false });
    if (!file) return;
    if (file.size !== task.fileSize) {
      task.error = 'Размер выбранного файла не совпадает';
      task.status = 'needs_file';
      touchRef.current(task);
      return;
    }
    task.file = file;
    task.hashValidated = false;
    task.status = 'hashing';
    task.error = null;
    task.progress = 0;
    touchRef.current(task);
    window.setTimeout(() => tryStartMoreRef.current(), 0);
  }

  const dismiss = React.useCallback((id: string) => {
    tasksRef.current = tasksRef.current.filter((task) => task.id !== id);
    void deleteUploadTask(id).catch(() => undefined);
    bump();
  }, [bump]);

  const clearCompleted = React.useCallback(() => {
    const removable = tasksRef.current
      .filter((task) => task.status === 'done' || task.status === 'failed' || task.status === 'skipped')
      .map((task) => task.id);
    tasksRef.current = tasksRef.current.filter((task) => !removable.includes(task.id));
    for (const id of removable) void deleteUploadTask(id).catch(() => undefined);
    bump();
  }, [bump]);

  const cancel = React.useCallback((id: string) => {
    const task = tasksRef.current.find((item) => item.id === id);
    if (!task) return;
    if (task.status !== 'hashing' && task.status !== 'checking' && task.status !== 'uploading') return;
    task.abortCtrl?.abort();
    task.abortCtrl = null;
    if (task.sessionId) void cancelUploadSession(task.sessionId).catch(() => undefined);
    task.status = 'failed';
    task.error = 'Отменено';
    touchRef.current(task);
  }, []);

  React.useEffect(() => {
    const transferring = tasksRef.current.some((task) =>
      task.status === 'hashing' || task.status === 'checking' || task.status === 'uploading');
    if (!transferring) return;
    const handler = (event: BeforeUnloadEvent) => { event.preventDefault(); };
    window.addEventListener('beforeunload', handler);
    return () => window.removeEventListener('beforeunload', handler);
  }, [rev]);

  const tasks = tasksRef.current;
  const summary = React.useMemo<UploadSummary>(() => summarize(tasksRef.current), [rev]);
  const hasActive = summary.active > 0;

  const actionsValue = React.useMemo<ActionsValue>(() => ({ enqueue, attachVersion }), [enqueue, attachVersion]);
  const stateValue = React.useMemo<StateValue>(() => ({
    tasks,
    summary,
    hasActive,
    dupPrompt,
    retry,
    reselect,
    dismiss,
    clearCompleted,
    cancel,
    answerDuplicate,
    // tasksRef intentionally publishes through rev.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }), [rev, dupPrompt]);

  return (
    <ActionsCtx.Provider value={actionsValue}>
      <StateCtx.Provider value={stateValue}>{children}</StateCtx.Provider>
    </ActionsCtx.Provider>
  );
}

function summarize(tasks: UploadTask[]): UploadSummary {
  let done = 0;
  let skipped = 0;
  let error = 0;
  let active = 0;
  let completed = 0;
  for (const task of tasks) {
    if (task.status === 'done') { done++; completed++; }
    else if (task.status === 'skipped') { skipped++; completed++; }
    else if (task.status === 'failed' || task.status === 'needs_file' || task.status === 'uploaded_not_attached') {
      error++;
      if (task.status === 'uploaded_not_attached') completed++;
    } else {
      active++;
      if (task.status === 'processing' || task.status === 'attaching') completed++;
      else if (task.status === 'uploading') completed += task.progress;
    }
  }
  const uploading = tasks.filter((task) => task.status === 'uploading' && task.eta !== null);
  return {
    total: tasks.length,
    done,
    skipped,
    error,
    active,
    overallProgress: tasks.length > 0 ? completed / tasks.length : 0,
    eta: uploading.length > 0 ? Math.max(...uploading.map((task) => task.eta!)) : null,
  };
}

function toPersisted(task: UploadTask): PersistedUploadTask {
  return {
    version: 1,
    id: task.id,
    idempotencyKey: task.idempotencyKey,
    batchId: task.batchId,
    fileName: task.fileName,
    fileSize: task.fileSize,
    fileType: task.fileType,
    lastModified: task.lastModified,
    sha256: task.sha256,
    sessionId: task.sessionId,
    fileId: task.fileId,
    status: task.status,
    attachOptions: { ...task.attachOptions },
    progress: task.progress,
    error: task.error,
    createdAt: task.createdAt,
    updatedAt: task.updatedAt,
  };
}

function fromPersisted(task: PersistedUploadTask): UploadTask {
  let status: TaskStatus = task.status;
  if (status === 'hashing' || status === 'checking' || status === 'uploading') status = 'needs_file';
  if (status === 'attaching') status = 'uploaded_not_attached';
  return {
    ...task,
    status,
    file: null,
    startedAt: null,
    eta: null,
    abortCtrl: null,
    hashValidated: false,
  };
}

function randomId(): string {
  return globalThis.crypto?.randomUUID?.() ?? `${Date.now()}-${Math.random().toString(16).slice(2)}`;
}

function isActiveTask(task: UploadTask): boolean {
  return task.status === 'hashing'
    || task.status === 'checking'
    || task.status === 'uploading'
    || task.status === 'processing'
    || task.status === 'attaching';
}

export const uploadTaskOrdering = (a: UploadTask, b: UploadTask): number =>
  Number(isActiveTask(b)) - Number(isActiveTask(a));

export function useUploadActions(): ActionsValue {
  const context = React.useContext(ActionsCtx);
  if (!context) throw new Error('useUploadActions must be used within UploadManagerProvider');
  return context;
}

export function useUploadState(): StateValue {
  const context = React.useContext(StateCtx);
  if (!context) throw new Error('useUploadState must be used within UploadManagerProvider');
  return context;
}
