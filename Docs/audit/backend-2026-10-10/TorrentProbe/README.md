# Локальная проверка torrent paths

```powershell
dotnet run --project Docs/audit/backend-2026-10-10/TorrentProbe/TorrentProbe.csproj --artifacts-path "$env:TEMP/barkcloud-audit-torrent-evidence"
```

Используется настоящий MonoTorrent 3.0.2. Метаданные создаются программно; менеджеры добавляются в engine, но не запускаются. DHT, discovery, port forwarding и listeners отключены; announce отсутствует. Создаётся уникальный каталог внутри temp, его путь печатается. Sentinel-файлы находятся только в этом каталоге. Можно удалить напечатанный каталог после проверки, убедившись в его абсолютном пути.

Вторая часть компилирует настоящий `TorrentController.cs` как linked source. Store, auth context и engine lookup заменены fake: проверяется fallback ветка owned entity при отсутствии manager. Создаётся `PhysicalFileResult`, его исполнение и чтение HTTP body не выполняются. Это проверка построения пути, а не доказательство реальной перезаписи/эксфильтрации через torrent peers.

На аудируемом коде Windows/.NET 10:

```text
SUMMARY loadAccepted=9/11 managersAdded=9 controllerFallbackEscapesUserDir=True sentinelsIntact=True
```

V2 file tree с `..`, v1 single-file `name` с traversal и rooted paths принимаются; рассчитанный `FullPath` выходит за `savePath`. V1 multifile `files[].path` с `..`/rooted component отклоняется. Сам по себе небезопасный `name` для проверенного v2 формата escape не дал. Rooted-path результаты зависят от ОС; повторная проверка в Linux обязательна при исправлении production-контейнера. Программа печатает наблюдения и возвращает 0 при воспроизведении fallback escape с неизменными sentinel; после исправления controller ожидается другой результат.
