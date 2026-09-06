using BarkCloud.Proto.Torrent;
using BarkCloud.Torrent.Domain;

using Google.Protobuf.WellKnownTypes;

using MonoTorrent;
using MonoTorrent.Client;

namespace BarkCloud.Torrent.Infrastructure;

/// <summary>Сборка proto-ответов из БД-сущности и живого состояния движка.</summary>
public static class TorrentMapper
{
    public static TorrentInfo ToInfo(TorrentEntity entity, TorrentEngineService.ManagedTorrent? managed)
    {
        var info = new TorrentInfo
        {
            Id = entity.Id.ToString(),
            InfoHash = entity.InfoHash,
            Name = entity.Name,
            TotalSize = entity.TotalSize,
            Downloaded = entity.Downloaded,
            Uploaded = entity.Uploaded,
            Progress = entity.Progress,
            Ratio = entity.Downloaded > 0 ? (double)entity.Uploaded / entity.Downloaded : 0,
            AddedAt = Timestamp.FromDateTime(DateTime.SpecifyKind(entity.AddedAt, DateTimeKind.Utc)),
            EtaSeconds = -1,
            Status = (TorrentStatus)entity.Status,
        };

        if (entity.CompletedAt.HasValue)
            info.CompletedAt = Timestamp.FromDateTime(DateTime.SpecifyKind(entity.CompletedAt.Value, DateTimeKind.Utc));

        if (managed != null)
        {
            var m = managed.Manager;
            info.Status = MapStatus(m.State, m.Complete, entity.Paused);
            info.Progress = m.Progress / 100.0;
            info.DownloadSpeed = m.Monitor.DownloadRate;
            info.UploadSpeed = m.Monitor.UploadRate;

            // Живые значения прямо из движка (не из 5-секундного кеша ManagedTorrent).
            info.Seeds = m.Peers.Seeds;
            info.Leechers = m.Peers.Leechs;

            // Живой трафик: накопленная база в DB + дельта текущей сессии, ещё не сброшенная.
            var downDelta = Math.Max(0, m.Monitor.DataBytesReceived - managed.LastSessionDownloaded);
            var upDelta = Math.Max(0, m.Monitor.DataBytesSent - managed.LastSessionUploaded);
            info.Downloaded = entity.Downloaded + downDelta;
            info.Uploaded = entity.Uploaded + upDelta;
            info.Ratio = info.Downloaded > 0 ? (double)info.Uploaded / info.Downloaded : 0;

            if (m.HasMetadata && m.Torrent != null)
                info.TotalSize = m.Torrent.Size;

            // ETA по текущей скорости и остатку.
            var remaining = info.TotalSize - (long)(info.TotalSize * info.Progress);
            info.EtaSeconds = m.Monitor.DownloadRate > 0 && remaining > 0
                ? remaining / m.Monitor.DownloadRate
                : -1;
        }

        return info;
    }

    public static TorrentFileInfo ToFileInfo(ITorrentManagerFile file, int index)
    {
        var downloaded = file.BytesDownloaded();
        return new TorrentFileInfo
        {
            Index = index,
            Path = file.Path.ToString(),
            Size = file.Length,
            Downloaded = downloaded,
            Progress = file.Length > 0 ? (double)downloaded / file.Length : 0,
            Priority = ToProtoPriority(file.Priority),
        };
    }

    public static TorrentFilePriority ToProtoPriority(Priority priority)
        => priority switch
        {
            Priority.DoNotDownload => TorrentFilePriority.Skip,
            Priority.Lowest or Priority.Low => TorrentFilePriority.Low,
            Priority.Normal => TorrentFilePriority.Normal,
            Priority.High or Priority.Highest or Priority.Immediate => TorrentFilePriority.High,
            _ => TorrentFilePriority.Normal,
        };

    public static Priority ToMonoTorrentPriority(TorrentFilePriority priority)
        => priority switch
        {
            TorrentFilePriority.Skip => Priority.DoNotDownload,
            TorrentFilePriority.Low => Priority.Low,
            TorrentFilePriority.Normal => Priority.Normal,
            TorrentFilePriority.High => Priority.High,
            _ => Priority.Normal,
        };

    public static TorrentStatus MapStatus(TorrentState state, bool complete, bool paused)
    {
        if (paused && state is TorrentState.Paused or TorrentState.Stopped or TorrentState.Stopping)
            return complete ? TorrentStatus.Completed : TorrentStatus.Paused;

        return state switch
        {
            TorrentState.Metadata => TorrentStatus.Metadata,
            TorrentState.Downloading or TorrentState.Hashing
                or TorrentState.Starting => TorrentStatus.Downloading,
            TorrentState.Seeding => TorrentStatus.Seeding,
            TorrentState.Paused => TorrentStatus.Paused,
            TorrentState.Stopped or TorrentState.Stopping => complete ? TorrentStatus.Completed : TorrentStatus.Paused,
            TorrentState.Error => TorrentStatus.Error,
            _ => TorrentStatus.Unknown,
        };
    }
}
