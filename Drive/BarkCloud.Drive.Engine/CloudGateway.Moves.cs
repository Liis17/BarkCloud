using System.Collections.Concurrent;

using BarkCloud.Drive.Contracts.Localization;
using BarkCloud.Proto.Files;

using Grpc.Core;

namespace BarkCloud.Drive.Engine;

// Отложенный пакетный перенос файла в другую папку без смены имени. Проводник получает успех
// сразу, а записи уходят пачками MoveFileEntries. Пока перенос не подтверждён, наложение
// показывает файл в новой папке в снимках и в каждом загруженном с сервера листинге.
// Если сервер отклонил перенос, наложение снимается, обе папки перечитываются (файл снова
// виден в исходной) и причина уходит в SyncError.
internal sealed partial class CloudGateway
{
    private const int MoveBatchMax = 100;
    private const int MoveMaxAttempts = 3;
    private static readonly TimeSpan MoveWindow = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan MoveDeadline = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MoveDrainLimit = TimeSpan.FromSeconds(40);
    private static readonly TimeSpan OptimisticMoveMaxSnapshotAge = TimeSpan.FromSeconds(15);

    private sealed class PendingMove(string entryId, string sourceDirectoryId, string destinationDirectoryId,
        FileEntryDetailed movedEntry, long accountEpoch)
    {
        public string EntryId { get; } = entryId;
        public string SourceDirectoryId { get; } = sourceDirectoryId;
        public string DestinationDirectoryId { get; } = destinationDirectoryId;
        public FileEntryDetailed MovedEntry { get; } = movedEntry;
        public long AccountEpoch { get; } = accountEpoch;
        public int Attempts { get; set; }
    }

    private readonly object _moveLock = new();      // очередь и таймер
    private readonly object _moveSendLock = new();  // сериализует отправку
    private readonly List<PendingMove> _moveQueue = new();
    private readonly ConcurrentDictionary<string, PendingMove> _pendingMoves = new(); // в очереди или в полёте
    private ITimer? _moveTimer;
    private volatile bool _batchMoveUnsupported;

    internal int PendingMoveCount => _pendingMoves.Count;
    internal bool QueueMovesForTesting { get; set; } = true;

    private void CreateMoveTimer()
        => _moveTimer = _time.CreateTimer(
            _ => SendMovesSafe(drainAll: false, CancellationToken.None), null,
            Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

    // Ставит перенос в очередь и сразу правит снимки обеих папок. false — нужных актуальных
    // снимков нет или в целевой папке занято имя: вызывающий переносит синхронно.
    public bool TryQueueMoveFileEntry(string entryId, string sourceDirectoryId, string destinationDirectoryId)
    {
        if (!QueueMovesForTesting)
            return false;

        // Параллельный перенос в ту же пару папок мог опубликовать снимок между копированием
        // и публикацией — пробуем ещё раз, а не уходим на медленный синхронный путь.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var result = TryQueueMoveOnce(entryId, sourceDirectoryId, destinationDirectoryId, out var sendNow);
            if (result is null)
                continue;
            if (sendNow)
                SendMovesSafe(drainAll: false, CancellationToken.None);
            return result.Value;
        }

        return false;
    }

