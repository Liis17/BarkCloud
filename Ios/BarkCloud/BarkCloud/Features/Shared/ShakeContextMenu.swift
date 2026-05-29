import SwiftUI
import UIKit

/// Тактильная отдача. Заметна только на реальном устройстве — симулятор не вибрирует.
enum Haptics {
    static func light() {
        let generator = UIImpactFeedbackGenerator(style: .soft)
        generator.prepare()
        generator.impactOccurred()
    }
}

/// Кастомное контекстное меню по удержанию: при долгом нажатии ячейка слегка
/// увеличивается, «трясётся» и даёт слабую вибрацию, затем открывается меню с
/// действиями над файлом. Заменяет нативный `.contextMenu` (по требованию —
/// собственная анимация удержания). В режиме мультивыбора (`isActive == false`)
/// жест полностью отключён, чтобы не мешать выбору ячеек.
private struct ShakeContextMenuModifier<MenuContent: View>: ViewModifier {
    let isActive: Bool
    @ViewBuilder let menu: () -> MenuContent

    @State private var pressing = false
    @State private var shaking = false
    @State private var showMenu = false

    private var raised: Bool { pressing || showMenu }

    func body(content: Content) -> some View {
        content
            .scaleEffect(raised ? 1.09 : 1.0)
            .rotationEffect(.degrees(shaking ? 1.5 : -1.5))
            .animation(.spring(response: 0.28, dampingFraction: 0.7), value: raised)
            .animation(
                shaking
                    ? .easeInOut(duration: 0.11).repeatForever(autoreverses: true)
                    : .easeInOut(duration: 0.12),
                value: shaking
            )
            .onLongPressGesture(minimumDuration: 0.4) {
                guard isActive else { return }
                Haptics.light()
                shaking = true
                showMenu = true
            } onPressingChanged: { isPressing in
                guard isActive else { return }
                pressing = isPressing
                if !isPressing && !showMenu { shaking = false }
            }
            .confirmationDialog("", isPresented: $showMenu, titleVisibility: .hidden) {
                menu()
            }
            .onChange(of: showMenu) { _, isShown in
                if !isShown {
                    pressing = false
                    shaking = false
                }
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
