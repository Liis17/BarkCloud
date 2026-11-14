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

        bool sendNow;
        lock (_cacheEpochLock)
        lock (_listLock)
        {
            var epoch = Volatile.Read(ref _accountEpoch);
            if (_pendingMoves.ContainsKey(entryId)
                || !TryGetCurrentListingLocked(sourceDirectoryId, epoch, out var source)
                || !TryGetCurrentListingLocked(destinationDirectoryId, epoch, out var destination))
                return false;

            var file = source.Listing.Files.FirstOrDefault(item => item.Entry.Id == entryId);
            if (file is null
                || destination.Index.FilesByName.ContainsKey(file.Entry.Name)
                || destination.Index.SubdirectoriesByName.ContainsKey(file.Entry.Name))
                return false;

            var moved = file.Clone();
            moved.Entry.DirectoryId = destinationDirectoryId;
            var pending = new PendingMove(entryId, sourceDirectoryId, destinationDirectoryId, moved, epoch);
            _pendingMoves[entryId] = pending;
            PublishListingLocked(sourceDirectoryId, source, CopyDirectoryListingWithoutFile(source.Listing, entryId));
            PublishListingLocked(destinationDirectoryId, destination,
                CopyDirectoryListingWithFile(destination.Listing, moved));

            lock (_moveLock)
            {
                _moveQueue.Add(pending);
                sendNow = _moveQueue.Count >= MoveBatchMax;
                if (!sendNow)
                    _moveTimer?.Change(MoveWindow, Timeout.InfiniteTimeSpan);
            }
        }

        if (sendNow)
            SendMovesSafe(drainAll: false, CancellationToken.None);
        return true;
    }

    // Досылает все отложенные переносы; вызывается перед операциями, которым важен серверный
    // порядок (другой перенос, удаление, сохранение, удаление папки).
    public void FlushPendingMoves(CancellationToken cancellationToken = default)
    {
        if (!_pendingMoves.IsEmpty)
            SendMovesSafe(drainAll: true, cancellationToken);
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
        copy.Files.AddRange(listing.Files.Where(file => !movedIds.Contains(file.Entry.Id)));
        foreach (var move in moves.Where(move => move.DestinationDirectoryId == directoryId))
            copy = CopyDirectoryListingWithFile(copy, move.MovedEntry);
        return copy;
    }

    private void DropPendingMoves()
    {
        lock (_moveLock)
        {
            _moveQueue.Clear();
            _moveTimer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }
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
        lock (_moveSendLock)
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

                foreach (var group in batch.Where(move => move.AccountEpoch == epoch)
                             .GroupBy(move => move.DestinationDirectoryId))
                foreach (var chunk in group.Chunk(MoveBatchMax))
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        RequeueMoves(chunk, new OperationCanceledException(cancellationToken), allowRetry: false);
                        continue;
                    }

                    SendMoveChunk(group.Key, chunk, cancellationToken);
                }
            }
            while (drainAll);
        }
    }

    private void SendMoveChunk(string destinationDirectoryId, PendingMove[] chunk, CancellationToken cancellationToken)
    {
        if (!_batchMoveUnsupported)
        {
            var request = new MoveFileEntriesRequest { NewDirectoryId = destinationDirectoryId };
            request.EntryIds.AddRange(chunk.Select(move => move.EntryId));
            try
            {
                var response = _cloud.MoveFileEntries(request, new CallOptions(
                    deadline: _time.GetUtcNow().UtcDateTime.Add(MoveDeadline), cancellationToken: cancellationToken));
                var failed = response.Failed.ToDictionary(item => item.EntryId, item => item.ErrorCode);
                CompleteMoves(chunk.Where(move => !failed.ContainsKey(move.EntryId)));
                foreach (var move in chunk.Where(move => failed.ContainsKey(move.EntryId)))
                    FailMove(move, DescribeMoveFailure(failed[move.EntryId], null));
                return;
            }
            catch (RpcException ex) when (ex.StatusCode == StatusCode.Unimplemented)
            {
                // Старый сервер без MoveFileEntries — переносим по одной записи.
                _batchMoveUnsupported = true;
            }
            catch (RpcException ex) when (IsMoveRejected(ex))
            {
                foreach (var move in chunk)
                    FailMove(move, DescribeMoveFailure(null, ex));
                return;
            }
            catch (Exception ex)
            {
                EngineLog.Error($"Массовый перенос {chunk.Length} записей", ex);
                RequeueMoves(chunk, ex, allowRetry: true);
                return;
            }
        }

        foreach (var move in chunk)
        {
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
                FailMove(move, DescribeMoveFailure(null, ex));
            }
            catch (Exception ex)
            {
                EngineLog.Error($"Перенос записи {move.EntryId}", ex);
                RequeueMoves([move], ex, allowRetry: true);
            }
        }
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
                FailMove(move, DescribeMoveFailure(null, error));
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
                // Листинги, загруженные до подтверждения, могли не увидеть перенос — их не кешируем.
                RenewGenerationLocked(move.SourceDirectoryId);
                RenewGenerationLocked(move.DestinationDirectoryId);
            }
        }
    }

    // Итог переноса неизвестен или отрицателен: снимаем наложение и перечитываем обе папки.
    private void FailMove(PendingMove move, string reason)
    {
        lock (_cacheEpochLock)
        lock (_listLock)
        {
            _pendingMoves.TryRemove(new KeyValuePair<string, PendingMove>(move.EntryId, move));
            InvalidateListingLocked(move.SourceDirectoryId);
            InvalidateListingLocked(move.DestinationDirectoryId);
        }

        var error = Loc.T("Eng_MoveFailedFmt", move.MovedEntry.Entry.Name, reason);
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

    // Новое поколение без сброса снимка: незавершённые загрузки этой папки не попадут в кеш.
    private void RenewGenerationLocked(string directoryId)
    {
        var epoch = Volatile.Read(ref _accountEpoch);
        var hasCurrent = TryGetCurrentListingLocked(directoryId, epoch, out var cached);
        var generation = BumpListingGenerationLocked(directoryId);
        if (hasCurrent)
            _listCache[directoryId] = cached with { Generation = generation };
    }

    private void PublishListingLocked(string directoryId, CachedDirectoryListing cached,
        DirectoryListingDetailed listing)
        => _listCache[directoryId] = cached with
        {
            Generation = BumpListingGenerationLocked(directoryId),
            Listing = listing,
            Index = CreateDirectoryListingIndex(listing),
        };
}
