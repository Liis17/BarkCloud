import SwiftUI

extension View {
    /// Пуш-ту-рефреш с фирменной «сценкой» (пиксельная собачка) в зазоре
    /// между потянутым контентом и тулбаром. Внутри использует системный
    /// `.refreshable` (хаптик/доступность/жест остаются нативные), а сверху
    /// рисует свой overlay-индикатор, маскируя системный спиннер фоном.
    func barkRefreshable(action: @escaping () async -> Void) -> some View {
        modifier(BarkRefreshableModifier(action: action))
    }
}

/// Состояние индикатора. Reference-тип (`@Observable`), чтобы частые обновления
/// `pullProgress` из `.onScrollGeometryChange` НЕ инвалидировали `body` модификатора
/// (он эти поля не читает) — перерисовывается только overlay-хедер. Иначе на каждом
/// кадре прокрутки пересобирался бы `.refreshable`, отменяя задачу обновления вместе
/// с gRPC-запросом («the transport threw an unexpected error»).
@MainActor
@Observable
final class BarkRefreshState {
    var pullProgress: CGFloat = 0
    var isRefreshing: Bool = false
}

private struct BarkRefreshableModifier: ViewModifier {
    let action: () async -> Void

    @State private var refreshState = BarkRefreshState()

    /// Полная амплитуда pull в pt, при которой `pullProgress` достигает 1.
    private let pullThreshold: CGFloat = 120

    func body(content: Content) -> some View {
        content
            .refreshable {
                refreshState.isRefreshing = true
                defer { refreshState.isRefreshing = false }
                await action()
            }
            .onScrollGeometryChange(for: CGFloat.self) { geo in
                // Перетягивание за верх. В покое contentOffset.y == -contentInsets.top
                // (у List инсет навбара ненулевой), поэтому вычитаем инсет — иначе
                // прогресс был бы положительным без всякого жеста.
                geo.contentOffset.y + geo.contentInsets.top
            } action: { _, topOverscroll in
                refreshState.pullProgress = max(0, -topOverscroll) / pullThreshold
            }
            .overlay(alignment: .top) {
                BarkRefreshHeader(state: refreshState)
                    .frame(height: 80)
                    .allowsHitTesting(false)
            }
    }
}
