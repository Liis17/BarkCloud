import React from 'react';
import { apiGet, apiPut } from '../../lib/api';
import { tagKey, tagStyle, type UserTag } from '../../lib/tags';
import { Icon } from '../Icon';

export function SearchMetadataEditor({ fileId, initialAlias, initialTags, onSaved }: {
  fileId: string;
  initialAlias: string;
  initialTags: string[];
  onSaved: (saved: { alias: string; tags: string[] }) => void;
}) {
  const [alias, setAlias] = React.useState(initialAlias);
  const [tags, setTags] = React.useState(initialTags);
  const [catalog, setCatalog] = React.useState<UserTag[]>([]);
  const [catalogError, setCatalogError] = React.useState('');
  const [loading, setLoading] = React.useState(true);
  const [query, setQuery] = React.useState('');
  const [open, setOpen] = React.useState(false);
  const [saving, setSaving] = React.useState(false);
  const [message, setMessage] = React.useState('');
  const picker = React.useRef<HTMLDivElement>(null);
  const addButton = React.useRef<HTMLButtonElement>(null);
  const pickerId = React.useId();

  React.useEffect(() => {
    let alive = true;
    apiGet<{ items: UserTag[] }>('/api/settings/tags').then((result) => {
      if (alive) setCatalog(result.items);
    }).catch((e: Error) => { if (alive) setCatalogError(e.message); })
      .finally(() => { if (alive) setLoading(false); });
    return () => { alive = false; };
  }, []);

  React.useEffect(() => {
    if (!open) return;
    const outside = (event: PointerEvent) => {
      if (!picker.current?.contains(event.target as Node)) setOpen(false);
    };
    document.addEventListener('pointerdown', outside);
    return () => document.removeEventListener('pointerdown', outside);
  }, [open]);

  const available = catalog.filter((item) => !tags.some((name) => tagKey(name) === tagKey(item.name))
    && tagKey(item.name).includes(tagKey(query)));
  const dirty = alias !== initialAlias || tags.length !== initialTags.length || tags.some((tag, i) => tag !== initialTags[i]);

  async function save(event: React.FormEvent) {
    event.preventDefault();
    setSaving(true);
    setMessage('');
    try {
      const saved = await apiPut<{ alias: string; tags: string[] }>(`/api/files/${encodeURIComponent(fileId)}/search-metadata`, { alias: alias.trim(), tags });
      setAlias(saved.alias || '');
      setTags(saved.tags || []);
      onSaved({ alias: saved.alias || '', tags: saved.tags || [] });
      setMessage('Сохранено');
    } catch (e) { setMessage((e as Error).message || 'Не удалось сохранить'); }
    finally { setSaving(false); }
  }

  return <form className="prop-search-meta" onSubmit={save}>
    <label className="tag-text-field"><span>Имя для поиска</span><input value={alias} maxLength={120} disabled={saving}
      onChange={(e) => { setAlias(e.target.value); setMessage(''); }} placeholder="Например, важный документ" /></label>
    <div className="prop-search-help">Помогает найти файл, сохраняя его настоящее имя.</div>
    <div className="prop-section">Теги</div>
    <div className="prop-tag-list" aria-label="Теги файла">
      {tags.map((name) => {
        const tag = catalog.find((item) => tagKey(item.name) === tagKey(name));
        return <span className="tag-chip" style={tagStyle(tag?.color)} key={tagKey(name)}><span className="tag-dot" /><span>{name}</span>
          <button type="button" aria-label={`Снять тег ${name}`} disabled={saving} onClick={() => { setTags(tags.filter((item) => item !== name)); setMessage(''); }}><Icon.x size={14} /></button></span>;
      })}
      <div className="tag-picker" ref={picker} onBlur={(event) => { if (!event.currentTarget.contains(event.relatedTarget as Node)) setOpen(false); }}
        onKeyDown={(event) => { if (event.key === 'Escape' && open) { event.preventDefault(); event.stopPropagation(); setOpen(false); addButton.current?.focus(); } }}>
        <button type="button" className="tag-chip tag-add" ref={addButton} disabled={saving || tags.length >= 20} aria-label="Добавить тег" aria-expanded={open}
          aria-controls={open ? pickerId : undefined} onClick={() => { setQuery(''); setOpen(!open); }}><Icon.plus size={18} /></button>
        {open && <div className="tag-picker-menu" id={pickerId}>
          <label className="tag-search"><Icon.search size={18} /><input autoFocus type="search" aria-label="Поиск тегов" placeholder="Поиск тегов" value={query} onChange={(e) => setQuery(e.target.value)} /></label>
          <div className="tag-picker-options" aria-label="Доступные теги">
            {loading ? <div className="prop-empty" role="status">Загрузка…</div> : catalogError ? <div className="prop-err" role="alert">{catalogError}</div> : <>
              {available.map((tag) => <button type="button" className="tag-picker-option" key={tag.id} style={tagStyle(tag.color)}
                onClick={() => { setTags([...tags, tag.name]); setMessage(''); setOpen(false); addButton.current?.focus(); }}><span className="tag-dot" />{tag.name}</button>)}
              {!available.length && <div className="prop-empty">{query ? 'Теги не найдены' : 'Нет доступных тегов'}</div>}
            </>}
          </div>
          <a className="tag-manage-link" href="/settings#tags">Управление тегами в настройках</a>
        </div>}
      </div>
    </div>
    {tags.length >= 20 && <div className="prop-search-help">Максимум 20 тегов на файл.</div>}
    <div className="prop-search-actions"><button className="btn outlined" disabled={saving || !dirty}>{saving ? 'Сохраняем…' : 'Сохранить'}</button>
      {message && <span role="status" className={message === 'Сохранено' ? 'prop-search-ok' : 'prop-err'}>{message}</span>}</div>
  </form>;
}
