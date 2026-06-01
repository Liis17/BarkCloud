import SwiftUI
import UIKit
import Photos

/// Что показывать в пикере: только фото, только видео, или и то и другое.
enum DeviceAssetPickerFilter {
    case photo, video, any

    fileprivate var predicate: NSPredicate {
        switch self {
        case .photo:
            return NSPredicate(format: "mediaType == %d", PHAssetMediaType.image.rawValue)
        case .video:
            return NSPredicate(format: "mediaType == %d", PHAssetMediaType.video.rawValue)
        case .any:
            return NSPredicate(
                format: "mediaType == %d OR mediaType == %d",
                PHAssetMediaType.image.rawValue, PHAssetMediaType.video.rawValue
            )
        }
    }
}

/// Состояние кастомного пикера ассетов устройства. Доступ/загрузка медиатеки как у
/// [[GalleryViewModel]]; индикация «уже в облаке» — общий [[CloudPresenceTracker]].
@MainActor
@Observable
final class DeviceAssetPickerViewModel {
    enum Access: Equatable { case undetermined, authorized, limited, denied }

    var access: Access = .undetermined
    var assets: [PHAsset] = []
    var selection: Set<String> = []   // localIdentifier выбранных

    /// Трекер наличия в облаке (бейдж «уже загружено»).
    let presence: CloudPresenceTracker
    /// Предупреждать о дубликатах: при подтверждении показать модалку, если среди
    /// выбранных есть уже загруженные (галерея — да; альбом — нет, дубль осмыслен).
    let blockAlreadyUploaded: Bool

    private let filter: DeviceAssetPickerFilter
    private var didLoad = false

    init(filter: DeviceAssetPickerFilter, blockAlreadyUploaded: Bool, cloud: CloudRepository) {
        self.filter = filter
        self.blockAlreadyUploaded = blockAlreadyUploaded
        self.presence = CloudPresenceTracker(cloud: cloud)
    }

    var hasSelection: Bool { !selection.isEmpty }

    func isSelected(_ asset: PHAsset) -> Bool { selection.contains(asset.localIdentifier) }

    func toggle(_ asset: PHAsset) {
        let id = asset.localIdentifier
        if selection.contains(id) { selection.remove(id) } else { selection.insert(id) }
    }

    /// Выбранные ассеты в порядке сетки.
    func selectedAssets() -> [PHAsset] {
        assets.filter { selection.contains($0.localIdentifier) }
    }

    /// Выбранные ассеты, которые уже есть в облаке (для предупреждения о дубликатах).
    func selectedDuplicates() -> [PHAsset] {
        selectedAssets().filter { presence.isInCloud($0.localIdentifier) }
    }

    func loadIfNeeded() async {
        guard !didLoad else { return }
        didLoad = true
        let status = await PHPhotoLibrary.requestAuthorization(for: .readWrite)
        apply(status)
        if access == .authorized || access == .limited { loadAssets() }
    }

    private func apply(_ status: PHAuthorizationStatus) {
        switch status {
        case .authorized: access = .authorized
        case .limited: access = .limited
        case .denied, .restricted: access = .denied
        case .notDetermined: access = .undetermined
        @unknown default: access = .denied
        }
    }

    private func loadAssets() {
        let options = PHFetchOptions()
        options.sortDescriptors = [NSSortDescriptor(key: "creationDate", ascending: false)]
        options.predicate = filter.predicate
        let result = PHAsset.fetchAssets(with: options)
        var list: [PHAsset] = []
        list.reserveCapacity(result.count)
        result.enumerateObjects { asset, _, _ in list.append(asset) }
        assets = list
    }
}

/// Кастомный пикер ассетов устройства для загрузки в облако — замена системного
/// `PhotosPicker`. Медиатека сеткой 3× (как вкладка «Галерея») с бейджами «уже в
/// облаке»; мультивыбор; уже загруженные могут блокироваться от повторного выбора.
/// По подтверждению вызывает `onConfirm` с выбранными `PHAsset` и закрывается.
struct DeviceAssetPickerScreen: View {
    let filter: DeviceAssetPickerFilter
    /// Подпись кнопки подтверждения («Загрузить» / «Добавить»).
    let confirmTitle: String
    /// `true` — для вкладок Фото/Видео (предупреждать перед повторной загрузкой дубликата);
    /// `false` — для альбома (дубль в альбом осмыслен, без предупреждения).
    var blockAlreadyUploaded: Bool = true
    let onConfirm: ([PHAsset]) -> Void

    @Environment(AppEnvironment.self) private var env
    @Environment(\.dismiss) private var dismiss
    @Environment(\.openURL) private var openURL

    @State private var vm: DeviceAssetPickerViewModel?
    @State private var pendingDuplicates: [PHAsset]?

