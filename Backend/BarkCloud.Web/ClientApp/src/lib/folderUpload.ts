import { ApiError, apiGet, apiPost } from './api';
import type { DirInfo, Listing } from './types';

/** Брошенные или выбранные файлы с относительными путями папок ('' — верхний уровень). */
export interface FolderTree {
  /** Папка каждого файла по индексу, через «/». */
  paths: string[];
  /** Все папки дерева, включая пустые. */
  dirs: string[];
}

/** Код DirectoryNameConflictException: папка с таким именем в родителе уже есть. */
const DIRECTORY_NAME_CONFLICT = 'C0A4E97C-2E73-4D5D-AB1A-7E3DC8A1D8F1';

function readEntries(reader: FileSystemDirectoryReader): Promise<FileSystemEntry[]> {
  return new Promise((resolve, reject) => reader.readEntries(resolve, reject));
}

function entryFile(entry: FileSystemFileEntry): Promise<File | null> {
  return new Promise((resolve) => entry.file(resolve, () => resolve(null)));
}

/**
 * Обходит брошенные записи (webkitGetAsEntry). Записи нужно получить синхронно в обработчике drop —
 * после него DataTransfer очищается; сам обход асинхронный. Нечитаемые файлы пропускаются.
 */
export async function walkEntries(entries: FileSystemEntry[]): Promise<{ files: File[]; tree: FolderTree }> {
  const files: File[] = [];
  const paths: string[] = [];
  const dirs: string[] = [];
  async function visit(entry: FileSystemEntry, dir: string): Promise<void> {
    if (entry.isFile) {
      const file = await entryFile(entry as FileSystemFileEntry);
      if (file) {
        files.push(file);
        paths.push(dir);
      }
    } else if (entry.isDirectory) {
      const path = dir ? `${dir}/${entry.name}` : entry.name;
      dirs.push(path);
      const reader = (entry as FileSystemDirectoryEntry).createReader();
      // readEntries отдаёт содержимое порциями, пустая порция — конец.
      for (let batch = await readEntries(reader); batch.length; batch = await readEntries(reader)) {
        for (const child of batch) await visit(child, path);
      }
    }
  }
  for (const entry of entries) await visit(entry, '');
  return { files, tree: { paths, dirs } };
}

/** Дерево из выбора папки через input webkitdirectory: путь берётся из webkitRelativePath. */
export function treeFromPicked(files: File[]): FolderTree {
  const paths = files.map((file) => {
    const rel = file.webkitRelativePath || '';
    const cut = rel.lastIndexOf('/');
    return cut < 0 ? '' : rel.slice(0, cut);
  });
  return { paths, dirs: [] };
}

/** Создаёт папку или, если она уже есть, возвращает существующую — повторная загрузка дополняет папку. */
async function createOrFindDir(parentId: string, name: string): Promise<string> {
  try {
    return (await apiPost<DirInfo>('/api/cloud/dir', { parentId, name })).id;
  } catch (error) {
    if (!(error instanceof ApiError) || error.code !== DIRECTORY_NAME_CONFLICT) throw error;
    // Подпапки приходят целиком в первой странице листинга, файлы не нужны.
    const listing = await apiGet<Listing>(`/api/cloud/list?dir=${encodeURIComponent(parentId)}&limit=1`);
    const existing = listing.dirs.find((dir) => dir.name === name.trim());
    if (!existing) throw error;
    return existing.id;
  }
}

/**
 * Воссоздаёт дерево внутри baseDir: родители раньше детей, последовательно (без гонок одинаковых имён).
 * Возвращает id папки для каждого файла дерева.
 */
export async function ensureTree(tree: FolderTree, baseDir: string): Promise<string[]> {
  const ids = new Map<string, string>([['', baseDir]]);
  async function ensure(path: string): Promise<string> {
    const known = ids.get(path);
    if (known !== undefined) return known;
    const cut = path.lastIndexOf('/');
    const parent = await ensure(cut < 0 ? '' : path.slice(0, cut));
    const id = await createOrFindDir(parent, path.slice(cut + 1));
    ids.set(path, id);
    return id;
  }
  for (const path of new Set([...tree.dirs, ...tree.paths])) await ensure(path);
  return tree.paths.map((path) => ids.get(path)!);
}
