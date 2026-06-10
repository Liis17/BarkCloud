import React from 'react';
import { Icon } from '../Icon';
import { Modal } from '../ui/Modal';
import { useUploadState } from '../../hooks/useUploadManager';
import type { UploadTask, TaskStatus } from '../../hooks/useUploadManager';

function fmtSize(bytes: number): string {
  if (!bytes) return '';
  const u = ['Б', 'КБ', 'МБ', 'ГБ', 'ТБ'];
  let i = 0, v = bytes;
  while (v >= 1024 && i < u.length - 1) { v /= 1024; i++; }
  return (i === 0 ? v.toFixed(0) : v.toFixed(v < 10 ? 1 : 0)).replace('.', ',') + ' ' + u[i];
}

function StatusIcon({ status }: { status: TaskStatus }) {
  switch (status) {
    case 'done': return <Icon.check size={14} className="upload-status done" />;
    case 'error': return <Icon.x size={14} className="upload-status err" />;
    case 'skipped': return <Icon.x size={14} className="upload-status skip" />;
    case 'uploading':
    case 'checking':
    case 'attaching':
      return <span className="spinner" style={{ width: 14, height: 14, borderWidth: 2 }} />;
    default: return null;
  }
}

function statusLabel(s: TaskStatus): string {
  switch (s) {
    case 'pending': return 'Ожидание…';
    case 'checking': return 'Проверка…';
    case 'uploading': return 'Загрузка…';
    case 'attaching': return 'Прикрепление…';
    case 'done': return 'Загружен';
    case 'error': return 'Ошибка';
    case 'skipped': return 'Пропущен';
  }
}

function TaskRow({ task, onRetry, onDismiss }: { task: UploadTask; onRetry: (id: string) => void; onDismiss: (id: string) => void }) {
  return (
    <div className={'upload-task' + (task.status === 'error' ? ' has-error' : '')}>
      <div className="upload-task-icon"><StatusIcon status={task.status} /></div>
      <div className="upload-task-body">
        <div className="upload-task-name" title={task.fileName}>{task.fileName}</div>
        <div className="upload-task-meta">
          {task.fileSize > 0 && <span className="upload-task-size">{fmtSize(task.fileSize)}</span>}
          <span className="upload-task-status">{statusLabel(task.status)}</span>
        </div>
        {task.status === 'uploading' && (
          <div className="upload-task-bar">
            <div className="bar-fill" style={{ width: Math.round(task.progress * 100) + '%' }} />
          </div>
        )}
        {task.status === 'attaching' && (
          <div className="upload-task-bar">
            <div className="bar-fill" style={{ width: '100%' }} />
          </div>
        )}
        {task.status === 'error' && task.error && (
          <div className="upload-task-error">{task.error}</div>
        )}
      </div>
      <div className="upload-task-actions">
        {task.status === 'error' && (
          <button className="icon-btn" title="Повторить" onClick={() => onRetry(task.id)}>
            <Icon.refresh size={16} />
          </button>
        )}
        {(task.status === 'done' || task.status === 'error' || task.status === 'skipped') && (
          <button className="icon-btn" title="Убрать" onClick={() => onDismiss(task.id)}>
            <Icon.x size={16} />
          </button>
        )}
      </div>
    </div>
  );
}

export function UploadIndicator() {
  const { tasks, summary, hasActive, dupPrompt, retry, dismiss, clearCompleted, answerDuplicate } = useUploadState();
  const [open, setOpen] = React.useState(false);

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
              <span className="upload-popup-pct">{Math.round(summary.overallProgress * 100)}%</span>
            </div>
          )}
          <div className="upload-popup-list">
            {tasks.map(t => (
              <TaskRow key={t.id} task={t} onRetry={retry} onDismiss={dismiss} />
            ))}
          </div>
          {summary.done + summary.skipped + summary.error > 0 && (
            <div className="upload-popup-foot">
              <button className="btn text" onClick={() => { clearCompleted(); if (!tasks.some(t => t.status !== 'done' && t.status !== 'error' && t.status !== 'skipped')) setOpen(false); }}>Очистить завершённые</button>
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
