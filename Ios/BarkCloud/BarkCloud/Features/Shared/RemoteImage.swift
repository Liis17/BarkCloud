import SwiftUI
import UIKit

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
    let url: URL?
    let contentMode: ContentMode
    @ViewBuilder var placeholder: () -> Placeholder

    @State private var image: UIImage?

    init(url: URL?, contentMode: ContentMode = .fill, @ViewBuilder placeholder: @escaping () -> Placeholder) {
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
    let urls: [URL]
    let contentMode: ContentMode
    @ViewBuilder var placeholder: () -> Placeholder

    @State private var image: UIImage?

    init(urls: [URL], contentMode: ContentMode = .fill, @ViewBuilder placeholder: @escaping () -> Placeholder) {
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
        for url in urls {
            if let cached = RemoteImageCache.shared.image(for: url) {
                image = cached
                return
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
