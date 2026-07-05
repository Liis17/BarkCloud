using MonoTorrent;

namespace BarkCloud.Torrent.Infrastructure;

public sealed class DuplicateTorrentException(TorrentException inner)
    : InvalidOperationException("Торрент с таким infohash уже зарегистрирован в движке", inner);
