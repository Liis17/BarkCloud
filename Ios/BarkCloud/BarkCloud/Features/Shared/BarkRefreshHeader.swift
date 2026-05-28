import SwiftUI

/// «Сценка» в зазоре pull-to-refresh: пиксельная собачка над тонкой линией-землёй.
/// Видимость и масштаб ведёт от `pullProgress` (0…1+) при оттягивании,
/// а активная анимация бега — от `isRefreshing` через TimelineView.
struct BarkRefreshHeader: View {
    let pullProgress: CGFloat
    let isRefreshing: Bool

    private let mascotPixelMax: CGFloat = 4
    private let stageWidth: CGFloat = 120
    private let stageHeight: CGFloat = 50

    var body: some View {
        TimelineView(.animation(minimumInterval: 0.15, paused: !isRefreshing)) { ctx in
            let tick = Int(ctx.date.timeIntervalSinceReferenceDate / 0.15)
            let progress = min(max(pullProgress, 0), 1)
            let pixelSize = isRefreshing
                ? mascotPixelMax
                : mascotPixelMax * progress
            let alpha = isRefreshing ? 1 : min(progress * 1.2, 1)

            VStack(spacing: 2) {
                ZStack {
                    // Локальный «коврик» цвета фона маскирует системный спиннер
                    // .refreshable, который рисуется в той же точке.
                    RoundedRectangle(cornerRadius: 14)
                        .fill(Color(.systemBackground))
                        .frame(width: stageWidth, height: stageHeight)

                    BarkMascot(
                        phase: isRefreshing ? .run(tick: tick) : .peek,
                        pixelSize: pixelSize
                    )
                    .frame(width: stageWidth, height: stageHeight)
                }

                // Тонкая «земля» под лапами.
                Rectangle()
                    .fill(Color.secondary.opacity(0.25))
                    .frame(width: stageWidth - 24, height: 1)
            }
            .padding(.top, 8)
            .frame(maxWidth: .infinity, alignment: .top)
            .opacity(alpha)
        }
    }
}
