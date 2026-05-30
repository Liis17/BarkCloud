import Foundation
import Observation

/// Отложенное удаление с возможностью отмены — паттерн «undo-snackbar», как в
/// Gmail. Логика:
/// 1. Элемент сразу убирается из списка (оптимистичное удаление).
/// 2. Внизу появляется snackbar с обратным отсчётом 5 секунд и кнопкой «Отменить».
/// 3. Если время вышло → выполняется `action` (запрос к серверу).
/// 4. Если пользователь нажал «Отменить» → выполняется `onUndo` (восстановление).
/// 5. Если за это время ставят новое удаление → предыдущее **немедленно**
///    исполняется (его `action` уходит на сервер), и snackbar показывает уже новый
///    элемент со своим отсчётом.
///
/// Живёт во ViewModel экрана. При выходе с экрана исполнение фонового action'а
/// доезжает само (Task держит ссылки на нужные значения).
@MainActor
@Observable
final class PendingDelete {
    struct Pending: Identifiable {
        let id = UUID()
        let label: String
        let action: () async -> Void
        let onUndo: (() -> Void)?
    }

    /// Текущее ожидающее удаление; `nil` — snackbar не показывается.
    private(set) var pending: Pending?

    /// Сколько секунд осталось до автоматического подтверждения.
    private(set) var remainingSeconds: Int = 0

    private var timerTask: Task<Void, Never>?
    private let delay: Int = 5

    /// Поставить новое удаление в ожидание. Если уже есть pending — оно
    /// немедленно исполняется (action летит на сервер, snackbar обновляется
    /// под новый элемент).
    func schedule(label: String, action: @escaping () async -> Void, onUndo: (() -> Void)? = nil) {
        if let previous = pending {
            timerTask?.cancel()
            timerTask = nil
            pending = nil
            // Не ждём результата: предыдущий бэкенд-запрос уезжает в фон,
            // UI сразу показывает новый snackbar.
            Task { await previous.action() }
        }

        let item = Pending(label: label, action: action, onUndo: onUndo)
        pending = item
        remainingSeconds = delay
        timerTask = Task { [weak self] in
            guard let self else { return }
            for _ in 0..<self.delay {
                try? await Task.sleep(nanoseconds: 1_000_000_000)
                if Task.isCancelled { return }
                self.remainingSeconds = max(0, self.remainingSeconds - 1)
            }
            await self.complete(itemID: item.id)
        }
    }

    /// Отменить ожидающее удаление и восстановить состояние (вызывает `onUndo`).
    func cancel() {
        guard let current = pending else { return }
        timerTask?.cancel()
        timerTask = nil
        pending = nil
        remainingSeconds = 0
        current.onUndo?()
    }

    /// Дождаться завершения ожидающего удаления (выполняет `action` сразу).
    /// Полезно, например, перед `reload()` — иначе сервер вернёт удаляемый
    /// элемент обратно.
    func flushIfAny() async {
        guard let current = pending else { return }
        timerTask?.cancel()
        timerTask = nil
        pending = nil
        remainingSeconds = 0
        await current.action()
    }

    private func complete(itemID: UUID) async {
        guard let current = pending, current.id == itemID else { return }
        pending = nil
        remainingSeconds = 0
        timerTask = nil
        await current.action()
    }
}
