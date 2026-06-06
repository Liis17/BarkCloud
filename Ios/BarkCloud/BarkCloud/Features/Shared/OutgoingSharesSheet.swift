import SwiftUI
import Observation
import BarkCloudKit

/// Унифицированная строка исходящего гранта (на файл или папку) для списка
/// получателей — общий вид независимо от типа сущности.
private struct GrantRow: Identifiable, Hashable {
    let grantID: String
    let recipientUserID: Int64
    let sharedAt: Date
    var id: String { grantID }
}

/// Sheet «Кто видит этот файл/папку»: список получателей, которым уже выдан
/// грант, с возможностью отозвать каждый. Открывается из `ShareWithUserSheet`
/// (когда `outgoingCount > 0`).
///
/// Оптимистичный revoke: убираем из списка сразу, при ошибке возвращаем.
struct OutgoingSharesSheet: View {
    let context: ShareWithUserContext
    let onClose: () -> Void

    @Environment(AppEnvironment.self) private var env
    @State private var items: [GrantRow] = []
    @State private var owners: [Int64: CloudUser] = [:]
    @State private var isLoading = true
    @State private var snackbar: String?
    @State private var pendingRevoke: GrantRow?

    var body: some View {
        NavigationStack {
            content
                .navigationTitle(String(format: String(localized: "shared_outgoing_title"), context.name))
                .navigationBarTitleDisplayMode(.inline)
                .toolbar {
                    ToolbarItem(placement: .topBarTrailing) {
                        Button(String(localized: "shared_done")) { onClose() }
                    }
                }
                .overlay(alignment: .bottom) { snackbarOverlay }
                .confirmationDialog(
                    String(localized: "shared_revoke_grant_confirm"),
                    isPresented: Binding(
                        get: { pendingRevoke != nil },
                        set: { if !$0 { pendingRevoke = nil } }
                    ),
                    titleVisibility: .visible
                ) {
                    Button(String(localized: "shared_revoke"), role: .destructive) {
                        if let share = pendingRevoke {
                            Task { await revoke(share) }
                        }
                        pendingRevoke = nil
                    }
                    Button(String(localized: "action_cancel"), role: .cancel) {
                        pendingRevoke = nil
                    }
                }
        }
        .presentationDetents([.medium, .large])
        .task { await load() }
    }

    @ViewBuilder
    private var content: some View {
        if isLoading {
            ProgressView().frame(maxWidth: .infinity, maxHeight: .infinity)
        } else if items.isEmpty {
            VStack(spacing: 12) {
                Image(systemName: "person.2.slash")
                    .font(.system(size: 48))
                    .foregroundStyle(AppColors.onSurfaceVariant)
                Text(String(localized: "shared_outgoing_empty_title"))
                    .font(AppTypography.titleMedium)
                    .foregroundStyle(AppColors.onSurface)
                Text(String(localized: "shared_outgoing_empty_hint"))
                    .font(AppTypography.bodySmall)
                    .foregroundStyle(AppColors.onSurfaceVariant)
                    .multilineTextAlignment(.center)
                    .padding(.horizontal, 24)
            }
            .frame(maxWidth: .infinity, maxHeight: .infinity)
        } else {
            List {
                ForEach(items) { grant in
                    OutgoingRow(
                        grant: grant,
                        user: owners[grant.recipientUserID],
                        onRevoke: { pendingRevoke = grant }
                    )
                }
            }
            .listStyle(.plain)
        }
    }

    @ViewBuilder
    private var snackbarOverlay: some View {
        if let text = snackbar {
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
                        snackbar = nil
                    }
                }
        }
    }

    private func load() async {
        isLoading = true
        defer { isLoading = false }
        do {
            let rows: [GrantRow]
            if context.isFolder {
                rows = try await env.cloudRepository.listMyOutgoingFolderShares()
                    .filter { $0.directoryID == context.entityID }
                    .map { GrantRow(grantID: $0.grantID, recipientUserID: $0.recipientUserID, sharedAt: $0.sharedAt) }
            } else {
                rows = try await env.cloudRepository.listMyOutgoingShares(fileID: context.entityID)
                    .map { GrantRow(grantID: $0.grantID, recipientUserID: $0.recipientUserID, sharedAt: $0.sharedAt) }
            }
            items = rows
            await resolveOwners(for: rows)
        } catch {
            snackbar = String(localized: "shared_load_failed")
        }
    }

    private func resolveOwners(for shares: [GrantRow]) async {
        let ids = Set(shares.map(\.recipientUserID)).subtracting(owners.keys)
        guard !ids.isEmpty else { return }
        await withTaskGroup(of: (Int64, CloudUser?).self) { group in
            for id in ids {
                group.addTask { @MainActor in
                    let raw = try? await env.userRepository.getUser(userID: id)
                    return (id, raw.map(CloudUser.init))
                }
            }
            for await (id, user) in group {
                if let user { owners[id] = user }
            }
        }
    }

    private func revoke(_ grant: GrantRow) async {
        guard let idx = items.firstIndex(where: { $0.id == grant.id }) else { return }
        items.remove(at: idx)
        do {
            if context.isFolder {
                try await env.cloudRepository.revokeFolderUserShare(grantID: grant.grantID)
            } else {
                try await env.cloudRepository.revokeUserShare(grantID: grant.grantID)
            }
            snackbar = String(localized: "shared_grant_revoked")
        } catch {
            items.insert(grant, at: min(idx, items.count))
            snackbar = String(localized: "shared_revoke_failed")
        }
    }
}

private struct OutgoingRow: View {
    let grant: GrantRow
    let user: CloudUser?
    let onRevoke: () -> Void

    var body: some View {
        HStack(spacing: 12) {
            avatar
            VStack(alignment: .leading, spacing: 2) {
                Text(displayName)
                    .font(AppTypography.bodyMedium)
                    .foregroundStyle(AppColors.onSurface)
                if let user, !user.username.isEmpty {
                    Text("@\(user.username)")
                        .font(.system(size: 12))
                        .foregroundStyle(AppColors.onSurfaceVariant)
                }
                Text(grant.sharedAt, format: .dateTime.day().month().year())
                    .font(.system(size: 12))
                    .foregroundStyle(AppColors.onSurfaceVariant)
            }
            Spacer(minLength: 8)
            Button(action: onRevoke) {
                Image(systemName: "trash")
                    .font(.system(size: 16))
                    .foregroundStyle(AppColors.error)
                    .frame(width: 36, height: 36)
            }
            .buttonStyle(.plain)
            .accessibilityLabel(String(localized: "shared_revoke"))
        }
        .padding(.vertical, 4)
    }

    private var displayName: String {
        user?.displayName ?? String(format: String(localized: "shared_owner_fallback"), String(grant.recipientUserID))
    }

    @ViewBuilder
    private var avatar: some View {
        ZStack {
            Circle()
                .fill(AppColors.onSurface.opacity(0.08))
                .frame(width: 36, height: 36)
            if let url = user?.avatarURL {
                RemoteImage(url: url) {
                    Image(systemName: "person.fill")
                        .foregroundStyle(AppColors.onSurfaceVariant)
                }
                .frame(width: 36, height: 36)
                .clipShape(Circle())
            } else {
                Image(systemName: "person.fill")
                    .foregroundStyle(AppColors.onSurfaceVariant)
            }
        }
    }
}
