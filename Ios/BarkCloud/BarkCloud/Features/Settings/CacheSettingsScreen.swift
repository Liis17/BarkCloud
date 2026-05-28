import SwiftUI

/// Раздел «Кеш»: текущий размер и число записей, выбор лимита и кнопки очистки.
struct CacheSettingsScreen: View {
    @Environment(AppEnvironment.self) private var env
    @State private var vm: CacheSettingsViewModel?
    @State private var showClearAllConfirm = false

    private static func gb(_ value: Int64) -> Int64 { value * 1024 * 1024 * 1024 }
    private static let limitOptions: [(key: LocalizedStringResource, bytes: Int64)] = [
        ("cache_limit_1gb", gb(1)),
        ("cache_limit_2gb", gb(2)),
        ("cache_limit_5gb", gb(5)),
        ("cache_limit_10gb", gb(10)),
        ("cache_limit_20gb", gb(20)),
    ]

    var body: some View {
        Group {
            if let vm {
                content(vm)
            } else {
                ProgressView().frame(maxWidth: .infinity, maxHeight: .infinity)
            }
        }
        .navigationTitle(String(localized: "settings_cache"))
        .navigationBarTitleDisplayMode(.inline)
        .task {
            if vm == nil {
                vm = CacheSettingsViewModel(cache: env.fileCache, settings: env.fileCacheSettings)
            }
            await vm?.load()
        }
    }

    @ViewBuilder
    private func content(_ vm: CacheSettingsViewModel) -> some View {
        Form {
            Section {
                LabeledContent(String(localized: "cache_size"),
                               value: FormatUtils.formatSize(vm.state.sizeBytes))
                LabeledContent(String(localized: "cache_entries"),
                               value: "\(vm.state.entryCount)")
            }

            Section {
                Picker(String(localized: "cache_limit"), selection: limitBinding(vm)) {
                    ForEach(Self.limitOptions, id: \.bytes) { option in
                        Text(option.key).tag(option.bytes)
                    }
                }
            }

            Section {
                Button(String(localized: "cache_clear_stale")) {
                    Task { await vm.clearStale() }
                }
                Button(String(localized: "cache_clear_all"), role: .destructive) {
                    showClearAllConfirm = true
                }
                .confirmationDialog(String(localized: "cache_clear_confirm"),
                                    isPresented: $showClearAllConfirm,
                                    titleVisibility: .visible) {
                    Button(String(localized: "cache_clear_all"), role: .destructive) {
                        Task { await vm.clearAll() }
                    }
                    Button(String(localized: "action_cancel"), role: .cancel) {}
                }
            }
            .disabled(vm.state.isWorking)
        }
        .overlay {
            if vm.state.isWorking {
                ProgressView()
                    .padding(20)
                    .background(.regularMaterial)
                    .clipShape(RoundedRectangle(cornerRadius: 14))
            }
        }
    }

    private func limitBinding(_ vm: CacheSettingsViewModel) -> Binding<Int64> {
        Binding(
            get: { vm.state.limitBytes },
            set: { newValue in Task { await vm.setLimit(newValue) } }
        )
    }
}
