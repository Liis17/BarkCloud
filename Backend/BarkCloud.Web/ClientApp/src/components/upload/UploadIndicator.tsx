import React from 'react';
import { Icon } from '../Icon';
import { Modal } from '../ui/Modal';
import { uploadTaskOrdering, useUploadState } from '../../hooks/useUploadManager';
import type { UploadTask, TaskStatus } from '../../hooks/useUploadManager';

function fmtSize(bytes: number): string {
  if (!bytes) return '';
  const u = ['Б', 'КБ', 'МБ', 'ГБ', 'ТБ'];
  let i = 0, v = bytes;
  while (v >= 1024 && i < u.length - 1) { v /= 1024; i++; }
  return (i === 0 ? v.toFixed(0) : v.toFixed(v < 10 ? 1 : 0)).replace('.', ',') + ' ' + u[i];
}

function fmtEta(seconds: number | null): string {
  if (seconds === null || seconds < 1) return '';
  if (seconds < 60) return Math.ceil(seconds) + ' сек';
  const m = Math.floor(seconds / 60);
  const s = Math.ceil(seconds % 60);
  if (m < 60) return m + ' мин ' + (s > 0 ? s + ' сек' : '');
  const h = Math.floor(m / 60);
  const rm = m % 60;
  return h + ' ч ' + (rm > 0 ? rm + ' мин' : '');
}

function StatusIcon({ status }: { status: TaskStatus }) {
  switch (status) {
    case 'done': return <Icon.check size={14} className="upload-status done" />;
    case 'failed':
    case 'needs_file':
    case 'uploaded_not_attached':
      return <Icon.x size={14} className="upload-status err" />;
    case 'skipped': return <Icon.x size={14} className="upload-status skip" />;
    case 'uploading':
    case 'hashing':
    case 'checking':
    case 'processing':
    case 'attaching':
      return <span className="spinner" style={{ width: 14, height: 14, borderWidth: 2 }} />;
    default: return null;
  }
}

function statusLabel(s: TaskStatus): string {
  switch (s) {
    case 'hashing': return 'Хэширование…';
    case 'checking': return 'Проверка…';
    case 'needs_file': return 'Нужен файл';
    case 'uploading': return 'Загрузка…';
    case 'processing': return 'Обработка…';
    case 'attaching': return 'Прикрепление…';
    case 'uploaded_not_attached': return 'Загружен, не прикреплён';
    case 'done': return 'Загружен';
    case 'failed': return 'Ошибка';
    case 'skipped': return 'Пропущен';
  }
}

function TaskRow({ task, onRetry, onReselect, onDismiss, onCancel }: { task: UploadTask; onRetry: (id: string) => void; onReselect: (id: string) => void; onDismiss: (id: string) => void; onCancel: (id: string) => void }) {
  const canCancel = task.status === 'hashing'
    || task.status === 'checking'
    || task.status === 'uploading';
  return (
    <div className={'upload-task' + (task.status === 'failed' || task.status === 'uploaded_not_attached' || task.status === 'needs_file' ? ' has-error' : '')}>
      <div className="upload-task-icon"><StatusIcon status={task.status} /></div>
      <div className="upload-task-body">
        <div className="upload-task-name" title={task.fileName}>{task.fileName}</div>
        <div className="upload-task-meta">
          {task.fileSize > 0 && <span className="upload-task-size">{fmtSize(task.fileSize)}</span>}
          <span className="upload-task-status">{statusLabel(task.status)}</span>
          {task.status === 'uploading' && task.eta !== null && task.eta > 1 && (
            <span className="upload-task-eta">~{fmtEta(task.eta)}</span>
          )}
        </div>
        {(task.status === 'hashing' || task.status === 'uploading') && (
          <div className="upload-task-bar">
            <div className="bar-fill" style={{ width: Math.round(task.progress * 100) + '%' }} />
          </div>
        )}
        {(task.status === 'processing' || task.status === 'attaching') && (
          <div className="upload-task-bar">
            <div className="bar-fill" style={{ width: '100%' }} />
          </div>
        )}
        {(task.status === 'failed' || task.status === 'needs_file' || task.status === 'uploaded_not_attached') && task.error && (
          <div className="upload-task-error">{task.error}</div>
        )}
      </div>
      <div className="upload-task-actions">
        {(task.status === 'failed' || task.status === 'uploaded_not_attached') && (
          <button className="icon-btn" title="Повторить" onClick={() => onRetry(task.id)}>
            <Icon.refresh size={16} />
          </button>
        )}
        {task.status === 'needs_file' && (
          <button className="icon-btn" title="Выбрать файл" onClick={() => onReselect(task.id)}>
            <Icon.upload size={16} />
          </button>
        )}
        {canCancel && (
          <button className="icon-btn" title="Отменить" onClick={() => onCancel(task.id)}>
            <Icon.x size={16} />
          </button>
        )}
        {(task.status === 'done' || task.status === 'skipped') && (
          <button className="icon-btn" title="Убрать" onClick={() => onDismiss(task.id)}>
            <Icon.x size={16} />
          </button>
        )}
        {(task.status === 'failed' || task.status === 'uploaded_not_attached' || task.status === 'needs_file') && (
          <button className="icon-btn" title="Убрать" onClick={() => onDismiss(task.id)}>
            <Icon.x size={16} />
          </button>
        )}
      </div>
    </div>
  );
}

