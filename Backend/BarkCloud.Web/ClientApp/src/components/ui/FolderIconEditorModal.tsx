import React from 'react';
import { Icon } from '../Icon';
import { Modal } from './Modal';
import { SearchField, TextField } from './TextField';
import { FolderIcon } from './FolderIcon';
import { HsvTrianglePicker } from './HsvTrianglePicker';
import { normalizeHex } from './TagColorPicker';
import { FOLDER_ICON_CATEGORIES, FOLDER_ICONS, findFolderIcon } from '../../lib/folderIcons';

export const FOLDER_COLOR_PRESETS = [
  '#9A4F1E', '#B3261E', '#C2185B', '#7B1FA2', '#4527A0', '#1565C0',
  '#00838F', '#2E7D32', '#689F38', '#F9A825', '#EF6C00', '#5D4037',
] as const;

/** Цвет, с которого начинается редактирование, если у папки цвета ещё нет. */
const START_COLOR = '#9A4F1E';

export interface FolderAppearance {
  iconKey: string | null;
  iconColor: string | null;
}

interface FolderIconEditorModalProps {
  folderName: string;
  initial: FolderAppearance;
  onClose: () => void;
  onSave: (appearance: FolderAppearance) => Promise<void>;
}

/** Редактор значка папки: иконка из пака FolderIcons и цвет подложки. */
export function FolderIconEditorModal({ folderName, initial, onClose, onSave }: FolderIconEditorModalProps) {
  const [iconKey, setIconKey] = React.useState<string | null>(findFolderIcon(initial.iconKey) ? initial.iconKey : null);
  const [iconColor, setIconColor] = React.useState<string | null>(initial.iconColor ? normalizeHex(initial.iconColor) : null);
  const [hexInput, setHexInput] = React.useState(iconColor || '');
  const [query, setQuery] = React.useState('');
  const [category, setCategory] = React.useState('all');
  const [busy, setBusy] = React.useState(false);

  const q = query.trim().toLowerCase();
  const visible = FOLDER_ICONS.filter((icon) =>
    (category === 'all' || icon.category === category)
    && (!q || icon.label.toLowerCase().includes(q) || icon.key.includes(q) || icon.keywords.some((k) => k.toLowerCase().includes(q))));

  function pickColor(hex: string | null) {
    setIconColor(hex);
    setHexInput(hex || '');
  }
  function changeHex(raw: string) {
    setHexInput(raw);
    const normalized = normalizeHex(raw);
    if (normalized) setIconColor(normalized);
  }
  async function save() {
    setBusy(true);
    try {
      await onSave({ iconKey, iconColor });
    } finally {
      setBusy(false);
    }
  }
  const hexInvalid = hexInput !== '' && !normalizeHex(hexInput);
  const changed = iconKey !== (initial.iconKey || null) || iconColor !== (initial.iconColor ? normalizeHex(initial.iconColor) : null);

  return (
    <Modal
      title="Значок папки"
      className="folder-icon-editor"
      onClose={onClose}
      actions={
        <>
          <button className="btn text reset" onClick={() => { setIconKey(null); pickColor(null); }} disabled={busy || (!iconKey && !iconColor)}>
            Сбросить
          </button>
          <button className="btn text" onClick={onClose}>Отмена</button>
          <button className="btn primary" onClick={save} disabled={busy || hexInvalid || !changed}>
            {busy ? '…' : 'Сохранить'}
          </button>
        </>
      }
    >
      <div className="fie-layout">
        <section className="fie-side" aria-label="Предпросмотр и цвет">
          <div className="fie-preview">
            <FolderIcon iconKey={iconKey} iconColor={iconColor} size={44} className="fie-preview-tile" />
            <div className="fie-preview-row">
              <FolderIcon iconKey={iconKey} iconColor={iconColor} />
              <span className="fie-preview-name" title={folderName}>{folderName}</span>
            </div>
          </div>
          <HsvTrianglePicker value={iconColor || START_COLOR} onChange={pickColor} />
          <div className="fie-swatches" role="group" aria-label="Готовые цвета">
            <button type="button" className="fie-swatch theme" aria-pressed={!iconColor} title="Цвет темы" aria-label="Цвет темы" onClick={() => pickColor(null)}>
              <Icon.folder size={14} />
            </button>
            {FOLDER_COLOR_PRESETS.map((color) => (
              <button key={color} type="button" className="fie-swatch" style={{ background: color }} aria-pressed={iconColor === color}
                title={color} aria-label={`Цвет ${color}`} onClick={() => pickColor(color)} />
            ))}
          </div>
          <TextField label="HEX" aria-label="HEX цвета подложки" value={hexInput} maxLength={7} spellCheck={false} placeholder="Цвет темы"
            aria-invalid={hexInvalid} error={hexInvalid ? 'Формат #RRGGBB' : undefined} onChange={(e) => changeHex(e.currentTarget.value)} />
        </section>
        <section className="fie-icons" aria-label="Иконка">
          <SearchField leading={<Icon.search size={18} />} label="Поиск иконок" placeholder="Музыка, архив, 18+…" value={query} onChange={(e) => setQuery(e.target.value)} />
          <div className="chip-row fie-categories">
            <button type="button" className={'chip' + (category === 'all' ? ' active' : '')} aria-pressed={category === 'all'} onClick={() => setCategory('all')}>Все</button>
            {FOLDER_ICON_CATEGORIES.map((c) => (
              <button key={c.id} type="button" className={'chip' + (category === c.id ? ' active' : '')} aria-pressed={category === c.id} onClick={() => setCategory(c.id)}>{c.label}</button>
            ))}
          </div>
          <div className="fie-grid" role="listbox" aria-label="Иконки">
            {category === 'all' && !q && (
              <button type="button" role="option" aria-selected={!iconKey} className="fie-icon" title="Обычная папка" onClick={() => setIconKey(null)}>
                <Icon.folder size={24} />
              </button>
            )}
            {visible.map((icon) => (
              <button key={icon.key} type="button" role="option" aria-selected={iconKey === icon.key} className="fie-icon" title={icon.label} aria-label={icon.label} onClick={() => setIconKey(icon.key)}>
                <span className="folder-glyph" aria-hidden="true" dangerouslySetInnerHTML={{ __html: icon.svg }} />
              </button>
            ))}
            {!visible.length && <div className="fie-empty">Ничего не найдено</div>}
          </div>
        </section>
      </div>
    </Modal>
  );
}
