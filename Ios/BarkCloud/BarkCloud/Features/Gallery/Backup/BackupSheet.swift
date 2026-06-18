import SwiftUI
import UIKit
import Photos

private let backupStorageOtherColor = AppColors.onSurfaceVariant
private let backupStorageS3Color = Color(red: 0.604, green: 0.310, blue: 0.118)
private let backupStorageFreeColor = AppColors.onSurface.opacity(0.12)

/// Модалка резервного копирования: hero-донат с занятым объёмом, тогл автозагрузки
/// с прогрессом и превью очереди (как Google Photos) и filled-кнопка «Освободить
/// место» с анимацией благодарности. Плавающая карточка с отступами со всех сторон.
struct BackupSheet: View {
    @Environment(AppEnvironment.self) private var env
    let onClose: () -> Void

    private var manager: BackupManager { env.backupManager }

    var body: some View {
        ZStack {
            Color.black.opacity(0.4)
                .ignoresSafeArea()
                .onTapGesture { onClose() }

            card
                .padding(20)

            if let freed = manager.lastFreedBytes {
                SpaceFreedView(bytes: freed) { manager.dismissCelebration() }
            }
        }
        .task { await manager.onOpen() }
    }

    private var card: some View {
        VStack(spacing: 0) {
            header
            ScrollView {
                VStack(alignment: .leading, spacing: 16) {
                    heroStorage
                    autoUploadSection
                    freeSpaceSection
                }
                .padding(20)
            }
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .background(.regularMaterial)
        .clipShape(RoundedRectangle(cornerRadius: 24))
    }

    // MARK: - Шапка

    private var header: some View {
        HStack(spacing: 12) {
            ZStack {
                Circle()
                    .fill(AppColors.accent.opacity(0.16))
                    .frame(width: 36, height: 36)
                Image(systemName: "icloud.fill")
                    .font(.system(size: 18, weight: .semibold))
                    .foregroundStyle(AppColors.accent)
            }
            Text(String(localized: "backup_title"))
                .font(.system(size: 22, weight: .semibold))
                .foregroundStyle(AppColors.onSurface)
            Spacer()
            Button(action: onClose) {
                Image(systemName: "xmark")
                    .font(.system(size: 14, weight: .semibold))
                    .foregroundStyle(AppColors.onSurfaceVariant)
                    .frame(width: 32, height: 32)
                    .background(AppColors.onSurface.opacity(0.08), in: Circle())
            }
        }
        .padding(.horizontal, 20)
        .padding(.top, 20)
        .padding(.bottom, 4)
    }

    // MARK: - Hero: хранилище донатом

    private var heroStorage: some View {
        let total = manager.diskTotal > 0 ? manager.diskTotal : manager.storageLimit
        let other = manager.diskTotal > 0 ? max(0, manager.diskOther) : 0
        let s3 = manager.diskTotal > 0 ? max(0, manager.diskS3) : max(0, manager.usedStorage)
        let used = other + s3
        let free = max(0, total - used)
        let fraction = total > 0 ? min(1.0, Double(used) / Double(total)) : 0
        let percent = Int((fraction * 100).rounded())
        return VStack(alignment: .leading, spacing: 14) {
            HStack(alignment: .center, spacing: 18) {
                StorageDonut(fraction: fraction, percent: percent)
                    .frame(width: 104, height: 104)
                VStack(alignment: .leading, spacing: 4) {
                    Text("settings_storage")
                        .font(AppTypography.titleSmall)
                        .foregroundStyle(AppColors.onSurfaceVariant)
                        .textCase(.uppercase)
                    if total > 0 {
                        Text(verbatim: FormatUtils.formatSize(used))
                            .font(.system(size: 26, weight: .semibold))
                            .foregroundStyle(AppColors.onSurface)
                        Text(verbatim: "из \(FormatUtils.formatSize(total)) на диске")
                            .font(AppTypography.bodyMedium)
                            .foregroundStyle(AppColors.onSurfaceVariant)
                    } else {
                        Text(verbatim: "—")
                            .font(.system(size: 26, weight: .semibold))
                            .foregroundStyle(AppColors.onSurface)
                    }
                }
                Spacer(minLength: 0)
            }
            if total > 0 {
                StorageSegmentedBar(total: total, other: other, s3: s3)
                    .frame(height: 8)
                VStack(alignment: .leading, spacing: 4) {
                    storageLegendRow(color: backupStorageOtherColor, titleKey: "settings_storage_other", value: FormatUtils.formatSize(other))
                    storageLegendRow(color: backupStorageS3Color, titleKey: "settings_storage_cloud", value: FormatUtils.formatSize(s3))
                    storageLegendRow(color: backupStorageFreeColor, titleKey: "settings_storage_free", value: FormatUtils.formatSize(free))
                }
            }
        }
        .padding(18)
        .background(
            RoundedRectangle(cornerRadius: 20)
                .fill(AppColors.accent.opacity(0.10))
        )
    }

    @ViewBuilder
    private func storageLegendRow(color: Color, titleKey: LocalizedStringKey, value: String) -> some View {
        HStack(spacing: 8) {
            RoundedRectangle(cornerRadius: 2)
                .fill(color)
                .frame(width: 10, height: 10)
            Text(titleKey)
                .font(AppTypography.bodySmall)
                .foregroundStyle(AppColors.onSurfaceVariant)
            Spacer(minLength: 8)
            Text(verbatim: value)
                .font(AppTypography.bodySmall.weight(.semibold))
                .foregroundStyle(AppColors.onSurface)
                .lineLimit(1)
                .minimumScaleFactor(0.8)
        }
    }

    // MARK: - Автозагрузка

    private var autoUploadSection: some View {
        VStack(alignment: .leading, spacing: 14) {
            Toggle(isOn: Binding(
                get: { manager.autoUploadEnabled },
                set: { manager.setAutoUpload($0) }
            )) {
                HStack(spacing: 14) {
                    ZStack {
                        Circle()
                            .fill(AppColors.accent.opacity(0.18))
                            .frame(width: 42, height: 42)
                        Image(systemName: "icloud.and.arrow.up.fill")
                            .font(.system(size: 18, weight: .semibold))
                            .foregroundStyle(AppColors.accent)
                    }
                    VStack(alignment: .leading, spacing: 2) {
                        Text("backup_autoupload_title")
                            .font(AppTypography.titleMedium)
                            .foregroundStyle(AppColors.onSurface)
                        Text("backup_autoupload_subtitle")
                            .font(AppTypography.bodySmall)
                            .foregroundStyle(AppColors.onSurfaceVariant)
                    }
                }
            }
            .tint(AppColors.accent)

            if manager.autoUploadEnabled {
                uploadProgress
                    .padding(.top, 2)
            }
        }
        .padding(16)
        .background(
            RoundedRectangle(cornerRadius: 20)
                .fill(AppColors.onSurface.opacity(0.05))
        )
    }

    private var uploadProgress: some View {
        let done = manager.uploadDone
        let total = done + manager.uploadFailed + manager.remainingCount
        return VStack(alignment: .leading, spacing: 12) {
            if manager.isScanning {
                HStack(spacing: 8) {
                    ProgressView().controlSize(.small)
                    Text(verbatim: String(
                        format: NSLocalizedString("backup_scanning", comment: ""),
                        manager.scannedCount, manager.totalAssets
                    ))
                    .font(AppTypography.bodySmall)
                    .foregroundStyle(AppColors.onSurfaceVariant)
                }
            }

            if total > 0 {
                ProgressView(value: Double(done), total: Double(total))
                    .tint(AppColors.accent)
                    .scaleEffect(x: 1, y: 1.4, anchor: .center)
            }

            if manager.remainingCount > 0 {
                Text(verbatim: String(
                    format: NSLocalizedString("backup_uploading_progress", comment: ""),
                    done, manager.remainingCount
                ))
                .font(AppTypography.bodySmall)
                .foregroundStyle(AppColors.onSurfaceVariant)
            } else if !manager.isScanning {
                HStack(spacing: 6) {
                    Image(systemName: "checkmark.circle.fill")
                        .foregroundStyle(.green)
                    Text("backup_all_uploaded")
                        .foregroundStyle(AppColors.onSurfaceVariant)
                }
                .font(AppTypography.bodySmall)
            }

            if !manager.queuePreview.isEmpty {
                // Spacer прижимает превью влево: при опустении очереди миниатюры
                // не растягиваются на освободившуюся ширину.
                HStack(spacing: 8) {
                    ForEach(manager.queuePreview, id: \.localIdentifier) { asset in
                        BackupThumb(
                            asset: asset,
                            isCurrent: asset.localIdentifier == manager.currentAsset?.localIdentifier
                        )
                        .transition(.asymmetric(
                            insertion: .move(edge: .trailing).combined(with: .opacity),
                            removal: .move(edge: .leading).combined(with: .opacity)
                        ))
                    }
                    Spacer(minLength: 0)
                }
                .clipped()
                .padding(.horizontal, 8)
                .animation(
                    .interpolatingSpring(stiffness: 180, damping: 20),
                    value: manager.queuePreview.map(\.localIdentifier)
                )
            }
        }
    }

    // MARK: - Освобождение места

    private var freeSpaceSection: some View {
        let hasReclaimable = !manager.reclaimable.isEmpty
        return VStack(alignment: .leading, spacing: 14) {
            HStack(spacing: 14) {
                ZStack {
                    Circle()
                        .fill(Color.orange.opacity(0.18))
                        .frame(width: 42, height: 42)
                    Image(systemName: "trash.fill")
                        .font(.system(size: 16, weight: .semibold))
                        .foregroundStyle(.orange)
                }
                VStack(alignment: .leading, spacing: 2) {
                    Text("backup_free_space")
                        .font(AppTypography.titleMedium)
                        .foregroundStyle(AppColors.onSurface)
                    if manager.reclaimableBytes > 0 {
                        Text(verbatim: String(
                            format: NSLocalizedString("backup_free_space_estimate", comment: ""),
                            FormatUtils.formatSize(manager.reclaimableBytes)
                        ))
                        .font(AppTypography.bodySmall)
                        .foregroundStyle(AppColors.onSurfaceVariant)
                    } else if !manager.isScanning {
                        Text("backup_nothing_to_free")
                            .font(AppTypography.bodySmall)
                            .foregroundStyle(AppColors.onSurfaceVariant)
                    }
                }
                Spacer(minLength: 0)
            }

            Button {
                Task { await manager.freeSpace() }
            } label: {
                HStack(spacing: 8) {
                    if manager.isFreeing {
                        ProgressView().tint(.white).controlSize(.small)
                    } else {
                        Image(systemName: "sparkles")
                    }
                    Text(manager.isFreeing ? "backup_freeing" : "backup_free_space")
                }
                .font(AppTypography.titleMedium)
                .frame(maxWidth: .infinity)
                .padding(.vertical, 14)
                .background(
                    hasReclaimable
                        ? AnyShapeStyle(AppColors.accent)
                        : AnyShapeStyle(AppColors.onSurface.opacity(0.10))
                )
                .foregroundStyle(hasReclaimable ? Color.white : AppColors.onSurfaceVariant)
                .clipShape(Capsule())
            }
            .disabled(!hasReclaimable || manager.isFreeing)
        }
        .padding(16)
        .background(
            RoundedRectangle(cornerRadius: 20)
                .fill(AppColors.onSurface.opacity(0.05))
        )
    }
}

// MARK: - Донат хранилища

/// Круговой прогресс «использовано на диске»: фон-кольцо + дуга поверх; в центре —
/// крупный процент. Дуга стартует сверху (повёрнута на -90°).
private struct StorageDonut: View {
    let fraction: Double
    let percent: Int

