import FSKit
import ExtensionFoundation

/// Точка входа FSKit-расширения (`com.apple.fskit.fsmodule`). FSKit поднимает
/// процесс расширения по запросу контейнер-app на монтирование.
@main
struct BarkCloudFSExtension: UnaryFileSystemExtension {
    let fileSystem = BarkCloudUnaryFileSystem()
}
