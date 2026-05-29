import SwiftUI
import UIKit
import Photos

/// Модалка резервного копирования: заполнение облака, тогл автозагрузки с прогрессом
/// и превью очереди (как Google Photos) и кнопка «Освободить место» с анимацией
/// благодарности. Показывается как плавающая карточка с отступами со всех сторон.
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
                VStack(alignment: .leading, spacing: 20) {
                    storageCard
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
        HStack {
            Label(String(localized: "backup_title"), systemImage: "icloud")
                .font(AppTypography.titleLarge)
                .foregroundStyle(AppColors.onSurface)
            Spacer()
            Button(action: onClose) {
                Image(systemName: "xmark")
                    .font(.system(size: 18, weight: .semibold))
                    .foregroundStyle(AppColors.onSurfaceVariant)
            }
        }
        .padding(20)
    }

    // MARK: - Хранилище

    private var storageCard: some View {
        let used = manager.usedStorage
        let limit = manager.storageLimit
        let fraction = limit > 0 ? min(1.0, Double(used) / Double(limit)) : 0
        return VStack(alignment: .leading, spacing: 8) {
            Text("settings_storage")
                .font(AppTypography.titleSmall)
                .foregroundStyle(AppColors.onSurfaceVariant)
                .textCase(.uppercase)
            ProgressView(value: fraction)
                .tint(AppColors.accent)
            Text(verbatim: "\(FormatUtils.formatSize(used)) / \(FormatUtils.formatSize(limit))")
                .font(AppTypography.bodySmall)
                .foregroundStyle(AppColors.onSurfaceVariant)
        }
        .padding(16)
        .background(AppColors.onSurface.opacity(0.04))
        .clipShape(RoundedRectangle(cornerRadius: 12))
    }

    // MARK: - Автозагрузка

    private var autoUploadSection: some View {
        VStack(alignment: .leading, spacing: 14) {
            Toggle(isOn: Binding(
                get: { manager.autoUploadEnabled },
                set: { manager.setAutoUpload($0) }
            )) {
                VStack(alignment: .leading, spacing: 2) {
                    Text("backup_autoupload_title")
                        .font(AppTypography.titleMedium)
                        .foregroundStyle(AppColors.onSurface)
                    Text("backup_autoupload_subtitle")
                        .font(AppTypography.bodySmall)
                        .foregroundStyle(AppColors.onSurfaceVariant)
                }
            }
            .tint(AppColors.accent)

            if manager.autoUploadEnabled {
                uploadProgress
            }
        }
        .padding(16)
        .background(AppColors.onSurface.opacity(0.04))
        .clipShape(RoundedRectangle(cornerRadius: 12))
    }

    private var uploadProgress: some View {
        let done = manager.uploadDone
        let total = done + manager.uploadFailed + manager.remainingCount
        return VStack(alignment: .leading, spacing: 10) {
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
            }

            if manager.remainingCount > 0 {
                Text(verbatim: String(
                    format: NSLocalizedString("backup_uploading_progress", comment: ""),
                    done, manager.remainingCount
                ))
                .font(AppTypography.bodySmall)
                .foregroundStyle(AppColors.onSurfaceVariant)
            } else if !manager.isScanning {
                Text("backup_all_uploaded")
                    .font(AppTypography.bodySmall)
                    .foregroundStyle(AppColors.onSurfaceVariant)
            }

            if !manager.queuePreview.isEmpty {
                HStack(spacing: 8) {
                    ForEach(Array(manager.queuePreview.enumerated()), id: \.element.localIdentifier) { idx, asset in
                        BackupThumb(asset: asset, isCurrent: idx == 0 && manager.currentAsset != nil)
                    }
                }
            }
        }
    }

    // MARK: - Освобождение места

    private var freeSpaceSection: some View {
        let hasReclaimable = !manager.reclaimable.isEmpty
        return VStack(spacing: 8) {
            Button {
                Task { await manager.freeSpace() }
            } label: {
                HStack(spacing: 8) {
                    if manager.isFreeing {
                        ProgressView().tint(.white)
                    } else {
                        Image(systemName: "trash")
                    }
                    Text(manager.isFreeing ? "backup_freeing" : "backup_free_space")
                }
                .font(AppTypography.titleMedium)
                .frame(maxWidth: .infinity)
                .padding(.vertical, 14)
                .background(hasReclaimable ? AppColors.accent : AppColors.onSurface.opacity(0.15))
                .foregroundStyle(.white)
                .clipShape(RoundedRectangle(cornerRadius: 12))
            }
            .disabled(!hasReclaimable || manager.isFreeing)

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
    }
}

/// Маленькое квадратное превью ассета в очереди автозагрузки; у текущего — спиннер.
private struct BackupThumb: View {
    let asset: PHAsset
    let isCurrent: Bool

    @State private var image: UIImage?

    var body: some View {
        RoundedRectangle(cornerRadius: 8)
            .fill(AppColors.onSurface.opacity(0.08))
            .frame(width: 64, height: 64)
            .overlay {
                if let image {
                    Image(uiImage: image)
                        .resizable()
                        .scaledToFill()
                }
            }
            .clipShape(RoundedRectangle(cornerRadius: 8))
            .overlay {
                if isCurrent {
                    ZStack {
                        Color.black.opacity(0.3)
                        ProgressView().tint(.white).controlSize(.small)
                    }
                    .clipShape(RoundedRectangle(cornerRadius: 8))
                }
            }
            .task(id: asset.localIdentifier) {
                let side = 64 * UIScreen.main.scale
                image = await DeviceMediaImageLoader.shared.thumbnail(
                    for: asset,
                    targetSize: CGSize(width: side, height: side)
                )
            }
    }
}
