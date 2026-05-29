import SwiftUI

/// Локальный сейф: защищённая биометрией подборка облачных фото/видео.
/// До разблокировки контент не показывается. Список хранится только на
/// устройстве (`VaultStore`), сервер о защите не знает.
struct VaultScreen: View {
    @Environment(AppEnvironment.self) private var env
    @Environment(\.scenePhase) private var scenePhase

    @State private var vm: VaultViewModel?
    @State private var selected: VaultItem?

    private static let spacing: CGFloat = 2
    private let columns = Array(repeating: GridItem(.flexible(), spacing: spacing), count: 3)

    var body: some View {
        Group {
            if let vm {
                content(vm)
            } else {
                ProgressView().frame(maxWidth: .infinity, maxHeight: .infinity)
            }
        }
        .navigationTitle(String(localized: "vault_title"))
        .navigationBarTitleDisplayMode(.inline)
        .toolbar { toolbarContent }
        .task {
            if vm == nil {
                vm = VaultViewModel(vault: env.vault, biometric: env.biometric)
            }
            if vm?.lockState == .locked { await vm?.unlock() }
        }
        // Уход в фон снова запирает сейф.
        .onChange(of: scenePhase) { _, phase in
            if phase != .active { vm?.lock() }
        }
        .fullScreenCover(item: $selected) { item in viewer(item) }
    }

    @ViewBuilder
    private func content(_ vm: VaultViewModel) -> some View {
        switch vm.lockState {
        case .locked, .unlocking:
            lockedView(vm)
        case .unlocked:
            if vm.isEmpty {
                emptyState
            } else {
                grid(vm)
            }
        }
    }

    private func lockedView(_ vm: VaultViewModel) -> some View {
        VStack(spacing: 20) {
            Image(systemName: "lock.shield.fill")
                .font(.system(size: 64))
                .foregroundStyle(AppColors.accent)
            Text("vault_locked_title")
                .font(AppTypography.titleMedium)
                .foregroundStyle(AppColors.onSurface)
            Text("vault_locked_message")
                .font(AppTypography.bodyMedium)
                .foregroundStyle(AppColors.onSurfaceVariant)
                .multilineTextAlignment(.center)
            Button {
                Task { await vm.unlock() }
            } label: {
                if vm.lockState == .unlocking {
                    ProgressView().tint(.white).frame(maxWidth: .infinity)
                } else {
                    Text("vault_unlock").frame(maxWidth: .infinity)
                }
            }
            .buttonStyle(.borderedProminent)
            .tint(AppColors.accent)
            .controlSize(.large)
            .disabled(vm.lockState == .unlocking)
        }
        .padding(32)
        .frame(maxWidth: .infinity, maxHeight: .infinity)
    }

    private var emptyState: some View {
        VStack(spacing: 16) {
            Image(systemName: "lock.open")
                .font(.system(size: 64))
                .foregroundStyle(AppColors.onSurfaceVariant)
            Text("vault_empty")
                .font(AppTypography.titleMedium)
                .foregroundStyle(AppColors.onSurfaceVariant)
                .multilineTextAlignment(.center)
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .padding()
    }

    private func grid(_ vm: VaultViewModel) -> some View {
        ScrollView {
            LazyVGrid(columns: columns, spacing: Self.spacing) {
                ForEach(vm.items) { item in
                    MediaThumb(
                        fileId: item.id,
                        previewWidth: item.previewWidth,
                        thumbnailURL: item.thumbnailURL,
                        isVideo: item.isVideo,
                        isSelecting: vm.isSelecting,
                        isSelected: vm.selection.contains(item.id)
                    )
                    .onTapGesture {
                        if vm.isSelecting {
                            vm.toggle(item.id)
                        } else {
                            selected = item
                        }
                    }
                }
            }
        }
        .safeAreaInset(edge: .bottom) {
            if vm.isSelecting { actionBar(vm).transition(.move(edge: .bottom).combined(with: .opacity)) }
        }
        .animation(.spring(response: 0.35, dampingFraction: 0.85), value: vm.isSelecting)
    }

    private func actionBar(_ vm: VaultViewModel) -> some View {
        Button(role: .destructive) {
            vm.removeSelected()
        } label: {
            Label("vault_remove", systemImage: "lock.open")
                .frame(maxWidth: .infinity)
        }
        .buttonStyle(.borderedProminent)
        .tint(AppColors.accent)
        .controlSize(.large)
        .disabled(!vm.hasSelection)
        .padding(.horizontal, 16)
        .padding(.vertical, 10)
    }

    @ToolbarContentBuilder
    private var toolbarContent: some ToolbarContent {
        if let vm, vm.lockState == .unlocked, !vm.isEmpty {
            ToolbarItem(placement: .topBarTrailing) {
                if vm.isSelecting {
                    Button(String(localized: "action_cancel")) { vm.exitSelection() }
                } else {
                    Button(String(localized: "gallery_select")) { vm.enterSelection() }
                }
            }
        }
    }

    private func viewer(_ item: VaultItem) -> some View {
        NavigationStack {
            RemoteFilePreviewScreen(fileID: item.id, fileName: item.fileName, transfer: env.fileTransfer, cache: env.fileCache)
                .toolbar {
                    ToolbarItem(placement: .topBarLeading) {
                        Button(String(localized: "action_close")) { selected = nil }
                    }
                }
        }
    }
}