    // null — снимки изменились во время подготовки копий, нужна повторная попытка.
    private bool? TryQueueMoveOnce(string entryId, string sourceDirectoryId, string destinationDirectoryId,
        out bool sendNow)
    {
        sendNow = false;
        CachedDirectoryListing source;
        CachedDirectoryListing destination;
        long epoch;
        lock (_cacheEpochLock)
        lock (_listLock)
        {
            epoch = Volatile.Read(ref _accountEpoch);
            if (_pendingMoves.ContainsKey(entryId)
                || !TryGetCurrentListingLocked(sourceDirectoryId, epoch, out source)
                || !TryGetCurrentListingLocked(destinationDirectoryId, epoch, out destination))
                return false;
        }

        // Успех отдаётся Проводнику по снимку: старый снимок чаще пропустит конфликт имени,
        // о котором тогда узнали бы только после ответа, поэтому такие переносы идут синхронно.
        var now = _time.GetUtcNow().UtcDateTime;
        var file = source.Listing.Files.FirstOrDefault(item => item.Entry.Id == entryId);
        if (file is null
            || now - source.At >= OptimisticMoveMaxSnapshotAge
            || now - destination.At >= OptimisticMoveMaxSnapshotAge
            || destination.Index.FilesByName.ContainsKey(file.Entry.Name)
            || destination.Index.SubdirectoriesByName.ContainsKey(file.Entry.Name))
            return false;

        // Копии строятся вне глобальной блокировки листингов и правят индексы инкрементально;
        // под блокировкой остаются только перепроверка снимков и публикация.
        var moved = file.Clone();
        moved.Entry.DirectoryId = destinationDirectoryId;
        var sourceListing = CopyDirectoryListingWithoutFile(source.Listing, entryId);
        var sourceIndex = CopyDirectoryListingIndex(source.Index);
        RemoveFileFromIndex(sourceIndex, sourceListing, file);
        var destinationListing = CopyDirectoryListingWithFile(destination.Listing, moved);
        var destinationIndex = CopyDirectoryListingIndex(destination.Index);
        AddFileToIndex(destinationIndex, moved);

        lock (_cacheEpochLock)
        lock (_listLock)
        {
            if (_pendingMoves.ContainsKey(entryId))
                return false;
            if (!TryGetCurrentListingLocked(sourceDirectoryId, epoch, out var currentSource)
                || !TryGetCurrentListingLocked(destinationDirectoryId, epoch, out var currentDestination)
                || !ReferenceEquals(currentSource, source)
                || !ReferenceEquals(currentDestination, destination))
                return null;

            var pending = new PendingMove(entryId, sourceDirectoryId, destinationDirectoryId, moved, epoch);
            _pendingMoves[entryId] = pending;
            PublishListingLocked(sourceDirectoryId, source, sourceListing, sourceIndex,
                listing => CopyDirectoryListingWithoutFile(listing, entryId));
            PublishListingLocked(destinationDirectoryId, destination, destinationListing, destinationIndex,
                listing => UpsertFile(listing, moved));

            lock (_moveLock)
            {
                _moveQueue.Add(pending);
                sendNow = _moveQueue.Count >= MoveBatchMax;
                if (!sendNow)
                    _moveTimer?.Change(MoveWindow, Timeout.InfiniteTimeSpan);
            }
        }

        return true;
    }

    // Досылает все отложенные переносы; вызывается перед операциями, которым важен серверный
    // порядок (другой перенос, удаление, сохранение, удаление папки).
    // Общий лимит не даёт недоступному серверу держать размонтирование или колбэк Dokany
    // дольше одного дедлайна RPC с запасом; неотправленное по истечении считается неизвестным.
    public void FlushPendingMoves(CancellationToken cancellationToken = default)
    {
        if (_pendingMoves.IsEmpty)
            return;

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(MoveDrainLimit);
        SendMovesSafe(drainAll: true, limit.Token);
    }

    // Наложение отложенных переносов на листинг, только что загруженный с сервера.
    private DirectoryListingDetailed ApplyPendingMoves(string directoryId, DirectoryListingDetailed listing)
    {
        if (_pendingMoves.IsEmpty)
            return listing;

        var epoch = Volatile.Read(ref _accountEpoch);
        var moves = _pendingMoves.Values
            .Where(move => move.AccountEpoch == epoch
                           && (move.SourceDirectoryId == directoryId || move.DestinationDirectoryId == directoryId))
            .ToList();
        if (moves.Count == 0)
            return listing;

        var movedIds = moves.Select(move => move.EntryId).ToHashSet(StringComparer.Ordinal);
        var copy = CopyDirectoryListing(listing);
        copy.Files.Clear();
        var files = listing.Files.Where(file => !movedIds.Contains(file.Entry.Id))
            .Concat(moves.Where(move => move.DestinationDirectoryId == directoryId).Select(move => move.MovedEntry))
            .ToList();
        files.Sort(CompareFileEntries);
        copy.Files.AddRange(files);
        return copy;
    }

