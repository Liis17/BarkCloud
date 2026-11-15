import React from 'react';
import { Icon } from '../Icon';
import { MediaThumb } from './MediaThumb';
import { GRID_SIZES } from '../../lib/format';
import type { MediaItem } from '../../lib/types';

/** Квадратная плитка медиа сетки `.photo-grid` (страницы «Фото» и «Видео»). */
export function PhotoTile({ m, selecting, checked, onToggle, onOpen, onMenu, badge }: {
  m: MediaItem;
  selecting: boolean;
  checked: boolean;
  onToggle: (shift: boolean) => void;
  onOpen: (m: MediaItem) => void;
  onMenu: (e: React.MouseEvent, m: MediaItem) => void;
  /** Плашка в правом нижнем углу; по умолчанию — «видео» у роликов. */
  badge?: React.ReactNode;
}) {
  const shownBadge = badge !== undefined ? badge : m.kind === 'video' ? <><Icon.play size={10} /> видео</> : null;
  return (
    <div
      className={'photo' + (checked ? ' checked' : '')}
      onClick={(e) => (e.shiftKey ? onToggle(true) : selecting ? onToggle(false) : onOpen(m))}
      onContextMenu={(e) => onMenu(e, m)}
    >
      <MediaThumb media={m} sizes={GRID_SIZES} colorPlaceholder />
      <button className="selbox" onClick={(e) => { e.stopPropagation(); onToggle(e.shiftKey); }} title="Выбрать">
        {checked ? <Icon.check size={14} /> : null}
      </button>
      {shownBadge && <div className="vbadge">{shownBadge}</div>}
    </div>
  );
}
