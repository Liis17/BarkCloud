// Пак иконок папок из корня репозитория (FolderIcons/, alias @folder-icons в vite.config.ts).
// SVG вставляются как разметка: это файлы репозитория, их безопасность проверяет folderIcons.test.ts.

export interface FolderIconCategory {
  id: string;
  label: string;
}

export interface FolderIconDef {
  key: string;
  label: string;
  category: string;
  keywords: string[];
  svg: string;
}

interface Manifest {
  version: number;
  categories: FolderIconCategory[];
  icons: Omit<FolderIconDef, 'svg'>[];
}

const manifestModules = import.meta.glob<Manifest>('@folder-icons/manifest.json', { eager: true, import: 'default' });
const svgModules = import.meta.glob<string>('@folder-icons/svg/*.svg', { eager: true, query: '?raw', import: 'default' });

const manifest = Object.values(manifestModules)[0] ?? { version: 1, categories: [], icons: [] };
const svgByKey = new Map(Object.entries(svgModules).map(([path, svg]) => [path.slice(path.lastIndexOf('/') + 1, -'.svg'.length), svg]));

export const FOLDER_ICON_CATEGORIES: FolderIconCategory[] = manifest.categories;
export const FOLDER_ICONS: FolderIconDef[] = manifest.icons
  .filter((icon) => svgByKey.has(icon.key))
  .map((icon) => ({ ...icon, svg: svgByKey.get(icon.key)! }));

const iconByKey = new Map(FOLDER_ICONS.map((icon) => [icon.key, icon]));

/** Иконка по ключу; неизвестный ключ (например, из более нового пака) — null, показывается обычная папка. */
export function findFolderIcon(key: string | null | undefined): FolderIconDef | null {
  return key ? iconByKey.get(key) ?? null : null;
}

/** Цвет глифа поверх подложки: чёрный или белый по контрасту WCAG. */
export function foregroundFor(hex: string): string {
  const channels = [1, 3, 5].map((index) => {
    const c = parseInt(hex.slice(index, index + 2), 16) / 255;
    return c <= 0.03928 ? c / 12.92 : ((c + 0.055) / 1.055) ** 2.4;
  });
  const luminance = 0.2126 * channels[0] + 0.7152 * channels[1] + 0.0722 * channels[2];
  return (luminance + 0.05) / 0.05 >= 1.05 / (luminance + 0.05) ? '#1A1110' : '#FFFFFF';
}