    private void DropPendingMoves()
    {
        lock (_moveLock)
        {
            _moveQueue.Clear();
            _moveTimer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }
        if (!_pendingMoves.IsEmpty)
            EngineLog.Info($"Сброшены неподтверждённые переносы: {_pendingMoves.Count}; листинги перечитаются с сервера");
        _pendingMoves.Clear();
    }

    private void SendMovesSafe(bool drainAll, CancellationToken cancellationToken)
    {
        try
        {
            SendMoves(drainAll, cancellationToken);
        }
        catch (Exception ex)
        {
            EngineLog.Error("Отправка отложенных переносов", ex);
        }
    }

    private void SendMoves(bool drainAll, CancellationToken cancellationToken)
    {
        // Ожидание чужой отправки тоже ограничено токеном: колбэк Dokany не должен висеть
        // за фоновой отправкой к недоступному серверу.
        if (!cancellationToken.CanBeCanceled)
            Monitor.Enter(_moveSendLock);
        else
        {
            while (!Monitor.TryEnter(_moveSendLock, TimeSpan.FromMilliseconds(100)))
                if (cancellationToken.IsCancellationRequested)
                    return;
        }

        try
        {
            do
            {
                List<PendingMove> batch;
                lock (_moveLock)
                {
                    if (_moveQueue.Count == 0)
                        return;
                    batch = new List<PendingMove>(_moveQueue);
                    _moveQueue.Clear();
                    _moveTimer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
                }

                var epoch = Volatile.Read(ref _accountEpoch);
                foreach (var stale in batch.Where(move => move.AccountEpoch != epoch))
                    _pendingMoves.TryRemove(new KeyValuePair<string, PendingMove>(stale.EntryId, stale));

                var chunks = batch.Where(move => move.AccountEpoch == epoch)
                    .GroupBy(move => move.DestinationDirectoryId)
                    .SelectMany(group => group.Chunk(MoveBatchMax).Select(chunk => (Directory: group.Key, Moves: chunk)))
                    .ToList();
                for (var i = 0; i < chunks.Count; i++)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        // Пакет не уходил на сервер — файл достоверно остаётся в исходной папке.
                        foreach (var move in chunks[i].Moves)
                            FailMove(move, Loc.T("Eng_MoveNotSent"), uncertain: false);
                        continue;
                    }

                    if (SendMoveChunk(chunks[i].Directory, chunks[i].Moves, cancellationToken) || drainAll)
                        continue;

                    // Фоновый проход после сетевой ошибки не перебирает остальные пакеты по дедлайну
                    // каждого: они вернутся в очередь к следующему тику.
                    lock (_moveLock)
                    {
                        _moveQueue.AddRange(chunks.Skip(i + 1).SelectMany(chunk => chunk.Moves));
                        _moveTimer?.Change(MoveWindow, Timeout.InfiniteTimeSpan);
                    }
                    return;
                }
            }
            while (drainAll);
        }
        finally
        {
            Monitor.Exit(_moveSendLock);
        }
    }

    // false — сетевая ошибка: пакет возвращён в очередь.
    private bool SendMoveChunk(string destinationDirectoryId, PendingMove[] chunk, CancellationToken cancellationToken)
    {
        if (!_batchMoveUnsupported)
        {
            var request = new MoveFileEntriesRequest { NewDirectoryId = destinationDirectoryId };
            request.EntryIds.AddRange(chunk.Select(move => move.EntryId));
            try
            {
                var response = _cloud.MoveFileEntries(request, new CallOptions(
                    deadline: _time.GetUtcNow().UtcDateTime.Add(MoveDeadline), cancellationToken: cancellationToken));
                var moved = response.MovedEntryIds.ToHashSet(StringComparer.Ordinal);
                var failed = response.Failed.ToDictionary(item => item.EntryId, item => item.ErrorCode);
                CompleteMoves(chunk.Where(move => moved.Contains(move.EntryId)));
                foreach (var move in chunk.Where(move => !moved.Contains(move.EntryId) && failed.ContainsKey(move.EntryId)))
                    FailMove(move, DescribeMoveFailure(failed[move.EntryId], null), uncertain: false);
                // Запись без результата в ответе — исход неизвестен; повтор пакета идемпотентен.
                var unanswered = chunk.Where(move => !moved.Contains(move.EntryId) && !failed.ContainsKey(move.EntryId))
                    .ToArray();
                if (unanswered.Length > 0)
                    RequeueMoves(unanswered, new InvalidOperationException("MoveFileEntries без результата записи"),
                        allowRetry: true);
                return true;
            }
            catch (RpcException ex) when (ex.StatusCode == StatusCode.Unimplemented)
            {
                // Старый сервер без MoveFileEntries — переносим по одной записи.
                _batchMoveUnsupported = true;
            }
            catch (RpcException ex) when (IsMoveRejected(ex))
            {
                foreach (var move in chunk)
                    FailMove(move, DescribeMoveFailure(null, ex), uncertain: false);
                return true;
            }
            catch (Exception ex)
            {
                EngineLog.Error($"Массовый перенос {chunk.Length} записей", ex);
                RequeueMoves(chunk, ex, allowRetry: true);
                return false;
            }
        }

        for (var i = 0; i < chunk.Length; i++)
        {
            var move = chunk[i];
            try
            {
                _cloud.MoveFileEntry(new MoveFileEntryRequest
                {
                    EntryId = move.EntryId,
                    NewDirectoryId = destinationDirectoryId,
                }, new CallOptions(deadline: _time.GetUtcNow().UtcDateTime.Add(MoveDeadline),
                    cancellationToken: cancellationToken));
                CompleteMoves([move]);
            }
            catch (RpcException ex) when (IsMoveRejected(ex))
            {
                FailMove(move, DescribeMoveFailure(null, ex), uncertain: false);
            }
            catch (Exception ex)
            {
                EngineLog.Error($"Перенос записи {move.EntryId}", ex);
                RequeueMoves([move], ex, allowRetry: true);
                // Остальные записи пакета не отправлялись — возвращаем их без расхода попыток.
                lock (_moveLock)
                    _moveQueue.AddRange(chunk.Skip(i + 1));
                return false;
            }
        }

        return true;
    }

    private static bool IsMoveRejected(RpcException exception)
        => exception.StatusCode == StatusCode.PermissionDenied
           || ServerErrors.HasCode(exception, ServerErrors.FileEntryNotFound)
           || ServerErrors.HasCode(exception, ServerErrors.DirectoryNotFound)
           || ServerErrors.HasCode(exception, ServerErrors.DirectoryNameConflict)
           || ServerErrors.HasCode(exception, ServerErrors.CloudAccessDenied)
           || ServerErrors.HasCode(exception, ServerErrors.CircularMove);

    private static string DescribeMoveFailure(string? errorCode, Exception? exception)
    {
        if (errorCode == ServerErrors.DirectoryNameConflict
            || exception is RpcException conflict && ServerErrors.HasCode(conflict, ServerErrors.DirectoryNameConflict))
            return Loc.T("Eng_MoveNameConflict");
        if (errorCode == ServerErrors.FileEntryNotFound
            || exception is RpcException gone && ServerErrors.HasCode(gone, ServerErrors.FileEntryNotFound))
            return Loc.T("Eng_MoveEntryGone");
        return exception is null ? errorCode ?? "" : ConnectionErrors.GetDisplayMessage(exception);
    }

    // Повтор безопасен: перенос в папку, где запись уже лежит, сервер считает успешным.
    private void RequeueMoves(IEnumerable<PendingMove> moves, Exception error, bool allowRetry)
    {
        var retry = new List<PendingMove>();
        foreach (var move in moves)
        {
            move.Attempts++;
            if (allowRetry && move.Attempts < MoveMaxAttempts)
                retry.Add(move);
            else
                FailMove(move, DescribeMoveFailure(null, error), uncertain: true);
        }

        if (retry.Count == 0)
            return;

        lock (_moveLock)
        {
            _moveQueue.InsertRange(0, retry);
            _moveTimer?.Change(MoveWindow, Timeout.InfiniteTimeSpan);
        }
    }

    private void CompleteMoves(IEnumerable<PendingMove> moves)
    {
        lock (_cacheEpochLock)
        lock (_listLock)
        {
            foreach (var move in moves)
            {
                _pendingMoves.TryRemove(new KeyValuePair<string, PendingMove>(move.EntryId, move));
                // Листинги, загруженные до подтверждения, могли не увидеть перенос — догоняются журналом правок.
                RecordConfirmedMoveLocked(move.SourceDirectoryId,
                    listing => CopyDirectoryListingWithoutFile(listing, move.EntryId));
                RecordConfirmedMoveLocked(move.DestinationDirectoryId, listing => UpsertFile(listing, move.MovedEntry));
            }
        }
    }

    // Итог переноса неизвестен или отрицателен: снимаем наложение и перечитываем обе папки.
    // При неизвестном исходе сервер мог перенос выполнить — сообщение не обещает возврат файла.
    private void FailMove(PendingMove move, string reason, bool uncertain)
    {
        lock (_cacheEpochLock)
        lock (_listLock)
        {
            _pendingMoves.TryRemove(new KeyValuePair<string, PendingMove>(move.EntryId, move));
            InvalidateListingLocked(move.SourceDirectoryId);
            InvalidateListingLocked(move.DestinationDirectoryId);
        }

        var error = Loc.T(uncertain ? "Eng_MoveUnconfirmedFmt" : "Eng_MoveFailedFmt", move.MovedEntry.Entry.Name, reason);
        EngineLog.Info(error);
        try { SyncError?.Invoke(error); }
        catch (Exception ex) { EngineLog.Error("Обновление ошибки синхронизации переноса", ex); }
    }

    private bool TryGetCurrentListingLocked(string directoryId, long accountEpoch, out CachedDirectoryListing cached)
    {
        _listGen.TryGetValue(directoryId, out var generation);
        if (_listCache.TryGetValue(directoryId, out var current)
            && current.Generation == generation
            && current.AccountEpoch == accountEpoch)
        {
            cached = current;
            return true;
        }

        cached = null!;
        return false;
    }

    // Снимок уже отражает перенос; новое поколение с записью в журнале нужно для загрузок,
    // прочитавших сервер до подтверждения: их результат догонится этой же правкой.
    private void RecordConfirmedMoveLocked(string directoryId,
        Func<DirectoryListingDetailed, DirectoryListingDetailed> patch)
    {
        var hasCurrent = TryGetCurrentListingLocked(directoryId, Volatile.Read(ref _accountEpoch), out var cached);
        var generation = BumpListingGenerationWithPatchLocked(directoryId, patch);
        if (hasCurrent)
            _listCache[directoryId] = cached with { Generation = generation };
    }

    private void PublishListingLocked(string directoryId, CachedDirectoryListing cached,
        DirectoryListingDetailed listing, DirectoryListingIndex index,
        Func<DirectoryListingDetailed, DirectoryListingDetailed> patch)
        => _listCache[directoryId] = cached with
        {
            Generation = BumpListingGenerationWithPatchLocked(directoryId, patch),
            Listing = listing,
            Index = index,
        };

    // Идемпотентная вставка: запись с тем же id заменяется, порядок листинга сохраняется.
    private static DirectoryListingDetailed UpsertFile(DirectoryListingDetailed listing, FileEntryDetailed entry)
        => CopyDirectoryListingWithFile(CopyDirectoryListingWithoutFile(listing, entry.Entry.Id), entry);
}
