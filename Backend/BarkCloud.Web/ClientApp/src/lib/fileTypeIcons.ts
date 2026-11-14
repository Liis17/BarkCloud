// Иконка типа файла из общего пака FolderIcons (см. folderIcons.ts) по расширению.
// Порядок: расширение → iconKind сервера (Rendering/FileKind.cs) → вид медиа → общий «file».
import type { CardFile } from './types';

const BY_EXT: Record<string, string> = {};
function map(key: string, exts: string) {
  for (const ext of exts.split(' ')) BY_EXT[ext] = key;
}
map('image', 'jpg jpeg png gif webp avif heic heif bmp tif tiff svg ico raw dng arw cr2 cr3 nef orf rw2');
map('film', 'mp4 mov mkv avi webm m4v wmv flv 3gp mts m2ts mpg mpeg');
map('music', 'mp3 flac wav ogg oga opus aac m4a wma aif aiff alac');
map('document', 'pdf doc docx odt rtf txt md log pages');
map('spreadsheet', 'xls xlsx ods csv tsv numbers');
map('presentation', 'ppt pptx odp key');
map('file-archive', 'zip rar 7z tar gz tgz bz2 xz zst');
map('disc', 'iso img dmg vhd vhdx vmdk');
map('subtitles', 'srt vtt ass ssa sub');
map('code', 'js jsx ts tsx mjs cjs cs py java kt swift go rs c h cpp hpp rb php html htm css scss json xml yaml yml toml ini');
map('terminal', 'sh bash zsh bat cmd ps1');
map('database', 'sql db sqlite sqlite3 mdb');
map('font', 'ttf otf woff woff2');
map('app-window', 'exe msi');
map('smartphone', 'apk aab ipa');
map('book', 'epub fb2 mobi djvu azw3');
map('pen-tool', 'psd ai fig sketch xd kra');
map('key', 'pem crt cer key p12 pfx');
map('download', 'torrent');

const BY_ICON_KIND: Record<string, string> = {
  img: 'image', vid: 'film', audio: 'music', pdf: 'document', zip: 'file-archive', code: 'code',
};
const BY_KIND: Record<string, string> = { photo: 'image', video: 'film', audio: 'music' };

/** Ключ иконки пака для файла; неизвестный тип — «file». */
export function fileTypeIconKey(media: Pick<CardFile, 'ext' | 'iconKind' | 'kind'> | null | undefined): string {
  const ext = (media?.ext || '').toLowerCase();
  return BY_EXT[ext] || BY_ICON_KIND[media?.iconKind || ''] || BY_KIND[media?.kind || ''] || 'file';
}
