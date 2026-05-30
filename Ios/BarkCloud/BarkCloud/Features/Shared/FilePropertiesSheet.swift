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

/// Экран свойств файла (аналог веб-модалки «Свойства»): базовые поля + расширенные
/// метаданные (EXIF/ffprobe/Office) через `CloudApi.GetFileMetadata` для облачных
/// файлов. Поля, которых нет у источника, опускаются.
struct FilePropertiesSheet: View {
    let target: FilePropertiesTarget
    @Environment(\.dismiss) private var dismiss
    @Environment(AppEnvironment.self) private var env

    @State private var metadata: CloudFileMetadata?
    @State private var isLoadingMetadata = false

    var body: some View {
        NavigationStack {
            List {
                switch target {
                case .cloud(let asset):
                    cloudBasic(asset)
                    if let metadata { metadataSections(metadata, asset: asset) }
                case .device(let asset):
                    deviceBasic(asset)
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
        .task { await loadMetadataIfNeeded() }
    }

    // MARK: - Облако: базовое

    @ViewBuilder
    private func cloudBasic(_ asset: MediaAsset) -> some View {
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
        if let device = asset.uploadDeviceName {
            row("props_uploaded_device", device)
        }
        row("props_id", asset.id)
    }

    // MARK: - Облако: расширенные метаданные

    @ViewBuilder
    private func metadataSections(_ m: CloudFileMetadata, asset: MediaAsset) -> some View {
        if let takenAt = m.takenAt {
            Section(String(localized: "props_section_general")) {
                row("props_taken_at", dateTime(takenAt))
            }
        }

        if let make = m.cameraMake, let model = m.cameraModel {
            Section(String(localized: "props_section_camera")) {
                row("props_camera", joinedTrim([make, model]))
                if let lens = m.lensModel { row("props_lens", lens) }
            }
        } else if let model = m.cameraModel ?? m.cameraMake {
            Section(String(localized: "props_section_camera")) {
                row("props_camera", model)
                if let lens = m.lensModel { row("props_lens", lens) }
            }
        } else if let lens = m.lensModel {
            Section(String(localized: "props_section_camera")) {
                row("props_lens", lens)
            }
        }

        if hasShotParams(m) {
            Section(String(localized: "props_section_shot")) {
                if let f = m.focalLengthMm {
                    row("props_focal_length", "\(formatDecimal(f, fractionDigits: 0)) \(String(localized: "unit_mm"))")
                }
                if let fn = m.fNumber {
                    row("props_aperture", "f/\(formatDecimal(fn, fractionDigits: 1))")
                }
                if let exp = m.exposureTimeSeconds {
                    row("props_exposure", formatExposure(exp))
                }
                if let iso = m.iso { row("props_iso", "ISO \(iso)") }
                if let flash = m.flash { row("props_flash", flash ? String(localized: "common_yes") : String(localized: "common_no")) }
            }
        }

        if asset.kind == .video, hasVideoParams(m) {
            Section(String(localized: "props_section_video")) {
                if let d = m.durationSeconds { row("props_duration", formatDuration(d)) }
                if let v = m.videoCodec { row("props_video_codec", v.uppercased()) }
                if let a = m.audioCodec { row("props_audio_codec", a.uppercased()) }
                if let br = m.bitrate { row("props_bitrate", formatBitrate(br)) }
                if let fps = m.frameRate { row("props_frame_rate", "\(formatDecimal(fps, fractionDigits: 2)) \(String(localized: "unit_fps"))") }
            }
        }

        if m.hasCoordinates || m.altitude != nil {
            Section(String(localized: "props_section_gps")) {
                if let lat = m.latitude, let lon = m.longitude {
                    row("props_coordinates", "\(formatDecimal(lat, fractionDigits: 6)), \(formatDecimal(lon, fractionDigits: 6))")
                }
                if let alt = m.altitude {
                    row("props_altitude", "\(formatDecimal(alt, fractionDigits: 0)) \(String(localized: "unit_meters"))")
                }
            }
        }

        if hasDocumentParams(m) {
            Section(String(localized: "props_section_document")) {
                if let t = m.documentTitle { row("props_doc_title", t) }
                if let a = m.documentAuthor { row("props_doc_author", a) }
                if let s = m.documentSubject { row("props_doc_subject", s) }
                if let p = m.documentPageCount { row("props_doc_pages", "\(p)") }
            }
        }

        if let tool = m.creatorTool {
            Section { row("props_creator_tool", tool) }
        }
    }

    // MARK: - Устройство

    @ViewBuilder
    private func deviceBasic(_ asset: PHAsset) -> some View {
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

    // MARK: - Подгрузка метаданных

    private func loadMetadataIfNeeded() async {
        guard case .cloud(let asset) = target, metadata == nil, !isLoadingMetadata else { return }
        isLoadingMetadata = true
        defer { isLoadingMetadata = false }
        metadata = try? await env.cloudRepository.getFileMetadata(fileID: asset.id)
    }

    // MARK: - Хелперы

    private func hasShotParams(_ m: CloudFileMetadata) -> Bool {
        m.focalLengthMm != nil || m.fNumber != nil || m.exposureTimeSeconds != nil
            || m.iso != nil || m.flash != nil
    }

    private func hasVideoParams(_ m: CloudFileMetadata) -> Bool {
        m.durationSeconds != nil || m.videoCodec != nil || m.audioCodec != nil
            || m.bitrate != nil || m.frameRate != nil
    }

    private func hasDocumentParams(_ m: CloudFileMetadata) -> Bool {
        m.documentTitle != nil || m.documentAuthor != nil
            || m.documentSubject != nil || m.documentPageCount != nil
    }

    private func joinedTrim(_ parts: [String]) -> String {
        parts
            .map { $0.trimmingCharacters(in: .whitespaces) }
            .filter { !$0.isEmpty }
            .joined(separator: " ")
    }

    private func dateTime(_ date: Date) -> String {
        date.formatted(date: .abbreviated, time: .shortened)
    }

    private func formatDecimal(_ value: Double, fractionDigits: Int) -> String {
        String(format: "%.\(fractionDigits)f", value)
    }

    /// Выдержка: < 1 c → дробь `1/N`, ≥ 1 c → `N.N с`.
    private func formatExposure(_ seconds: Double) -> String {
        guard seconds > 0 else { return "—" }
        if seconds >= 1 {
            return "\(formatDecimal(seconds, fractionDigits: 1)) \(String(localized: "unit_seconds"))"
        }
        let denom = Int((1.0 / seconds).rounded())
        return "1/\(denom) \(String(localized: "unit_seconds"))"
    }

    /// Длительность видео: `h:mm:ss` или `mm:ss`.
    private func formatDuration(_ seconds: Double) -> String {
        let total = Int(seconds.rounded())
        let h = total / 3600
        let m = (total % 3600) / 60
        let s = total % 60
        if h > 0 {
            return String(format: "%d:%02d:%02d", h, m, s)
        }
        return String(format: "%d:%02d", m, s)
    }

    /// Битрейт: > 1 Мбит/с → `Х.Х Мбит/с`, иначе → `Х Кбит/с`.
    private func formatBitrate(_ bps: Int64) -> String {
        if bps >= 1_000_000 {
            return "\(formatDecimal(Double(bps) / 1_000_000.0, fractionDigits: 1)) \(String(localized: "unit_mbps"))"
        }
        return "\(bps / 1000) \(String(localized: "unit_kbps"))"
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
