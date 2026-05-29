import Foundation
import Observation

@MainActor
@Observable
final class VaultViewModel {
    enum LockState { case locked, unlocking, unlocked }

    var lockState: LockState = .locked
    var isSelecting = false
    var selection: Set<String> = []

    private let vault: VaultStore
    private let biometric: BiometricGate

    init(vault: VaultStore, biometric: BiometricGate) {
        self.vault = vault
        self.biometric = biometric
    }

    var items: [VaultItem] { vault.items }
    var isEmpty: Bool { vault.isEmpty }

    /// Запросить биометрию и открыть сейф при успехе.
    func unlock() async {
        guard lockState != .unlocked else { return }
        lockState = .unlocking
        let ok = await biometric.authenticate(reason: String(localized: "vault_unlock_reason"))
        lockState = ok ? .unlocked : .locked
    }

    /// Снова запереть (уход в фон / выход с экрана).
    func lock() {
        lockState = .locked
        exitSelection()
    }

    // MARK: - Мультивыбор «убрать из сейфа»

    func enterSelection() { isSelecting = true }

    func exitSelection() {
        isSelecting = false
        selection = []
    }

    func toggle(_ id: String) {
        if selection.contains(id) { selection.remove(id) } else { selection.insert(id) }
    }

    var hasSelection: Bool { !selection.isEmpty }

    /// Убрать выбранные элементы из сейфа — они вернутся в обычную галерею.
    func removeSelected() {
        vault.remove(ids: selection)
        exitSelection()
    }
}
