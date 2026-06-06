import SwiftUI
import Observation
import BarkCloudKit

/// Контекст «что расшариваем» — файл или папка. Передаётся в `.sheet(item:)` из
/// вызывающего экрана. Два инициализатора: файловый (старый API сохранён) и
/// папочный. `entityID` — fileID либо directoryID в зависимости от `isFolder`.
struct ShareWithUserContext: Identifiable, Hashable, Sendable {
    let entityID: String
    let name: String
    let isFolder: Bool
    /// Префикс разводит file/folder с одинаковым id в разные `.sheet(item:)`.
    var id: String { (isFolder ? "d:" : "f:") + entityID }

    init(fileID: String, fileName: String) {
        self.entityID = fileID
        self.name = fileName
        self.isFolder = false
    }

    init(folderID: String, folderName: String) {
        self.entityID = folderID
        self.name = folderName
        self.isFolder = true
    }
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
    /// Кому уже выдан грант на этот файл — сразу подгружаем при открытии,
    /// чтобы показать счётчик и баннер «Уже расшарено: N → Управление».
    /// Пустой массив → баннера нет. Заполняется в `task` через
    /// `cloud.listMyOutgoingShares`.
    @State private var outgoingCount: Int = 0
    @State private var showOutgoing = false

    var body: some View {
        NavigationStack {
            VStack(spacing: 0) {
                searchField
                if outgoingCount > 0 { outgoingBanner }
                content
            }
            .navigationTitle(String(format: String(localized: "shared_with_user_title"), context.name))
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .topBarTrailing) {
                    Button(String(localized: "shared_done")) { onClose() }
                }
            }
            .overlay(alignment: .bottom) { snackbarOverlay }
            .sheet(isPresented: $showOutgoing) {
                OutgoingSharesSheet(context: context, onClose: {
                    showOutgoing = false
                    Task { await refreshOutgoingCount() }
                })
            }
        }
        .presentationDetents([.medium, .large])
        .onDisappear { searchTask?.cancel() }
        .task { await refreshOutgoingCount() }
    }

    private var outgoingBanner: some View {
        Button { showOutgoing = true } label: {
            HStack(spacing: 10) {
                Image(systemName: "person.2.fill")
                    .font(.system(size: 14, weight: .semibold))
                    .foregroundStyle(AppColors.accent)
                Text(String(format: String(localized: "shared_already_count"), outgoingCount))
                    .font(AppTypography.bodySmall)
                    .foregroundStyle(AppColors.onSurface)
                Spacer()
                Text(String(localized: "shared_manage_grants"))
                    .font(AppTypography.bodySmall)
                    .foregroundStyle(AppColors.accent)
                Image(systemName: "chevron.right")
                    .font(.system(size: 11, weight: .semibold))
                    .foregroundStyle(AppColors.accent)
            }
            .padding(.horizontal, 12)
            .padding(.vertical, 10)
            .background(AppColors.accent.opacity(0.10))
            .clipShape(RoundedRectangle(cornerRadius: 10))
        }
        .buttonStyle(.plain)
        .padding(.horizontal, 16)
        .padding(.top, 8)
    }

    /// Подтянуть актуальный счётчик активных грантов на файл — для баннера.
    /// Вызывается при открытии sheet и после закрытия `OutgoingSharesSheet`
    /// (в нём могли отозвать).
    private func refreshOutgoingCount() async {
        if context.isFolder {
            let all = (try? await env.cloudRepository.listMyOutgoingFolderShares()) ?? []
            outgoingCount = all.filter { $0.directoryID == context.entityID }.count
        } else {
            outgoingCount = (try? await env.cloudRepository.listMyOutgoingShares(fileID: context.entityID).count) ?? 0
        }
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
        let alreadyShared = sharedIDs.contains(user.id)
        do {
            if context.isFolder {
                try await env.cloudRepository.shareFolderWithUser(
                    directoryID: context.entityID,
                    recipientUserID: user.id
                )
            } else {
                try await env.cloudRepository.shareFileWithUser(
                    fileID: context.entityID,
                    recipientUserID: user.id
                )
            }
            sharedIDs.insert(user.id)
            if !alreadyShared { outgoingCount += 1 }
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
