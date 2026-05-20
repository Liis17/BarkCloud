import SwiftUI

struct PlaceholderScreen: View {
    let destination: MainDestination

    var body: some View {
        VStack(spacing: 16) {
            Image(systemName: destination.iconOutlined)
                .font(.system(size: 64))
                .foregroundStyle(AppColors.onSurfaceVariant)
            Text(destination.placeholderKey)
                .font(AppTypography.titleMedium)
                .foregroundStyle(AppColors.onSurfaceVariant)
                .multilineTextAlignment(.center)
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .padding()
    }
}
