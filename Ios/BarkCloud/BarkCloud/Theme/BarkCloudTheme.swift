import SwiftUI

struct BarkCloudTheme: ViewModifier {
    func body(content: Content) -> some View {
        content
            .tint(AppColors.accent)
    }
}
