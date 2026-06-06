import SwiftUI
import UIKit
import BarkCloudKit

/// Карточки файлов из таба «Мне доступны» в `SharedHubScreen`. Превью — через
/// `MediaThumb` (с дисковым кешем по `fileId`), под именем владельца — дата;
/// кнопка «Скачать» → `UIDocumentPickerViewController(forExporting:)` через
/// `pendingExportFile`.
struct SharedWithMeListView: View {
    @Bindable var vm: SharedWithMeViewModel

    var body: some View {
        Group {
            if vm.state.isPlaceholder {
                ProgressView().frame(maxWidth: .infinity, maxHeight: .infinity)
            } else if vm.state.isEmpty {
                ScrollView { emptyState.containerRelativeFrame(.vertical) }
                    .barkRefreshable { await vm.reload() }
            } else {
                list
            }
        }
        .overlay(alignment: .bottom) { snackbar }
        .sheet(item: Binding(
            get: { vm.state.pendingExportFile.map(ExportFile.init) },
            set: { _ in vm.exportShown() }
        )) { file in
            DocumentExporter(url: file.url)
                .ignoresSafeArea()
        }
    }

    private var list: some View {
        List {
            if !vm.state.folders.isEmpty {
                Section(String(localized: "shared_folders_with_me")) {
                    ForEach(vm.state.folders) { folder in
                        NavigationLink {
                            SharedFolderBrowserScreen(directoryID: folder.directoryID, title: folder.name)
                        } label: {
                            SharedFolderRow(folder: folder, owner: vm.state.owners[folder.ownerUserID])
                        }
                    }
                }
            }
            ForEach(vm.state.items) { entry in
                SharedRow(
                    entry: entry,
                    owner: vm.state.owners[entry.ownerUserID],
                    isDownloading: vm.state.downloading.contains(entry.file.id),
                    onDownload: { Task { await vm.download(entry) } }
                )
                .onAppear { Task { await vm.loadMoreIfNeeded(current: entry) } }
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
            Image(systemName: "tray.and.arrow.down")
                .font(.system(size: 48))
                .foregroundStyle(AppColors.onSurfaceVariant)
            Text(String(localized: "shared_with_me_empty_title_real"))
                .font(AppTypography.titleMedium)
                .foregroundStyle(AppColors.onSurface)
                .multilineTextAlignment(.center)
            Text(String(localized: "shared_with_me_empty_hint"))
                .font(AppTypography.bodySmall)
                .foregroundStyle(AppColors.onSurfaceVariant)
                .multilineTextAlignment(.center)
                .padding(.horizontal, 32)
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .padding(.vertical, 60)
    }
}

private struct SharedRow: View {
    let entry: SharedFileEntry
    let owner: CloudUser?
    let isDownloading: Bool
    let onDownload: () -> Void

    var body: some View {
        HStack(alignment: .top, spacing: 12) {
            preview
            VStack(alignment: .leading, spacing: 4) {
                Text(verbatim: entry.file.fileName.isEmpty ? String(localized: "shared_unnamed") : entry.file.fileName)
                    .font(AppTypography.bodyMedium)
                    .lineLimit(1)
                    .truncationMode(.middle)
                    .foregroundStyle(AppColors.onSurface)
                HStack(spacing: 6) {
                    Image(systemName: "person.fill")
                        .font(.system(size: 11))
                        .foregroundStyle(AppColors.onSurfaceVariant)
                    Text(verbatim: ownerName)
                        .font(.system(size: 12))
                        .foregroundStyle(AppColors.onSurfaceVariant)
                        .lineLimit(1)
                }
                Text(entry.sharedAt, format: .dateTime.day().month().year())
                    .font(.system(size: 12))
                    .foregroundStyle(AppColors.onSurfaceVariant)
            }
            Spacer(minLength: 8)
            actionButton
        }
        .padding(.vertical, 4)
    }

    private var ownerName: String {
        owner?.displayName ?? String(format: String(localized: "shared_owner_fallback"), String(entry.ownerUserID))
    }

    @ViewBuilder
    private var preview: some View {
        let mediaPreview = entry.file.preview(preferredWidth: 128)
        ZStack {
            RoundedRectangle(cornerRadius: 8)
                .fill(AppColors.onSurface.opacity(0.06))
                .frame(width: 48, height: 48)
            if let mediaPreview {
                RemoteImage(
                    fileId: entry.file.id,
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
        Image(systemName: entry.file.isVideo ? "video.fill" : "doc.fill")
            .font(.system(size: 18))
            .foregroundStyle(AppColors.onSurfaceVariant)
    }

    @ViewBuilder
    private var actionButton: some View {
        if isDownloading {
            ProgressView()
                .frame(width: 36, height: 36)
        } else {
            Button(action: onDownload) {
                Image(systemName: "arrow.down.circle.fill")
                    .font(.system(size: 28))
                    .foregroundStyle(AppColors.accent)
            }
            .buttonStyle(.plain)
            .accessibilityLabel(String(localized: "shared_download"))
        }
    }
}

/// Строка доступной мне папки: иконка папки + имя + «от кого». Тап ведёт в
/// `SharedFolderBrowserScreen` (навигация по поддереву).
private struct SharedFolderRow: View {
    let folder: SharedFolderItem
    let owner: CloudUser?

    var body: some View {
        HStack(spacing: 12) {
            ZStack {
                RoundedRectangle(cornerRadius: 8)
                    .fill(AppColors.accent.opacity(0.12))
                    .frame(width: 48, height: 48)
                Image(systemName: "folder.fill")
                    .font(.system(size: 20))
                    .foregroundStyle(AppColors.accent)
            }
            VStack(alignment: .leading, spacing: 4) {
                Text(verbatim: folder.name.isEmpty ? String(localized: "shared_unnamed") : folder.name)
                    .font(AppTypography.bodyMedium)
                    .lineLimit(1)
                    .truncationMode(.middle)
                    .foregroundStyle(AppColors.onSurface)
                HStack(spacing: 6) {
                    Image(systemName: "person.fill")
                        .font(.system(size: 11))
                        .foregroundStyle(AppColors.onSurfaceVariant)
                    Text(verbatim: ownerName)
                        .font(.system(size: 12))
                        .foregroundStyle(AppColors.onSurfaceVariant)
                        .lineLimit(1)
                }
            }
            Spacer(minLength: 8)
        }
        .padding(.vertical, 4)
    }

    private var ownerName: String {
        owner?.displayName ?? String(format: String(localized: "shared_owner_fallback"), String(folder.ownerUserID))
    }
}

/// Адаптер `UIDocumentPickerViewController` для экспорта одного файла. После
/// закрытия пикером оригинал из `temporaryDirectory` система удаляет сама.
private struct DocumentExporter: UIViewControllerRepresentable {
    let url: URL

    func makeUIViewController(context: Context) -> UIDocumentPickerViewController {
        let picker = UIDocumentPickerViewController(forExporting: [url], asCopy: true)
        picker.shouldShowFileExtensions = true
        return picker
    }

    func updateUIViewController(_ uiViewController: UIDocumentPickerViewController, context: Context) {}
}

/// Identifiable-обёртка для `.sheet(item:)` (URL сам не Identifiable).
private struct ExportFile: Identifiable, Hashable {
    let url: URL
    var id: String { url.path }
}
