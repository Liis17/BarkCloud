import SwiftUI

struct LoginScreen: View {
    @Environment(AppEnvironment.self) private var env
    @State private var viewModel: LoginViewModel?
    @State private var showServerSetup = false
    @FocusState private var focusedField: Field?

    let onAuthenticated: () -> Void

    private enum Field: Hashable { case login, password, otp }

    var body: some View {
        let vm = viewModel ?? LoginViewModel(auth: env.authRepository)
        let state = vm.state

        return ZStack(alignment: .bottom) {
            ScrollView {
                VStack(alignment: .leading, spacing: 24) {
                    Text("login_title")
                        .font(AppTypography.displaySmall)
                        .padding(.top, 32)

                    if state.otpRequired {
                        otpBlock(vm: vm)
                            .transition(.asymmetric(
                                insertion: .move(edge: .trailing).combined(with: .opacity),
                                removal: .move(edge: .leading).combined(with: .opacity)
                            ))
                    } else {
                        credentialsBlock(vm: vm)
                            .transition(.asymmetric(
                                insertion: .move(edge: .leading).combined(with: .opacity),
                                removal: .move(edge: .trailing).combined(with: .opacity)
                            ))
                    }

                    submitButton(vm: vm)

                    if !state.otpRequired {
                        HStack {
                            Button { vm.onComingSoon() } label: {
                                Text("login_create_account")
                            }
                            Spacer()
                            Button { vm.onComingSoon() } label: {
                                Text("login_forgot_password")
                            }
                        }
                        .font(AppTypography.labelLarge)

                        Button { showServerSetup = true } label: {
                            Label("server_setup_login_link", systemImage: "server.rack")
                                .font(AppTypography.labelLarge)
                                .foregroundStyle(AppColors.onSurfaceVariant)
                        }
                        .frame(maxWidth: .infinity, alignment: .center)
                    }
                }
                .padding(.horizontal, 24)
                .animation(.easeInOut(duration: 0.25), value: state.otpRequired)
            }

            if let snackbar = state.snackbarMessage {
                snackbarView(snackbar)
                    .transition(.move(edge: .bottom).combined(with: .opacity))
                    .onAppear {
                        Task { @MainActor in
                            try? await Task.sleep(nanoseconds: 2_500_000_000)
                            vm.snackbarShown()
                        }
                    }
            }
        }
        .animation(.easeInOut(duration: 0.25), value: state.snackbarMessage != nil)
        .sheet(isPresented: $showServerSetup) {
            ServerSetupScreen(
                onCancel: { showServerSetup = false },
                onComplete: { showServerSetup = false }
            )
        }
        .onAppear {
            if viewModel == nil {
                viewModel = LoginViewModel(auth: env.authRepository)
            }
            focusedField = .login
        }
        .onChange(of: vm.navigateToMainRequest) { _, requested in
            if requested { onAuthenticated() }
        }
    }

    @ViewBuilder
    private func credentialsBlock(vm: LoginViewModel) -> some View {
        VStack(alignment: .leading, spacing: 16) {
            TextField(String(localized: "login_field_label"), text: Binding(
                get: { vm.state.login },
                set: { vm.onLoginChange($0) }
            ))
            .textFieldStyle(.roundedBorder)
            .textContentType(.username)
            .autocorrectionDisabled()
            .textInputAutocapitalization(.never)
            .focused($focusedField, equals: .login)
            .submitLabel(.next)
            .onSubmit { focusedField = .password }

            HStack(spacing: 0) {
                Group {
                    if vm.state.passwordVisible {
                        TextField(String(localized: "login_password_label"), text: Binding(
                            get: { vm.state.password },
                            set: { vm.onPasswordChange($0) }
                        ))
                    } else {
                        SecureField(String(localized: "login_password_label"), text: Binding(
                            get: { vm.state.password },
                            set: { vm.onPasswordChange($0) }
                        ))
                    }
                }
                .textContentType(.password)
                .autocorrectionDisabled()
                .textInputAutocapitalization(.never)
                .focused($focusedField, equals: .password)
                .submitLabel(.go)
                .onSubmit { vm.submit() }

                Button {
                    vm.togglePasswordVisibility()
                } label: {
                    Image(systemName: vm.state.passwordVisible ? "eye.slash" : "eye")
                        .foregroundStyle(AppColors.onSurfaceVariant)
                }
                .accessibilityLabel(vm.state.passwordVisible
                    ? String(localized: "login_password_hide")
                    : String(localized: "login_password_show"))
            }
            .padding(8)
            .background(AppColors.onSurface.opacity(0.05))
            .clipShape(RoundedRectangle(cornerRadius: 8))

            if let err = vm.state.credentialsError {
                Text(err)
                    .font(AppTypography.bodySmall)
                    .foregroundStyle(AppColors.error)
            }
        }
    }

    @ViewBuilder
    private func otpBlock(vm: LoginViewModel) -> some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("login_otp_label")
                .font(AppTypography.titleMedium)
            Text("login_otp_hint")
                .font(AppTypography.bodySmall)
                .foregroundStyle(AppColors.onSurfaceVariant)
            TextField("", text: Binding(
                get: { vm.state.otp },
                set: { vm.onOtpChange($0) }
            ))
            .textFieldStyle(.roundedBorder)
            .keyboardType(.numberPad)
            .focused($focusedField, equals: .otp)
            .onAppear { focusedField = .otp }
        }
    }

    @ViewBuilder
    private func submitButton(vm: LoginViewModel) -> some View {
        Button {
            vm.submit()
        } label: {
            HStack {
                if vm.state.isLoading {
                    ProgressView()
                        .tint(.white)
                } else {
                    Text("login_submit")
                        .font(AppTypography.titleMedium)
                }
            }
            .frame(maxWidth: .infinity)
            .padding(.vertical, 6)
        }
        .buttonStyle(.borderedProminent)
        .controlSize(.large)
        .disabled(!vm.state.canSubmit)
    }

    @ViewBuilder
    private func snackbarView(_ message: String) -> some View {
        Text(message)
            .font(AppTypography.bodyMedium)
            .padding(.horizontal, 16)
            .padding(.vertical, 12)
            .background(AppColors.onSurface.opacity(0.85))
            .foregroundStyle(Color.white)
            .clipShape(Capsule())
            .padding(.bottom, 32)
    }
}
