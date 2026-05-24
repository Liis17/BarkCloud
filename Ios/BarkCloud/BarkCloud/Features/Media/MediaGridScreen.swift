import SwiftUI

/// Сетка медиа в 3 столбика с квадратными ячейками. Используется вкладками
/// Фото и Видео. Пока данные с сервера не реализованы — рисует скелетоны.
struct MediaGridScreen: View {
    let kind: MediaKind

    @State private var vm: MediaGridViewModel

    private static let columnCount = 3
    private static let spacing: CGFloat = 2

    private let columns = Array(
        repeating: GridItem(.flexible(), spacing: spacing),
        count: columnCount
    )

    init(kind: MediaKind) {
        self.kind = kind
        _vm = State(initialValue: MediaGridViewModel(kind: kind))
    }

    var body: some View {
        Group {
            if !vm.state.isPlaceholder && vm.state.items.isEmpty {
                emptyState
            } else {
                grid
            }
        }
        .navigationTitle(String(localized: kind.titleKey))
        .navigationBarTitleDisplayMode(.inline)
        .task { await vm.load() }
    }

    private var grid: some View {
        ScrollView {
            LazyVGrid(columns: columns, spacing: Self.spacing) {
                ForEach(vm.state.items) { item in
                    MediaCell(item: item)
                }
            }
            .redacted(reason: vm.state.isPlaceholder ? .placeholder : [])
        }
    }

    private var emptyState: some View {
        VStack(spacing: 16) {
            Image(systemName: kind.emptyIcon)
                .font(.system(size: 64))
                .foregroundStyle(AppColors.onSurfaceVariant)
            Text(kind.emptyKey)
                .font(AppTypography.titleMedium)
                .foregroundStyle(AppColors.onSurfaceVariant)
                .multilineTextAlignment(.center)
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .padding()
    }
}

/// Квадратная ячейка сетки: скелетон (серый прямоугольник) или превью.
private struct MediaCell: View {
    let item: MediaItem

    var body: some View {
        RoundedRectangle(cornerRadius: 4)
            .fill(AppColors.onSurface.opacity(0.08))
            .aspectRatio(1, contentMode: .fit)
            .overlay {
                if let url = item.thumbnailURL {
                    AsyncImage(url: url) { image in
                        image.resizable().aspectRatio(contentMode: .fill)
                    } placeholder: {
                        Color.clear
                    }
                    .clipShape(RoundedRectangle(cornerRadius: 4))
                }
            }
            .overlay(alignment: .bottomTrailing) {
                // Бейдж видео — только при реальных данных (под .placeholder скрыт).
                if item.isVideo && item.thumbnailURL != nil {
                    Image(systemName: "play.circle.fill")
                        .font(.system(size: 18))
                        .foregroundStyle(.white)
                        .shadow(radius: 2)
                        .padding(6)
                }
            }
            .clipped()
    }
}
