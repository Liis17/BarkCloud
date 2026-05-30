import Foundation
import Observation
import SwiftUI

/// Координатор блокировки приложения: связывает [[AppLockSettings]] (PIN/флаг),
/// [[BiometricGate]] (Face ID/Touch ID) и состояние «разблокировано в текущей
/// сессии» с учётом 30-секундной задержки после ухода в фон.
///
/// `RootView` показывает [[AppLockScreen]], когда `shouldShowLock == true`.
/// Принудительный wipe (3 неверных PIN) делегируется обратно в `AppEnvironment`
/// через коллбэк `onWipe` — там сносятся токены, кеши, vault и сама блокировка.
@MainActor
@Observable
final class AppLockManager {
    let settings: AppLockSettings
    let biometric: BiometricGate

    /// `true` — экран блокировки можно не показывать в текущей сессии.
    private(set) var isUnlocked: Bool

    /// Не показывать блокировку, если приложение было в фоне меньше 30 секунд.
    private static let graceSeconds: TimeInterval = 30
    private var backgroundedAt: Date?

    /// Колбэк для полной очистки данных: ставится из `AppEnvironment` при init.
    var onWipe: (() async -> Void)?

    init(settings: AppLockSettings, biometric: BiometricGate) {
        self.settings = settings
        self.biometric = biometric
        // Холодный старт при включённой блокировке — закрыто.
        self.isUnlocked = !settings.isEnabled
    }

    var shouldShowLock: Bool { settings.isEnabled && !isUnlocked }

    /// Реакция на смену `ScenePhase`. Вызывается из `BarkCloudApp`.
    func handleScenePhase(_ phase: ScenePhase) {
        guard settings.isEnabled else { return }
        switch phase {
        case .active:
            if let bg = backgroundedAt, Date().timeIntervalSince(bg) > Self.graceSeconds {
                isUnlocked = false
            }
            backgroundedAt = nil
        case .inactive, .background:
            if backgroundedAt == nil { backgroundedAt = Date() }
        @unknown default:
            break
        }
    }

    /// Запрос биометрии. Возвращает успех; при ошибке/отмене состояние не меняем.
    func unlockWithBiometric(reason: String) async -> Bool {
        let ok = await biometric.authenticate(reason: reason)
        if ok {
            isUnlocked = true
            settings.resetFailures()
        }
        return ok
    }

    enum PinResult: Equatable {
        case success
        case wrong(remaining: Int)
        case wiped
    }

    /// Проверка PIN. На 3-й ошибке стираем все данные и возвращаем `.wiped`.
    func verifyPin(_ pin: String) async -> PinResult {
        if settings.verify(pin: pin) {
            isUnlocked = true
            settings.resetFailures()
            return .success
        }
        let exhausted = settings.registerFailure()
        if exhausted {
            await performWipe()
            return .wiped
        }
        return .wrong(remaining: settings.remainingAttempts)
    }

    /// Полная очистка: сама блокировка → `onWipe` (стирает сессию, кеши, vault).
    private func performWipe() async {
        settings.disable()
        await onWipe?()
        // Блокировка отключена, экран замка больше не нужен.
        isUnlocked = true
    }
}
