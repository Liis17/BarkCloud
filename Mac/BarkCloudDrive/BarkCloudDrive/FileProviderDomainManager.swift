import Foundation
import FileProvider
import AppKit
import Observation

/// Регистрация/удаление домена File Provider'a (`NSFileProviderManager.add`).
///
/// В отличие от FSKit (где был отдельный «маунт» через `/sbin/mount`), здесь
/// домен — это запись в системе, при появлении которой Finder показывает
/// папку «BarkCloud» в боковой панели Locations и в `~/Library/CloudStorage/`.
/// File Provider-расширение (`BarkCloudFS.appex`) поднимается системным
/// демоном `fileproviderd` по запросу — это и есть аналог монтирования.
@MainActor
@Observable
final class FileProviderDomainManager {

    enum State: Equatable {
        case disabled, enabling, enabled, disabling
        case failed(String)
    }

    var state: State = .disabled
    /// Пользовательский URL папки домена (заполняется после `enable()`).
    var visibleURL: URL?

    /// Идентификатор и displayName домена. Идентификатор стабилен между
    /// запусками — система ассоциирует с ним постоянное состояние.
    private let domain = NSFileProviderDomain(
        identifier: NSFileProviderDomainIdentifier("com.barkfluff.BarkCloud.Drive.MainDomain"),
        displayName: "BarkCloud"
    )

    var isEnabled: Bool { state == .enabled }

    init() {
        Task { await refreshState() }
    }

    /// Проверить, зарегистрирован ли домен в системе (после перезапуска app).
    func refreshState() async {
        let domains = (try? await NSFileProviderManager.domains()) ?? []
        if domains.contains(where: { $0.identifier == domain.identifier }) {
            state = .enabled
            visibleURL = try? await NSFileProviderManager(for: domain)?
                .getUserVisibleURL(for: .rootContainer)
        } else {
            state = .disabled
            visibleURL = nil
        }
    }

    func enable() async {
        guard state != .enabled, state != .enabling else { return }
        state = .enabling
        do {
            try await NSFileProviderManager.add(domain)
            visibleURL = try? await NSFileProviderManager(for: domain)?
                .getUserVisibleURL(for: .rootContainer)
            state = .enabled
        } catch {
            state = .failed(error.localizedDescription)
        }
    }

    func disable() async {
        guard state == .enabled else { return }
        state = .disabling
        do {
            try await NSFileProviderManager.remove(domain)
            visibleURL = nil
            state = .disabled
        } catch {
            state = .failed(error.localizedDescription)
        }
    }

    /// Открыть папку домена в Finder.
    func revealInFinder() {
        guard let url = visibleURL else { return }
        NSWorkspace.shared.activateFileViewerSelecting([url])
    }
}
