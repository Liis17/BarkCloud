import SwiftUI
import BarkCloudKit

/// Квадратная карточка умной папки (зеркало `AlbumCardView`): обложка-превью
/// первого файла либо акцентная плитка с SF-иконкой по `iconKey`, затем имя и
/// счётчик файлов.
struct SmartFolderCardView: View {
    let folder: DynamicFolderCard

    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            SquareThumbClip(cornerRadius: 10) {
                if let url = folder.coverPreviewURL {
                    RemoteImage(fileId: folder.id, variant: .previewCover, url: url, contentMode: .fill) {
                        tile
                    }
                } else {
                    tile
                }
            }
            Text(verbatim: folder.name)
                .font(AppTypography.titleMedium)
                .lineLimit(1)
            Text(verbatim: FormatUtils.formatChildCount(folder.itemsCount))
                .font(AppTypography.bodySmall)
                .foregroundStyle(AppColors.onSurfaceVariant)
        }
    }

    private var tile: some View {
        AppColors.accent.opacity(0.12)
            .overlay {
                Image(systemName: Self.icon(for: folder.iconKey))
                    .font(.system(size: 34))
                    .foregroundStyle(AppColors.accent)
            }
    }

    /// Соответствие `iconKey` бэкенда → SF Symbol (зеркало веб-ICONS).
    static func icon(for key: String) -> String {
        switch key {
        case "clock": return "clock"
        case "hdd": return "externaldrive"
        case "camera": return "photo"
        case "screenshot": return "camera.viewfinder"
        default: return "folder"
        }
    }
}
