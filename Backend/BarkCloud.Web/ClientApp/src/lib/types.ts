// Типы JSON-карточек, которые отдаёт бэкенд (см. Rendering/CloudJson.cs и PageDataBuilder.cs).

export type MediaKind = 'photo' | 'video' | 'document' | 'audio' | 'other';

export interface Preview {
  w: number;
  target: number;
  url: string;
}

/** Карточка файла-блоба (CloudJson.Card). */
export interface CardFile {
  id: string;
  name: string;
  ext: string;
  kind: MediaKind;
  iconKind: string; // img / vid / doc / pdf / zip / code / audio
  size: number;
  sizeLabel: string;
  width: number;
  height: number;
  previews: Preview[];
  createdAt: string | null;
  uploadedAt: string | null;
}

/** Элемент галереи: карточка + записи каталога владельца (CloudJson.MediaItem). */
export interface MediaItem extends CardFile {
  entriesCount: number;
  entryNames: string[];
  entryIds: string[];
}

/** Группа «Воспоминаний» за один год (CloudJson.MemoryGroup). */
export interface MemoryGroup {
  year: number;
  yearsAgo: number;
  totalCount: number;
  items: CardFile[];
}

/** Точка на карте — медиа с GPS (CloudJson.MapPoint). */
export interface MapPoint {
  id: string;
  lat: number;
  lng: number;
  kind: MediaKind;
  previewUrl: string;
  takenAt: string | null;
  createdAt: string | null;
}

/** Папка (CloudJson.Dir). */
export interface DirInfo {
  id: string;
  parentId: string;
  name: string;
  createdAt: string | null;
  updatedAt: string | null;
}

/** Запись каталога: метаданные + вложенная карточка файла (CloudJson.Entry). */
export interface Entry {
  entryId: string;
  fileId: string;
  directoryId: string;
  name: string;
  createdAt: string | null;
  media: CardFile | null;
}

/** Содержимое папки (GET /api/cloud/list). */
export interface Listing {
  dirs: DirInfo[];
  files: Entry[];
}

/** Запись в корзине (CloudJson.Trash). */
export interface TrashItem {
  entryId: string;
  fileId: string;
  name: string;
  deletedAt: string | null;
  purgeAt: string | null;
  media: CardFile | null;
}

/** Альбом (CloudJson.Album). */
export interface Album {
  id: string;
  name: string;
  description: string;
  coverFileId: string;
  coverUrl: string;
  count: number;
  createdAt: string | null;
  updatedAt: string | null;
}

/** Правило умной папки (поле/оператор/значение — числовые коды совпадают с proto). */
export interface DynamicFolderRule {
  field: number;
  op: number;
  value: string;
}

/** Умная (динамическая) папка (CloudJson.DynamicFolder). */
export interface DynamicFolder {
  id: string;
  name: string;
  isSystem: boolean;
  combinator: number; // 0 = все условия (И), 1 = любое (ИЛИ)
  rules: DynamicFolderRule[];
  iconKey: string;
  coverColor: string;
  coverUrl: string;
  count: number;
  sortOrder: number;
  createdAt: string | null;
  updatedAt: string | null;
}

/** Свойства файла (GET /api/files/info). */
export interface FileInfo extends CardFile {
  etag?: string;
  previewCount?: number;
  uploadDeviceName?: string;
  metadata?: FileMetadata | null;
}

/**
 * Метаданные блоба (EXIF / ffprobe / PDF / Office). Все поля опциональны —
 * сервер отдаёт только заданные, остальные приходят как undefined.
 */
export interface FileMetadata {
  // Общие
  takenAt?: string;
  creatorTool?: string;

  // GPS
  latitude?: number;
  longitude?: number;
  altitude?: number;

  // Камера
  cameraMake?: string;
  cameraModel?: string;
  lensModel?: string;

  // Параметры съёмки
  focalLengthMm?: number;
  fNumber?: number;
  exposureTimeSeconds?: number;
  iso?: number;
  orientation?: number;
  flash?: boolean;

  // Видео
  durationSeconds?: number;
  videoCodec?: string;
  audioCodec?: string;
  bitrate?: number;
  frameRate?: number;

  // Документ
  documentAuthor?: string;
  documentTitle?: string;
  documentSubject?: string;
  documentPageCount?: number;
}

/** Публичная ссылка на файл (/api/shares) или папку (/api/folder-shares). */
export interface ShareLink {
  id: string;
  token: string;
  url: string;
  fileId?: string;
  directoryId?: string;
  name: string;
  createdAt: string | null;
  clickCount: number;
  kind?: 'file' | 'folder';
}

/** Ответ курсор-пагинации. */
export interface Page<T> {
  items: T[];
  nextCursorAt: string | null;
  nextCursorId: string | null;
}

// ───────────────────────── Каркас (GET /api/me) ─────────────────────────

export interface Shell {
  user: { initials: string; displayName: string; role: string; avatarUrl: string };
  storage: { usedLabel: string; totalLabel: string; percent: number };
  app: { version: string; edition: string };
  server: { host: string };
  sync: { status: string; lastAt: string };
}

// ───────────────────────── Настройки (GET /api/settings/full) ─────────────────────────

export interface Session {
  deviceId: string;
  device: string;
  os: string;
  location: string;
  when: string;
  current: boolean;
}

export interface Privacy {
  profileVisibility: number;
  emailVisibility: number;
  lastSeenVisibility: number;
  searchableByUsername: boolean;
}

export interface StorageBreakdown {
  k: string;
  v: string;
  color: string;
  pct: number;
}

export interface SettingsState {
  profile: {
    initials: string;
    firstName: string;
    lastName: string;
    name: string;
    email: string;
    username: string;
    bio: string;
    avatarUrl: string;
    avatarPreviewUrl: string;
  };
  security: { twoFa: boolean; authenticator: boolean; emailOtp: boolean };
  privacy: Privacy;
  storage: {
    used: number;
    total: number;
    unit: string;
    percent: number;
    forecast: string;
    breakdown: StorageBreakdown[];
    freeLabel: string;
    autoUpload: boolean;
    devicesCount: string;
    trashLabel: string;
  };
  sessions: Session[];
  sessionsHeader: string;
  admin: { enabled: boolean; unlocked: boolean };
  system: { version: string; edition: string };
}
