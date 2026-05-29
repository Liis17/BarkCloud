import SwiftUI

/// Раздел «Кеш»: хранилище устройства с долей кеша, размер/записи, лимит,
/// период автоочистки и кнопки очистки.
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

    private static let day: TimeInterval = 24 * 3600
    private static let autoCleanOptions: [(key: LocalizedStringResource, value: TimeInterval?)] = [
        ("cache_auto_clean_1d", day),
        ("cache_auto_clean_7d", 7 * day),
        ("cache_auto_clean_30d", 30 * day),
        ("cache_auto_clean_never", nil),
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
            Section(String(localized: "cache_device_storage")) {
                VStack(alignment: .leading, spacing: 10) {
                    StorageBar(
                        other: vm.state.deviceOtherBytes,
                        cache: vm.state.sizeBytes,
                        total: vm.state.deviceTotalBytes
                    )
                    HStack(spacing: 6) {
                        Circle().fill(AppColors.accent).frame(width: 8, height: 8)
                        Text(verbatim: "\(String(localized: "settings_cache")) \(FormatUtils.formatSize(vm.state.sizeBytes))")
                        Spacer()
                        Text(verbatim: "\(String(localized: "cache_free")) \(FormatUtils.formatSize(vm.state.deviceFreeBytes)) / \(FormatUtils.formatSize(vm.state.deviceTotalBytes))")
                    }
                    .font(AppTypography.bodySmall)
                    .foregroundStyle(AppColors.onSurfaceVariant)
                }
                .padding(.vertical, 4)
            }

            Section {
                LabeledContent(String(localized: "cache_size"),
                               value: FormatUtils.formatSize(vm.state.sizeBytes))
                LabeledContent(String(localized: "cache_entries"),
                               value: "\(vm.state.entryCount)")
                Picker(String(localized: "cache_limit"), selection: limitBinding(vm)) {
                    ForEach(Self.limitOptions, id: \.bytes) { option in
                        Text(option.key).tag(option.bytes)
                    }
                }
                Picker(String(localized: "cache_auto_clean"), selection: autoCleanBinding(vm)) {
                    ForEach(Array(Self.autoCleanOptions.enumerated()), id: \.offset) { _, option in
                        Text(option.key).tag(option.value)
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

    private func autoCleanBinding(_ vm: CacheSettingsViewModel) -> Binding<TimeInterval?> {
        Binding(
            get: { vm.state.staleMaxAge },
            set: { newValue in vm.setStaleMaxAge(newValue) }
        )
    }
}

/// Сегментированная полоса хранилища устройства: занятое другим (серое) + кеш
/// (акцент) + свободное. Кеш всегда виден минимум на 3pt, если он не пуст.
private struct StorageBar: View {
    let other: Int64
    let cache: Int64
    let total: Int64

    var body: some View {
        GeometryReader { geo in
            let width = geo.size.width
            let otherW = fraction(other) * width
            let cacheW = cache > 0 ? max(3, fraction(cache) * width) : 0
            HStack(spacing: 0) {
                Rectangle().fill(AppColors.onSurfaceVariant.opacity(0.45)).frame(width: otherW)
                Rectangle().fill(AppColors.accent).frame(width: cacheW)
                Rectangle().fill(AppColors.onSurface.opacity(0.10))
            }
        }
        .frame(height: 12)
        .clipShape(Capsule())
    }

    private func fraction(_ value: Int64) -> CGFloat {
        total > 0 ? min(1, CGFloat(value) / CGFloat(total)) : 0
    }
}
