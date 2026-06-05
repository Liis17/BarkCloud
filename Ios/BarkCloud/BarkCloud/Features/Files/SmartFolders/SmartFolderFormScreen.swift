import SwiftUI
import BarkCloudKit

/// Конструктор умной папки: имя, комбинатор И/ИЛИ, режим отображения (сетка/список)
/// и список правил поле→оператор→значение. Зеркало веб-`DynamicFolderFormModal`.
struct SmartFolderFormScreen: View {
    @Environment(\.dismiss) private var dismiss
    @State private var vm: SmartFolderFormViewModel

    /// Вызывается после успешного сохранения с обновлённой/созданной карточкой.
    let onSaved: (DynamicFolderCard) -> Void

    init(repo: DynamicFolderRepository, existing: DynamicFolderCard?, onSaved: @escaping (DynamicFolderCard) -> Void) {
        _vm = State(initialValue: SmartFolderFormViewModel(existing: existing, repo: repo))
        self.onSaved = onSaved
    }

    private static let dateFmt: DateFormatter = {
        let f = DateFormatter()
        f.dateFormat = "yyyy-MM-dd"
        f.locale = Locale(identifier: "en_US_POSIX")
        return f
    }()

    var body: some View {
        NavigationStack {
            Form {
                Section {
                    TextField("smart_folder_name_placeholder", text: $vm.name)
                }

                Section("smart_folder_match") {
                    Picker("smart_folder_match", selection: $vm.combinator) {
                        Text("smart_folder_match_all").tag(Barkcloud_Files_DfCombinator.dfAll)
                        Text("smart_folder_match_any").tag(Barkcloud_Files_DfCombinator.dfAny)
                    }
                    .pickerStyle(.segmented)
                }

                Section("smart_folder_view") {
                    Picker("smart_folder_view", selection: $vm.viewMode) {
                        Text("smart_folder_view_grid").tag(Barkcloud_Files_DfViewMode.dfViewGrid)
                        Text("smart_folder_view_list").tag(Barkcloud_Files_DfViewMode.dfViewList)
                    }
                    .pickerStyle(.segmented)
                }

                Section("smart_folder_conditions") {
                    ForEach(Array(vm.rules.enumerated()), id: \.element.id) { pair in
                        ruleRow(pair.offset)
                    }
                    Button {
                        vm.addRule()
                    } label: {
                        Label("smart_folder_add_condition", systemImage: "plus.circle")
                    }
                }
            }
            .navigationTitle(vm.isEditing ? Text("smart_folder_edit_title") : Text("smart_folder_create_title"))
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .cancellationAction) {
                    Button(String(localized: "action_cancel")) { dismiss() }
                }
                ToolbarItem(placement: .confirmationAction) {
                    if vm.isSaving {
                        ProgressView()
                    } else {
                        Button(String(localized: "action_save")) {
                            Task {
                                if let card = await vm.save() {
                                    onSaved(card)
                                    dismiss()
                                }
                            }
                        }
                    }
                }
            }
            .alert(
                vm.error ?? "",
                isPresented: Binding(get: { vm.error != nil }, set: { if !$0 { vm.error = nil } })
            ) {
                Button(String(localized: "action_ok"), role: .cancel) { vm.error = nil }
            }
        }
    }

    // MARK: - Строка правила

    @ViewBuilder
    private func ruleRow(_ index: Int) -> some View {
        let meta = DfFieldMeta.meta(for: vm.rules[index].field)
        VStack(alignment: .leading, spacing: 8) {
            HStack {
                Picker(
                    "df_field",
                    selection: Binding(
                        get: { vm.rules[index].field },
                        set: { vm.setField($0, at: index) }
                    )
                ) {
                    ForEach(DfFieldMeta.all) { m in
                        Text(m.titleKey).tag(m.field)
                    }
                }
                .pickerStyle(.menu)
                Spacer()
                if vm.rules.count > 1 {
                    Button {
                        vm.removeRule(at: index)
                    } label: {
                        Image(systemName: "minus.circle.fill")
                            .foregroundStyle(AppColors.error)
                    }
                    .buttonStyle(.plain)
                }
            }

            Picker(
                "df_operator",
                selection: Binding(
                    get: { vm.rules[index].op },
                    set: { vm.setOp($0, at: index) }
                )
            ) {
                ForEach(meta.operators, id: \.rawValue) { op in
                    Text(DfFieldMeta.opTitle(op)).tag(op)
                }
            }
            .pickerStyle(.menu)

            valueEditor(index, meta)
        }
        .padding(.vertical, 4)
    }

    @ViewBuilder
    private func valueEditor(_ index: Int, _ meta: DfFieldMeta) -> some View {
        switch meta.editor {
        case .daysOrDate:
            if vm.rules[index].op == .dfWithinLastDays {
                TextField("df_value_days", text: valueBinding(index))
                    .keyboardType(.numberPad)
            } else {
                DatePicker(selection: dateBinding(index), displayedComponents: .date) {
                    Text("df_value_date")
                }
            }
        case .size:
            HStack {
                TextField("df_value_size_mb", text: sizeMBBinding(index))
                    .keyboardType(.decimalPad)
                Text("df_unit_mb").foregroundStyle(AppColors.onSurfaceVariant)
            }
        case .number:
            HStack {
                TextField("df_value_px", text: valueBinding(index))
                    .keyboardType(.numberPad)
                Text("df_unit_px").foregroundStyle(AppColors.onSurfaceVariant)
            }
        case .text:
            TextField("df_value_text", text: valueBinding(index))
        case .ext:
            TextField("df_value_ext", text: valueBinding(index))
                .autocorrectionDisabled()
                .textInputAutocapitalization(.never)
        case .mediaKind:
            Picker("df_field_format", selection: valueBinding(index)) {
                ForEach(DfFieldMeta.mediaKinds, id: \.code) { mk in
                    Text(mk.titleKey).tag(mk.code)
                }
            }
            .pickerStyle(.menu)
        }
    }

    // MARK: - Биндинги значения

    private func valueBinding(_ index: Int) -> Binding<String> {
        Binding(
            get: { vm.rules[index].value },
            set: { vm.rules[index].value = $0 }
        )
    }

    /// Размер: отображаем МБ, храним байты (×1 048 576), как в вебе.
    private func sizeMBBinding(_ index: Int) -> Binding<String> {
        Binding(
            get: {
                let bytes = Int64(vm.rules[index].value) ?? 0
                if bytes == 0 { return "" }
                let mb = Double(bytes) / 1_048_576.0
                return mb == mb.rounded() ? String(Int(mb)) : String(format: "%.2f", mb)
            },
            set: { text in
                let mb = Double(text.replacingOccurrences(of: ",", with: ".")) ?? 0
                vm.rules[index].value = mb > 0 ? String(Int64(mb * 1_048_576.0)) : ""
            }
        )
    }

    /// Дата до/после: храним ISO `yyyy-MM-dd`.
    private func dateBinding(_ index: Int) -> Binding<Date> {
        Binding(
            get: { Self.dateFmt.date(from: vm.rules[index].value) ?? Date() },
            set: { vm.rules[index].value = Self.dateFmt.string(from: $0) }
        )
    }
}
