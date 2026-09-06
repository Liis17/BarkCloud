import React from 'react';
import { Modal } from '../ui/Modal';
import { Icon } from '../Icon';
import { apiPost } from '../../lib/api';
import type { DynamicFolder, DynamicFolderRule } from '../../lib/types';
import type { ToastPush } from '../../hooks/useToast';

// Коды совпадают с proto DfField / DfOperator / DfCombinator.
type Op = { v: number; label: string };
type FieldKind = 'date' | 'size' | 'num' | 'text' | 'ext' | 'mediakind';
type FieldDef = { v: number; label: string; ops: Op[]; kind: FieldKind };

const DATE_OPS: Op[] = [
  { v: 1, label: 'за последние N дней' },
  { v: 2, label: 'до даты' },
  { v: 3, label: 'после даты' },
];
const NUM_OPS: Op[] = [
  { v: 4, label: 'больше' },
  { v: 5, label: 'меньше' },
  { v: 7, label: 'равно' },
];
const NAME_OPS: Op[] = [
  { v: 6, label: 'содержит' },
  { v: 9, label: 'начинается с' },
  { v: 8, label: 'заканчивается на' },
  { v: 7, label: 'равно' },
];
const DEVICE_OPS: Op[] = [
  { v: 7, label: 'равно' },
  { v: 6, label: 'содержит' },
  { v: 9, label: 'начинается с' },
  { v: 8, label: 'заканчивается на' },
];

const FIELDS: FieldDef[] = [
  { v: 1, label: 'Дата загрузки', ops: DATE_OPS, kind: 'date' },
  { v: 2, label: 'Дата съёмки', ops: DATE_OPS, kind: 'date' },
  { v: 3, label: 'Размер', ops: NUM_OPS, kind: 'size' },
  { v: 4, label: 'Имя файла', ops: NAME_OPS, kind: 'text' },
  { v: 5, label: 'Формат', ops: [{ v: 7, label: 'равен' }], kind: 'mediakind' },
  { v: 6, label: 'Расширение', ops: [{ v: 8, label: 'заканчивается на' }], kind: 'ext' },
  { v: 7, label: 'Ширина (px)', ops: NUM_OPS, kind: 'num' },
  { v: 8, label: 'Высота (px)', ops: NUM_OPS, kind: 'num' },
  { v: 9, label: 'Устройство загрузки', ops: DEVICE_OPS, kind: 'text' },
  { v: 10, label: 'Устройство из метаданных', ops: DEVICE_OPS, kind: 'text' },
];
const MEDIA_KINDS = [
  { v: '1', label: 'Фото' },
  { v: '2', label: 'Видео' },
  { v: '3', label: 'Документ' },
  { v: '4', label: 'Аудио' },
  { v: '0', label: 'Другое' },
];

const MB = 1048576;

function fieldDef(v: number): FieldDef {
  return FIELDS.find((f) => f.v === v) || FIELDS[0];
}
function defaultValue(kind: FieldKind): string {
  return kind === 'mediakind' ? '1' : '';
}

interface Props {
  folder?: DynamicFolder | null;
  onClose: () => void;
  onSaved: () => void;
  toast: ToastPush;
}

