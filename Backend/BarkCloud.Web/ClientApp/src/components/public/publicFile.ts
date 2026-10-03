import { plural } from '../../lib/format';

const DOCUMENT_EXTS = new Set(['pdf', 'doc', 'docx', 'xls', 'xlsx', 'ppt', 'pptx', 'odt', 'ods', 'odp', 'rtf', 'txt']);

export type PublicTone = 'primary' | 'secondary' | 'tertiary';

export interface PublicFileInfo {
  /** Расширение заглавными буквами; пусто, если его нет или оно слишком длинное для бейджа. */
  ext: string;
  /** Чип «Изображение · HEIC» / «Документ · PDF». */
  typeLabel: string;
  /** Цвет плитки-иконки: фото — tertiary, видео — primary, аудио — tertiary, остальное — secondary. */
  tone: PublicTone;
}

export function describePublicFile(name: string, mediaKind: string): PublicFileInfo {
  const dot = name.lastIndexOf('.');
  const rawExt = dot > 0 ? name.slice(dot + 1).toLowerCase() : '';
  const ext = rawExt && rawExt.length <= 5 ? rawExt.toUpperCase() : '';
  const kind =
    mediaKind === 'photo' ? 'Изображение'
    : mediaKind === 'video' ? 'Видео'
    : mediaKind === 'audio' ? 'Аудиотрек'
    : DOCUMENT_EXTS.has(rawExt) ? 'Документ'
    : 'Файл';
  const tone: PublicTone = mediaKind === 'video' ? 'primary' : mediaKind === 'photo' || mediaKind === 'audio' ? 'tertiary' : 'secondary';
  return { ext, typeLabel: ext ? `${kind} · ${ext}` : kind, tone };
}

export function fmtSize(bytes: number): string {
  if (!bytes) return '';
  const u = ['Б', 'КБ', 'МБ', 'ГБ', 'ТБ'];
  let i = 0;
  let v = bytes;
  while (v >= 1024 && i < u.length - 1) {
    v /= 1024;
    i++;
  }
  return (i === 0 ? v.toFixed(0) : v.toFixed(v < 10 ? 1 : 0)).replace('.', ',') + ' ' + u[i];
}

export function isPublicMedia(mediaKind: string): boolean {
  return mediaKind === 'photo' || mediaKind === 'video';
}

export function countLabel(n: number, one: string, few: string, many: string): string {
  return `${n} ${plural(n, one, few, many)}`;
}
