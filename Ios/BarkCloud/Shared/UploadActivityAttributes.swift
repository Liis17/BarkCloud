import ActivityKit
import Foundation

/// Attributes одной агрегированной Live Activity «Загружаю в BarkCloud». Активность
/// одна на все источники (Share / Backup / Manual): счётчик «N из M» считает
/// файлы всех UploadJob, currentFileName показывает имя текущего файла.
///
/// Используется и main app (через `ActivityKit.request/update/end`), и Widget
/// Extension (для рендеринга Lock Screen + Dynamic Island), поэтому файл общий
/// для обоих таргетов.
public struct UploadActivityAttributes: ActivityAttributes {
    public struct ContentState: Codable, Hashable {
        public var totalFiles: Int
        public var completedFiles: Int
        public var failedFiles: Int
        public var currentFileName: String
        public var currentProgress: Double   // 0…1 для текущего файла
        public var overallProgress: Double   // 0…1 для всей очереди
        public var isFinished: Bool

        public init(
            totalFiles: Int,
            completedFiles: Int,
            failedFiles: Int,
            currentFileName: String,
            currentProgress: Double,
            overallProgress: Double,
            isFinished: Bool
        ) {
            self.totalFiles = totalFiles
            self.completedFiles = completedFiles
            self.failedFiles = failedFiles
            self.currentFileName = currentFileName
            self.currentProgress = currentProgress
            self.overallProgress = overallProgress
            self.isFinished = isFinished
        }
    }

    public var startedAt: Date

    public init(startedAt: Date) {
        self.startedAt = startedAt
    }
}
