/** M3 Switch (кнопка role="switch", стили .toggle). */
export function Switch({ on, onChange, disabled, label }: { on: boolean; onChange: (v: boolean) => void; disabled?: boolean; label?: string }) {
  return <button type="button" role="switch" aria-checked={on} aria-label={label || 'Переключить настройку'}
    className={'toggle' + (on ? ' on' : '')} disabled={disabled} onClick={() => onChange(!on)} />;
}
