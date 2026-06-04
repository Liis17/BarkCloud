import SwiftUI

/// Логин по email/паролю с поддержкой OTP (второй фактор). По успеху — переход к
/// дашборду (токены сохраняются в Keychain через `SessionStore`).
struct LoginView: View {
    @Environment(AppModel.self) private var model

    @State private var login = ""
    @State private var password = ""
    @State private var otp = ""

    var body: some View {
        VStack(spacing: 16) {
            Image(systemName: "person.crop.circle")
                .font(.system(size: 48))
                .foregroundStyle(.secondary)
            Text("Вход в BarkCloud").font(.title2).bold()

            Form {
                TextField("Email или логин", text: $login)
                    .textContentType(.username)
                SecureField("Пароль", text: $password)
                    .textContentType(.password)
                if model.otpRequired {
                    TextField("Код подтверждения", text: $otp)
                }
            }
            .formStyle(.grouped)

            if let error = model.errorMessage {
                Text(error).foregroundStyle(.red).font(.callout)
            }

            Button(action: submit) {
                if model.isBusy { ProgressView().controlSize(.small) }
                else { Text(model.otpRequired ? "Подтвердить" : "Войти") }
            }
            .keyboardShortcut(.defaultAction)
            .disabled(model.isBusy || login.isEmpty || password.isEmpty)

            Button("Сменить адрес сервера") {
                Task { await model.forgetServer() }
            }
            .buttonStyle(.link)
        }
        .padding(32)
        .frame(maxWidth: 420)
    }

    private func submit() {
        Task { await model.login(login: login, password: password, otp: model.otpRequired ? otp : nil) }
    }
}
