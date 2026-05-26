import SwiftUI

/// Универсальный экран-заглушка «функция в разработке». Используется там, где
/// UI-пункт уже есть, а серверной поддержки ещё нет (например, «Общие файлы»).
struct ComingSoonScreen: View {
    let titleKey: LocalizedStringResource
    let iconName: String

    var body: some View {
        VStack(spacing: 16) {
            Image(systemName: iconName)
                .font(.system(size: 64))
                .foregroundStyle(AppColors.onSurfaceVariant)
            Text("coming_soon")
                .font(AppTypography.titleMedium)
                .foregroundStyle(AppColors.onSurfaceVariant)
                .multilineTextAlignment(.center)
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .padding()
        .navigationTitle(String(localized: titleKey))
        .navigationBarTitleDisplayMode(.inline)
    }
}
