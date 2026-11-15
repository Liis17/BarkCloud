/** Поле, где клавиши печатают текст: горячие клавиши страницы в нём не срабатывают. */
export function isTextEntry(target: EventTarget | null): boolean {
  if (target instanceof HTMLTextAreaElement || target instanceof HTMLSelectElement) return true;
  if (target instanceof HTMLInputElement) return !['checkbox', 'radio', 'range', 'button', 'submit'].includes(target.type);
  return target instanceof HTMLElement && target.isContentEditable;
}

/** Открыт модальный слой (диалог, просмотрщик, контекстное меню): клавиши принадлежат ему, а не странице под ним. */
export function overlayOpen(): boolean {
  return !!document.querySelector('[aria-modal="true"], .lightbox, .ctx-menu');
}
