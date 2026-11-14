import { describe, expect, it } from 'vitest';
import { FOLDER_ICON_CATEGORIES, FOLDER_ICONS, findFolderIcon, foregroundFor } from './folderIcons';

const svgFiles = import.meta.glob<string>('@folder-icons/svg/*.svg', { eager: true, query: '?raw', import: 'default' });
const manifestFiles = import.meta.glob<{ icons: { key: string; category: string }[] }>('@folder-icons/manifest.json', { eager: true, import: 'default' });
const manifestIcons = Object.values(manifestFiles)[0].icons;
const fileKeys = Object.keys(svgFiles).map((path) => path.slice(path.lastIndexOf('/') + 1, -4)).sort();

describe('пак FolderIcons', () => {
  it('манифест и SVG-файлы совпадают в обе стороны', () => {
    expect(manifestIcons.map((icon) => icon.key).sort()).toEqual(fileKeys);
    expect(FOLDER_ICONS).toHaveLength(manifestIcons.length);
  });

  it('содержит не меньше 50 иконок с уникальными ключами в формате сервера', () => {
    const keys = manifestIcons.map((icon) => icon.key);
    expect(keys.length).toBeGreaterThanOrEqual(50);
    expect(new Set(keys).size).toBe(keys.length);
    for (const key of keys) {
      expect(key).toMatch(/^[a-z0-9]+(-[a-z0-9]+)*$/);
      expect(key.length).toBeLessThanOrEqual(64);
    }
  });

  it('у каждой иконки известная категория', () => {
    const ids = new Set(FOLDER_ICON_CATEGORIES.map((c) => c.id));
    for (const icon of manifestIcons) expect(ids.has(icon.category)).toBe(true);
  });

  it('SVG безопасны для вставки разметкой и рисуются currentColor в 24×24', () => {
    for (const [path, svg] of Object.entries(svgFiles)) {
      expect(svg, path).toMatch(/^<svg [^>]*viewBox="0 0 24 24"/);
      expect(svg, path).toContain('currentColor');
      expect(svg, path).not.toMatch(/<script|<foreignObject|<style|\son[a-z]+\s*=|href\s*=|url\(/i);
    }
  });
});

describe('findFolderIcon', () => {
  it('возвращает null для пустого и неизвестного ключа', () => {
    expect(findFolderIcon(null)).toBeNull();
    expect(findFolderIcon('icon-from-future-pack')).toBeNull();
    expect(findFolderIcon('music')?.label).toBe('Музыка');
  });
});

describe('foregroundFor', () => {
  it('выбирает контрастный цвет глифа', () => {
    expect(foregroundFor('#FFFFFF')).not.toBe('#FFFFFF');
    expect(foregroundFor('#F9A825')).not.toBe('#FFFFFF');
    expect(foregroundFor('#000000')).toBe('#FFFFFF');
    expect(foregroundFor('#4527A0')).toBe('#FFFFFF');
  });
});
