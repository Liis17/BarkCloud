import UIKit
import UniformTypeIdentifiers

/// Share Extension «Сохранить в BarkCloud». Принятые из системного share sheet
/// фото/видео/файлы пользователь может загрузить в выбранную папку: по умолчанию
/// — «Недавно загруженные» (auto-folder, совпадает с веб-клиентом), но через
/// меню можно выбрать любую папку из корня. После «Загрузить» файлы ставятся
/// в фоновую очередь `BackgroundUploadCoordinator` и продолжают грузиться даже
/// после закрытия расширения и убийства main app — этим занимается iOS-демон.
///
/// Поток:
/// 1. Прочитать access-token из shared Keychain (`SessionStore` использует тот
///    же keychain-access-group, что и main app).
/// 2. Сохранить файлы оригиналов в App Group container.
/// 3. Подгрузить список папок корня + создать/найти «Недавно загруженные» (default).
/// 4. Показать UI: иконка, имя файла(ов), кнопка-чип «Папка: ...» и кнопка
///    «Загрузить».
/// 5. По «Загрузить» — для каждого attachment: getUploadURL, multipart body
///    в App Group, UploadJob в `UploadQueueStore`, submit в координатор.
/// 6. `notifyChanged()` на координаторе → стартует Live Activity (Dynamic Island).
/// 7. `extensionContext.completeRequest(...)` закрывает расширение.
final class ShareViewController: UIViewController {
    private let appGroupID = "group.com.barkfluff.BarkCloud"

    private let sessionStore = SessionStore()
    private lazy var grpc = GrpcManager(session: sessionStore)
    private lazy var transfer = FileTransferService(grpc: grpc)

    // MARK: - UI

    private let containerView = UIView()
    private let titleLabel = UILabel()
    private let subtitleLabel = UILabel()
    private let spinner = UIActivityIndicatorView(style: .medium)
    private let folderButton = UIButton(type: .system)
    private let uploadButton = UIButton(type: .system)
    private let cancelButton = UIButton(type: .system)

    // MARK: - State

    private struct PreparedAttachment {
        let stagedURL: URL          // оригинал в App Group container
        let fileName: String
        let mimeType: String
    }

    private struct FolderItem {
        let id: String              // "" = корень
        let name: String
    }

    private var preparedAttachments: [PreparedAttachment] = []
    private var availableFolders: [FolderItem] = []
    private var selectedFolder: FolderItem?
    private var isPreparing = true
    private var isUploading = false

    override func viewDidLoad() {
        super.viewDidLoad()
        setupUI()
        // tokenProvider у координатора может быть не установлен (Share Extension
        // живёт в своём процессе) — поставим свой, чтобы запрос имел `x-auth-token`.
        BackgroundUploadCoordinator.shared.tokenProvider = { [transfer] in
            await transfer.validAccessToken()
        }
        Task { await prepare() }
    }

    // MARK: - UI setup

