import SwiftUI

/// Благодарственная анимация после освобождения места: пиксельная лиса-маскот с
/// вилянием хвоста, искры вокруг и счётчик освобождённых байт, который «накручивается»
/// от нуля. Появляется пружиной, авто-скрывается через пару секунд (или по тапу).
struct SpaceFreedView: View {
    let bytes: Int64
    let onDismiss: () -> Void

    @State private var appear = false
    @State private var displayBytes: Int64 = 0

    var body: some View {
        ZStack {
            Color.black.opacity(0.55)
                .ignoresSafeArea()
                .onTapGesture { onDismiss() }

            VStack(spacing: 14) {
                TimelineView(.animation(minimumInterval: 1.0 / 60.0)) { ctx in
                    let t = ctx.date.timeIntervalSinceReferenceDate
                    ZStack {
                        Sparkles(time: t)
                        BarkMascot(time: t, scale: 1)
                            .frame(width: 120, height: 60)
                    }
                }
                .frame(width: 170, height: 120)

                Text(verbatim: String(
                    format: NSLocalizedString("backup_freed_title", comment: ""),
                    FormatUtils.formatSize(displayBytes)
                ))
                .font(AppTypography.titleLarge)
                .foregroundStyle(AppColors.onSurface)

                Text("backup_freed_thanks")
                    .font(AppTypography.bodyMedium)
                    .foregroundStyle(AppColors.onSurfaceVariant)
            }
            .padding(28)
            .background(.regularMaterial)
            .clipShape(RoundedRectangle(cornerRadius: 24))
            .scaleEffect(appear ? 1 : 0.6)
            .opacity(appear ? 1 : 0)
        }
        .task {
            withAnimation(.spring(response: 0.45, dampingFraction: 0.6)) { appear = true }
            await countUp()
            try? await Task.sleep(nanoseconds: 2_200_000_000)
            onDismiss()
        }
    }

    /// «Накрутка» числа от 0 к итогу примерно за 0.8 c.
    private func countUp() async {
        let steps = 28
        for i in 1...steps {
            displayBytes = Int64(Double(bytes) * Double(i) / Double(steps))
            try? await Task.sleep(nanoseconds: 28_000_000)
        }
        displayBytes = bytes
    }
}

/// Мерцающие оранжевые искры по кругу — рисуются в Canvas, мерцание ведётся `time`.
private struct Sparkles: View {
    let time: Double

    var body: some View {
        Canvas { ctx, size in
            let count = 7
            let orange = Color(red: 0.93, green: 0.49, blue: 0.15)
            let radius = min(size.width, size.height) * 0.46
            for i in 0..<count {
                let phase = Double(i) / Double(count)
                let angle = phase * .pi * 2
                let cx = size.width / 2 + cos(angle) * radius
                let cy = size.height / 2 + sin(angle) * radius * 0.7
                let twinkle = (sin(time * 3 + phase * 6.2832) + 1) / 2
                let side = 2 + twinkle * 5
                ctx.opacity = 0.25 + twinkle * 0.75
                ctx.fill(
                    Path(ellipseIn: CGRect(x: cx - side / 2, y: cy - side / 2, width: side, height: side)),
                    with: .color(orange)
                )
            }
        }
    }
}
