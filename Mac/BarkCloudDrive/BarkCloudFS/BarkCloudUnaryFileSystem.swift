import Foundation
import FSKit

/// Унарная файловая система BarkCloud: один том на «ресурс» (облако, без блочного
/// устройства). FSKit вызывает `probe`/`load` при монтировании из контейнер-app.
final class BarkCloudUnaryFileSystem: FSUnaryFileSystem, FSUnaryFileSystemOperations {

    func probeResource(resource: FSResource) async throws -> FSProbeResult {
        // Облачная ФС не привязана к носителю — наш ресурс всегда «узнан и пригоден».
        FSProbeResult.usable(name: "BarkCloud", containerID: FSContainerIdentifier(uuid: UUID()))
    }

    func loadResource(resource: FSResource, options: FSTaskOptions) async throws -> FSVolume {
        let services = await BarkCloudSession.current()
        return BarkCloudVolume(label: "BarkCloud",
                               cloud: services.cloud,
                               reader: services.reader,
                               transfer: services.transfer)
    }

    func unloadResource(resource: FSResource, options: FSTaskOptions) async throws {}
}
