import SwiftUI
import BarkCloudKit

/// Навигация по доступной мне папке (грант от другого пользователя): подпапки +
/// файлы. Подпапки — рекурсивная навигация этим же экраном; тап по файлу качает
/// его по временной публичной ссылке и открывает в QuickLook. Аналог веб-страницы
/// публичной папки, но для приватного гранта (`ListSharedDirectory`).
struct SharedFolderBrowserScreen: View {
    let directoryID: String
    let title: String

    @Environment(AppEnvironment.self) private var env
    @State private var listing: SharedDirectoryListing?
    @State private var isLoading = true
    @State private var loadFailed = false
    @State private var downloadingID: String?
    @State private var previewFile: PreviewFile?
    @State private var snackbar: String?

    var body: some View {
        Group {
            if isLoading {
                ProgressView().frame(maxWidth: .infinity, maxHeight: .infinity)
            } else if loadFailed {
                ContentUnavailableView(
                    String(localized: "shared_folder_unavailable"),
                    systemImage: "folder.badge.questionmark"
                )
            } else if let listing, listing.subdirs.isEmpty, listing.files.isEmpty {
                ScrollView {
                    VStack(spacing: 16) {
                        Image(systemName: "folder")
                            .font(.system(size: 56))
                            .foregroundStyle(AppColors.onSurfaceVariant)
                        Text("cloud_empty_folder")
                            .font(AppTypography.titleMedium)
                            .foregroundStyle(AppColors.onSurfaceVariant)
                    }
                    .frame(maxWidth: .infinity)
                    .containerRelativeFrame(.vertical)
                }
                .barkRefreshable { await load() }
            } else if let listing {
                content(listing)
            }
        }
        .navigationTitle(title)
        .navigationBarTitleDisplayMode(.inline)
        .overlay(alignment: .bottom) { snackbarView }
        .fullScreenCover(item: $previewFile) { file in
            NavigationStack {
                FilePreviewController(fileURL: file.url)
                    .ignoresSafeArea()
                    .toolbar {
                        ToolbarItem(placement: .topBarLeading) {
                            Button(String(localized: "action_close")) { previewFile = nil }
                        }
                    }
            }
        }
        .task { await load() }
    }

    private func content(_ listing: SharedDirectoryListing) -> some View {
        List {
            ForEach(listing.subdirs) { sub in
                NavigationLink {
                    SharedFolderBrowserScreen(directoryID: sub.id, title: sub.name)
                } label: {
                    HStack(spacing: 14) {
                        Image(systemName: "folder.fill")
                            .font(.system(size: 22))
                            .foregroundStyle(AppColors.accent)
                            .frame(width: 36)
                        Text(verbatim: sub.name)
                            .font(AppTypography.bodyLarge)
                        Spacer()
                    }
                }
            }
            ForEach(listing.files) { file in
                fileRow(file)
                    .contentShape(Rectangle())
                    .onTapGesture { Task { await openFile(file) } }
            }
        }
        .listStyle(.plain)
        .barkRefreshable { await load() }
    }

    private func fileRow(_ file: SharedDirFile) -> some View {
        HStack(spacing: 14) {
            ZStack {
                RoundedRectangle(cornerRadius: 8)
                    .fill(AppColors.onSurface.opacity(0.06))
                    .frame(width: 40, height: 40)
                if let preview = file.previewURL {
                    RemoteImage(url: preview, contentMode: .fill) { fileIcon(file) }
                        .frame(width: 40, height: 40)
                        .clipShape(RoundedRectangle(cornerRadius: 8))
                } else {
                    fileIcon(file)
                }
            }
            VStack(alignment: .leading, spacing: 2) {
                Text(verbatim: file.name.isEmpty ? String(localized: "shared_unnamed") : file.name)
                    .font(AppTypography.bodyLarge)
                    .lineLimit(1)
                    .truncationMode(.middle)
                if file.fileSize > 0 {
                    Text(verbatim: ByteCountFormatter.string(fromByteCount: file.fileSize, countStyle: .file))
                        .font(.system(size: 12))
                        .foregroundStyle(AppColors.onSurfaceVariant)
                }
            }
            Spacer(minLength: 8)
            if downloadingID == file.id {
                ProgressView()
            }
        }
        .padding(.vertical, 2)
    }

    private func fileIcon(_ file: SharedDirFile) -> some View {
        Image(systemName: file.isVideo ? "video.fill" : "doc.fill")
            .font(.system(size: 16))
            .foregroundStyle(AppColors.onSurfaceVariant)
    }

    @ViewBuilder
    private var snackbarView: some View {
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
            let result = try await env.cloudRepository.listSharedDirectory(directoryID: directoryID)
            listing = result
            loadFailed = !result.found
        } catch {
            loadFailed = true
        }
    }

    /// Скачать файл по готовой временной ссылке во временный файл с верным именем
    /// → открыть в QuickLook (там же доступен системный «Поделиться»).
    private func openFile(_ file: SharedDirFile) async {
        guard downloadingID == nil, let url = file.downloadURL else { return }
        downloadingID = file.id
        defer { downloadingID = nil }
        do {
            let (tmp, _) = try await URLSession.shared.download(from: url)
            let destDir = FileManager.default.temporaryDirectory
                .appendingPathComponent("shared-\(UUID().uuidString)")
            try FileManager.default.createDirectory(at: destDir, withIntermediateDirectories: true)
            let dest = destDir.appendingPathComponent(file.name.isEmpty ? "file" : file.name)
            try? FileManager.default.removeItem(at: dest)
            try FileManager.default.moveItem(at: tmp, to: dest)
            previewFile = PreviewFile(url: dest)
        } catch {
            snackbar = String(localized: "shared_download_failed")
        }
    }

    /// Identifiable-обёртка локального URL для `.fullScreenCover(item:)`.
    private struct PreviewFile: Identifiable, Hashable {
        let url: URL
        var id: String { url.path }
    }
}
