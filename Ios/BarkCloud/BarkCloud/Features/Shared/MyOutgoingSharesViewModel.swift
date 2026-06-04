import Foundation
import Observation
import BarkCloudKit

/// Получатель внутри группы «Я поделился» — один грант на файл.
struct SharedByMeRecipient: Identifiable, Hashable, Sendable {
    let grantID: String
    let userID: Int64
    let sharedAt: Date
    var id: String { grantID }
}

/// Группа таба «Я поделился»: один файл и все, с кем им поделились. Бэкенд отдаёт
/// плоский список грантов, группировку по файлу делает VM (как веб-слой).
struct SharedByMeGroup: Identifiable, Hashable, Sendable {
    let file: MediaAsset
    var recipients: [SharedByMeRecipient]
    var id: String { file.id }
}

/// Состояние таба «Я поделился» в `SharedHubScreen`.
struct MyOutgoingSharesUiState {
    /// Сырые гранты (плоско, как с бэкенда), сортировка от свежих к старым.
    var raw: [OutgoingShareFull] = []
    /// Сгруппированные по файлу для отображения (порядок — по первому появлению файла).
    var groups: [SharedByMeGroup] = []
    /// recipientUserID → CloudUser. Нет в словаре → рендер показывает «id N».
    var users: [Int64: CloudUser] = [:]
    var isPlaceholder: Bool = true
    var isLoadingMore: Bool = false
    var canLoadMore: Bool = false
    var snackbar: String?

    fileprivate var cursorSharedAt: Date?
    fileprivate var cursorGrantID: String = ""
}

/// View-model раздела «Я поделился»: пагинируемый список исходящих грантов,
/// сгруппированных по файлу, с резолвом имён получателей и отзывом гранта.
///
/// Revoke оптимистичен: убираем грант из `raw` и пересобираем группы; при ошибке
/// возвращаем. `revokeUserShare` идемпотентен на бэкенде.
@MainActor
@Observable
final class MyOutgoingSharesViewModel {
    var state = MyOutgoingSharesUiState()

    private let cloud: CloudRepository
    private let users: UserRepository
    private var didLoad = false

    init(cloud: CloudRepository, users: UserRepository) {
        self.cloud = cloud
        self.users = users
    }

    func loadIfNeeded() async {
        guard !didLoad else { return }
        didLoad = true
        await reload()
    }

    func reload() async {
        do {
            let page = try await cloud.listMyOutgoingSharesAll(limit: 60)
            state.raw = page.items
            state.cursorSharedAt = page.nextCursorSharedAt
            state.cursorGrantID = page.nextCursorGrantID
            state.canLoadMore = page.hasMore
            regroup()
            await resolveRecipients(for: page.items)
        } catch {
            state.raw = []
            state.groups = []
            state.snackbar = String(localized: "shared_load_failed")
        }
        state.isPlaceholder = false
    }

    func loadMoreIfNeeded(current group: SharedByMeGroup) async {
        guard state.canLoadMore, !state.isLoadingMore, !state.isPlaceholder,
              group.id == state.groups.last?.id else { return }
        state.isLoadingMore = true
        do {
            let page = try await cloud.listMyOutgoingSharesAll(
                limit: 60,
                cursorSharedAt: state.cursorSharedAt,
                cursorGrantID: state.cursorGrantID
            )
            state.raw.append(contentsOf: page.items)
            state.cursorSharedAt = page.nextCursorSharedAt
            state.cursorGrantID = page.nextCursorGrantID
            state.canLoadMore = page.hasMore
            regroup()
            await resolveRecipients(for: page.items)
        } catch {
            state.snackbar = String(localized: "shared_load_failed")
        }
        state.isLoadingMore = false
    }

    /// Оптимистично отозвать один грант: убираем из `raw`, пересобираем группы,
    /// зовём бэкенд. При ошибке возвращаем грант и пересобираем заново.
    func revoke(grantID: String) async {
        guard let removed = state.raw.first(where: { $0.grantID == grantID }) else { return }
        state.raw.removeAll { $0.grantID == grantID }
        regroup()
        do {
            try await cloud.revokeUserShare(grantID: grantID)
            state.snackbar = String(localized: "shared_grant_revoked")
        } catch {
            state.raw.append(removed)
            regroup()
            state.snackbar = String(localized: "shared_revoke_failed")
        }
    }

    func snackbarShown() { state.snackbar = nil }

    /// Сгруппировать `raw` по файлу, сохраняя порядок первого появления файла
    /// (raw отсортирован от свежих к старым → группы тоже от свежих).
    private func regroup() {
        var order: [String] = []
        var map: [String: SharedByMeGroup] = [:]
        for item in state.raw {
            let recipient = SharedByMeRecipient(
                grantID: item.grantID, userID: item.recipientUserID, sharedAt: item.sharedAt)
            if var group = map[item.file.id] {
                group.recipients.append(recipient)
                map[item.file.id] = group
            } else {
                order.append(item.file.id)
                map[item.file.id] = SharedByMeGroup(file: item.file, recipients: [recipient])
            }
        }
        state.groups = order.compactMap { map[$0] }
    }

    /// Резолв карточек получателей для новых recipientUserID. Ошибки одного
    /// пользователя проглатываем — UI отрисует фоллбек «id N».
    private func resolveRecipients(for entries: [OutgoingShareFull]) async {
        let ids = Set(entries.map(\.recipientUserID)).subtracting(state.users.keys)
        guard !ids.isEmpty else { return }
        await withTaskGroup(of: (Int64, CloudUser?).self) { group in
            for id in ids {
                group.addTask { [users] in
                    let raw = try? await users.getUser(userID: id)
                    return (id, raw.map(CloudUser.init))
                }
            }
            for await (id, user) in group {
                if let user { state.users[id] = user }
            }
        }
    }
}