    private static let spacing: CGFloat = 2
    private let columns = Array(repeating: GridItem(.flexible(), spacing: spacing), count: 3)

    var body: some View {
        NavigationStack {
            Group {
                if let vm {
                    content(vm)
                } else {
                    ProgressView().frame(maxWidth: .infinity, maxHeight: .infinity)
                }
            }
            .navigationTitle(String(localized: "picker_select_title"))
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .topBarLeading) {
                    Button(String(localized: "action_cancel")) { dismiss() }
                }
            }
            .task {
                if vm == nil {
                    vm = DeviceAssetPickerViewModel(
                        filter: filter,
                        blockAlreadyUploaded: blockAlreadyUploaded,
                        cloud: env.cloudRepository
                    )
                }
                await vm?.loadIfNeeded()
            }
        }
    }

    @ViewBuilder
    private func content(_ vm: DeviceAssetPickerViewModel) -> some View {
        switch vm.access {
        case .undetermined:
            ProgressView().frame(maxWidth: .infinity, maxHeight: .infinity)
        case .denied:
            deniedState
        case .authorized, .limited:
            if vm.assets.isEmpty {
                emptyState
            } else {
                grid(vm)
            }
        }
    }

    private func grid(_ vm: DeviceAssetPickerViewModel) -> some View {
        ScrollView {
            LazyVGrid(columns: columns, spacing: Self.spacing) {
                ForEach(vm.assets, id: \.localIdentifier) { asset in
                    let inCloud = vm.presence.isInCloud(asset.localIdentifier)
                    DeviceMediaThumb(
                        asset: asset,
                        isSelecting: true,
                        isSelected: vm.isSelected(asset),
                        isInCloud: inCloud
                    )
                    .onTapGesture { vm.toggle(asset) }
                    .onAppear { vm.presence.observe(asset) }
                }
            }
        }
        .safeAreaInset(edge: .bottom) { confirmBar(vm) }
        .confirmationDialog(
            "Некоторые файлы уже загружены",
            isPresented: Binding(
                get: { pendingDuplicates != nil },
                set: { if !$0 { pendingDuplicates = nil } }
            ),
            titleVisibility: .visible
        ) {
            Button("Загрузить всё") {
                if let selected = pendingDuplicates {
                    onConfirm(selected)
                    dismiss()
                }
                pendingDuplicates = nil
            }
            Button("Только новые") {
                if let selected = pendingDuplicates {
                    let fresh = selected.filter { !vm.presence.isInCloud($0.localIdentifier) }
                    onConfirm(fresh)
                    dismiss()
                }
                pendingDuplicates = nil
            }
            Button("Отмена", role: .cancel) { pendingDuplicates = nil }
        } message: {
            Text("Часть выбранных файлов уже есть в облаке. Загрузить их ещё раз?")
        }
    }

    @ViewBuilder
    private func confirmBar(_ vm: DeviceAssetPickerViewModel) -> some View {
        if vm.hasSelection {
            Button {
                let selected = vm.selectedAssets()
                if vm.blockAlreadyUploaded && !vm.selectedDuplicates().isEmpty {
                    pendingDuplicates = selected
                } else {
                    onConfirm(selected)
                    dismiss()
                }
            } label: {
                Label("\(confirmTitle) (\(vm.selection.count))", systemImage: "icloud.and.arrow.up")
                    .font(AppTypography.titleMedium)
                    .frame(maxWidth: .infinity)
                    .padding(.vertical, 14)
                    .background(AppColors.accent)
                    .foregroundStyle(.white)
                    .clipShape(RoundedRectangle(cornerRadius: 12))
            }
            .padding(.horizontal, 16)
            .padding(.bottom, 8)
            .background(.regularMaterial)
        }
    }

    private var emptyState: some View {
        VStack(spacing: 16) {
            Image(systemName: "photo.on.rectangle")
                .font(.system(size: 64))
                .foregroundStyle(AppColors.onSurfaceVariant)
            Text("gallery_empty")
                .font(AppTypography.titleMedium)
                .foregroundStyle(AppColors.onSurfaceVariant)
                .multilineTextAlignment(.center)
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .padding()
    }

    private var deniedState: some View {
        VStack(spacing: 16) {
            Image(systemName: "lock.shield")
                .font(.system(size: 64))
                .foregroundStyle(AppColors.onSurfaceVariant)
            Text("gallery_access_denied")
                .font(AppTypography.titleMedium)
                .foregroundStyle(AppColors.onSurface)
                .multilineTextAlignment(.center)
            Button(String(localized: "gallery_open_settings")) {
                if let url = URL(string: UIApplication.openSettingsURLString) {
                    openURL(url)
                }
            }
            .buttonStyle(.borderedProminent)
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .padding()
    }
}
