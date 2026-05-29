import SwiftUI

extension View {
    /// Пуш-ту-рефреш с фирменной лисой-маскотом. Полностью свой (без системного
    /// `.refreshable`), поэтому нет ни системного спиннера, ни подложки под него —
    /// в зазоре видна только лиса. Жест отслеживается через геометрию и фазы
    /// скролла; контент во время обновления опускается вниз, освобождая место.
    func barkRefreshable(action: @escaping () async -> Void) -> some View {
        modifier(BarkRefreshableModifier(action: action))
    }
}

/// Состояние индикатора. Reference-тип (`@Observable`), чтобы частые обновления
/// `pullProgress` из `.onScrollGeometryChange` НЕ инвалидировали `body` модификатора
/// (он это поле не читает) — перерисовывается только overlay-хедер. Иначе на каждом
/// кадре прокрутки пересобирался бы весь модификатор. Задача обновления хранится
/// здесь же, чтобы перерисовки view её не отменяли (раньше это рвало gRPC-запрос —
/// «the transport threw an unexpected error»).
@MainActor
@Observable
final class BarkRefreshState {
    var pullProgress: CGFloat = 0
    var isRefreshing: Bool = false
    private var task: Task<Void, Never>?

    func start(_ action: @escaping () async -> Void) {
        guard !isRefreshing else { return }
        withAnimation(.spring(duration: 0.3)) { isRefreshing = true }
        task = Task {
            await action()
            withAnimation(.spring(duration: 0.3)) { isRefreshing = false }
            task = nil
        }
    }
}

private struct BarkRefreshableModifier: ViewModifier {
    let action: () async -> Void

    @State private var refreshState = BarkRefreshState()

    /// Полная амплитуда pull в pt, при которой `pullProgress` достигает 1 (порог триггера).
    private let pullThreshold: CGFloat = 120
    /// На сколько опускаем контент во время обновления, чтобы лиса была в чистом зазоре.
    private let refreshGap: CGFloat = 72

    func body(content: Content) -> some View {
        content
            // Читаем только isRefreshing (редкий тогл) — pullProgress не трогаем,
            // чтобы прокрутка не пересобирала модификатор.
            .contentMargins(.top, refreshState.isRefreshing ? refreshGap : 0, for: .scrollContent)
            .onScrollGeometryChange(for: CGFloat.self) { geo in
                // Перетягивание за верх. В покое contentOffset.y == -contentInsets.top
                // (у List инсет навбара ненулевой), поэтому вычитаем инсет — иначе
                // прогресс был бы положительным без всякого жеста.
                geo.contentOffset.y + geo.contentInsets.top
            } action: { _, topOverscroll in
                refreshState.pullProgress = max(0, -topOverscroll) / pullThreshold
            }
            .onScrollPhaseChange { _, newPhase in
                // Палец отпущен (скролл встал/тормозит) и порог пройден — запускаем.
                guard newPhase == .idle || newPhase == .decelerating else { return }
                if refreshState.pullProgress >= 1 {
                    refreshState.start(action)
                }
            }
            .overlay(alignment: .top) {
                BarkRefreshHeader(state: refreshState)
                    .frame(height: 80)
                    .allowsHitTesting(false)
            }
    }
}
