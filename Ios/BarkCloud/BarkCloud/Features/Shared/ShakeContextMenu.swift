import SwiftUI

/// Кастомное контекстное меню по удержанию: пока палец прижат, ячейка
/// начинает плавно «тряситься» — амплитуда, частота тряски и сила слабой
/// вибрации растут по нарастающей кривой. В момент срабатывания длинного
/// нажатия — резкий тяжёлый удар вибрации, тряска останавливается, ячейка
/// слегка приподнята, появляется меню. В режиме мультивыбора
/// (`isActive == false`) жест отключён.
private struct ShakeContextMenuModifier<MenuContent: View>: ViewModifier {
    let isActive: Bool
    @ViewBuilder let menu: () -> MenuContent

    @State private var pressing = false
    @State private var showMenu = false
    @State private var shakeAngle: Double = 0
    @State private var holdTask: Task<Void, Never>? = nil
    @State private var hapticIntensity: Double = 0.3
    @State private var lightTick: Int = 0
    @State private var menuTick: Int = 0

    private var raised: Bool { pressing || showMenu }

    func body(content: Content) -> some View {
        content
            .scaleEffect(raised ? 1.09 : 1.0)
            .rotationEffect(.degrees(shakeAngle))
            .animation(.spring(response: 0.28, dampingFraction: 0.7), value: raised)
            .sensoryFeedback(.impact(weight: .light, intensity: hapticIntensity), trigger: lightTick)
            .sensoryFeedback(.impact(weight: .heavy, intensity: 1.0), trigger: menuTick)
            .onLongPressGesture(minimumDuration: 0.4) {
                guard isActive else { return }
                stopHold()
                menuTick &+= 1
                showMenu = true
            } onPressingChanged: { isPressing in
                guard isActive else { return }
                pressing = isPressing
                if isPressing {
                    startHold()
                } else if !showMenu {
                    stopHold()
                }
            }
            .confirmationDialog("", isPresented: $showMenu, titleVisibility: .hidden) {
                menu()
            }
            .onChange(of: showMenu) { _, isShown in
                if !isShown {
                    pressing = false
                    stopHold()
                }
            }
    }

    private func startHold() {
        holdTask?.cancel()
        let started = Date()
        let total: Double = 0.4
        holdTask = Task { @MainActor in
            var step = 0
            while !Task.isCancelled {
                let elapsed = Date().timeIntervalSince(started)
                let progress = min(elapsed / total, 1.0)
                let curve = progress * progress
                let amp = 0.8 + 2.2 * curve
                let sign: Double = (step % 2 == 0) ? 1.0 : -1.0
                withAnimation(.easeInOut(duration: 0.045)) {
                    shakeAngle = sign * amp
                }
                hapticIntensity = 0.3 + 0.5 * curve
                lightTick &+= 1
                step += 1
                let interval = 80.0 - 45.0 * curve
                try? await Task.sleep(nanoseconds: UInt64(interval * 1_000_000))
            }
        }
    }

    private func stopHold() {
        holdTask?.cancel()
        holdTask = nil
        withAnimation(.easeOut(duration: 0.12)) {
            shakeAngle = 0
        }
    }
}

extension View {
    /// Вешает кастомное меню по удержанию. `isActive == false` отключает жест
    /// (например, в режиме мультивыбора). `menu` — кнопки меню (`Button`,
    /// у «Удалить» — `role: .destructive`).
    func shakeContextMenu<MenuContent: View>(
        isActive: Bool = true,
        @ViewBuilder menu: @escaping () -> MenuContent
    ) -> some View {
        modifier(ShakeContextMenuModifier(isActive: isActive, menu: menu))
    }
}
