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
    case 'done': return <Icon.check size={16} className="upload-status done" />;
    case 'error': return <Icon.x size={16} className="upload-status err" />;
    case 'skipped': return <Icon.x size={14} className="upload-status skip" />;
    case 'uploading':
    case 'checking':
    case 'attaching':
      return <span className="spinner" style={{ width: 16, height: 16, borderWidth: 2 }} />;
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

export function UploadBanner() {
  const { tasks, summary, hasActive, dupPrompt, retry, dismiss, clearCompleted, answerDuplicate } = useUploadState();
  const [expanded, setExpanded] = React.useState(false);

  if (tasks.length === 0) return null;

  const finished = summary.done + summary.skipped + summary.error;
  const allDone = !hasActive && finished > 0;
  const processed = summary.done + summary.skipped + summary.error + summary.active;

  return (
    <div className="upload-area">
      <button className="upload-bar" onClick={() => setExpanded(e => !e)}>
        <span className="upload-bar-icon">
          {hasActive
            ? <span className="spinner" style={{ width: 16, height: 16, borderWidth: 2 }} />
            : <Icon.check size={16} />}
        </span>
        <span className="upload-bar-text">
          {hasActive
            ? <>Загрузка {processed} из {summary.total} файл{summary.total === 1 ? 'а' : 'ов'}</>
            : allDone
              ? summary.error > 0
                ? <>Завершено с ошибками ({summary.error} из {summary.total})</>
                : summary.skipped > 0
                  ? <>Загружено {summary.done}, пропущено {summary.skipped}</>
                  : <>Загружено: {summary.done} из {summary.total}</>
              : null}
        </span>
        <div className="upload-bar-progress">
          <div className="bar-fill" style={{ width: Math.round(summary.overallProgress * 100) + '%' }} />
        </div>
        <span className={'upload-bar-chev' + (expanded ? ' open' : '')}>
          <Icon.chevDown size={18} />
        </span>
      </button>

      {expanded && (
        <div className="upload-panel">
          <div className="upload-panel-head">
            <span className="upload-panel-title">Загрузки</span>
            {(summary.done + summary.skipped + summary.error > 0) && (
              <button className="btn text" onClick={clearCompleted}>Очистить</button>
            )}
          </div>
          <div className="upload-panel-list">
            {tasks.map(t => (
              <TaskRow key={t.id} task={t} onRetry={retry} onDismiss={dismiss} />
            ))}
          </div>
        </div>
      )}

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
    </div>
  );
}
