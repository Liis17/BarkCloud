import SwiftUI
import UIKit

/// Список «Мои публичные ссылки»: имя файла, обрезанный URL, переходы и дата
/// создания. Действия: «Скопировать» — кладёт URL в буфер; «Отозвать» —
/// `confirmationDialog` → `MySharesViewModel.revoke`. Подгружается по курсору
/// при появлении последней карточки.
struct MySharesListView: View {
    @Bindable var vm: MySharesViewModel
    @State private var pendingRevoke: ShareLink?

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
        .confirmationDialog(
            String(localized: "shared_revoke_confirm"),
            isPresented: Binding(
                get: { pendingRevoke != nil },
                set: { if !$0 { pendingRevoke = nil } }
            ),
            titleVisibility: .visible
        ) {
            Button(String(localized: "shared_revoke"), role: .destructive) {
                if let link = pendingRevoke {
                    Task { await vm.revoke(link) }
                }
                pendingRevoke = nil
            }
            Button(String(localized: "action_cancel"), role: .cancel) {
                pendingRevoke = nil
            }
        } message: {
            if let link = pendingRevoke {
                Text(String(format: String(localized: "shared_revoke_message"), link.name.isEmpty ? String(localized: "shared_unnamed") : link.name))
            }
        }
    }

    private var list: some View {
        List {
            ForEach(vm.state.items) { link in
                ShareLinkRow(link: link, onCopy: { copy(link) }, onRevoke: { pendingRevoke = link })
                    .onAppear { Task { await vm.loadMoreIfNeeded(current: link) } }
            }
            if vm.state.isLoadingMore {
                HStack { Spacer(); ProgressView(); Spacer() }
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

    private func copy(_ link: ShareLink) {
        guard let url = link.url else { return }
        UIPasteboard.general.url = url
        vm.state.snackbar = String(localized: "snack_link_copied")
    }
}

private struct ShareLinkRow: View {
    let link: ShareLink
    let onCopy: () -> Void
    let onRevoke: () -> Void

    var body: some View {
        HStack(alignment: .top, spacing: 12) {
            ZStack {
                Circle()
                    .fill(AppColors.accent.opacity(0.18))
                    .frame(width: 40, height: 40)
                Image(systemName: "link")
                    .font(.system(size: 18, weight: .semibold))
                    .foregroundStyle(AppColors.accent)
            }
            VStack(alignment: .leading, spacing: 4) {
                Text(link.name.isEmpty ? String(localized: "shared_unnamed") : link.name)
                    .font(AppTypography.bodyMedium)
                    .lineLimit(1)
                    .truncationMode(.middle)
                    .foregroundStyle(AppColors.onSurface)
                if let url = link.url {
                    Text(verbatim: url.absoluteString)
                        .font(.system(size: 12))
                        .lineLimit(1)
                        .truncationMode(.middle)
                        .foregroundStyle(AppColors.onSurfaceVariant)
                }
                HStack(spacing: 12) {
                    Label("\(link.clickCount)", systemImage: "arrow.up.right.square")
                        .font(.system(size: 12))
                        .foregroundStyle(AppColors.onSurfaceVariant)
                    Text(link.createdAt, format: .dateTime.day().month().year())
                        .font(.system(size: 12))
                        .foregroundStyle(AppColors.onSurfaceVariant)
                }
                .padding(.top, 2)
            }
            Spacer(minLength: 8)
            VStack(spacing: 4) {
                Button(action: onCopy) {
                    Image(systemName: "doc.on.doc")
                        .font(.system(size: 16))
                        .foregroundStyle(AppColors.accent)
                        .frame(width: 36, height: 36)
                }
                .buttonStyle(.plain)
                .accessibilityLabel(String(localized: "shared_copy_link"))
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
