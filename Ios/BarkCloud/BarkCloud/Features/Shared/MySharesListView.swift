import SwiftUI
import UIKit
import BarkCloudKit

/// Список «Мои публичные ссылки»: файлы, папки и альбомы вперемешку (иконка по
/// типу). Имя, обрезанный URL, переходы и дата создания. Действия: «Поделиться»
/// — открывает диалог «Скопировать / Поделиться…»; «Отозвать» —
/// `confirmationDialog` → `MySharesViewModel.revoke`.
struct MySharesListView: View {
    @Bindable var vm: MySharesViewModel
    @State private var pendingRevoke: PublicShareItem?

    var body: some View {
        Group {
            if vm.state.isPlaceholder {
                ProgressView().frame(maxWidth: .infinity, maxHeight: .infinity)
            } else if vm.state.items.isEmpty {
                ScrollView {
                    emptyState.containerRelativeFrame(.vertical)
                }
                .barkRefreshable { await vm.reload() }
            } else {
                list
            }
        }
        .overlay(alignment: .bottom) { snackbar }
        .sharePresenter(url: $vm.state.pendingShareURL)
        .confirmationDialog(
            String(localized: "shared_revoke_confirm"),
            isPresented: Binding(
                get: { pendingRevoke != nil },
                set: { if !$0 { pendingRevoke = nil } }
            ),
            titleVisibility: .visible
        ) {
            Button(String(localized: "shared_revoke"), role: .destructive) {
                if let item = pendingRevoke {
                    Task { await vm.revoke(item) }
                }
                pendingRevoke = nil
            }
            Button(String(localized: "action_cancel"), role: .cancel) {
                pendingRevoke = nil
            }
        } message: {
            if let item = pendingRevoke {
                Text(String(format: String(localized: "shared_revoke_message"), item.name.isEmpty ? String(localized: "shared_unnamed") : item.name))
            }
        }
    }

    private var list: some View {
        List {
            ForEach(vm.state.items) { item in
                ShareLinkRow(item: item, onCopy: { copy(item) }, onRevoke: { pendingRevoke = item })
            }
        }
        .listStyle(.plain)
        .barkRefreshable { await vm.reload() }
    }

    @ViewBuilder
    private var snackbar: some View {
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
        VStack(spacing: 12) {
            Image(systemName: "link")
                .font(.system(size: 48))
                .foregroundStyle(AppColors.onSurfaceVariant)
            Text(String(localized: "shared_my_empty_title"))
                .font(AppTypography.titleMedium)
                .foregroundStyle(AppColors.onSurface)
                .multilineTextAlignment(.center)
            Text(String(localized: "shared_my_empty_hint"))
                .font(AppTypography.bodySmall)
                .foregroundStyle(AppColors.onSurfaceVariant)
                .multilineTextAlignment(.center)
                .padding(.horizontal, 32)
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .padding(.vertical, 60)
    }

    private func copy(_ item: PublicShareItem) {
        guard let url = item.url else { return }
        vm.state.pendingShareURL = ShareableURL(url: url)
    }
}

private struct ShareLinkRow: View {
    let item: PublicShareItem
    let onCopy: () -> Void
    let onRevoke: () -> Void

    private var icon: String {
        switch item.kind {
        case .file:   return "link"
        case .folder: return "folder.fill"
        case .album:  return "photo.on.rectangle.angled"
        }
    }

    var body: some View {
        HStack(alignment: .top, spacing: 12) {
            ZStack {
                Circle()
                    .fill(AppColors.accent.opacity(0.18))
                    .frame(width: 40, height: 40)
                Image(systemName: icon)
                    .font(.system(size: 18, weight: .semibold))
                    .foregroundStyle(AppColors.accent)
            }
            VStack(alignment: .leading, spacing: 4) {
                Text(item.name.isEmpty ? String(localized: "shared_unnamed") : item.name)
                    .font(AppTypography.bodyMedium)
                    .lineLimit(1)
                    .truncationMode(.middle)
                    .foregroundStyle(AppColors.onSurface)
                if let url = item.url {
                    Text(verbatim: url.absoluteString)
                        .font(.system(size: 12))
                        .lineLimit(1)
                        .truncationMode(.middle)
                        .foregroundStyle(AppColors.onSurfaceVariant)
                }
                HStack(spacing: 12) {
                    Label("\(item.clickCount)", systemImage: "arrow.up.right.square")
                        .font(.system(size: 12))
                        .foregroundStyle(AppColors.onSurfaceVariant)
                    Text(item.createdAt, format: .dateTime.day().month().year())
                        .font(.system(size: 12))
                        .foregroundStyle(AppColors.onSurfaceVariant)
                }
                .padding(.top, 2)
            }
            Spacer(minLength: 8)
            VStack(spacing: 4) {
                Button(action: onCopy) {
                    Image(systemName: "square.and.arrow.up")
                        .font(.system(size: 16))
                        .foregroundStyle(AppColors.accent)
                        .frame(width: 36, height: 36)
                }
                .buttonStyle(.plain)
                .accessibilityLabel(String(localized: "shared_share_action"))
                Button(action: onRevoke) {
                    Image(systemName: "trash")
                        .font(.system(size: 16))
                        .foregroundStyle(AppColors.error)
                        .frame(width: 36, height: 36)
                }
                .buttonStyle(.plain)
                .accessibilityLabel(String(localized: "shared_revoke"))
            }
        }
        .padding(.vertical, 4)
    }
}
