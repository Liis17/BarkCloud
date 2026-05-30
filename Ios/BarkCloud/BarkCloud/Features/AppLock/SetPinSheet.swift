import SwiftUI

/// Sheet-мастер на 2 шага: ввод нового PIN и подтверждение. Если оба ввода
/// совпали — отдаёт значение через `onComplete`; иначе сбрасывает шаг и
/// показывает ошибку.
struct SetPinSheet: View {
    let pinLength: Int
    let onComplete: (String) -> Void
    let onCancel: () -> Void

    @State private var firstPin: String?
    @State private var errorMessage: String?

    var body: some View {
        NavigationStack {
            VStack(spacing: 0) {
                Spacer(minLength: 16)
                PinEntryView(
                    pinLength: pinLength,
                    title: firstPin == nil ? "app_lock_set_pin" : "app_lock_confirm_pin",
                    subtitle: "app_lock_set_pin_subtitle",
                    errorMessage: errorMessage,
                    onSubmit: handleSubmit
                )
                .id(firstPin == nil ? "step1" : "step2")
                Spacer(minLength: 16)
            }
            .frame(maxWidth: .infinity, maxHeight: .infinity)
            .navigationBarTitleDisplayMode(.inline)
            .navigationTitle(String(localized: "app_lock_set_pin_title"))
            .toolbar {
                ToolbarItem(placement: .topBarLeading) {
                    Button(String(localized: "action_cancel"), action: onCancel)
                }
            }
        }
    }

    private func handleSubmit(_ value: String) {
        if let first = firstPin {
            if first == value {
                onComplete(value)
            } else {
                errorMessage = String(localized: "app_lock_pin_mismatch")
                firstPin = nil
            }
        } else {
            firstPin = value
            errorMessage = nil
        }
    }
}
