import SwiftUI
import UIKit

/// Список таба «Я поделился»: карточка на файл (превью + имя), под ней — чипсы
/// получателей, у каждого крестик для отзыва доступа. Зеркалит `ISharedCard`
/// веб-клиента. Подгружается по курсору при появлении последней карточки.
struct MyOutgoingSharesListView: View {
    @Bindable var vm: MyOutgoingSharesViewModel
    @State private var pendingRevoke: PendingRevoke?

    var body: some View {
        Group {
            if vm.state.isPlaceholder {
                ProgressView().frame(maxWidth: .infinity, maxHeight: .infinity)
            } else if vm.state.groups.isEmpty {
                ScrollView { emptyState.containerRelativeFrame(.vertical) }
                    .barkRefreshable { await vm.reload() }
            } else {
                list
            }
        }
        .overlay(alignment: .bottom) { snackbar }
        .confirmationDialog(
            String(localized: "shared_revoke_grant_confirm"),
            isPresented: Binding(
                get: { pendingRevoke != nil },
                set: { if !$0 { pendingRevoke = nil } }
            ),
            titleVisibility: .visible
        ) {
            Button(String(localized: "shared_revoke"), role: .destructive) {
                if let p = pendingRevoke {
                    Task { await vm.revoke(grantID: p.grantID) }
                }
                pendingRevoke = nil
            }
            Button(String(localized: "action_cancel"), role: .cancel) { pendingRevoke = nil }
        }
    }

    private var list: some View {
        List {
            ForEach(vm.state.groups) { group in
                GroupRow(
                    group: group,
                    users: vm.state.users,
                    onRevoke: { grantID, name in pendingRevoke = PendingRevoke(grantID: grantID, name: name) }
                )
                .onAppear { Task { await vm.loadMoreIfNeeded(current: group) } }
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
            Image(systemName: "person.crop.circle.badge.checkmark")
                .font(.system(size: 48))
                .foregroundStyle(AppColors.onSurfaceVariant)
            Text(String(localized: "shared_ishared_empty_title"))
                .font(AppTypography.titleMedium)
                .foregroundStyle(AppColors.onSurface)
                .multilineTextAlignment(.center)
            Text(String(localized: "shared_ishared_empty_hint"))
                .font(AppTypography.bodySmall)
                .foregroundStyle(AppColors.onSurfaceVariant)
                .multilineTextAlignment(.center)
                .padding(.horizontal, 32)
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .padding(.vertical, 60)
    }

    private struct PendingRevoke: Identifiable {
        let grantID: String
        let name: String
        var id: String { grantID }
    }
}

private struct GroupRow: View {
    let group: SharedByMeGroup
    let users: [Int64: CloudUser]
    let onRevoke: (_ grantID: String, _ recipientName: String) -> Void

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            HStack(alignment: .top, spacing: 12) {
                preview
                VStack(alignment: .leading, spacing: 4) {
                    Text(verbatim: group.file.fileName.isEmpty ? String(localized: "shared_unnamed") : group.file.fileName)
                        .font(AppTypography.bodyMedium)
                        .lineLimit(1)
                        .truncationMode(.middle)
                        .foregroundStyle(AppColors.onSurface)
                    Text(String(localized: "shared_ishared_recipients"))
                        .font(.system(size: 12))
                        .foregroundStyle(AppColors.onSurfaceVariant)
                }
                Spacer(minLength: 0)
            }
            chips
        }
        .padding(.vertical, 4)
    }

    @ViewBuilder
    private var preview: some View {
        let mediaPreview = group.file.preview(preferredWidth: 128)
        ZStack {
            RoundedRectangle(cornerRadius: 8)
                .fill(AppColors.onSurface.opacity(0.06))
                .frame(width: 48, height: 48)
            if let mediaPreview {
                RemoteImage(
                    fileId: group.file.id,
                    variant: .preview(width: mediaPreview.width),
                    url: mediaPreview.url,
                    contentMode: .fill
                ) {
                    fallbackIcon
                }
                .frame(width: 48, height: 48)
                .clipShape(RoundedRectangle(cornerRadius: 8))
            } else {
                fallbackIcon
            }
        }
    }

    private var fallbackIcon: some View {
        Image(systemName: group.file.isVideo ? "video.fill" : "doc.fill")
            .font(.system(size: 18))
            .foregroundStyle(AppColors.onSurfaceVariant)
    }

    /// Чипсы получателей с переносом по строкам.
    private var chips: some View {
        FlowLayout(spacing: 6) {
            ForEach(group.recipients) { recipient in
                RecipientChip(
                    name: name(for: recipient.userID),
                    onRevoke: { onRevoke(recipient.grantID, name(for: recipient.userID)) }
                )
            }
        }
    }

    private func name(for userID: Int64) -> String {
        users[userID]?.displayName ?? String(format: String(localized: "shared_owner_fallback"), String(userID))
    }
}

private struct RecipientChip: View {
    let name: String
    let onRevoke: () -> Void

    var body: some View {
        HStack(spacing: 6) {
            Text(verbatim: name)
                .font(.system(size: 13))
                .foregroundStyle(AppColors.onSurface)
                .lineLimit(1)
            Button(action: onRevoke) {
                Image(systemName: "xmark")
                    .font(.system(size: 11, weight: .semibold))
                    .foregroundStyle(AppColors.onSurfaceVariant)
            }
            .buttonStyle(.plain)
            .accessibilityLabel(String(localized: "shared_revoke"))
        }
        .padding(.leading, 10)
        .padding(.trailing, 6)
        .padding(.vertical, 5)
        .background(AppColors.onSurface.opacity(0.08))
        .clipShape(Capsule())
    }
}

/// Простой layout с переносом элементов по строкам (для чипсов получателей).
private struct FlowLayout: Layout {
    var spacing: CGFloat = 6

    func sizeThatFits(proposal: ProposedViewSize, subviews: Subviews, cache: inout Void) -> CGSize {
        let maxWidth = proposal.width ?? .infinity
        var x: CGFloat = 0
        var y: CGFloat = 0
        var rowHeight: CGFloat = 0
        for subview in subviews {
            let size = subview.sizeThatFits(.unspecified)
            if x + size.width > maxWidth, x > 0 {
                x = 0
                y += rowHeight + spacing
                rowHeight = 0
            }
            x += size.width + spacing
            rowHeight = max(rowHeight, size.height)
        }
        return CGSize(width: maxWidth == .infinity ? x : maxWidth, height: y + rowHeight)
    }

    func placeSubviews(in bounds: CGRect, proposal: ProposedViewSize, subviews: Subviews, cache: inout Void) {
        var x = bounds.minX
        var y = bounds.minY
        var rowHeight: CGFloat = 0
        for subview in subviews {
            let size = subview.sizeThatFits(.unspecified)
            if x + size.width > bounds.maxX, x > bounds.minX {
                x = bounds.minX
                y += rowHeight + spacing
                rowHeight = 0
            }
            subview.place(at: CGPoint(x: x, y: y), proposal: ProposedViewSize(size))
            x += size.width + spacing
            rowHeight = max(rowHeight, size.height)
        }
    }
}