/** Создание / редактирование умной папки: имя + конструктор правил + комбинатор И/ИЛИ. */
export function DynamicFolderFormModal({ folder, onClose, onSaved, toast }: Props) {
  const [name, setName] = React.useState(folder ? folder.name : '');
  const [combinator, setCombinator] = React.useState<number>(folder ? folder.combinator : 0);
  const [viewMode, setViewMode] = React.useState<number>(folder ? folder.viewMode : 0);
  const [rules, setRules] = React.useState<DynamicFolderRule[]>(
    folder && folder.rules.length ? folder.rules.map((r) => ({ ...r })) : [{ field: 4, op: 6, value: '' }],
  );
  const [busy, setBusy] = React.useState(false);

  function setRule(i: number, patch: Partial<DynamicFolderRule>) {
    setRules((rs) => rs.map((r, idx) => (idx === i ? { ...r, ...patch } : r)));
  }
  function changeField(i: number, field: number) {
    const def = fieldDef(field);
    setRule(i, { field, op: def.ops[0].v, value: defaultValue(def.kind) });
  }
  function changeOp(i: number, op: number) {
    // при смене типа оператора (например «за N дней» ↔ «до даты») прежнее значение несовместимо
    setRule(i, { op, value: '' });
  }
  function addRule() {
    setRules((rs) => [...rs, { field: 4, op: 6, value: '' }]);
  }
  function removeRule(i: number) {
    setRules((rs) => rs.filter((_, idx) => idx !== i));
  }

  async function save() {
    if (!name.trim()) {
      toast('Введите название', 'err');
      return;
    }
    const clean = rules.filter((r) => r.value.trim().length > 0);
    if (!clean.length) {
      toast('Добавьте хотя бы одно условие', 'err');
      return;
    }
    setBusy(true);
    try {
      const payload = { name: name.trim(), combinator, rules: clean, viewMode };
      if (folder) await apiPost('/api/dynamic-folders/update', { folder: folder.id, ...payload });
      else await apiPost('/api/dynamic-folders', payload);
      onSaved();
    } catch (e) {
      toast((e as Error).message, 'err');
    } finally {
      setBusy(false);
    }
  }

  return (
    <Modal
      title={folder ? 'Изменить умную папку' : 'Новая умная папка'}
      onClose={onClose}
      wide
      actions={
        <>
          <button className="btn text" onClick={onClose}>
            Отмена
          </button>
          <button className="btn primary" onClick={save} disabled={busy}>
            {busy ? '…' : 'Сохранить'}
          </button>
        </>
      }
    >
      <label className="field-label">Название</label>
      <input type="text" value={name} onChange={(e) => setName(e.target.value)} autoFocus placeholder="Например: Большие видео" />

      <label className="field-label" style={{ marginTop: 14 }}>Файлы попадают, если выполнено</label>
      <div className="df-comb">
        <button type="button" className={'seg' + (combinator === 0 ? ' on' : '')} onClick={() => setCombinator(0)}>
          все условия
        </button>
        <button type="button" className={'seg' + (combinator === 1 ? ' on' : '')} onClick={() => setCombinator(1)}>
          любое условие
        </button>
      </div>

      <div className="df-rules">
        {rules.map((r, i) => {
          const def = fieldDef(r.field);
          return (
            <div className="df-rule" key={i}>
              <select value={r.field} onChange={(e) => changeField(i, Number(e.target.value))}>
                {FIELDS.map((f) => (
                  <option key={f.v} value={f.v}>
                    {f.label}
                  </option>
                ))}
              </select>
              <select value={r.op} onChange={(e) => changeOp(i, Number(e.target.value))}>
                {def.ops.map((o) => (
                  <option key={o.v} value={o.v}>
                    {o.label}
                  </option>
                ))}
              </select>
              <RuleValue def={def} rule={r} onChange={(v) => setRule(i, { value: v })} />
              <button className="icon-btn" title="Удалить условие" onClick={() => removeRule(i)} disabled={rules.length === 1}>
                <Icon.x size={16} />
              </button>
            </div>
          );
        })}
      </div>
      <button className="btn text" onClick={addRule}>
        <Icon.plus size={15} /> Добавить условие
      </button>

      <label className="field-label" style={{ marginTop: 14 }}>Отображение содержимого</label>
      <div className="df-comb">
        <button type="button" className={'seg' + (viewMode === 0 ? ' on' : '')} onClick={() => setViewMode(0)}>
          сеткой
        </button>
        <button type="button" className={'seg' + (viewMode === 1 ? ' on' : '')} onClick={() => setViewMode(1)}>
          списком
        </button>
      </div>
    </Modal>
  );
}

function RuleValue({ def, rule, onChange }: { def: FieldDef; rule: DynamicFolderRule; onChange: (v: string) => void }) {
  if (def.kind === 'mediakind')
    return (
      <select value={rule.value || '1'} onChange={(e) => onChange(e.target.value)}>
        {MEDIA_KINDS.map((k) => (
          <option key={k.v} value={k.v}>
            {k.label}
          </option>
        ))}
      </select>
    );

  if (def.kind === 'date') {
    if (rule.op === 1)
      return <input type="number" min={1} value={rule.value} placeholder="дней" onChange={(e) => onChange(e.target.value)} />;
    return <input type="date" value={rule.value} onChange={(e) => onChange(e.target.value)} />;
  }

  if (def.kind === 'size') {
    const mb = rule.value ? String(Math.round(Number(rule.value) / MB)) : '';
    return (
      <span className="df-suffix">
        <input
          type="number"
          min={0}
          value={mb}
          placeholder="0"
          onChange={(e) => onChange(e.target.value ? String(Math.round(Number(e.target.value) * MB)) : '')}
        />
        МБ
      </span>
    );
  }

  if (def.kind === 'num')
    return (
      <span className="df-suffix">
        <input type="number" min={0} value={rule.value} placeholder="0" onChange={(e) => onChange(e.target.value)} />
        px
      </span>
    );

  if (def.kind === 'ext')
    return <input type="text" value={rule.value} placeholder=".png" onChange={(e) => onChange(e.target.value)} />;

  return <input type="text" value={rule.value} placeholder="текст" onChange={(e) => onChange(e.target.value)} />;
}
