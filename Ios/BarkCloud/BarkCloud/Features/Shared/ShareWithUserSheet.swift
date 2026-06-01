import SwiftUI
import Observation

/// Контекст «что расшариваем». Передаётся в `.sheet(item:)` из вызывающего
/// экрана. Identifiable, чтобы биндить по fileID.
struct ShareWithUserContext: Identifiable, Hashable, Sendable {
    let fileID: String
    let fileName: String
    var id: String { fileID }
}

/// Sheet «Поделиться с пользователем»: поиск получателя по юзернейму/имени
/// (debounce 300мс, минимум 2 символа) → список найденных → тап «Поделиться» →
/// статус «Выдан». Идемпотентно: повторный тап на уже выданном проходит без
/// ошибки и просто обновляет UI.
///
/// Эквивалент `ShareWithUserModal.tsx` на вебе.
struct ShareWithUserSheet: View {
    let context: ShareWithUserContext
    let onClose: () -> Void

    @Environment(AppEnvironment.self) private var env
    @State private var query: String = ""
    @State private var users: [CloudUser] = []
    @State private var isLoading = false
    @State private var sharedIDs: Set<Int64> = []
    @State private var snackbar: String?
    @State private var searchTask: Task<Void, Never>?

    var body: some View {
        NavigationStack {
            VStack(spacing: 0) {
                searchField
                content
            }
            .navigationTitle(String(format: String(localized: "shared_with_user_title"), context.fileName))
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .topBarTrailing) {
                    Button(String(localized: "shared_done")) { onClose() }
                }
            }
            .overlay(alignment: .bottom) { snackbarOverlay }
        }
        .presentationDetents([.medium, .large])
        .onDisappear { searchTask?.cancel() }
    }

    private var searchField: some View {
        HStack(spacing: 8) {
            Image(systemName: "magnifyingglass")
                .foregroundStyle(AppColors.onSurfaceVariant)
            TextField(String(localized: "shared_search_placeholder"), text: $query)
                .textInputAutocapitalization(.never)
                .autocorrectionDisabled()
                .onChange(of: query) { _, newValue in scheduleSearch(for: newValue) }
        }
        .padding(.horizontal, 14)
        .padding(.vertical, 10)
        .background(AppColors.onSurface.opacity(0.06))
        .clipShape(RoundedRectangle(cornerRadius: 10))
        .padding(.horizontal, 16)
        .padding(.top, 12)
    }

    @ViewBuilder
    private var content: some View {
        let trimmed = query.trimmingCharacters(in: .whitespacesAndNewlines)
        if trimmed.count < 2 {
            hint(text: String(localized: "shared_search_min_chars"))
        } else if isLoading {
            hint(text: String(localized: "shared_search_loading"))
        } else if users.isEmpty {
            hint(text: String(localized: "shared_search_no_results"))
        } else {
            List {
                ForEach(users) { user in
                    UserRow(
                        user: user,
                        isShared: sharedIDs.contains(user.id),
                        onShare: { Task { await share(user) } }
                    )
                }
            }
            .listStyle(.plain)
        }
    }

    private func hint(text: String) -> some View {
        Text(text)
            .font(AppTypography.bodySmall)
            .foregroundStyle(AppColors.onSurfaceVariant)
            .multilineTextAlignment(.center)
            .frame(maxWidth: .infinity, maxHeight: .infinity)
            .padding(.horizontal, 24)
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

    /// Debounce 300мс: отменяем предыдущий поиск, спим, потом запрашиваем.
    /// Так не нагружаем сервер при печати по символу.
    private func scheduleSearch(for value: String) {
        searchTask?.cancel()
        let trimmed = value.trimmingCharacters(in: .whitespacesAndNewlines)
        guard trimmed.count >= 2 else {
            users = []
            isLoading = false
            return
        }
        isLoading = true
        searchTask = Task { @MainActor in
            try? await Task.sleep(nanoseconds: 300_000_000)
            if Task.isCancelled { return }
            do {
                let found = try await env.userRepository.searchUsers(query: trimmed, limit: 20)
                if Task.isCancelled { return }
                users = found
            } catch {
                if !Task.isCancelled {
                    users = []
                    snackbar = String(localized: "shared_search_failed")
                }
            }
            isLoading = false
        }
    }

    private func share(_ user: CloudUser) async {
        do {
            try await env.cloudRepository.shareFileWithUser(
                fileID: context.fileID,
                recipientUserID: user.id
            )
            sharedIDs.insert(user.id)
            snackbar = String(format: String(localized: "shared_grant_success"), user.displayName)
        } catch {
            snackbar = String(localized: "shared_grant_failed")
        }
    }
}

private struct UserRow: View {
    let user: CloudUser
    let isShared: Bool
    let onShare: () -> Void

    var body: some View {
        HStack(spacing: 12) {
            avatar
            VStack(alignment: .leading, spacing: 2) {
                Text(user.displayName)
                    .font(AppTypography.bodyMedium)
                    .foregroundStyle(AppColors.onSurface)
                if !user.username.isEmpty {
                    Text("@\(user.username)")
                        .font(.system(size: 12))
                        .foregroundStyle(AppColors.onSurfaceVariant)
                }
            }
            Spacer(minLength: 8)
            if isShared {
                Label(String(localized: "shared_granted"), systemImage: "checkmark.circle.fill")
                    .font(.system(size: 13, weight: .medium))
                    .foregroundStyle(AppColors.accent)
            } else {
                Button(String(localized: "shared_share_action"), action: onShare)
                    .buttonStyle(.bordered)
                    .controlSize(.small)
            }
        }
        .padding(.vertical, 4)
    }

    @ViewBuilder
    private var avatar: some View {
        ZStack {
            Circle()
                .fill(AppColors.onSurface.opacity(0.08))
                .frame(width: 36, height: 36)
            if let url = user.avatarURL {
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