    private func setupUI() {
        view.backgroundColor = .clear

        containerView.backgroundColor = .secondarySystemBackground
        containerView.layer.cornerRadius = 24
        containerView.translatesAutoresizingMaskIntoConstraints = false
        view.addSubview(containerView)

        titleLabel.text = "Куда загрузить"
        titleLabel.textColor = .label
        titleLabel.font = .preferredFont(forTextStyle: .title3)
        titleLabel.numberOfLines = 1
        titleLabel.textAlignment = .center
        titleLabel.translatesAutoresizingMaskIntoConstraints = false

        subtitleLabel.text = "..."
        subtitleLabel.textColor = .secondaryLabel
        subtitleLabel.font = .preferredFont(forTextStyle: .footnote)
        subtitleLabel.numberOfLines = 2
        subtitleLabel.textAlignment = .center
        subtitleLabel.translatesAutoresizingMaskIntoConstraints = false

        spinner.translatesAutoresizingMaskIntoConstraints = false
        spinner.startAnimating()

        configureFolderButton()
        configureActionButtons()

        let stack = UIStackView(arrangedSubviews: [
            titleLabel, subtitleLabel, spinner, folderButton, uploadButton, cancelButton
        ])
        stack.axis = .vertical
        stack.spacing = 14
        stack.alignment = .fill
        stack.setCustomSpacing(8, after: titleLabel)
        stack.setCustomSpacing(20, after: subtitleLabel)
        stack.setCustomSpacing(20, after: folderButton)
        stack.setCustomSpacing(8, after: uploadButton)
        stack.translatesAutoresizingMaskIntoConstraints = false
        containerView.addSubview(stack)

        NSLayoutConstraint.activate([
            containerView.centerXAnchor.constraint(equalTo: view.centerXAnchor),
            containerView.centerYAnchor.constraint(equalTo: view.centerYAnchor),
            containerView.leadingAnchor.constraint(greaterThanOrEqualTo: view.leadingAnchor, constant: 16),
            containerView.trailingAnchor.constraint(lessThanOrEqualTo: view.trailingAnchor, constant: -16),
            containerView.widthAnchor.constraint(lessThanOrEqualToConstant: 480),

            stack.topAnchor.constraint(equalTo: containerView.topAnchor, constant: 24),
            stack.leadingAnchor.constraint(equalTo: containerView.leadingAnchor, constant: 20),
            stack.trailingAnchor.constraint(equalTo: containerView.trailingAnchor, constant: -20),
            stack.bottomAnchor.constraint(equalTo: containerView.bottomAnchor, constant: -20),

            folderButton.heightAnchor.constraint(equalToConstant: 44),
            uploadButton.heightAnchor.constraint(equalToConstant: 48),
            cancelButton.heightAnchor.constraint(equalToConstant: 36)
        ])

        folderButton.isHidden = true
        uploadButton.isHidden = true
        cancelButton.isHidden = true
    }

