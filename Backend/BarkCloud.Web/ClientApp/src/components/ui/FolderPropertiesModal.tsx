import React from 'react';
import { apiGet, apiPost } from '../../lib/api';
import type { DirProperties } from '../../lib/types';
import { fmtFull } from '../../lib/format';
import { fmtSize } from '../public/publicFile';
import type { ToastPush } from '../../hooks/useToast';
import { Modal } from './Modal';
import { Switch } from './Switch';

interface FolderPropertiesModalProps {
  directoryId: string;
  onClose: () => void;
  toast: ToastPush;
}

/** Свойства каталога и настройки его видимости. */
export function FolderPropertiesModal({ directoryId, onClose, toast }: FolderPropertiesModalProps) {
  const [properties, setProperties] = React.useState<DirProperties | null>(null);
  const [loading, setLoading] = React.useState(true);
  const [loadError, setLoadError] = React.useState<string | null>(null);
  const [saving, setSaving] = React.useState(false);
  const confirmed = React.useRef<DirProperties | null>(null);

  React.useEffect(() => {
    let alive = true;
    confirmed.current = null;
    setProperties(null);
    setLoading(true);
    setLoadError(null);
    apiGet<DirProperties>('/api/cloud/dir/properties?id=' + encodeURIComponent(directoryId))
      .then((value) => {
        if (!alive) return;
        confirmed.current = value;
        setProperties(value);
      })
      .catch((e) => {
        if (alive) setLoadError((e as Error).message || 'Не удалось загрузить свойства папки');
      })
      .finally(() => {
        if (alive) setLoading(false);
      });
    return () => { alive = false; };
  }, [directoryId]);

  async function save(next: DirProperties) {
    const previous = confirmed.current;
    if (!previous || saving) return;
    setProperties(next);
    setSaving(true);
    try {
      const saved = await apiPost<DirProperties>('/api/cloud/dir/visibility', {
        id: directoryId,
        inherit: next.inherit,
        hideFromGallery: next.hideFromGallery,
        hideFromSearch: next.hideFromSearch,
      });
      confirmed.current = saved;
      setProperties(saved);
    } catch (e) {
      setProperties(previous);
      toast((e as Error).message || 'Не удалось сохранить настройки видимости', 'err');
    } finally {
      setSaving(false);
    }
  }

  function toggleInherit(inherit: boolean) {
    if (!properties) return;
    save({
      ...properties,
      inherit,
      ...(inherit ? {} : {
        hideFromGallery: properties.effectiveHideFromGallery,
        hideFromSearch: properties.effectiveHideFromSearch,
      }),
    });
  }

  function toggleVisibility(key: 'hideFromGallery' | 'hideFromSearch', value: boolean) {
    if (!properties) return;
    save({ ...properties, [key]: value });
  }

  const rows: Array<[string, React.ReactNode]> = properties ? [
    ['Имя', properties.name],
    ['Создана', fmtFull(properties.createdAt)],
    ['Изменена', fmtFull(properties.updatedAt)],
    ['Файлов', String(properties.fileCount)],
    ['Папок', String(properties.folderCount)],
    ['Размер', fmtSize(properties.totalSize) || '0 Б'],
  ] : [];

  return (
    <Modal title="Свойства папки" onClose={onClose} actions={<button className="btn primary" onClick={onClose}>Закрыть</button>}>
      {loadError && <div className="prop-err">Не удалось загрузить свойства: {loadError}</div>}
      {loading && <div className="prop-empty">Загрузка…</div>}
      {properties && (
        <>
          <div className="prop-grid">
            {rows.map(([label, value]) => (
              <React.Fragment key={label}>
                <div className="pk">{label}</div>
                <div className="pv">{value || '—'}</div>
              </React.Fragment>
            ))}
          </div>

          <div className="prop-section">Видимость</div>
          <div className="folder-visibility-list">
            {properties.hasParent && (
              <div className="folder-visibility-row">
                <span>Наследовать настройки родительской папки</span>
                <Switch
                  label="Наследовать настройки родительской папки"
                  on={properties.inherit}
                  disabled={saving}
                  onChange={toggleInherit}
                />
              </div>
            )}
            <div className="folder-visibility-row">
              <span>Скрыть из галереи</span>
              <Switch
                label="Скрыть из галереи"
                on={properties.inherit ? properties.effectiveHideFromGallery : properties.hideFromGallery}
                disabled={saving || properties.inherit}
                onChange={(value) => toggleVisibility('hideFromGallery', value)}
              />
            </div>
            <div className="folder-visibility-row">
              <span>Скрыть из поиска</span>
              <Switch
                label="Скрыть из поиска"
                on={properties.inherit ? properties.effectiveHideFromSearch : properties.hideFromSearch}
                disabled={saving || properties.inherit}
                onChange={(value) => toggleVisibility('hideFromSearch', value)}
              />
            </div>
          </div>
        </>
      )}
    </Modal>
  );
}
