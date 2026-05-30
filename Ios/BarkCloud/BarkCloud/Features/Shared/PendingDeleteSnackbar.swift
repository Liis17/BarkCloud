import SwiftUI

/// Snackbar внизу экрана для отложенного удаления: имя файла, обратный отсчёт
/// и кнопка «Отменить». Сам появляется/исчезает с пружиной, ничего не делает
/// — отсчёт ведёт `PendingDelete`.
struct PendingDeleteSnackbar: View {
    let store: PendingDelete

    var body: some View {
        Group {
            if let pending = store.pending {
                HStack(spacing: 14) {
                    Image(systemName: "trash.fill")
                        .font(.system(size: 16, weight: .semibold))
                        .foregroundStyle(.orange)
                        .frame(width: 36, height: 36)
                        .background(Color.orange.opacity(0.18), in: Circle())

                    VStack(alignment: .leading, spacing: 2) {
                        Text(verbatim: pending.label)
                            .font(AppTypography.bodyMedium)
                            .foregroundStyle(AppColors.onSurface)
                            .lineLimit(1)
                        Text(verbatim: String(
                            format: NSLocalizedString("pending_delete_countdown", comment: ""),
                            store.remainingSeconds
                        ))
                        .font(AppTypography.bodySmall)
                        .foregroundStyle(AppColors.onSurfaceVariant)
                    }
                    Spacer(minLength: 8)

                    Button {
                        store.cancel()
                    } label: {
                        Text("action_undo")
                            .font(AppTypography.titleSmall)
                            .foregroundStyle(AppColors.accent)
                            .padding(.horizontal, 14)
                            .padding(.vertical, 8)
                            .background(AppColors.accent.opacity(0.14), in: Capsule())
                    }
                    .buttonStyle(.plain)
                }
                .padding(.horizontal, 14)
                .padding(.vertical, 10)
                .background(.regularMaterial, in: RoundedRectangle(cornerRadius: 16))
                .overlay(
                    RoundedRectangle(cornerRadius: 16)
                        .stroke(AppColors.onSurface.opacity(0.06), lineWidth: 1)
                )
                .padding(.horizontal, 12)
                .padding(.bottom, 12)
                .transition(.move(edge: .bottom).combined(with: .opacity))
                .id(pending.id)
            }
        }
        .animation(.spring(response: 0.35, dampingFraction: 0.85), value: store.pending?.id)
    }
}
