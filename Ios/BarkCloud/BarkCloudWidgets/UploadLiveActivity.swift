import ActivityKit
import SwiftUI
import WidgetKit

/// Live Activity для агрегированной фоновой загрузки в BarkCloud. Видна и на
/// Lock Screen (баннер), и в Dynamic Island (compact/expanded/minimal), и
/// поверх Home Screen у iPhone 14 Pro+. Не «съезжает» при открытии main app —
/// именно для этого ActivityKit и придуман.
///
/// Все три представления используют общий язык:
/// - фирменный оранжевый акцент (`AccentOrange`)
/// - пульсирующая иконка облака (`.symbolEffect(.pulse)`)
/// - анимированный шиммер на прогресс-баре
/// - `.contentTransition(.numericText())` на счётчиках — цифры плавно «прокручиваются»
struct UploadLiveActivity: Widget {
    var body: some WidgetConfiguration {
        ActivityConfiguration(for: UploadActivityAttributes.self) { context in
            LockScreenView(state: context.state)
                .padding(.horizontal, 18)
                .padding(.vertical, 14)
                .activityBackgroundTint(.black.opacity(0.9))
                .activitySystemActionForegroundColor(.white)
        } dynamicIsland: { context in
            DynamicIsland {
                DynamicIslandExpandedRegion(.leading) {
                    ZStack {
                        Circle()
                            .fill(AccentOrange.opacity(0.22))
                            .frame(width: 36, height: 36)
                        Image(systemName: iconName(for: context.state))
                            .font(.system(size: 17, weight: .semibold))
                            .foregroundStyle(AccentOrange)
                            .symbolEffect(.pulse, options: .repeating, isActive: !context.state.isFinished && !needsForeground(context.state))
                    }
                }
                DynamicIslandExpandedRegion(.trailing) {
                    if needsForeground(context.state) {
                        Image(systemName: "hand.tap.fill")
                            .font(.system(size: 16, weight: .semibold))
                            .foregroundStyle(AccentOrange)
                    } else {
                        Text("\(context.state.completedFiles)/\(context.state.totalFiles)")
                            .font(.system(size: 16, weight: .semibold))
                            .monospacedDigit()
                            .foregroundStyle(.white)
                            .contentTransition(.numericText())
                    }
                }
                DynamicIslandExpandedRegion(.center) {
                    if needsForeground(context.state) {
                        Text("Откройте BarkCloud, чтобы продолжить")
                            .font(.system(size: 14, weight: .semibold))
                            .lineLimit(2)
                            .multilineTextAlignment(.center)
                            .foregroundStyle(.white)
                    } else {
                        Text(context.state.currentFileName.isEmpty
                             ? "BarkCloud"
                             : context.state.currentFileName)
                            .font(.system(size: 14, weight: .medium))
                            .lineLimit(1)
                            .truncationMode(.middle)
                            .foregroundStyle(.white.opacity(0.85))
                    }
                }
                DynamicIslandExpandedRegion(.bottom) {
                    if !needsForeground(context.state) {
                        ShimmerProgressBar(
                            progress: context.state.overallProgress,
                            animating: !context.state.isFinished
                        )
                        .frame(height: 6)
                        .padding(.top, 2)
                    }
                }
            } compactLeading: {
                Image(systemName: iconName(for: context.state))
                    .foregroundStyle(AccentOrange)
                    .symbolEffect(.pulse, options: .repeating, isActive: !context.state.isFinished && !needsForeground(context.state))
            } compactTrailing: {
                if needsForeground(context.state) {
                    Image(systemName: "hand.tap.fill")
                        .font(.system(size: 12, weight: .semibold))
                        .foregroundStyle(AccentOrange)
                } else {
                    CompactRing(progress: context.state.overallProgress)
                        .frame(width: 18, height: 18)
                        .overlay {
                            Text("\(context.state.completedFiles)")
                                .font(.system(size: 9, weight: .semibold))
                                .monospacedDigit()
                                .foregroundStyle(.white)
                                .contentTransition(.numericText())
                        }
                }
            } minimal: {
                if needsForeground(context.state) {
                    Image(systemName: "hand.tap.fill")
                        .font(.system(size: 12, weight: .semibold))
                        .foregroundStyle(AccentOrange)
                } else {
                    BreathingRing(progress: context.state.overallProgress, animating: !context.state.isFinished)
                }
            }
            .keylineTint(AccentOrange)
        }
    }

    private func iconName(for state: UploadActivityAttributes.ContentState) -> String {
        if needsForeground(state) { return "icloud.slash.fill" }
        if state.isFinished && state.failedFiles == 0 { return "checkmark.icloud.fill" }
        if state.failedFiles > 0 && state.isFinished { return "exclamationmark.icloud.fill" }
        return "icloud.and.arrow.up.fill"
    }

    private func needsForeground(_ state: UploadActivityAttributes.ContentState) -> Bool {
        state.requiresForeground == true && !state.isFinished
    }
}

/// Фирменный оранжевый акцент. Дублирует `AppColors.accent` main app — но
/// здесь WidgetExtension target, у которого нет доступа к AppColors, поэтому
/// держим литералом.
private let AccentOrange = Color(red: 1.0, green: 0.46, blue: 0.16)

// MARK: - Lock Screen

private struct LockScreenView: View {
    let state: UploadActivityAttributes.ContentState

    private var needsForeground: Bool {
        state.requiresForeground == true && !state.isFinished
    }

