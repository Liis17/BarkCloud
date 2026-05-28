import { Icon } from '../Icon';

export interface BulkAction {
  label: string;
  icon?: string;
  onClick: () => void;
  danger?: boolean;
}

/** Плавающая панель групповых действий — видна, когда что-то выбрано. */
export function SelectionBar({ count, actions, onClear }: { count: number; actions: BulkAction[]; onClear: () => void }) {
  if (!count) return null;
  return (
    <div className="selbar">
      <span className="selbar-count">Выбрано: {count}</span>
      <div className="selbar-actions">
        {actions.map((a, i) => {
          const Ic = a.icon ? Icon[a.icon] : null;
          return (
            <button key={i} className={'btn ' + (a.danger ? 'danger' : 'outlined')} onClick={a.onClick}>
              {Ic ? <Ic size={16} /> : null} {a.label}
            </button>
          );
        })}
      </div>
      <button className="btn text" onClick={onClear}>
        <Icon.x size={16} /> Снять
      </button>
    </div>
  );
}
