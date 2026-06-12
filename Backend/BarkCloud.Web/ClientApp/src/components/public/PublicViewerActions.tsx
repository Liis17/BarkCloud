import React from 'react';
import { Icon } from '../Icon';

interface Props {
  /** Имя файла — для атрибута download. */
  name: string;
  /** Ссылка на скачивание оригинала. */
  downloadHref: string;
  /** Тип медиа: для фото добавляется «Скопировать изображение». */
  mediaKind: string;
  /** URL изображения для копирования в буфер (только для фото). */
  imageSrc?: string;
  /** Закрыть вьювер (для оверлеев папки/альбома); на странице /v не передаётся. */
  onClose?: () => void;
}

/** Обрезанная плавающая панель действий для публичных вьюверов (страницы шаринга).
 *  В стиле облачной `.lb-actions`, но без авторизованных действий: скачать,
 *  скопировать изображение (фото), копировать ссылку на страницу, закрыть. */
export function PublicViewerActions({ name, downloadHref, mediaKind, imageSrc, onClose }: Props) {
  const [flash, setFlash] = React.useState<string | null>(null);
  const timer = React.useRef<number | undefined>(undefined);

  function showFlash(msg: string) {
    setFlash(msg);
    window.clearTimeout(timer.current);
    timer.current = window.setTimeout(() => setFlash(null), 1800);
  }
  React.useEffect(() => () => window.clearTimeout(timer.current), []);

  async function copyLink() {
    try {
      await navigator.clipboard.writeText(window.location.href);
      showFlash('Ссылка скопирована');
    } catch {
      showFlash('Не удалось скопировать');
    }
  }
  async function copyImage() {
    try {
      if (!imageSrc) throw new Error();
      const blob = await (await fetch(imageSrc)).blob();
      // Clipboard API принимает только PNG — перегоняем через canvas.
      const bitmap = await createImageBitmap(blob);
      const canvas = document.createElement('canvas');
      canvas.width = bitmap.width;
      canvas.height = bitmap.height;
      canvas.getContext('2d')!.drawImage(bitmap, 0, 0);
      const png = await new Promise<Blob>((res, rej) =>
        canvas.toBlob((b) => (b ? res(b) : rej(new Error())), 'image/png'),
      );
      await navigator.clipboard.write([new ClipboardItem({ 'image/png': png })]);
      showFlash('Изображение скопировано');
    } catch {
      showFlash('Не удалось скопировать изображение');
    }
  }

  return (
    <div className="lb-actions public-actions" onClick={(e) => e.stopPropagation()}>
      <a className="icon-btn" href={downloadHref} download={name} title="Скачать">
        <Icon.download size={20} />
      </a>
      {mediaKind === 'photo' && (
        <button className="icon-btn" title="Скопировать изображение" onClick={copyImage}>
          <Icon.copy size={20} />
        </button>
      )}
      <button className="icon-btn" title="Копировать ссылку" onClick={copyLink}>
        <Icon.link size={20} />
      </button>
      {onClose && (
        <button className="icon-btn" title="Закрыть" onClick={onClose}>
          <Icon.x size={20} />
        </button>
      )}
      {flash && <span className="public-actions-flash">{flash}</span>}
    </div>
  );
}
