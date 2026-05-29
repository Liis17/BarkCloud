import SwiftUI
import Photos

/// Что показываем в свойствах: облачный файл (`MediaAsset`) или ассет устройства
/// (`PHAsset`). Identifiable — чтобы открывать через `.sheet(item:)`.
enum FilePropertiesTarget: Identifiable {
    case cloud(MediaAsset)
    case device(PHAsset)

    var id: String {
        switch self {
        case .cloud(let a): return "cloud-\(a.id)"
        case .device(let a): return "device-\(a.localIdentifier)"
        }
    }
}

/// Экран свойств файла (аналог веб-модалки «Свойства»): имя, тип, размер,
/// разрешение, даты, ID. Поля, которых нет у источника, опускаются.
struct FilePropertiesSheet: View {
    let target: FilePropertiesTarget
    @Environment(\.dismiss) private var dismiss

    var body: some View {
        NavigationStack {
            List {
                switch target {
                case .cloud(let asset): cloudRows(asset)
                case .device(let asset): deviceRows(asset)
                }
            }
            .navigationTitle(String(localized: "props_title"))
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .topBarTrailing) {
                    Button(String(localized: "action_close")) { dismiss() }
                }
            }
        }
        .presentationDetents([.medium, .large])
    }

    // MARK: - Облако

    @ViewBuilder
    private func cloudRows(_ asset: MediaAsset) -> some View {
        row("props_name", asset.fileName)
        row("props_type", typeLabel(asset.kind))
        row("props_size", FormatUtils.formatSize(asset.fileSize))
        if asset.imageWidth > 0 && asset.imageHeight > 0 {
            row("props_resolution", "\(asset.imageWidth)×\(asset.imageHeight)")
        }
        row("props_created", dateTime(asset.createdAt))
        if let uploadedAt = asset.uploadedAt {
            row("props_uploaded", dateTime(uploadedAt))
        }
        row("props_id", asset.id)
    }

    // MARK: - Устройство

    @ViewBuilder
    private func deviceRows(_ asset: PHAsset) -> some View {
        row("props_name", deviceFileName(asset))
        row("props_type", deviceTypeLabel(asset))
        let size = DeviceAssetResource.originalByteSize(for: asset)
        if size > 0 { row("props_size", FormatUtils.formatSize(size)) }
        if asset.pixelWidth > 0 && asset.pixelHeight > 0 {
            row("props_resolution", "\(asset.pixelWidth)×\(asset.pixelHeight)")
        }
        if let created = asset.creationDate {
            row("props_created", dateTime(created))
        }
    }

    // MARK: - Строка

    private func row(_ labelKey: String.LocalizationValue, _ value: String) -> some View {
        HStack(alignment: .top, spacing: 12) {
            Text(String(localized: labelKey))
                .font(AppTypography.bodyMedium)
                .foregroundStyle(AppColors.onSurfaceVariant)
            Spacer(minLength: 12)
            Text(verbatim: value)
                .font(AppTypography.bodyMedium)
                .foregroundStyle(AppColors.onSurface)
                .multilineTextAlignment(.trailing)
                .textSelection(.enabled)
        }
    }

    // MARK: - Хелперы

    private func dateTime(_ date: Date) -> String {
        date.formatted(date: .abbreviated, time: .shortened)
    }

    private func typeLabel(_ kind: CloudMediaKind) -> String {
        switch kind {
        case .video: return String(localized: "media_type_video")
        case .photo: return String(localized: "media_type_photo")
        default: return String(localized: "media_type_document")
        }
    }

    private func deviceTypeLabel(_ asset: PHAsset) -> String {
        asset.mediaType == .video
            ? String(localized: "media_type_video")
            : String(localized: "media_type_photo")
    }

    private func deviceFileName(_ asset: PHAsset) -> String {
        PHAssetResource.assetResources(for: asset).first?.originalFilename ?? "—"
    }
}
