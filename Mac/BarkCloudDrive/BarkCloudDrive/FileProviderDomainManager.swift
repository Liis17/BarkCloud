import Foundation
import FileProvider
import AppKit
import Observation

/// Регистрация/удаление/приостановка домена File Provider'a.
///
/// В отличие от FSKit, домен — это запись в системе, при появлении которой
/// Finder показывает папку «BarkCloud» в Locations и в
/// `~/Library/CloudStorage/`. Расширение (`BarkCloudFS.appex`) поднимается
/// системным демоном `fileproviderd` по запросу — это и есть аналог
/// монтирования.
///
/// Различаем три действия:
/// - `enable()`  — `add` при первой регистрации, `reconnect` если домен уже
///                 зарегистрирован, но был приостановлен через `disable()`.
/// - `disable()` — `disconnect(.temporary)`: sync паузируется, materialized
///                 (скачанные ранее) файлы **остаются на диске** и будут
///                 доступны после повторного `enable()`.
/// - `purge()`   — `remove`: жёсткое удаление домена и всех его данных в
///                 replica. Вызывается при logout/смене сервера.
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

    private let domain = NSFileProviderDomain(
        identifier: NSFileProviderDomainIdentifier("com.barkfluff.BarkCloud.Drive.MainDomain"),
        displayName: "BarkCloud"
    )

    var isEnabled: Bool { state == .enabled }

    init() {
        Task { await refreshState() }
    }

    /// Проверить, в каком состоянии домен в системе. Disconnected-домен
    /// считается «disabled» с точки зрения UI — пользователь видит кнопку
    /// «Подключить», но `enable()` сделает `reconnect`, а не `add`.
    func refreshState() async {
        let domains = (try? await NSFileProviderManager.domains()) ?? []
        if let existing = domains.first(where: { $0.identifier == domain.identifier }) {
            if existing.isDisconnected {
                state = .disabled
                visibleURL = nil
            } else {
                state = .enabled
                visibleURL = try? await NSFileProviderManager(for: domain)?
                    .getUserVisibleURL(for: .rootContainer)
            }
        } else {
            state = .disabled
            visibleURL = nil
        }
    }

    /// Подключить папку. Если домен уже зарегистрирован (например, был
    /// приостановлен через `disable()`) — делаем `reconnect`, чтобы скачанные
    /// файлы остались на диске. Иначе — первичная регистрация `add`.
    func enable() async {
        guard state != .enabled, state != .enabling else { return }
        state = .enabling
        do {
            let domains = (try? await NSFileProviderManager.domains()) ?? []
            if domains.contains(where: { $0.identifier == domain.identifier }) {
                try await NSFileProviderManager(for: domain)?.reconnect()
            } else {
                try await NSFileProviderManager.add(domain)
            }
            visibleURL = try? await NSFileProviderManager(for: domain)?
                .getUserVisibleURL(for: .rootContainer)
            state = .enabled
        } catch {
            state = .failed(error.localizedDescription)
        }
    }

    /// Приостановить sync без удаления materialized файлов. Папка остаётся
    /// зарегистрированной (видна в Finder с пометкой «недоступно»), но
    /// операции отвергаются. Повторный `enable()` сделает `reconnect`, и
    /// скачанные ранее файлы будут сразу доступны без повторной загрузки.
    func disable() async {
        guard state == .enabled else { return }
        state = .disabling
        do {
            try await NSFileProviderManager(for: domain)?
                .disconnect(reason: "Папка приостановлена",
                            options: [.temporary])
            visibleURL = nil
            state = .disabled
        } catch {
            state = .failed(error.localizedDescription)
        }
    }

    /// Жёсткое удаление домена и его replica на диске. Используется при
    /// logout/смене сервера, чтобы полностью стереть следы предыдущей сессии.
    /// После этого `enable()` снова сделает `add` (новая, пустая replica).
    func purge() async {
        state = .disabling
        do {
            try await NSFileProviderManager.remove(domain)
        } catch {
            // Если домен не был зарегистрирован — это не ошибка для purge.
        }
        visibleURL = nil
        state = .disabled
    }

    /// Открыть папку домена в Finder.
    func revealInFinder() {
        guard let url = visibleURL else { return }
        NSWorkspace.shared.activateFileViewerSelecting([url])
    }
}
