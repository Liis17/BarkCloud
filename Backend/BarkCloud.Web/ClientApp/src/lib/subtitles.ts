/** Текст файла субтитров: UTF-8, а если он невалиден — Windows-1251 (частая кодировка русских .srt). */
export function decodeSubtitleBytes(bytes: ArrayBuffer): string {
  try {
    return new TextDecoder('utf-8', { fatal: true }).decode(bytes);
  } catch {
    return new TextDecoder('windows-1251').decode(bytes);
  }
}

/** Приводит SRT к WebVTT для <track>: заголовок WEBVTT и точка вместо запятой в таймкодах. WebVTT возвращается как есть. */
export function toWebVtt(text: string): string {
  const body = text.replace(/^﻿/, '').replace(/\r\n?/g, '\n');
  if (/^WEBVTT/.test(body)) return body;
  const cues = body
    .split('\n')
    .map((line) => (line.includes('-->')
      ? line.replace(/(\d+):(\d{2}):(\d{2}),(\d{3})/g, (_, h: string, m: string, s: string, ms: string) => `${h.padStart(2, '0')}:${m}:${s}.${ms}`)
      : line))
    .join('\n');
  return 'WEBVTT\n\n' + cues;
}
