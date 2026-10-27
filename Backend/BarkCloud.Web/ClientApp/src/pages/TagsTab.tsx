import React from 'react';
import { apiGet, apiPost } from '../lib/api';
import { tagStyle, type UserTag } from '../lib/tags';
import { Icon } from '../components/Icon';
import { Modal } from '../components/ui/Modal';
import { SearchField, TextField } from '../components/ui/TextField';
import { useConfirm } from '../hooks/useDialog';

export function TagsTab({ active }: { active: boolean }) {
  const [tags, setTags] = React.useState<UserTag[]>([]);
  const [loading, setLoading] = React.useState(true);
  const [error, setError] = React.useState('');
  const [query, setQuery] = React.useState('');
  const [editor, setEditor] = React.useState<UserTag | null>(null);
  const [busy, setBusy] = React.useState(false);
  const [confirmNode, confirm] = useConfirm();

  React.useEffect(() => {
    if (!active) return;
    let alive = true;
    setLoading(true);
    apiGet<{ items: UserTag[] }>('/api/settings/tags').then((result) => {
      if (alive) { setTags(result.items); setError(''); }
    }).catch((e: Error) => { if (alive) setError(e.message); })
      .finally(() => { if (alive) setLoading(false); });
    return () => { alive = false; };
  }, [active]);

  async function save(event: React.FormEvent) {
    event.preventDefault();
    if (!editor || busy) return;
    setBusy(true);
    setError('');
    try {
      const saved = await apiPost<UserTag>('/api/settings/tags/save', editor);
      setTags((current) => [...current.filter((tag) => tag.id !== saved.id), saved].sort((a, b) => a.name.localeCompare(b.name)));
      setEditor(null);
    } catch (e) { setError((e as Error).message); }
    finally { setBusy(false); }
  }

  async function remove(tag: UserTag) {
    if (!await confirm({ title: 'Удалить тег?', message: `«${tag.name}» будет удалён из каталога и со всех ваших файлов.`, confirmLabel: 'Удалить', danger: true })) return;
    setBusy(true);
    setError('');
    try {
      await apiPost('/api/settings/tags/delete', { id: tag.id });
      setTags((current) => current.filter((item) => item.id !== tag.id));
    } catch (e) { setError((e as Error).message); }
    finally { setBusy(false); }
  }

  const filtered = tags.filter((tag) => tag.name.toLocaleLowerCase().includes(query.trim().toLocaleLowerCase()));
  return <div className="tags-settings">
    <div className="tags-heading"><div><h3>Теги</h3><p>Цветные метки помогают находить файлы. Изменения применяются ко всем вашим файлам.</p></div>
      <button className="btn primary" disabled={busy || loading} onClick={() => { setError(''); setEditor({ id: '', name: '', color: '#6750A4' }); }}><Icon.plus size={18} />Создать тег</button></div>
    {error && !editor && <div className="prop-err" role="alert">{error}</div>}
    <SearchField containerClassName="tags-settings-search" leading={<Icon.search size={18} />} label="Поиск тегов" placeholder="Поиск тегов" value={query} onChange={(e) => setQuery(e.target.value)} />
    {loading ? <p role="status">Загрузка тегов…</p> : <div className="tag-catalog">
      {filtered.map((tag) => <div className="tag-catalog-row" key={tag.id}>
        <span className="tag-chip" style={tagStyle(tag.color)}><span className="tag-dot" />{tag.name}</span>
        <div className="tag-row-actions"><button className="btn text" disabled={busy} onClick={() => { setError(''); setEditor({ ...tag }); }} aria-label={`Изменить тег ${tag.name}`}>Изменить</button>
          <button className="icon-btn" disabled={busy} aria-label={`Удалить тег ${tag.name}`} onClick={() => void remove(tag)}><Icon.x size={18} /></button></div>
      </div>)}
      {!filtered.length && <p className="prop-empty">{query ? 'Теги не найдены' : 'Тегов пока нет. Создайте первую метку для файлов.'}</p>}
    </div>}
    {editor && <Modal title={editor.id ? 'Изменить тег' : 'Создать тег'} onClose={busy ? undefined : () => setEditor(null)}>
      <form className="tag-editor" onSubmit={save}>
        <TextField label="Название тега" autoFocus required maxLength={50} value={editor.name} disabled={busy} onChange={(e) => setEditor((current) => current ? { ...current, name: e.target.value } : current)} />
        <label className="tag-color-field">Цвет тега<input type="color" value={editor.color} disabled={busy} onChange={(e) => setEditor({ ...editor, color: e.target.value })} /></label>
        <div><span className="tag-chip" style={tagStyle(editor.color)}><span className="tag-dot" />{editor.name.trim() || 'Название тега'}</span></div>
        {error && <div className="prop-err" role="alert">{error}</div>}
        <div className="prop-search-actions"><button className="btn text" type="button" disabled={busy} onClick={() => setEditor(null)}>Отмена</button><button className="btn primary" disabled={busy || !editor.name.trim()}>{busy ? 'Сохраняем…' : 'Сохранить'}</button></div>
      </form>
    </Modal>}
    {confirmNode}
  </div>;
}
