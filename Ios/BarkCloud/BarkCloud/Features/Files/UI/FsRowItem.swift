import SwiftUI

struct FsRowItem: View {
    let entry: FsEntry
    let selected: Bool
    let selectionActive: Bool
    let onTap: () -> Void
    let onLongPress: () -> Void
    let onAction: (RowAction) -> Void

    @State private var thumbnail: UIImage?

    enum RowAction {
        case open, share, rename, delete, copy, move
    }

    var body: some View {
        HStack(spacing: 12) {
            leading
            VStack(alignment: .leading, spacing: 2) {
                Text(entry.name).font(AppTypography.bodyLarge).lineLimit(1)
                Text(supportingText).font(AppTypography.bodySmall).foregroundStyle(AppColors.onSurfaceVariant).lineLimit(1)
            }
            Spacer()
            if !selectionActive {
                Menu {
                    if case .file = entry { Button { onAction(.open) } label: { Label("files_action_open", systemImage: "arrow.up.right.square") } }
                    Button { onAction(.share) } label: { Label("files_action_share", systemImage: "square.and.arrow.up") }
                    Button { onAction(.rename) } label: { Label("files_action_rename", systemImage: "pencil") }
                    Button { onAction(.copy) } label: { Label("files_action_copy", systemImage: "doc.on.doc") }
                    Button { onAction(.move) } label: { Label("files_action_move", systemImage: "folder") }
                    Button(role: .destructive) { onAction(.delete) } label: { Label("files_action_delete", systemImage: "trash") }
                } label: {
                    Image(systemName: "ellipsis.circle")
                        .foregroundStyle(AppColors.onSurfaceVariant)
                }
            }
        }
        .contentShape(Rectangle())
        .onTapGesture { onTap() }
        .onLongPressGesture { onLongPress() }
        .padding(.vertical, 6)
    }

    @ViewBuilder
    private var leading: some View {
        ZStack {
            RoundedRectangle(cornerRadius: 10)
                .fill(AppColors.accent.opacity(selected ? 0.25 : 0.1))
                .frame(width: 44, height: 44)
            if selected {
                Image(systemName: "checkmark.circle.fill")
                    .font(.system(size: 22))
                    .foregroundStyle(AppColors.accent)
            } else if let thumbnail {
                Image(uiImage: thumbnail)
                    .resizable()
                    .aspectRatio(contentMode: .fill)
                    .frame(width: 44, height: 44)
                    .clipShape(RoundedRectangle(cornerRadius: 10))
            } else {
                Image(systemName: iconSymbol)
                    .font(.system(size: 20))
                    .foregroundStyle(AppColors.accent.opacity(0.8))
            }
        }
        .task(id: entry.path) {
            if case .file(let f) = entry, ThumbnailLoader.canRender(forFileName: f.name) {
                thumbnail = await ThumbnailLoader.thumbnail(for: f.path, lastModified: f.lastModified)
            } else {
                thumbnail = nil
            }
        }
    }

    private var iconSymbol: String {
        if selected { return "checkmark.circle.fill" }
        if case .directory = entry { return MimeIcon.folderSymbol }
        return MimeIcon.iconSymbol(forFileName: entry.name)
    }

    private var supportingText: String {
        switch entry {
        case .directory(let d):
            return "\(FormatUtils.formatChildCount(d.childCount)) · \(FormatUtils.formatDate(d.lastModified))"
        case .file(let f):
            return "\(FormatUtils.formatSize(f.sizeBytes)) · \(FormatUtils.formatDate(f.lastModified))"
        }
    }
}
