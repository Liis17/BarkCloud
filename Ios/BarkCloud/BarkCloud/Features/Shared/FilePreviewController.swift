import SwiftUI
import QuickLook

/// Обёртка `QLPreviewController` для предпросмотра локального файла —
/// единообразно для фото / видео / PDF / документов.
struct FilePreviewController: UIViewControllerRepresentable {
    let fileURL: URL

    func makeCoordinator() -> Coordinator { Coordinator(fileURL: fileURL) }

    func makeUIViewController(context: Context) -> QLPreviewController {
        let controller = QLPreviewController()
        controller.dataSource = context.coordinator
        return controller
    }

    func updateUIViewController(_ controller: QLPreviewController, context: Context) {
        context.coordinator.fileURL = fileURL
        controller.reloadData()
    }

    final class Coordinator: NSObject, QLPreviewControllerDataSource {
        var fileURL: URL
        init(fileURL: URL) { self.fileURL = fileURL }

        func numberOfPreviewItems(in controller: QLPreviewController) -> Int { 1 }
        func previewController(_ controller: QLPreviewController, previewItemAt index: Int) -> QLPreviewItem {
            fileURL as NSURL
        }
    }
}

/// Экран предпросмотра удалённого файла: тянет временную ссылку на оригинал
/// (`GetTempDownloadUrl`), скачивает его и показывает в QuickLook.
struct RemoteFilePreviewScreen: View {
    let fileID: String
    let fileName: String
    let transfer: FileTransferService

    @State private var localURL: URL?
    @State private var failed = false

    var body: some View {
        Group {
            if let localURL {
                FilePreviewController(fileURL: localURL)
                    .ignoresSafeArea()
            } else if failed {
                ContentUnavailableView(String(localized: "preview_failed"), systemImage: "exclamationmark.triangle")
            } else {
                ProgressView()
            }
        }
        .task { await loadOriginal() }
    }

    private func loadOriginal() async {
        do {
            let urls = try await transfer.tempDownloadURLs(fileIDs: [fileID])
            guard let remote = urls[fileID] else { failed = true; return }
            localURL = try await transfer.download(from: remote, suggestedName: fileName)
        } catch {
            failed = true
        }
    }
}
