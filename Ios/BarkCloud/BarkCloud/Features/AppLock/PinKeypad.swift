import SwiftUI

/// Кастомная цифровая клавиатура для ввода PIN — без вызова системной IME.
/// Используется в [[AppLockScreen]] и [[SetPinSheet]].
struct PinKeypad: View {
    let onDigit: (Int) -> Void
    let onBackspace: () -> Void
    var isEnabled: Bool = true

    private let buttonSize: CGFloat = 72

    var body: some View {
        VStack(spacing: 18) {
            row(1, 2, 3)
            row(4, 5, 6)
            row(7, 8, 9)
            HStack(spacing: 28) {
                Color.clear.frame(width: buttonSize, height: buttonSize)
                digit(0)
                backspace
            }
        }
        .disabled(!isEnabled)
        .opacity(isEnabled ? 1 : 0.4)
    }

    private func row(_ a: Int, _ b: Int, _ c: Int) -> some View {
        HStack(spacing: 28) { digit(a); digit(b); digit(c) }
    }

    private func digit(_ n: Int) -> some View {
        Button { onDigit(n) } label: {
            Text(verbatim: "\(n)")
                .font(.system(size: 30, weight: .regular, design: .rounded))
                .foregroundStyle(AppColors.onSurface)
                .frame(width: buttonSize, height: buttonSize)
                .background(AppColors.onSurface.opacity(0.06), in: Circle())
        }
        .buttonStyle(.plain)
    }

    private var backspace: some View {
        Button { onBackspace() } label: {
            Image(systemName: "delete.left")
                .font(.system(size: 22, weight: .regular))
                .foregroundStyle(AppColors.onSurface)
                .frame(width: buttonSize, height: buttonSize)
        }
        .buttonStyle(.plain)
        .accessibilityLabel(String(localized: "app_lock_backspace"))
    }
}

/// Точки-индикаторы текущей длины PIN.
struct PinDots: View {
    let filled: Int
    let total: Int

    var body: some View {
        HStack(spacing: 16) {
            ForEach(0..<total, id: \.self) { index in
                Circle()
                    .fill(index < filled ? AppColors.accent : AppColors.onSurface.opacity(0.18))
                    .frame(width: 14, height: 14)
            }
        }
    }
}

/// Готовая «секция ввода PIN»: заголовок + точки + клавиатура + опциональная
/// ошибка. Сама хранит частичный ввод; при достижении `pinLength` отдаёт значение
/// и обнуляется.
struct PinEntryView: View {
    let pinLength: Int
    let title: LocalizedStringResource
    var subtitle: LocalizedStringResource?
    var errorMessage: String?
    let onSubmit: (String) -> Void

    @State private var pin: String = ""

    var body: some View {
        VStack(spacing: 24) {
            VStack(spacing: 6) {
                Text(title)
                    .font(AppTypography.titleMedium)
                    .foregroundStyle(AppColors.onSurface)
                if let subtitle {
                    Text(subtitle)
                        .font(AppTypography.bodyMedium)
                        .foregroundStyle(AppColors.onSurfaceVariant)
                        .multilineTextAlignment(.center)
                }
            }
            PinDots(filled: pin.count, total: pinLength)
            if let errorMessage, !errorMessage.isEmpty {
                Text(verbatim: errorMessage)
                    .font(AppTypography.bodySmall)
                    .foregroundStyle(AppColors.error)
                    .multilineTextAlignment(.center)
                    .padding(.horizontal, 24)
            }
            PinKeypad(
                onDigit: { digit in
                    guard pin.count < pinLength else { return }
                    pin.append("\(digit)")
                    if pin.count == pinLength {
                        let value = pin
                        pin = ""
                        onSubmit(value)
                    }
                },
                onBackspace: {
                    if !pin.isEmpty { pin.removeLast() }
                }
            )
        }
        .padding(.horizontal, 16)
    }

    /// Сбросить введённое значение (например, при возврате к шагу подтверждения).
    func clear() { /* no-op: state is internal, reset происходит через изменение `errorMessage` извне */ }
}
