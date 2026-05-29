import SwiftUI

/// Индикатор pull-to-refresh: одна пиксельная оранжевая лиса в зазоре, без фона
/// и без линий. Видимость и масштаб ведёт от `pullProgress` (0…1+) при
/// оттягивании, а виляние хвостом идёт непрерывно — и при вытягивании, и при
/// активном обновлении.
struct BarkRefreshHeader: View {
    let state: BarkRefreshState

    private let stageWidth: CGFloat = 120
    private let stageHeight: CGFloat = 50

    var body: some View {
        let isRefreshing = state.isRefreshing
        let progress = min(max(state.pullProgress, 0), 1)
        // Таймлайн крутится пока сцена видима (тянем или обновляем) — хвост
        // виляет в обеих фазах; на покое паузится.
        return TimelineView(.animation(minimumInterval: 1.0 / 60.0,
                                       paused: !(isRefreshing || progress > 0.001))) { ctx in
            let scale = isRefreshing ? 1 : min(progress * 1.15, 1)
            let alpha = isRefreshing ? 1 : min(progress * 1.2, 1)

            BarkMascot(
                time: ctx.date.timeIntervalSinceReferenceDate,
                scale: scale
            )
            .frame(width: stageWidth, height: stageHeight)
            .padding(.top, 8)
            .frame(maxWidth: .infinity, alignment: .top)
            .opacity(alpha)
        }
    }
}
