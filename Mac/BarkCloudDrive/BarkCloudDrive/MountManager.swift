import Foundation
import Observation

/// Монтирование/размонтирование FSKit-тома BarkCloud из контейнер-приложения.
///
/// ⚠️ **Рантайм-риск (главный технический вопрос Этапа 1, не проверяемо сборкой):**
/// точный механизм монтирования URL-based унарной FSKit-ФС не задокументирован
/// публично. `FSClient` отдаёт только список установленных модулей, без mount-API.
/// Здесь — best-effort обёртка над системным `mount`/`umount` с типом ФС по
/// `FSShortName` («BarkCloud») и ресурсом-URL по схеме `barkcloud://` (см.
/// `FSSupportedSchemes` в Info.plist расширения). На устройстве нужно:
/// 1) включить расширение в System Settings → File System Extensions;
/// 2) проверить фактическую команду монтирования (возможно, потребуется
///    непесочный helper или иной FSKit-вызов).
@MainActor
@Observable
final class MountManager {
    enum State: Equatable {
        case unmounted, mounting, mounted, unmounting
        case failed(String)
    }

    var state: State = .unmounted
    /// Точка монтирования (по умолчанию — в домашней папке пользователя).
    var mountPoint: URL = FileManager.default.homeDirectoryForCurrentUser
        .appendingPathComponent("BarkCloud", isDirectory: true)
    /// Метка тома (= `FSShortName` расширения).
    let fsType = "BarkCloud"

    var isMounted: Bool { state == .mounted }

    func mount() async {
        guard state != .mounted, state != .mounting else { return }
        state = .mounting
        do {
            try FileManager.default.createDirectory(at: mountPoint, withIntermediateDirectories: true)
            let result = try await run("/sbin/mount",
                                       ["-t", fsType, "-o", "nobrowse", "barkcloud://", mountPoint.path])
            if result.code == 0 {
                state = .mounted
            } else {
                state = .failed(result.output.isEmpty ? "mount exit \(result.code)" : result.output)
            }
        } catch {
            state = .failed(error.localizedDescription)
        }
    }

    func unmount() async {
        guard state == .mounted else { return }
        state = .unmounting
        do {
            let result = try await run("/sbin/umount", [mountPoint.path])
            state = result.code == 0 ? .unmounted : .failed(result.output)
        } catch {
            state = .failed(error.localizedDescription)
        }
    }

    /// Запустить процесс и собрать stdout+stderr.
    private func run(_ launchPath: String, _ args: [String]) async throws -> (code: Int32, output: String) {
        try await withCheckedThrowingContinuation { cont in
            let proc = Process()
            proc.executableURL = URL(fileURLWithPath: launchPath)
            proc.arguments = args
            let pipe = Pipe()
            proc.standardOutput = pipe
            proc.standardError = pipe
            proc.terminationHandler = { p in
                let data = pipe.fileHandleForReading.readDataToEndOfFile()
                let out = String(data: data, encoding: .utf8) ?? ""
                cont.resume(returning: (p.terminationStatus, out.trimmingCharacters(in: .whitespacesAndNewlines)))
            }
            do { try proc.run() } catch { cont.resume(throwing: error) }
        }
    }
}