    private func configureFolderButton() {
        folderButton.translatesAutoresizingMaskIntoConstraints = false
        folderButton.contentHorizontalAlignment = .center
        folderButton.titleLabel?.font = .preferredFont(forTextStyle: .body)
        folderButton.setTitleColor(.label, for: .normal)
        folderButton.layer.cornerRadius = 12
        folderButton.layer.borderWidth = 1
        folderButton.layer.borderColor = UIColor.separator.cgColor
        folderButton.backgroundColor = .systemBackground
        folderButton.addTarget(self, action: #selector(folderTapped), for: .touchUpInside)
    }

    private func configureActionButtons() {
        uploadButton.translatesAutoresizingMaskIntoConstraints = false
        uploadButton.setTitle("Загрузить", for: .normal)
        uploadButton.titleLabel?.font = .preferredFont(forTextStyle: .headline)
        uploadButton.backgroundColor = .systemOrange
        uploadButton.setTitleColor(.white, for: .normal)
        uploadButton.layer.cornerRadius = 14
        uploadButton.addTarget(self, action: #selector(uploadTapped), for: .touchUpInside)

        cancelButton.translatesAutoresizingMaskIntoConstraints = false
        cancelButton.setTitle("Отмена", for: .normal)
        cancelButton.setTitleColor(.secondaryLabel, for: .normal)
        cancelButton.addTarget(self, action: #selector(cancelTapped), for: .touchUpInside)
    }

    // MARK: - Подготовка (асинхронная)

    private func prepare() async {
        guard sessionStore.hasValidRefreshToken() else {
            await showTerminal("Войдите в BarkCloud, чтобы делиться файлами.", autoClose: 1.5)
            return
        }

        // 1. Собрать и заштажить attachments в App Group container.
        let raw = await collectAttachments()
        guard !raw.isEmpty else {
            await showTerminal("Не удалось получить файл.", autoClose: 1.2)
            return
        }
        preparedAttachments = raw
        subtitleLabel.text = previewSubtitle(for: raw)

        // 2. Подгрузить список папок корня (с default = «Недавно загруженные»).
        await loadFoldersAndDefault()

        // 3. Показать UI выбора.
        isPreparing = false
        spinner.stopAnimating()
        spinner.isHidden = true
        folderButton.isHidden = false
        uploadButton.isHidden = false
        cancelButton.isHidden = false
        updateFolderButtonTitle()
    }

    /// Чип-кнопка папки: подписать названием текущей выбранной. Сам список папок
    /// показывается в `folderTapped()` через `UIAlertController.actionSheet` —
    /// он раскрывается на всю ширину экрана, в отличие от `UIMenu`, который
    /// подстраивается под ширину кнопки.
    private func updateFolderButtonTitle() {
        let name = selectedFolder?.name ?? "Без папки"
        var config = UIButton.Configuration.bordered()
        config.cornerStyle = .medium
        config.baseBackgroundColor = .systemBackground
        config.image = UIImage(systemName: "folder.fill")
        config.imagePlacement = .leading
        config.imagePadding = 8
        var title = AttributedString("Папка: \(name)")
        title.font = .preferredFont(forTextStyle: .body)
        config.attributedTitle = title
        folderButton.configuration = config
    }

    @objc private func folderTapped() {
        let sheet = UIAlertController(
            title: "Выберите папку",
            message: "Куда сохранить файл в BarkCloud",
            preferredStyle: .actionSheet
        )
        let none = UIAlertAction(title: "Без папки", style: .default) { [weak self] _ in
            self?.selectedFolder = nil
            self?.updateFolderButtonTitle()
        }
        if selectedFolder == nil {
            none.setValue(true, forKey: "checked")
        }
        sheet.addAction(none)
        for folder in availableFolders {
            let action = UIAlertAction(title: folder.name, style: .default) { [weak self] _ in
                self?.selectedFolder = folder
                self?.updateFolderButtonTitle()
            }
            if folder.id == selectedFolder?.id {
                action.setValue(true, forKey: "checked")
            }
            sheet.addAction(action)
        }
        sheet.addAction(UIAlertAction(title: "Отмена", style: .cancel))
        // На iPad action sheet требует sourceView для popover.
        if let popover = sheet.popoverPresentationController {
            popover.sourceView = folderButton
            popover.sourceRect = folderButton.bounds
        }
        present(sheet, animated: true)
    }

    /// Список папок корня + создание/поиск «Недавно загруженные». При ошибке
    /// сети — список пустой, default остаётся «Недавно загруженные» (создастся
    /// сервером при первой загрузке).
    private func loadFoldersAndDefault() async {
        let recentName = "Недавно загруженные"
        do {
            let stub = try await grpc.cloudStub()
            var req = Barkcloud_Files_ListDirectoryRequest()
            req.directoryID = ""
            let resp = try await stub.listDirectoryDetailed(req)
            let folders = resp.subdirs.map { FolderItem(id: $0.id, name: $0.name) }
            self.availableFolders = folders
            if let existing = folders.first(where: { $0.name == recentName }) {
                self.selectedFolder = existing
            } else {
                // Создать сразу, чтобы был валидный id для attachFile.
                var createReq = Barkcloud_Files_CreateDirectoryRequest()
                createReq.parentID = ""
                createReq.name = recentName
                let created = try await stub.createDirectory(createReq)
                let new = FolderItem(id: created.id, name: created.name)
                self.availableFolders = [new] + folders
                self.selectedFolder = new
            }
        } catch {
            // Без сети показываем только default-вариант и «корень»; selectedFolder
            // оставим nil, чтобы файл просто ушёл в облако без папки (auto-folder
            // сервер не создаст без вызова, и мы это пометим в UI).
            self.availableFolders = []
            self.selectedFolder = nil
        }
    }

    // MARK: - Actions

    @objc private func uploadTapped() {
        guard !isUploading else { return }
        isUploading = true
        uploadButton.isEnabled = false
        folderButton.isEnabled = false
        cancelButton.isEnabled = false
        let label = UIActivityIndicatorView(style: .medium)
        label.color = .white
        label.translatesAutoresizingMaskIntoConstraints = false
        label.startAnimating()
        uploadButton.setTitle("", for: .normal)
        uploadButton.addSubview(label)
        NSLayoutConstraint.activate([
            label.centerXAnchor.constraint(equalTo: uploadButton.centerXAnchor),
            label.centerYAnchor.constraint(equalTo: uploadButton.centerYAnchor)
        ])
        Task { await runEnqueueAndFinish() }
    }

    @objc private func cancelTapped() {
        // Удаляем зашаженные originals — они уже скопированы в App Group.
        for prepared in preparedAttachments {
            try? FileManager.default.removeItem(at: prepared.stagedURL)
        }
        extensionContext?.completeRequest(returningItems: nil)
    }

    private func runEnqueueAndFinish() async {
        var enqueued = 0
        for prepared in preparedAttachments {
            if await enqueue(prepared) {
                enqueued += 1
            }
        }
        if enqueued > 0 {
            await showTerminal("Загружаю в BarkCloud…", autoClose: 0.8)
        } else {
            await showTerminal("Не удалось подготовить загрузку.", autoClose: 1.2)
        }
    }

    private func showTerminal(_ text: String, autoClose: TimeInterval) async {
        titleLabel.text = text
        subtitleLabel.text = ""
        spinner.stopAnimating(); spinner.isHidden = true
        folderButton.isHidden = true
        uploadButton.isHidden = true
        cancelButton.isHidden = true
        try? await Task.sleep(nanoseconds: UInt64(autoClose * 1_000_000_000))
        extensionContext?.completeRequest(returningItems: nil)
    }

    // MARK: - Сбор файлов из NSExtensionItem (с сохранением в App Group)

    private func previewSubtitle(for items: [PreparedAttachment]) -> String {
        if items.count == 1 { return items[0].fileName }
        return "\(items.count) файлов"
    }

    private func collectAttachments() async -> [PreparedAttachment] {
        guard let items = extensionContext?.inputItems as? [NSExtensionItem] else { return [] }
        guard let stagingDir = UploadConstants.stagingDirectory else { return [] }
        var result: [PreparedAttachment] = []
        for item in items {
            for provider in item.attachments ?? [] {
                if let prepared = await prepareAttachment(provider, stagingDir: stagingDir) {
                    result.append(prepared)
                }
            }
        }
        return result
    }

    private func prepareAttachment(_ provider: NSItemProvider, stagingDir: URL) async -> PreparedAttachment? {
        let types: [UTType] = [.image, .movie, .pdf, .fileURL, .data]
        for type in types where provider.hasItemConformingToTypeIdentifier(type.identifier) {
            if let url = await loadItem(provider: provider, typeID: type.identifier) {
                let name = url.lastPathComponent.isEmpty ? "file" : url.lastPathComponent
                let staged = stagingDir.appendingPathComponent("\(UUID().uuidString)-\(name)")
                do {
                    let scoped = url.startAccessingSecurityScopedResource()
                    defer { if scoped { url.stopAccessingSecurityScopedResource() } }
                    try FileManager.default.copyItem(at: url, to: staged)
                } catch {
                    continue
                }
                let mime = inferMime(for: name)
                return PreparedAttachment(stagedURL: staged, fileName: name, mimeType: mime)
            }
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

    // MARK: - Постановка в фоновую очередь

    private func enqueue(_ prepared: PreparedAttachment) async -> Bool {
        guard let stagingDir = UploadConstants.stagingDirectory else { return false }

        // 1. Получить uploadURL через gRPC.
        let upload: (url: String, fileID: String)
        do {
            upload = try await transfer.getUploadURL(type: .cloudFile)
        } catch {
            return false
        }

        // 2. Подготовить multipart body файл в App Group.
        let multipartURL = stagingDir.appendingPathComponent("\(UUID().uuidString).body")
        let totalBytes: Int64
        do {
            totalBytes = try MultipartBodyBuilder.writeMultipartFile(
                boundary: UploadConstants.multipartBoundary,
                fileName: prepared.fileName,
                mimeType: prepared.mimeType,
                sourceFile: prepared.stagedURL,
                destination: multipartURL
            )
        } catch {
            return false
        }

        // 3. Создать UploadJob и submit. directoryID нужен — координатор сам
        // attachFile сделать не может (это в main app), но мы записываем его в
        // UploadJob; AppEnvironment.onJobCompleted в main app выполнит attach.
        let snapshot = await UploadQueueStore.shared.create(
            sourceKind: .share,
            sourceFilePath: prepared.stagedURL.path,
            multipartBodyPath: multipartURL.path,
            fileName: prepared.fileName,
            mimeType: prepared.mimeType,
            directoryID: selectedFolder?.id,
            uploadURL: upload.url,
            preparedFileID: upload.fileID,
            totalBytes: totalBytes
        )
        await BackgroundUploadCoordinator.shared.submit(jobID: snapshot.id)
        return true
    }

    private func inferMime(for fileName: String) -> String {
        let ext = (fileName as NSString).pathExtension
        guard !ext.isEmpty,
              let type = UTType(filenameExtension: ext),
              let mime = type.preferredMIMEType else {
            return "application/octet-stream"
        }
        return mime
    }
}
