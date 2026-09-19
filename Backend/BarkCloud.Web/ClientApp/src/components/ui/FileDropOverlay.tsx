import { Icon } from '../Icon';

export function FileDropOverlay({ detail }: { detail: string }) {
  return (
    <div className="drop-overlay" role="status" aria-live="polite">
      <Icon.upload size={40} />
      <span>Отпустите файлы для загрузки</span>
      <small className="drop-overlay-note">{detail}</small>
    </div>
  );
}
