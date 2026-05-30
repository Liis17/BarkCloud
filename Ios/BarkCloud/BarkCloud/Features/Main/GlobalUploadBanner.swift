import SwiftUI

/// Плавающая плашка над TabBar, показывающая прогресс фоновой загрузки. Видна
/// на любой вкладке, пока есть активные UploadJob (наблюдатель —
/// [[UploadProgressObserver]]). Скрывается через секунду после завершения.
///
/// Логика прогресса нарочно повторяет UI Live Activity (общий стиль: оранжевая
/// иконка облака, имя файла, счётчик N/M, тонкий шиммер-бар), чтобы у юзера
/// был единый язык — что в Dynamic Island, что внутри app.
struct GlobalUploadBanner: View {
    let observer: UploadProgressObserver
    /// Тап по баннеру: открыть BackupSheet (если есть backup-job) или экран
    /// загрузок в Cloud Browser. Передаётся из MainScreen.
    let onTap: () -> Void

    @State private var shimmerPhase: CGFloat = 0

    var body: some View {
        Button(action: onTap) {
            content
        }
        .buttonStyle(.plain)
        .transition(.move(edge: .bottom).combined(with: .opacity))
    }

    private var content: some View {
        HStack(spacing: 12) {
            iconBadge
            VStack(alignment: .leading, spacing: 4) {
                HStack(spacing: 6) {
                    Text(titleText)
                        .font(.system(size: 14, weight: .semibold))
                        .foregroundStyle(AppColors.onSurface)
                    Spacer(minLength: 4)
                    Text("\(observer.completedFiles)/\(observer.totalFiles)")
                        .font(.system(size: 13, weight: .medium))
                        .monospacedDigit()
                        .foregroundStyle(AppColors.onSurfaceVariant)
                }
                if !observer.currentFileName.isEmpty {
                    Text(verbatim: observer.currentFileName)
                        .font(.system(size: 12))
                        .lineLimit(1)
                        .truncationMode(.middle)
                        .foregroundStyle(AppColors.onSurfaceVariant)
                }
                progressBar
            }
        }
        .padding(.horizontal, 14)
        .padding(.vertical, 10)
        .background(
            RoundedRectangle(cornerRadius: 16, style: .continuous)
                .fill(.regularMaterial)
                .shadow(color: .black.opacity(0.18), radius: 10, x: 0, y: 4)
        )
    }

    private var iconBadge: some View {
        ZStack {
            Circle()
                .fill(AppColors.accent.opacity(0.18))
                .frame(width: 36, height: 36)
            Image(systemName: iconName)
                .font(.system(size: 16, weight: .semibold))
                .foregroundStyle(AppColors.accent)
                .symbolEffect(.pulse, options: .repeating, isActive: observer.isActive)
        }
    }

    private var iconName: String {
        switch observer.currentSource {
        case .backup: return "photo.on.rectangle.angled"
        case .share:  return "square.and.arrow.up.fill"
        case .manual: return "icloud.and.arrow.up.fill"
        }
    }

    private var titleText: String {
        if observer.failedFiles > 0 && observer.completedFiles + observer.failedFiles == observer.totalFiles {
            return String(localized: "upload_banner_failed")
        }
        if observer.completedFiles == observer.totalFiles && observer.totalFiles > 0 {
            return String(localized: "upload_banner_done")
        }
        switch observer.currentSource {
        case .backup: return String(localized: "upload_banner_backup")
        case .share:  return String(localized: "upload_banner_share")
        case .manual: return String(localized: "upload_banner_uploading")
        }
    }

    /// Тонкий прогресс-бар c шиммером поверх: добавляет ощущение «живой передачи»,
    /// важно при больших файлах когда секундный прогресс плохо заметен.
    private var progressBar: some View {
        GeometryReader { geo in
            let width = geo.size.width
            let progress = max(0.02, min(1, observer.overallProgress))
            ZStack(alignment: .leading) {
                Capsule()
                    .fill(AppColors.onSurface.opacity(0.08))
                Capsule()
                    .fill(
                        LinearGradient(
                            colors: [AppColors.accent.opacity(0.7), AppColors.accent],
                            startPoint: .leading,
                            endPoint: .trailing
                        )
                    )
                    .frame(width: width * progress)
                    .overlay(alignment: .leading) {
                        // Шиммер: тонкая полоса света, бегущая слева направо
                        // по уже-заполненной части бара.
                        Capsule()
                            .fill(
                                LinearGradient(
                                    colors: [
                                        .white.opacity(0),
                                        .white.opacity(0.45),
                                        .white.opacity(0)
                                    ],
                                    startPoint: .leading,
                                    endPoint: .trailing
                                )
                            )
                            .frame(width: 36)
                            .offset(x: shimmerPhase * (width * progress - 36))
                            .clipShape(Capsule())
                            .opacity(observer.isActive ? 1 : 0)
                    }
                    .clipShape(Capsule())
                    .animation(.easeOut(duration: 0.25), value: progress)
            }
        }
        .frame(height: 4)
        .onAppear {
            withAnimation(.linear(duration: 1.2).repeatForever(autoreverses: false)) {
                shimmerPhase = 1
            }
        }
    }
}
