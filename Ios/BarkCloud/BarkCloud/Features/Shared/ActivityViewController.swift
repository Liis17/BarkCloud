import SwiftUI
import UIKit

/// Системный Share Sheet (`UIActivityViewController`) как SwiftUI-обёртка.
/// На iPhone презентуется как sheet с двумя detents; на iPad — Apple сам
/// разворачивает popover, если `sourceView` не указан.
///
/// Используется во всех точках «Создать публичную ссылку» (Gallery, MediaGrid,
/// AlbumDetail, CloudBrowser) и в MySharesListView для «Скопировать ссылку».
struct ActivityViewController: UIViewControllerRepresentable {
    let activityItems: [Any]

    func makeUIViewController(context: Context) -> UIActivityViewController {
        UIActivityViewController(activityItems: activityItems, applicationActivities: nil)
    }

    func updateUIViewController(_ uiViewController: UIActivityViewController, context: Context) {}
}

/// Identifiable-обёртка над URL, чтобы открывать `ActivityViewController` через
/// `.sheet(item:)`. URL сам не Identifiable, а одинаковая ссылка — это та же
/// «сущность», поэтому `id = absoluteString`.
struct ShareableURL: Identifiable, Hashable {
    let url: URL
    var id: String { url.absoluteString }
}
