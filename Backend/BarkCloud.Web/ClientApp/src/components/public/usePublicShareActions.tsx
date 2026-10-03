import React from 'react';
import { proxiedImageUrl } from '../../lib/api';
import { useToast } from '../../hooks/useToast';

/** Действия публичных страниц без авторизации: копировать ссылку на страницу и (для фото) само изображение.
 *  Результат — M3 Snackbar; `toastNode` нужно отрисовать рядом с кнопками. */
export function usePublicShareActions(imageSrc?: string) {
  const [toastNode, toast] = useToast();

  const copyLink = React.useCallback(async () => {
    try {
      await navigator.clipboard.writeText(window.location.href);
      toast('Ссылка скопирована');
    } catch {
      toast('Не удалось скопировать', 'err');
    }
  }, [toast]);

  const copyImage = React.useCallback(async () => {
    try {
      if (!imageSrc) throw new Error();
      // Через same-origin прокси — иначе чужой origin Files даёт CORS/tainted-canvas.
      const blob = await (await fetch(proxiedImageUrl(imageSrc))).blob();
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
      toast('Изображение скопировано');
    } catch {
      toast('Не удалось скопировать изображение', 'err');
    }
  }, [imageSrc, toast]);

  return { copyLink, copyImage, toastNode };
}
