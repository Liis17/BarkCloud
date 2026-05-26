import SwiftUI

/// Вкладка «Корзина»: список удалённых файлов с превью, датой удаления и сроком
/// окончательного удаления. Свайп — восстановить / удалить навсегда; в тулбаре —
/// очистить корзину целиком.
struct TrashScreen: View {
    @Environment(AppEnvironment.self) private var env
    @State private var vm: TrashViewModel?
    @State private var showEmptyConfirm = false

    var body: some View {
        Group {
            if let vm {
                content(vm)
            } else {
                ProgressView().frame(maxWidth: .infinity, maxHeight: .infinity)
            }
        }
        .navigationTitle(String(localized: "trash_title"))
        .toolbar { toolbarContent }
        .disabled(vm?.state.isProcessing == true)
        .overlay { if vm?.state.isProcessing == true { processingOverlay } }
        .task {
            if vm == nil { vm = TrashViewModel(cloud: env.cloudRepository) }
            await vm?.loadIfNeeded()
        }
        .confirmationDialog(
            String(localized: "trash_empty_confirm"),
            isPresented: $showEmptyConfirm,
            titleVisibility: .visible
        ) {
            Button(String(localized: "trash_empty_all"), role: .destructive) {
                Task { await vm?.emptyAll() }
            }
            Button(String(localized: "action_cancel"), role: .cancel) {}
        }
    }

    @ViewBuilder
    private func content(_ vm: TrashViewModel) -> some View {
        if vm.state.isLoading {
            ProgressView().frame(maxWidth: .infinity, maxHeight: .infinity)
        } else if vm.state.items.isEmpty {
            emptyState
        } else {
            list(vm)
        }
    }

    private func list(_ vm: TrashViewModel) -> some View {
        List {
            ForEach(vm.state.items) { item in
                TrashRow(item: item)
                    .onAppear { Task { await vm.loadMoreIfNeeded(current: item) } }
                    .swipeActions(edge: .leading) {
                        Button {
                            Task { await vm.restore(item) }
                        } label: {
                            Label(String(localized: "trash_restore"), systemImage: "arrow.uturn.backward")
                        }
                        .tint(.green)
                    }
                    .swipeActions(edge: .trailing) {
                        Button(role: .destructive) {
                            Task { await vm.deleteForever(item) }
                        } label: {
                            Label(String(localized: "trash_delete_forever"), systemImage: "trash")
                        }
                    }
            }
            if vm.state.isLoadingMore {
                HStack { Spacer(); ProgressView(); Spacer() }
            }
        }
        .listStyle(.plain)
        .overlay(alignment: .bottom) { snackbar(vm) }
    }

    @ViewBuilder
    private func snackbar(_ vm: TrashViewModel) -> some View {
        if let text = vm.state.snackbar {
            Text(verbatim: text)
                .font(AppTypography.bodySmall)
                .foregroundStyle(AppColors.onSurface)
                .padding(12)
                .background(.regularMaterial)
                .clipShape(RoundedRectangle(cornerRadius: 10))
                .padding(.bottom, 16)
                .onAppear {
                    Task { @MainActor in
                        try? await Task.sleep(nanoseconds: 2_000_000_000)
                        vm.snackbarShown()
                    }
                }
        }
    }

    private var emptyState: some View {
        VStack(spacing: 16) {
            Image(systemName: "trash")
                .font(.system(size: 64))
                .foregroundStyle(AppColors.onSurfaceVariant)
            Text("trash_empty")
                .font(AppTypography.titleMedium)
                .foregroundStyle(AppColors.onSurfaceVariant)
                .multilineTextAlignment(.center)
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .padding()
    }

    @ToolbarContentBuilder
    private var toolbarContent: some ToolbarContent {
        ToolbarItem(placement: .topBarTrailing) {
            if let vm, !vm.state.items.isEmpty {
                Button(role: .destructive) {
                    showEmptyConfirm = true
                } label: {
                    Image(systemName: "trash.slash")
                }
            }
        }
    }

    private var processingOverlay: some View {
        ZStack {
            Color.black.opacity(0.25).ignoresSafeArea()
            ProgressView()
                .padding(24)
                .background(.regularMaterial)
                .clipShape(RoundedRectangle(cornerRadius: 16))
        }
    }
}

/// Строка корзины: превью/иконка, имя, дата удаления и срок очистки.
private struct TrashRow: View {
    let item: TrashItem

    var body: some View {
        HStack(spacing: 12) {
            thumb
            VStack(alignment: .leading, spacing: 2) {
                Text(verbatim: item.name)
                    .font(AppTypography.titleMedium)
                    .lineLimit(1)
                Text(verbatim: "\(String(localized: "trash_deleted_at")) \(Self.dateText(item.deletedAt))")
                    .font(AppTypography.bodySmall)
                    .foregroundStyle(AppColors.onSurfaceVariant)
                Text(verbatim: "\(String(localized: "trash_purge_at")) \(Self.dateText(item.purgeAt))")
                    .font(AppTypography.bodySmall)
                    .foregroundStyle(AppColors.error)
            }
            Spacer()
        }
        .padding(.vertical, 4)
    }

    @ViewBuilder
    private var thumb: some View {
        let size: CGFloat = 48
        if let url = item.asset.previewURL(preferredWidth: 128) {
            RemoteImage(url: url, contentMode: .fill) {
                AppColors.onSurface.opacity(0.08)
            }
            .frame(width: size, height: size)
            .clipShape(RoundedRectangle(cornerRadius: 8))
        } else {
            RoundedRectangle(cornerRadius: 8)
                .fill(AppColors.onSurface.opacity(0.08))
                .frame(width: size, height: size)
                .overlay {
                    Image(systemName: iconName)
                        .font(.system(size: 20))
                        .foregroundStyle(AppColors.onSurfaceVariant)
                }
        }
    }

    private var iconName: String {
        switch item.asset.kind {
        case .photo: return "photo"
        case .video: return "video"
        case .audio: return "music.note"
        case .document: return "doc.text"
        case .other: return "doc"
        }
    }

    private static let formatter: DateFormatter = {
        let f = DateFormatter()
        f.dateStyle = .medium
        f.timeStyle = .none
        return f
    }()

    private static func dateText(_ date: Date) -> String {
        formatter.string(from: date)
    }
}
