import SwiftUI

@MainActor
@Observable
final class DevicesViewModel {
    struct UiState {
        var devices: [Barkcloud_Users_Device] = []
        var currentDeviceID: String = ""
        var isLoading = true
        var snackbar: String?
    }

    var state = UiState()
    private let users: UserRepository

    init(users: UserRepository) { self.users = users }

    func load() async {
        state.isLoading = true
        do {
            state.currentDeviceID = (try? await users.getCurrentDevice())?.deviceID ?? ""
            state.devices = try await users.getDevices()
        } catch {
            state.snackbar = domainErrorMessage(error)
        }
        state.isLoading = false
    }

    func rename(deviceID: String, name: String) async {
        do {
            try await users.renameDevice(deviceID: deviceID, customName: name)
            await load()
        } catch {
            state.snackbar = domainErrorMessage(error)
        }
    }

    func delete(deviceID: String) async {
        do {
            try await users.deleteDevice(deviceID: deviceID)
            await load()
        } catch {
            state.snackbar = domainErrorMessage(error)
        }
    }
}

struct DevicesScreen: View {
    @Environment(AppEnvironment.self) private var env
    @State private var vm: DevicesViewModel?
    @State private var renameTarget: Barkcloud_Users_Device?
    @State private var renameText = ""
    @State private var deleteTarget: Barkcloud_Users_Device?

    var body: some View {
        Group {
            if let vm {
                list(vm)
            } else {
                ProgressView()
            }
        }
        .navigationTitle(String(localized: "settings_devices"))
        .navigationBarTitleDisplayMode(.inline)
        .task {
            if vm == nil {
                vm = DevicesViewModel(users: env.userRepository)
            }
            await vm?.load()
        }
    }

    @ViewBuilder
    private func list(_ vm: DevicesViewModel) -> some View {
        List {
            if vm.state.devices.isEmpty && !vm.state.isLoading {
                Text("devices_empty").foregroundStyle(AppColors.onSurfaceVariant)
            }
            ForEach(vm.state.devices, id: \.deviceID) { device in
                deviceRow(device, isCurrent: device.deviceID == vm.state.currentDeviceID)
                    .swipeActions(edge: .trailing) {
                        if device.deviceID != vm.state.currentDeviceID {
                            Button(role: .destructive) {
                                deleteTarget = device
                            } label: {
                                Image(systemName: "trash")
                            }
                            .accessibilityLabel(String(localized: "action_delete"))
                        }
                        Button {
                            renameText = device.customName.isEmpty ? device.originalName : device.customName
                            renameTarget = device
                        } label: {
                            Image(systemName: "pencil")
                        }
                        .tint(AppColors.accent)
                        .accessibilityLabel(String(localized: "action_rename"))
                    }
            }
        }
        .alert(String(localized: "devices_rename_title"), isPresented: Binding(
            get: { renameTarget != nil }, set: { if !$0 { renameTarget = nil } })) {
            TextField(String(localized: "devices_rename_placeholder"), text: $renameText)
            Button(String(localized: "action_save")) {
                if let target = renameTarget {
                    Task { await vm.rename(deviceID: target.deviceID, name: renameText) }
                }
                renameTarget = nil
            }
            Button(String(localized: "action_cancel"), role: .cancel) { renameTarget = nil }
        }
        .confirmationDialog(String(localized: "devices_delete_confirm"), isPresented: Binding(
            get: { deleteTarget != nil }, set: { if !$0 { deleteTarget = nil } }), titleVisibility: .visible) {
            Button(String(localized: "action_delete"), role: .destructive) {
                if let target = deleteTarget {
                    Task { await vm.delete(deviceID: target.deviceID) }
                }
                deleteTarget = nil
            }
            Button(String(localized: "action_cancel"), role: .cancel) { deleteTarget = nil }
        }
    }

    @ViewBuilder
    private func deviceRow(_ device: Barkcloud_Users_Device, isCurrent: Bool) -> some View {
        HStack(spacing: 14) {
            Image(systemName: deviceIcon(device.operationSystem))
                .font(.system(size: 22))
                .frame(width: 40)
                .foregroundStyle(AppColors.accent)
            VStack(alignment: .leading, spacing: 2) {
                HStack(spacing: 6) {
                    Text(verbatim: device.customName.isEmpty ? device.originalName : device.customName)
                        .font(AppTypography.titleMedium)
                    if isCurrent {
                        Text("devices_current")
                            .font(AppTypography.labelMedium)
                            .padding(.horizontal, 6)
                            .padding(.vertical, 2)
                            .background(AppColors.accent.opacity(0.15))
                            .foregroundStyle(AppColors.accent)
                            .clipShape(Capsule())
                    }
                }
                let subtitle = [device.operationSystem, device.appName, device.location]
                    .filter { !$0.isEmpty }.joined(separator: " · ")
                if !subtitle.isEmpty {
                    Text(verbatim: subtitle)
                        .font(AppTypography.bodySmall)
                        .foregroundStyle(AppColors.onSurfaceVariant)
                }
            }
            Spacer()
        }
        .padding(.vertical, 4)
    }

    private func deviceIcon(_ os: String) -> String {
        let lower = os.lowercased()
        if lower.contains("ios") || lower.contains("iphone") || lower.contains("ipad") { return "iphone" }
        if lower.contains("android") { return "candybarphone" }
        if lower.contains("mac") || lower.contains("windows") || lower.contains("linux") { return "laptopcomputer" }
        return "desktopcomputer"
    }
}