    var body: some View {
        VStack(spacing: 12) {
            HStack(spacing: 12) {
                ZStack {
                    Circle()
                        .fill(
                            RadialGradient(
                                colors: [AccentOrange.opacity(0.45), AccentOrange.opacity(0.15)],
                                center: .center,
                                startRadius: 2,
                                endRadius: 26
                            )
                        )
                        .frame(width: 48, height: 48)
                    Image(systemName: iconName)
                        .font(.system(size: 22, weight: .semibold))
                        .foregroundStyle(.white)
                        .symbolEffect(.pulse, options: .repeating, isActive: !state.isFinished && !needsForeground)
                }
                VStack(alignment: .leading, spacing: 2) {
                    Text(titleText)
                        .font(.system(size: 16, weight: .semibold))
                        .foregroundStyle(.white)
                    Text(subtitleText)
                        .font(.system(size: 13))
                        .lineLimit(2)
                        .truncationMode(.middle)
                        .foregroundStyle(.white.opacity(0.7))
                }
                Spacer(minLength: 8)
                if !needsForeground {
                    Text("\(state.completedFiles)/\(state.totalFiles)")
                        .font(.system(size: 17, weight: .semibold))
                        .monospacedDigit()
                        .foregroundStyle(.white)
                        .contentTransition(.numericText())
                }
            }
            if !needsForeground {
                ShimmerProgressBar(
                    progress: state.overallProgress,
                    animating: !state.isFinished
                )
                .frame(height: 6)
            }
        }
    }

    private var iconName: String {
        if needsForeground { return "icloud.slash.fill" }
        if state.isFinished && state.failedFiles == 0 { return "checkmark.icloud.fill" }
        if state.failedFiles > 0 && state.isFinished { return "exclamationmark.icloud.fill" }
        return "icloud.and.arrow.up.fill"
    }

    private var titleText: String {
        if needsForeground { return "Загрузка приостановлена" }
        if state.isFinished && state.failedFiles == 0 { return "Загрузка завершена" }
        if state.failedFiles > 0 && state.isFinished { return "Не все файлы загружены" }
        return "Загружаю в BarkCloud"
    }

    private var subtitleText: String {
        if needsForeground { return "Откройте BarkCloud, чтобы продолжить загрузку" }
        return state.currentFileName.isEmpty ? " " : state.currentFileName
    }
}

// MARK: - Анимированные элементы

/// Прогресс-бар с шиммером поверх. Использует TimelineView, чтобы Live Activity
/// рендерилась плавно даже когда main app не запущен (Activity-снимок обновляется
/// системой по тайм-линии, а внутри SwiftUI крутит локальную анимацию).
private struct ShimmerProgressBar: View {
    let progress: Double
    let animating: Bool

    var body: some View {
        GeometryReader { geo in
            let width = geo.size.width
            let clamped = max(0.02, min(1, progress))
            let filledWidth = width * clamped

            ZStack(alignment: .leading) {
                Capsule()
                    .fill(.white.opacity(0.12))
                Capsule()
                    .fill(
                        LinearGradient(
                            colors: [AccentOrange.opacity(0.85), AccentOrange],
                            startPoint: .leading,
                            endPoint: .trailing
                        )
                    )
                    .frame(width: filledWidth)
                    .animation(.easeOut(duration: 0.3), value: progress)

                if animating {
                    TimelineView(.animation(minimumInterval: 1.0 / 30, paused: !animating)) { ctx in
                        let t = ctx.date.timeIntervalSinceReferenceDate
                        let phase = (t.truncatingRemainder(dividingBy: 1.4)) / 1.4
                        Capsule()
                            .fill(
                                LinearGradient(
                                    colors: [
                                        .white.opacity(0),
                                        .white.opacity(0.55),
                                        .white.opacity(0)
                                    ],
                                    startPoint: .leading,
                                    endPoint: .trailing
                                )
                            )
                            .frame(width: 40)
                            .offset(x: max(0, (filledWidth - 40)) * CGFloat(phase))
                            .clipShape(Capsule())
                            .frame(width: filledWidth, alignment: .leading)
                            .clipShape(Capsule())
                    }
                }
            }
        }
    }
}

/// Компактное кольцо для Dynamic Island compactTrailing: внутри кольца — счётчик
/// completed-файлов, само кольцо отражает overallProgress.
private struct CompactRing: View {
    let progress: Double

    var body: some View {
        let clamped = max(0.02, min(1, progress))
        ZStack {
            Circle()
                .stroke(AccentOrange.opacity(0.3), lineWidth: 2)
            Circle()
                .trim(from: 0, to: clamped)
                .stroke(AccentOrange, style: StrokeStyle(lineWidth: 2, lineCap: .round))
                .rotationEffect(.degrees(-90))
                .animation(.easeOut(duration: 0.3), value: progress)
        }
    }
}

/// Minimal-вариант: дышащий круг с прогресс-дугой. Дыхание (масштаб 0.92↔1.0)
/// помогает выделить активность среди прочих в Dynamic Island.
private struct BreathingRing: View {
    let progress: Double
    let animating: Bool
    @State private var pulse = false

    var body: some View {
        let clamped = max(0.02, min(1, progress))
        ZStack {
            Circle()
                .stroke(AccentOrange.opacity(0.3), lineWidth: 2)
            Circle()
                .trim(from: 0, to: clamped)
                .stroke(AccentOrange, style: StrokeStyle(lineWidth: 2.2, lineCap: .round))
                .rotationEffect(.degrees(-90))
                .animation(.easeOut(duration: 0.3), value: progress)
        }
        .scaleEffect(pulse ? 1.0 : 0.92)
        .onAppear {
            guard animating else { return }
            withAnimation(.easeInOut(duration: 0.9).repeatForever(autoreverses: true)) {
                pulse = true
            }
        }
    }
}