    var body: some View {
        ZStack {
            Circle()
                .stroke(AppColors.accent.opacity(0.18), lineWidth: 10)
            Circle()
                .trim(from: 0, to: fraction)
                .stroke(
                    AppColors.accent,
                    style: StrokeStyle(lineWidth: 10, lineCap: .round)
                )
                .rotationEffect(.degrees(-90))
                .animation(.easeOut(duration: 0.4), value: fraction)
            Text(verbatim: "\(percent)%")
                .font(.system(size: 24, weight: .semibold))
                .foregroundStyle(AppColors.onSurface)
        }
    }
}

private struct StorageSegmentedBar: View {
    let total: Int64
    let other: Int64
    let s3: Int64

    private var denominator: Double {
        Double(max(total, other + s3, 1))
    }

    var body: some View {
        GeometryReader { geo in
            let full = geo.size.width
            HStack(spacing: 0) {
                Rectangle()
                    .fill(backupStorageOtherColor)
                    .frame(width: full * CGFloat(max(0.0, Double(other)) / denominator))
                Rectangle()
                    .fill(backupStorageS3Color)
                    .frame(width: full * CGFloat(max(0.0, Double(s3)) / denominator))
                Spacer(minLength: 0)
            }
        }
        .background(backupStorageFreeColor)
        .clipShape(Capsule())
    }
}

/// Квадратное превью ассета в очереди автозагрузки; у текущего — спиннер.
/// Размер фиксированный, чтобы миниатюры не разрастались по мере опустения очереди.
private struct BackupThumb: View {
    let asset: PHAsset
    let isCurrent: Bool

    private static let side: CGFloat = 64

    @State private var image: UIImage?

    var body: some View {
        Color.clear
            .frame(width: Self.side, height: Self.side)
            .background(AppColors.onSurface.opacity(0.08))
            .overlay {
                if let image {
                    Image(uiImage: image)
                        .resizable()
                        .scaledToFill()
                }
            }
            .clipShape(RoundedRectangle(cornerRadius: 10))
            .overlay {
                if isCurrent {
                    ZStack {
                        Color.black.opacity(0.3)
                        ProgressView().tint(.white).controlSize(.small)
                    }
                    .clipShape(RoundedRectangle(cornerRadius: 10))
                }
            }
            .task(id: asset.localIdentifier) {
                let side = Self.side * UIScreen.main.scale
                image = await DeviceMediaImageLoader.shared.thumbnail(
                    for: asset,
                    targetSize: CGSize(width: side, height: side)
                )
            }
    }
}
