// Типы JSON-карточек, которые отдаёт бэкенд (см. Rendering/CloudJson.cs и PageDataBuilder.cs).

export type MediaKind = 'photo' | 'video' | 'document' | 'audio' | 'other';

export interface Preview {
  w: number;
  target: number;
  url: string;
}

/** Тех-метаданные видео для тайла галереи (CloudJson.Card.video; только kind === 'video'). */
export interface VideoMeta {
  duration?: number; // секунды
  videoCodec?: string;
  audioCodec?: string;
  bitrate?: number; // бит/с контейнера (≈ сумма аудио+видео)
  hdr?: boolean;
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
  /** Полноразмерный JPEG для просмотра (HEIC и пр.); пусто/нет — показывать оригинал. */
  jpegViewUrl?: string;
  entryNames?: string[];
  entryIds?: string[];
  /** Тех-метаданные видео для тайла (только kind === 'video'); присутствует, если извлечены. */
  video?: VideoMeta;
}

/** Элемент галереи: карточка + записи каталога владельца (CloudJson.MediaItem). */
export interface MediaItem extends CardFile {
  entriesCount: number;
  entryNames: string[];
  entryIds: string[];
  duplicateGroupKey?: string;
}

/** Группа «Воспоминаний» за один год (CloudJson.MemoryGroup). */
export interface MemoryGroup {
  year: number;
  yearsAgo: number;
  totalCount: number;
  items: CardFile[];
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
  viewMode: number; // 0 = сетка, 1 = список
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

/** Событие истории файла (GET /api/files/activity). */
export interface FileActivity {
  id: string;
  fileId: string;
  entryId: string;
  actorUserId: number;
  kind: string;
  summary: string;
  detailsJson: string;
  createdAt: string | null;
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
  isHdr?: boolean;

  // Аудио
  audioTitle?: string;
  audioArtist?: string;
  audioAlbum?: string;
  audioTrackNumber?: number;

  // Документ
  documentAuthor?: string;
  documentTitle?: string;
  documentSubject?: string;
  documentPageCount?: number;
}

/** Публичная ссылка на файл (/api/shares), папку (/api/folder-shares) или альбом (/api/album-shares). */
export interface ShareLink {
  id: string;
  token: string;
  url: string;
  fileId?: string;
  directoryId?: string;
  albumId?: string;
  playlistId?: string;
  name: string;
  createdAt: string | null;
  clickCount: number;
  kind?: 'file' | 'folder' | 'album' | 'musicPlaylist';
  mediaKind?: string;
  previewUrl?: string;
}

/** Ответ курсор-пагинации. */
export interface Page<T> {
  items: T[];
  nextCursorAt: string | null;
  nextCursorId: string | null;
}

export interface MusicTrack {
  file: CardFile;
  title: string;
  artist: string;
  album: string;
  duration: number;
  coverUrl: string;
  largeCoverUrl: string;
  url: string;
  metadata?: FileMetadata | null;
}

export interface MusicPlaylist {
  id: string;
  name: string;
  description: string;
  coverFileId: string;
  coverUrl: string;
  count: number;
  ownerUserId: number;
  canReorder: boolean;
  createdAt: string | null;
  updatedAt: string | null;
}

export interface MusicPlaylistTrack {
  track: MusicTrack;
  position: number;
  addedAt: string | null;
}

export interface SharedMusicPlaylist {
  grantId: string;
  playlist: MusicPlaylist;
  ownerUserId: number;
  sharedAt: string | null;
}

// ───────────────────────── Каркас (GET /api/me) ─────────────────────────

export interface Shell {
  user: { initials: string; displayName: string; role: string; avatarUrl: string };
  storage: { usedLabel: string; totalLabel: string; percent: number; otherPct: number; s3Pct: number };
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
    disk: {
      totalLabel: string;
      usedLabel: string;
      otherLabel: string;
      s3Label: string;
      freeLabel: string;
      usedPct: number;
      otherPct: number;
      s3Pct: number;
    };
  };
  sessions: Session[];
  sessionsHeader: string;
  admin: { enabled: boolean; unlocked: boolean };
  system: { version: string; edition: string; emailEnabled: boolean; registrationEnabled: boolean };
}
