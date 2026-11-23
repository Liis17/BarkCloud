# Shared — пак иконок папок FolderIcons

Parent: [[Architecture]] · See also: [[Backend/FilesCloud]] · [[Web/WebApp]] · [[Api/FilesClientGuide]]

## Назначение

`FolderIcons/` в корне репозитория — общий для всех клиентов пак иконок пользовательских папок (медиа, документы, архивы, разработка, игры, работа, личное и 18+, метки и предупреждения вроде «не открывать», «неизвестное», «на проверке» и т. д.). Формат плоский, без тулинга, чтобы его можно было подключать в любом проекте.

Тот же пак даёт иконки типов файлов без превью в Web: `Backend/BarkCloud.Web/ClientApp/src/lib/fileTypeIcons.ts` сопоставляет расширение с ключом пака (запасной путь — `iconKind` сервера, затем вид медиа, затем общий `file`), `components/ui/FileTypeIcon.tsx` рисует глиф на цвете `iconKind`. Иконки, добавленные ради типов файлов (`file`, `disc`, `subtitles`), тоже видны в выборе значка папки — манифест у пака один.

## Источники

| Путь | Роль |
|---|---|
| `FolderIcons/svg/<key>.svg` | Штриховые SVG 24×24 на `currentColor` |
| `FolderIcons/manifest.json` | Категории, ключи, русские подписи, ключевые слова поиска, происхождение (`source`) |
| `FolderIcons/README.md` | Правила формата и добавления иконок |
| `FolderIcons/LICENSE-lucide` | ISC-лицензия Lucide: большинство иконок взято из `lucide-static` (`source: "lucide:<имя>"`); собственные рисунки отмечены `source: "barkcloud"` |

## Контракт

- Ключ иконки хранится в `CloudDirectory.IconKey` и передаётся в `DirectoryInfo.icon_key` ([[Backend/FilesCloud]]). Поэтому ключи только добавляются: переименование или удаление ломает уже сохранённые папки.
- Сервер пак не знает и проверяет только формат ключа (`^[a-z0-9]+(-[a-z0-9]+)*$`, ≤ 64). Клиент, которому ключ неизвестен, показывает обычную папку.
- Цвет глифа клиент выбирает сам по контрасту с подложкой (`icon_color`), поэтому SVG используют только `currentColor`.

## Сборка

- Web подключает пак через alias `@folder-icons` и `import.meta.glob` (`Backend/BarkCloud.Web/ClientApp/vite.config.ts`, `src/lib/folderIcons.ts`); SVG встраиваются в бандл, отдельных запросов нет. На Windows alias задаётся путём без ведущего `/` перед буквой диска, иначе glob ничего не находит.
- Изменение `FolderIcons/**` пересобирает образ web при push в `dev` (`.github/scripts/ci_plan.py`); полный `Backend/BarkCloud.Web/Dockerfile` копирует пак в client-стадию, повторяя структуру репозитория.
- `src/lib/folderIcons.test.ts` сверяет манифест с файлами, формат ключей и отсутствие опасной разметки в SVG (они вставляются в DOM как разметка). Vitest в CI пока не запускается — тест нужно прогонять локально.
- Android, iOS, Windows Drive и macOS пак пока не используют.