export function UploadIndicator() {
  const { tasks, summary, hasActive, dupPrompt, retry, reselect, dismiss, clearCompleted, cancel, answerDuplicate } = useUploadState();
  const [open, setOpen] = React.useState(false);
  const orderedTasks = [...tasks].sort(uploadTaskOrdering);

  if (tasks.length === 0) return null;

  return (
    <>
      <button
        className={'icon-btn upload-indicator' + (hasActive ? ' active' : '')}
        title={hasActive ? `Загрузка ${summary.active} файл(ов)` : 'Загрузки'}
        onClick={() => setOpen(v => !v)}
      >
        <Icon.upload size={20} />
        {hasActive && <span className="upload-ind-badge">{summary.active}</span>}
        {!hasActive && summary.error > 0 && <span className="upload-ind-badge err">{summary.error}</span>}
      </button>

      {open && (
        <div className="upload-popup">
          <div className="upload-popup-head">
            <span className="upload-popup-title">Загрузки</span>
            <button className="icon-btn sm" title="Закрыть" onClick={() => setOpen(false)}>
              <Icon.x size={16} />
            </button>
          </div>
          {hasActive && (
            <div className="upload-popup-progress">
              <div className="upload-popup-bar">
                <div className="bar-fill" style={{ width: Math.round(summary.overallProgress * 100) + '%' }} />
              </div>
              <span className="upload-popup-pct">
                {Math.round(summary.overallProgress * 100)}%
                {summary.eta !== null && summary.eta > 1 && <span className="upload-popup-eta"> ~{fmtEta(summary.eta)}</span>}
              </span>
            </div>
          )}
          <div className="upload-popup-list">
            {orderedTasks.map(t => (
              <TaskRow key={t.id} task={t} onRetry={retry} onReselect={reselect} onDismiss={dismiss} onCancel={cancel} />
            ))}
          </div>
          {summary.done + summary.skipped + summary.error > 0 && (
            <div className="upload-popup-foot">
              <button className="btn text" onClick={() => { clearCompleted(); if (!tasks.some(t => t.status !== 'done' && t.status !== 'failed' && t.status !== 'skipped')) setOpen(false); }}>Очистить завершённые</button>
            </div>
          )}
        </div>
      )}

      {open && <div className="upload-popup-backdrop" onClick={() => setOpen(false)} />}

      {dupPrompt && (
        <Modal
          title="Такой файл уже есть"
          onClose={() => answerDuplicate('skip')}
          actions={
            <>
              <button className="btn text" onClick={() => answerDuplicate('skip')}>Пропустить</button>
              <button className="btn text" onClick={() => answerDuplicate('skip-all')}>Пропустить все</button>
              <button className="btn outlined" onClick={() => answerDuplicate('upload-all')}>Загрузить все</button>
              <button className="btn primary" onClick={() => answerDuplicate('upload')}>Загрузить ещё раз</button>
            </>
          }
        >
          <div className="confirm-msg">
            <p>Файл <b>«{dupPrompt.fileName}»</b> уже есть в вашем облаке.</p>
            {dupPrompt.locations.length > 0 && (
              <ul className="dup-locations">
                {dupPrompt.locations.map(l => (
                  <li key={l.entryId}>{l.name} — {l.directoryName || 'Корневая папка'}</li>
                ))}
              </ul>
            )}
            <p>Загрузить ещё одну копию?</p>
          </div>
        </Modal>
      )}
    </>
  );
}
