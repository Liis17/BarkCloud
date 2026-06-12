import React from 'react';
import { Icon } from '../Icon';
import { useContextMenu } from '../ui/ContextMenu';
import { apiGet, proxiedImageUrl } from '../../lib/api';
import { persistVolumeRef } from '../../lib/volume';
import { pickDocumentIcon, useDocumentHead } from '../../hooks/useDocumentHead';
import type { MediaActionsApi } from '../../hooks/useMediaActions';
import type { CardFile, MediaItem } from '../../lib/types';

interface DownloadResponse {
  urls: Record<string, string | null>;
}

interface LightboxProps {
  items?: CardFile[];
  index?: number;
  media?: CardFile;
  onClose?: () => void;
  /** Действия панели под изображением (useMediaActions().api); без них панель не рендерится. */
  actions?: MediaActionsApi;
}

/** Полноэкранный просмотр ОРИГИНАЛА (временная ссылка через /api/files/download).
 *  Листание стрелками/кнопками, зум колесом для фото, перемотка ±5c для видео.
 *  При переданных actions под изображением — плавающая панель действий. */
export function Lightbox({ items, index = 0, media, onClose, actions }: LightboxProps) {
  const list: CardFile[] = Array.isArray(items) ? items : media ? [media] : [];
  const [i, setI] = React.useState(index);
  const [urls, setUrls] = React.useState<Record<string, string | null>>({});
  const [err, setErr] = React.useState<string | null>(null);
  const [scale, setScale] = React.useState(1);
  const [pan, setPan] = React.useState({ x: 0, y: 0 });
  const videoRef = React.useRef<HTMLVideoElement | null>(null);
  const stageRef = React.useRef<HTMLDivElement | null>(null);
  const dragRef = React.useRef<{ x: number; y: number; px: number; py: number } | null>(null);
  const { menu, openAt } = useContextMenu();

  const cur = list[i] || null;
  const fileId = cur && cur.id;
  const isVideo = !!cur && cur.kind === 'video';

  useDocumentHead(
    () => ({ title: cur?.name || null, iconUrl: pickDocumentIcon(cur) }),
    [cur?.id, cur?.name, cur?.jpegViewUrl, cur?.previews],
    20,
  );

  React.useEffect(() => {
    let alive = true;
    setErr(null);
    if (!fileId || urls[fileId]) return;
    apiGet<DownloadResponse>('/api/files/download?ids=' + encodeURIComponent(fileId))
      .then((d) => {
        if (alive) setUrls((u) => ({ ...u, [fileId]: (d.urls && d.urls[fileId]) || null }));
      })
      .catch((e) => {
        if (alive) setErr(e.message);
      });
    return () => {
      alive = false;
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [fileId]);

  React.useEffect(() => {
    setScale(1);
    setPan({ x: 0, y: 0 });
  }, [i]);

  // Родитель убрал элемент(ы) из items (например, после удаления) — корректируем индекс.
  React.useEffect(() => {
    if (list.length === 0) {
      onClose && onClose();
      return;
    }
    if (i >= list.length) setI(list.length - 1);
  }, [list.length, i, onClose]);

  const go = React.useCallback(
    (delta: number) => {
      setI((prev) => Math.min(list.length - 1, Math.max(0, prev + delta)));
    },
    [list.length],
  );

  React.useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') {
        onClose && onClose();
        return;
      }
      if (e.key === 'ArrowLeft') {
        if (isVideo && videoRef.current) videoRef.current.currentTime = Math.max(0, videoRef.current.currentTime - 5);
        else go(-1);
      } else if (e.key === 'ArrowRight') {
        if (isVideo && videoRef.current) {
          const d = videoRef.current.duration || Infinity;
          videoRef.current.currentTime = Math.min(d, videoRef.current.currentTime + 5);
        } else go(1);
      }
    };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [onClose, isVideo, go]);

  React.useEffect(() => {
    const el = stageRef.current;
    if (!el) return;
    const onWheel = (e: WheelEvent) => {
      if (isVideo) return;
      e.preventDefault();
      setScale((s) => Math.min(5, Math.max(1, +(s - Math.sign(e.deltaY) * 0.25).toFixed(2))));
    };
    el.addEventListener('wheel', onWheel, { passive: false });
    return () => el.removeEventListener('wheel', onWheel);
  }, [isVideo]);

  if (!cur) return null;
  // Оригинал (временная ссылка) — для скачивания, видео и фолбэка фото.
  const url = fileId ? urls[fileId] : null;
  // Для фото показываем JpegView (HEIC и пр. браузеро-недружелюбные форматы), если он есть.
  const photoSrc = isVideo ? null : (cur.jpegViewUrl && cur.jpegViewUrl.length > 0 ? cur.jpegViewUrl : url);
  const ready = isVideo ? !!url : !!photoSrc;
  // Для действий панели: элементы галерей — MediaItem, у прочих entryIds может не быть.
  const curM = cur as MediaItem;
  const hasEntry = !!(curM.entryIds && curM.entryIds.length > 0);

  function onMouseDown(e: React.MouseEvent) {
    if (isVideo || scale <= 1) return;
    e.preventDefault();
    dragRef.current = { x: e.clientX, y: e.clientY, px: pan.x, py: pan.y };
  }
  function onMouseMove(e: React.MouseEvent) {
    if (!dragRef.current) return;
    setPan({
      x: dragRef.current.px + (e.clientX - dragRef.current.x),
      y: dragRef.current.py + (e.clientY - dragRef.current.y),
    });
  }
  function endDrag() {
    dragRef.current = null;
  }

  function openShareMenu(e: React.MouseEvent) {
    if (!actions) return;
    openAt(e, [
      { label: 'Создать публичную ссылку', icon: 'share', onClick: () => actions.createPublicLink(curM) },
      { label: 'Копировать временную ссылку', icon: 'link', onClick: () => actions.copyTempLink(curM) },
      { label: 'Поделиться с пользователем', icon: 'user', onClick: () => actions.shareWithUser(curM) },
    ]);
  }
  function openAlbumMenu(e: React.MouseEvent) {
    if (!actions) return;
    actions.membership.ensureLoaded();
    const inAlbums = actions.membership.of(curM.id);
    const available = actions.albums.filter((a) => !inAlbums.has(a.id));
    openAt(
      e,
      available.length
        ? available.map((a) => ({ label: a.name, onClick: () => actions.addToAlbum(curM, a.id) }))
        : [{ label: actions.albums.length ? 'Уже во всех альбомах' : 'Нет альбомов', disabled: true }],
    );
  }
  async function copyImage() {
    if (!actions) return;
    try {
      if (!photoSrc) throw new Error('Изображение ещё не загружено');
      // Через same-origin прокси — иначе чужой origin Files даёт CORS/tainted-canvas.
      const blob = await (await fetch(proxiedImageUrl(photoSrc))).blob();
      // Clipboard API принимает только PNG — перегоняем через canvas.
      const bitmap = await createImageBitmap(blob);
      const canvas = document.createElement('canvas');
      canvas.width = bitmap.width;
      canvas.height = bitmap.height;
      canvas.getContext('2d')!.drawImage(bitmap, 0, 0);
      const png = await new Promise<Blob>((res, rej) =>
        canvas.toBlob((b) => (b ? res(b) : rej(new Error('Не удалось подготовить изображение'))), 'image/png'),
      );
      await navigator.clipboard.write([new ClipboardItem({ 'image/png': png })]);
      actions.toast('Изображение скопировано');
    } catch {
      actions.toast('Не удалось скопировать изображение', 'err');
    }
  }

  return (
    <div className="lightbox" onClick={onClose} onMouseMove={onMouseMove} onMouseUp={endDrag} onMouseLeave={endDrag}>
      <button className="lb-close icon-btn" onClick={onClose} title="Закрыть">
        <Icon.x size={24} />
      </button>

      {i > 0 && (
        <button className="lb-nav left" onClick={(e) => { e.stopPropagation(); go(-1); }} title="Назад (←)">
          <Icon.chev size={30} />
        </button>
      )}
      {i < list.length - 1 && (
        <button className="lb-nav right" onClick={(e) => { e.stopPropagation(); go(1); }} title="Вперёд (→)">
          <Icon.chev size={30} />
        </button>
      )}

      <div className="lb-stage" ref={stageRef} onClick={(e) => e.stopPropagation()}>
        {err && <div className="lb-msg">Не удалось загрузить: {err}</div>}
        {!err && !ready && (
          <div className="lb-msg">
            <span className="spinner" /> Загрузка…
          </div>
        )}
        {ready && isVideo && (
          <video
            ref={(el) => {
              videoRef.current = el;
              persistVolumeRef(el);
            }}
            src={url!}
            controls
            autoPlay
          />
        )}
        {ready && !isVideo && (
          <img
            src={photoSrc!}
            alt={cur.name || ''}
            draggable={false}
            className={scale > 1 ? 'zoomed' : ''}
            style={{ transform: `translate(${pan.x}px, ${pan.y}px) scale(${scale})` }}
            onMouseDown={onMouseDown}
            onDoubleClick={() => {
              setScale((s) => (s > 1 ? 1 : 2));
              setPan({ x: 0, y: 0 });
            }}
          />
        )}
      </div>

      {actions ? (
        <div className="lb-actions" onClick={(e) => e.stopPropagation()}>
          <button className="icon-btn" title="Поделиться" onClick={openShareMenu}>
            <Icon.share size={20} />
          </button>
          <button className="icon-btn" title="Добавить в альбом" onClick={openAlbumMenu}>
            <Icon.plus size={20} />
          </button>
          <button
            className="icon-btn"
            title={hasEntry ? 'Показать в папке' : 'Файл не привязан к папке'}
            disabled={!hasEntry}
            onClick={() => {
              onClose && onClose();
              actions.revealInFolder(curM);
            }}
          >
            <Icon.folder size={20} />
          </button>
          {url && (
            <a className="icon-btn" href={url} download={cur.name} title="Скачать оригинал">
              <Icon.download size={20} />
            </a>
          )}
          {!isVideo && (
            <button className="icon-btn" title="Скопировать в буфер обмена" onClick={copyImage}>
              <Icon.copy size={20} />
            </button>
          )}
          <button className="icon-btn" title="Свойства" onClick={() => actions.showProperties(curM)}>
            <Icon.info size={20} />
          </button>
          <button className="icon-btn danger" title="Удалить" onClick={() => actions.requestDelete(curM)}>
            <Icon.trash size={20} />
          </button>
        </div>
      ) : (
        url && (
          <a className="lb-download btn" href={url} download={cur.name} onClick={(e) => e.stopPropagation()}>
            <Icon.download size={16} /> Скачать оригинал
          </a>
        )
      )}

      {menu && (
        <div onClick={(e) => e.stopPropagation()} onMouseDown={(e) => e.stopPropagation()}>
          {menu}
        </div>
      )}
    </div>
  );
}
