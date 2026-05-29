import UIKit
import UniformTypeIdentifiers

/// Share Extension «Сохранить в BarkCloud». Принятые из системного share sheet
/// фото/видео/файлы складываются в общий контейнер App Group; само приложение
/// догружает их в облако при следующем открытии (сеть/токены расширению не нужны).
///
/// Каждый файл кладётся в свою подпапку `<uuid>/<оригинальное-имя>` — имя сохраняем,
/// по нему бэкенд определяет тип/`MediaKind`. Формат согласован с `ShareInbox` приложения.
final class ShareViewController: UIViewController {
    private let appGroupID = "group.com.barkfluff.BarkCloud"
    private let inboxFolderName = "ShareInbox"

    private let statusLabel = UILabel()
    private let spinner = UIActivityIndicatorView(style: .large)

    override func viewDidLoad() {
        super.viewDidLoad()
        setupUI()
        Task { await processAndFinish() }
    }

    private func setupUI() {
        view.backgroundColor = .clear

        let card = UIView()
        card.backgroundColor = .secondarySystemBackground
        card.layer.cornerRadius = 16
        card.translatesAutoresizingMaskIntoConstraints = false
        view.addSubview(card)

        spinner.translatesAutoresizingMaskIntoConstraints = false
        spinner.startAnimating()

        statusLabel.text = "Сохраняю в BarkCloud…"
        statusLabel.textColor = .label
        statusLabel.font = .preferredFont(forTextStyle: .headline)
        statusLabel.numberOfLines = 0
        statusLabel.textAlignment = .center
        statusLabel.translatesAutoresizingMaskIntoConstraints = false

        card.addSubview(spinner)
        card.addSubview(statusLabel)
        NSLayoutConstraint.activate([
            card.centerXAnchor.constraint(equalTo: view.centerXAnchor),
            card.centerYAnchor.constraint(equalTo: view.centerYAnchor),
            card.widthAnchor.constraint(lessThanOrEqualTo: view.widthAnchor, multiplier: 0.85),
            spinner.topAnchor.constraint(equalTo: card.topAnchor, constant: 28),
            spinner.centerXAnchor.constraint(equalTo: card.centerXAnchor),
            statusLabel.topAnchor.constraint(equalTo: spinner.bottomAnchor, constant: 16),
            statusLabel.leadingAnchor.constraint(equalTo: card.leadingAnchor, constant: 24),
            statusLabel.trailingAnchor.constraint(equalTo: card.trailingAnchor, constant: -24),
            statusLabel.bottomAnchor.constraint(equalTo: card.bottomAnchor, constant: -28),
        ])
    }

    private func processAndFinish() async {
        let count = await saveAllAttachments()
        spinner.stopAnimating()
        spinner.isHidden = true
        statusLabel.text = count > 0
            ? "Готово. Загрузим при следующем открытии BarkCloud."
            : "Не удалось сохранить файл."
        try? await Task.sleep(nanoseconds: 1_200_000_000)
        extensionContext?.completeRequest(returningItems: nil)
    }

    /// Сохранить все вложения из всех input items. Возвращает число сохранённых.
    private func saveAllAttachments() async -> Int {
        guard let items = extensionContext?.inputItems as? [NSExtensionItem],
              let inbox = inboxURL() else { return 0 }
        var saved = 0
        for item in items {
            for provider in item.attachments ?? [] {
                if let url = await loadFileURL(from: provider), store(originalURL: url, into: inbox) {
                    saved += 1
                }
            }
        }
        return saved
    }

    /// Выбрать наиболее подходящий тип вложения и загрузить во временный файл.
    private func loadFileURL(from provider: NSItemProvider) async -> URL? {
        let types: [UTType] = [.image, .movie, .pdf, .fileURL, .data]
        for type in types where provider.hasItemConformingToTypeIdentifier(type.identifier) {
            if let url = await loadItem(provider: provider, typeID: type.identifier) { return url }
        }
        return nil
    }

    private func loadItem(provider: NSItemProvider, typeID: String) async -> URL? {
        await withCheckedContinuation { continuation in
            provider.loadItem(forTypeIdentifier: typeID, options: nil) { item, _ in
                switch item {
                case let url as URL:
                    continuation.resume(returning: url)
                case let data as Data:
                    let ext = UTType(typeID)?.preferredFilenameExtension ?? "dat"
                    continuation.resume(returning: Self.writeTemp(data, ext: ext))
                case let image as UIImage:
                    continuation.resume(returning: image.jpegData(compressionQuality: 0.95).flatMap { Self.writeTemp($0, ext: "jpg") })
                default:
                    continuation.resume(returning: nil)
                }
            }
        }
    }

    private static func writeTemp(_ data: Data, ext: String) -> URL? {
        let tmp = FileManager.default.temporaryDirectory
            .appendingPathComponent(UUID().uuidString)
            .appendingPathExtension(ext)
        do { try data.write(to: tmp); return tmp } catch { return nil }
    }

    /// Скопировать файл в ящик: `<inbox>/<uuid>/<оригинальное-имя>`.
    private func store(originalURL: URL, into inbox: URL) -> Bool {
        let name = originalURL.lastPathComponent.isEmpty ? "file" : originalURL.lastPathComponent
        let dir = inbox.appendingPathComponent(UUID().uuidString, isDirectory: true)
        do {
            try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
            let dest = dir.appendingPathComponent(name)
            let scoped = originalURL.startAccessingSecurityScopedResource()
            defer { if scoped { originalURL.stopAccessingSecurityScopedResource() } }
            try FileManager.default.copyItem(at: originalURL, to: dest)
            return true
        } catch {
            return false
        }
    }

    private func inboxURL() -> URL? {
        guard let container = FileManager.default
            .containerURL(forSecurityApplicationGroupIdentifier: appGroupID) else { return nil }
        let inbox = container.appendingPathComponent(inboxFolderName, isDirectory: true)
        try? FileManager.default.createDirectory(at: inbox, withIntermediateDirectories: true)
        return inbox
    }
}
