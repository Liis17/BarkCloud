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

private struct BarkRefreshableModifier: ViewModifier {
    let action: () async -> Void

    @State private var isRefreshing = false
    @State private var pullProgress: CGFloat = 0

    /// Полная амплитуда pull в pt, при которой `pullProgress` достигает 1.
    private let pullThreshold: CGFloat = 120

    func body(content: Content) -> some View {
        content
            .refreshable {
                isRefreshing = true
                defer { isRefreshing = false }
                await action()
            }
            .onScrollGeometryChange(for: CGFloat.self) { geo in
                geo.contentOffset.y
            } action: { _, y in
                // contentOffset.y отрицательный при оттягивании вниз.
                pullProgress = max(0, -y) / pullThreshold
            }
            .overlay(alignment: .top) {
                BarkRefreshHeader(
                    pullProgress: pullProgress,
                    isRefreshing: isRefreshing
                )
                .frame(height: 80)
                .allowsHitTesting(false)
            }
    }
}
