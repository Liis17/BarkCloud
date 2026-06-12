import React from 'react';
import { uploadFile, checkDuplicate, apiPost } from '../lib/api';
import type { DuplicateLocation } from '../lib/api';
import type { DuplicateDecision } from './useDuplicatePrompt';

const MAX_CONCURRENT = 4;

export interface AttachOptions {
  dir?: string;
  routeByMediaKind?: boolean;
}

export type TaskStatus = 'pending' | 'checking' | 'uploading' | 'attaching' | 'done' | 'error' | 'skipped';

export interface UploadTask {
  id: string;
  file: File;
  fileName: string;
  fileSize: number;
  status: TaskStatus;
  progress: number;
  error: string | null;
  attachOptions: AttachOptions;
  batchId: string;
  startedAt: number | null;
  eta: number | null;
  abortCtrl: AbortController | null;
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
  dismiss: (id: string) => void;
  clearCompleted: () => void;
  cancel: (id: string) => void;
  answerDuplicate: (d: DuplicateDecision) => void;
}

const ActionsCtx = React.createContext<ActionsValue | null>(null);
const StateCtx = React.createContext<StateValue | null>(null);

let _id = 0;
let _batch = 0;

export function UploadManagerProvider({ children }: { children: React.ReactNode }) {
  const tasksRef = React.useRef<UploadTask[]>([]);
  const [rev, setRev] = React.useState(0);
  const [attachVersion, setAttachVersion] = React.useState(0);
  const [dupPrompt, setDupPrompt] = React.useState<DupPromptReq | null>(null);
  const dupQueueRef = React.useRef<DupPromptReq[]>([]);
  const batchDecisionsRef = React.useRef<Map<string, DuplicateDecision>>(new Map());
  const runningRef = React.useRef(0);

  const bump = React.useCallback(() => setRev(r => r + 1), []);
  const bumpRef = React.useRef(bump);
  bumpRef.current = bump;

  const showNextDup = React.useCallback(() => {
    if (dupQueueRef.current.length > 0) {
      setDupPrompt(dupQueueRef.current.shift()!);
    }
  }, []);

  const answerDuplicate = React.useCallback((d: DuplicateDecision) => {
    setDupPrompt(prev => {
      if (prev) prev.resolve(d);
      return null;
    });
  }, []);

  React.useEffect(() => {
    if (!dupPrompt) showNextDup();
  }, [dupPrompt, showNextDup]);

  const askDuplicateRef = React.useRef((_f: string, _l: DuplicateLocation[]): Promise<DuplicateDecision> => Promise.resolve('upload'));
  askDuplicateRef.current = (fileName: string, locations: DuplicateLocation[]): Promise<DuplicateDecision> => {
    return new Promise<DuplicateDecision>((resolve) => {
      const req: DupPromptReq = { fileName, locations, resolve };
      setDupPrompt(prev => {
        if (prev) {
          dupQueueRef.current.push(req);
          return prev;
        }
        return req;
      });
    });
  };

  const processTask = React.useCallback(async (task: UploadTask) => {
    task.status = 'checking';
    task.progress = 0;
    task.startedAt = Date.now();
    task.eta = null;
    bumpRef.current();

    try {
      const d = await checkDuplicate(task.file);
      if (d.exists) {
        const batchDecision = batchDecisionsRef.current.get(task.batchId);
        let decision: DuplicateDecision;
        if (batchDecision === 'skip-all' || batchDecision === 'upload-all') {
          decision = batchDecision;
        } else {
          decision = await askDuplicateRef.current(task.file.name, d.locations);
          if (decision === 'skip-all' || decision === 'upload-all') {
            batchDecisionsRef.current.set(task.batchId, decision);
          }
        }
        if (decision === 'skip' || decision === 'skip-all') {
          task.status = 'skipped';
          bumpRef.current();
          return;
        }
      }
    } catch {
      // proceed with upload
    }

    task.status = 'uploading';
    const ctrl = new AbortController();
    task.abortCtrl = ctrl;
    bumpRef.current();

    try {
      const result = await uploadFile(task.file, (frac) => {
        task.progress = frac;
        if (frac > 0.01 && task.startedAt) {
          const elapsed = (Date.now() - task.startedAt) / 1000;
          const totalEst = elapsed / frac;
          task.eta = Math.max(0, totalEst - elapsed);
        }
        bumpRef.current();
      }, ctrl.signal);

      task.status = 'attaching';
      task.progress = 1;
      bumpRef.current();

      try {
        const body: Record<string, unknown> = {
          fileId: result.fileId,
          name: result.name || task.file.name,
        };
        if (task.attachOptions.dir) body.dir = task.attachOptions.dir;
        if (task.attachOptions.routeByMediaKind) body.routeByMediaKind = true;
        await apiPost('/api/cloud/attach', body);
      } catch {
        // best-effort
      }

      task.status = 'done';
      bumpRef.current();
      setAttachVersion(v => v + 1);
    } catch (e) {
      if ((e as Error).name === 'AbortError') {
        task.status = 'error';
        task.error = 'Отменено';
      } else {
        task.status = 'error';
        task.error = (e as Error).message || 'Ошибка загрузки';
      }
      task.abortCtrl = null;
      bumpRef.current();
    }
  }, []);

  const processTaskRef = React.useRef(processTask);
  processTaskRef.current = processTask;

  const tryStartMore = React.useCallback(() => {
    while (runningRef.current < MAX_CONCURRENT) {
      const task = tasksRef.current.find(t => t.status === 'pending');
      if (!task) break;
      runningRef.current++;
      processTaskRef.current(task)
        .catch(() => {})
        .finally(() => {
          runningRef.current--;
          tryStartMore();
        });
    }
  }, []);

  const tryStartMoreRef = React.useRef(tryStartMore);
  tryStartMoreRef.current = tryStartMore;

  const enqueue = React.useCallback((files: File[], attachOptions: AttachOptions) => {
    const batchId = 'b' + (++_batch);
    const newTasks: UploadTask[] = files.map(f => ({
      id: 'u' + (++_id),
      file: f,
      fileName: f.name,
      fileSize: f.size,
      status: 'pending' as TaskStatus,
      progress: 0,
      error: null,
      attachOptions,
      batchId,
      startedAt: null,
      eta: null,
      abortCtrl: null,
    }));
    tasksRef.current = [...tasksRef.current, ...newTasks];
    bumpRef.current();
    setTimeout(() => tryStartMoreRef.current(), 0);
  }, []);

  const retry = React.useCallback((id: string) => {
    const task = tasksRef.current.find(t => t.id === id);
    if (!task || task.status !== 'error') return;
    task.status = 'pending';
    task.progress = 0;
    task.error = null;
    bumpRef.current();
    setTimeout(() => tryStartMoreRef.current(), 0);
  }, []);

  const dismiss = React.useCallback((id: string) => {
    tasksRef.current = tasksRef.current.filter(t => t.id !== id);
    bumpRef.current();
  }, []);

  const clearCompleted = React.useCallback(() => {
    tasksRef.current = tasksRef.current.filter(
      t => t.status !== 'done' && t.status !== 'error' && t.status !== 'skipped',
    );
    bumpRef.current();
  }, []);

  const cancel = React.useCallback((id: string) => {
    const task = tasksRef.current.find(t => t.id === id);
    if (!task) return;
    if (task.abortCtrl) { task.abortCtrl.abort(); task.abortCtrl = null; }
    if (task.status === 'pending') {
      tasksRef.current = tasksRef.current.filter(t => t.id !== id);
    }
    bumpRef.current();
  }, []);

  React.useEffect(() => {
    const active = tasksRef.current.some(
      t => t.status === 'pending' || t.status === 'checking' || t.status === 'uploading' || t.status === 'attaching',
    );
    if (!active) return;
    const handler = (e: BeforeUnloadEvent) => { e.preventDefault(); };
    window.addEventListener('beforeunload', handler);
    return () => window.removeEventListener('beforeunload', handler);
  }, [rev]);

  const tasks = tasksRef.current;

  const summary: UploadSummary = React.useMemo(() => {
    const t = tasksRef.current;
    let done = 0, skipped = 0, error = 0, active = 0, uploaded = 0;
    for (const x of t) {
      if (x.status === 'done') { done++; uploaded++; }
      else if (x.status === 'skipped') { skipped++; uploaded++; }
      else if (x.status === 'error') error++;
      else {
        active++;
        if (x.status === 'uploading') uploaded += x.progress;
      }
    }
    const total = t.length;
    let eta: number | null = null;
    const uploadingTasks = t.filter(x => x.status === 'uploading' && x.eta !== null);
    if (uploadingTasks.length > 0) {
      eta = Math.max(...uploadingTasks.map(x => x.eta!));
    }
    return { total, done, skipped, error, active, overallProgress: total > 0 ? uploaded / total : 0, eta };
  }, [rev]);

  const hasActive = summary.active > 0;

  const actionsValue = React.useMemo<ActionsValue>(() => ({
    enqueue,
    attachVersion,
  }), [enqueue, attachVersion]);

  const stateValue = React.useMemo<StateValue>(() => ({
    tasks,
    summary,
    hasActive,
    dupPrompt,
    retry,
    dismiss,
    clearCompleted,
    cancel,
    answerDuplicate,
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }), [rev, dupPrompt]);

  return (
    <ActionsCtx.Provider value={actionsValue}>
      <StateCtx.Provider value={stateValue}>
        {children}
      </StateCtx.Provider>
    </ActionsCtx.Provider>
  );
}

export function useUploadActions(): ActionsValue {
  const ctx = React.useContext(ActionsCtx);
  if (!ctx) throw new Error('useUploadActions must be used within UploadManagerProvider');
  return ctx;
}

export function useUploadState(): StateValue {
  const ctx = React.useContext(StateCtx);
  if (!ctx) throw new Error('useUploadState must be used within UploadManagerProvider');
  return ctx;
}
