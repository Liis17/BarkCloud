import Foundation
import Observation
import BarkCloudKit

/// Тип редактора значения для правила (определяет UI-контрол в строке).
enum DfValueEditor {
    case daysOrDate  // дата: «за N дней» → число, до/после → DatePicker
    case size        // размер: ввод в МБ, хранение в байтах
    case number      // ширина/высота: целое px
    case text        // имя/устройство: произвольный текст
    case ext         // расширение: ".png"
    case mediaKind   // формат: выбор Фото/Видео/Документ/Аудио/Другое
}

/// Метаданные поля критерия: заголовок, допустимые операторы, тип редактора.
/// Зеркало веб-таблицы `FIELDS` в `DynamicFolderFormModal.tsx`.
struct DfFieldMeta: Identifiable {
    let field: Barkcloud_Files_DfField
    let titleKey: LocalizedStringResource
    let operators: [Barkcloud_Files_DfOperator]
    let editor: DfValueEditor

    var id: Int { field.rawValue }

    static let all: [DfFieldMeta] = [
        .init(field: .dfDate, titleKey: "df_field_date", operators: [.dfWithinLastDays, .dfBefore, .dfAfter], editor: .daysOrDate),
        .init(field: .dfTakenAt, titleKey: "df_field_taken", operators: [.dfWithinLastDays, .dfBefore, .dfAfter], editor: .daysOrDate),
        .init(field: .dfSize, titleKey: "df_field_size", operators: [.dfGt, .dfLt, .dfEquals], editor: .size),
        .init(field: .dfName, titleKey: "df_field_name", operators: [.dfContains, .dfStartsWith, .dfEndsWith, .dfEquals], editor: .text),
        .init(field: .dfMediaKind, titleKey: "df_field_format", operators: [.dfEquals], editor: .mediaKind),
        .init(field: .dfExtension, titleKey: "df_field_ext", operators: [.dfEndsWith], editor: .ext),
        .init(field: .dfImgWidth, titleKey: "df_field_width", operators: [.dfGt, .dfLt, .dfEquals], editor: .number),
        .init(field: .dfImgHeight, titleKey: "df_field_height", operators: [.dfGt, .dfLt, .dfEquals], editor: .number),
        .init(field: .dfDevice, titleKey: "df_field_device", operators: [.dfEquals, .dfContains], editor: .text),
    ]

    static func meta(for field: Barkcloud_Files_DfField) -> DfFieldMeta {
        all.first { $0.field == field } ?? all[0]
    }

    static func opTitle(_ op: Barkcloud_Files_DfOperator) -> LocalizedStringResource {
        switch op {
        case .dfWithinLastDays: return "df_op_within_days"
        case .dfBefore: return "df_op_before"
        case .dfAfter: return "df_op_after"
        case .dfGt: return "df_op_gt"
        case .dfLt: return "df_op_lt"
        case .dfContains: return "df_op_contains"
        case .dfEquals: return "df_op_equals"
        case .dfEndsWith: return "df_op_ends"
        case .dfStartsWith: return "df_op_starts"
        default: return "df_op_equals"
        }
    }

    /// Коды MediaKind (как на проводе) + заголовки для Picker'а формата.
    static let mediaKinds: [(code: String, titleKey: LocalizedStringResource)] = [
        ("1", "df_kind_photo"),
        ("2", "df_kind_video"),
        ("3", "df_kind_document"),
        ("4", "df_kind_audio"),
        ("0", "df_kind_other"),
    ]
}

/// Состояние конструктора умной папки (создание/редактирование).
@MainActor
@Observable
final class SmartFolderFormViewModel {
    var name: String
    var combinator: Barkcloud_Files_DfCombinator
    var viewMode: Barkcloud_Files_DfViewMode
    var rules: [DynamicFolderRule]
    var error: String?
    var isSaving = false

    let isEditing: Bool
    private let existingID: String?
    private let repo: DynamicFolderRepository

    init(existing: DynamicFolderCard?, repo: DynamicFolderRepository) {
        self.repo = repo
        self.existingID = existing?.id
        self.isEditing = existing != nil
        self.name = existing?.name ?? ""
        self.combinator = existing?.combinator ?? .dfAll
        self.viewMode = existing?.viewMode ?? .dfViewGrid
        let existingRules = existing?.rules ?? []
        self.rules = existingRules.isEmpty ? [DynamicFolderRule()] : existingRules
    }

    /// Смена поля: оператор сбрасывается на первый допустимый, значение —
    /// на дефолт типа (формат → "1" = Фото, иначе пусто). Зеркало веб-`changeField`.
    func setField(_ field: Barkcloud_Files_DfField, at index: Int) {
        guard rules.indices.contains(index) else { return }
        let meta = DfFieldMeta.meta(for: field)
        rules[index].field = field
        rules[index].op = meta.operators.first ?? .dfEquals
        rules[index].value = field == .dfMediaKind ? "1" : ""
    }

    /// Смена оператора: для дат «за N дней» ↔ «до/после» формат значения разный,
    /// поэтому значение очищается.
    func setOp(_ op: Barkcloud_Files_DfOperator, at index: Int) {
        guard rules.indices.contains(index) else { return }
        let wasDays = rules[index].op == .dfWithinLastDays
        let nowDays = op == .dfWithinLastDays
        rules[index].op = op
        if wasDays != nowDays { rules[index].value = "" }
    }

    func addRule() { rules.append(DynamicFolderRule()) }

    func removeRule(at index: Int) {
        guard rules.count > 1, rules.indices.contains(index) else { return }
        rules.remove(at: index)
    }

    /// Валидация (имя непустое + хотя бы одно правило со значением) и сохранение.
    /// Возвращает обновлённую карточку при успехе, иначе `nil` (ошибка в `error`).
    func save() async -> DynamicFolderCard? {
        let trimmed = name.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty else {
            error = String(localized: "smart_folder_err_name")
            return nil
        }
        let clean = rules.filter { !$0.value.trimmingCharacters(in: .whitespaces).isEmpty }
        guard !clean.isEmpty else {
            error = String(localized: "smart_folder_err_rules")
            return nil
        }
        isSaving = true
        defer { isSaving = false }
        do {
            if let id = existingID {
                return try await repo.update(folderID: id, name: trimmed, combinator: combinator, rules: clean, viewMode: viewMode)
            } else {
                return try await repo.create(name: trimmed, combinator: combinator, rules: clean, viewMode: viewMode)
            }
        } catch {
            self.error = domainErrorMessage(error)
            return nil
        }
    }
}
