import SwiftUI
import UIKit
import BarkCloudKit

/// Общий кэш загруженных превью/изображений.
@MainActor
final class RemoteImageCache {
    static let shared = RemoteImageCache()
    private let cache = NSCache<NSURL, UIImage>()

    func image(for url: URL) -> UIImage? { cache.object(forKey: url as NSURL) }
    func store(_ image: UIImage, for url: URL) { cache.setObject(image, forKey: url as NSURL) }

    /// Очистить кэш (например, при выходе из аккаунта, чтобы новый пользователь
    /// не видел аватары/превью предыдущего).
    func clear() { cache.removeAllObjects() }
}

/// Async-картинка по URL через `InsecureHTTP.session` (доверяет self-signed TLS
/// сервера) с кэшем в памяти. Замена `AsyncImage`, который отвергает self-signed
/// сертификат файлового сервиса.
struct RemoteImage<Placeholder: View>: View {
    let fileId: String?
    let variant: CacheVariant?
    let url: URL?
    let contentMode: ContentMode
    @ViewBuilder var placeholder: () -> Placeholder

    @Environment(AppEnvironment.self) private var env
    @State private var image: UIImage?

    /// Cache-aware: при наличии `fileId` байты берутся из дискового кеша
    /// (`FileCacheService`), а не из сети напрямую.
    init(
        fileId: String?,
        variant: CacheVariant,
        url: URL?,
        contentMode: ContentMode = .fill,
        @ViewBuilder placeholder: @escaping () -> Placeholder
    ) {
        self.fileId = fileId
        self.variant = variant
        self.url = url
        self.contentMode = contentMode
        self.placeholder = placeholder
    }

    /// Legacy: без дискового кеша (для ссылок без известного `fileId`).
    init(url: URL?, contentMode: ContentMode = .fill, @ViewBuilder placeholder: @escaping () -> Placeholder) {
        self.fileId = nil
        self.variant = nil
        self.url = url
        self.contentMode = contentMode
        self.placeholder = placeholder
    }

    var body: some View {
        Group {
            if let image {
                Image(uiImage: image)
                    .resizable()
                    .aspectRatio(contentMode: contentMode)
            } else {
                placeholder()
            }
        }
        .task(id: url) { await load() }
    }

    private func load() async {
        guard let url else { image = nil; return }
        if let cached = RemoteImageCache.shared.image(for: url) {
            image = cached
            return
        }
        if let fileId, let variant {
            if let data = try? await env.fileCache.loadData(fileId: fileId, variant: variant, sourceURL: url),
               !Task.isCancelled, let ui = UIImage(data: data) {
                RemoteImageCache.shared.store(ui, for: url)
                image = ui
            }
            return
        }
        do {
            let (data, _) = try await InsecureHTTP.session.data(from: url)
            guard !Task.isCancelled, let ui = UIImage(data: data) else { return }
            RemoteImageCache.shared.store(ui, for: url)
            image = ui
        } catch {
            // оставляем плейсхолдер
        }
    }
}

extension RemoteImage where Placeholder == Color {
    /// Удобный инициализатор с серым плейсхолдером.
    init(url: URL?, contentMode: ContentMode = .fill) {
        self.init(url: url, contentMode: contentMode) { Color.primary.opacity(0.08) }
    }
}

/// Картинка, которая пробует загрузиться по нескольким URL по очереди и берёт
/// первый успешный ответ (HTTP 2xx + валидное изображение). Нужна для аватара:
/// сначала пробуем превью, при недоступности — полное изображение. В отличие от
/// `RemoteImage`, проверяет HTTP-статус, чтобы не принять 404-страницу за картинку.
struct FallbackRemoteImage<Placeholder: View>: View {
    let fileId: String?
    let urls: [URL]
    let contentMode: ContentMode
    @ViewBuilder var placeholder: () -> Placeholder

    @Environment(AppEnvironment.self) private var env
    @State private var image: UIImage?

    init(
        fileId: String? = nil,
        urls: [URL],
        contentMode: ContentMode = .fill,
        @ViewBuilder placeholder: @escaping () -> Placeholder
    ) {
        self.fileId = fileId
        self.urls = urls
        self.contentMode = contentMode
        self.placeholder = placeholder
    }

    var body: some View {
        Group {
            if let image {
                Image(uiImage: image)
                    .resizable()
                    .aspectRatio(contentMode: contentMode)
            } else {
                placeholder()
            }
        }
        .task(id: urls) { await load() }
    }

    private func load() async {
        for (index, url) in urls.enumerated() {
            if let cached = RemoteImageCache.shared.image(for: url) {
                image = cached
                return
            }
            // Cache-aware путь: первый URL — превью аватара, второй — оригинал.
            if let fileId {
                let variant: CacheVariant = index == 0 ? .avatarPreview : .avatar
                if let data = try? await env.fileCache.loadData(fileId: fileId, variant: variant, sourceURL: url),
                   let ui = UIImage(data: data) {
                    RemoteImageCache.shared.store(ui, for: url)
                    guard !Task.isCancelled else { return }
                    image = ui
                    return
                }
                continue
            }
            do {
                let (data, response) = try await InsecureHTTP.session.data(from: url)
                if let http = response as? HTTPURLResponse, !(200...299).contains(http.statusCode) {
                    continue
                }
                guard let ui = UIImage(data: data) else { continue }
                RemoteImageCache.shared.store(ui, for: url)
                guard !Task.isCancelled else { return }
                image = ui
                return
            } catch {
                continue
            }
        }
    }
}
