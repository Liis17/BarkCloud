import Foundation

/// Делегат, доверяющий self-signed TLS-сертификату сервера BarkCloud
/// (зеркалит `allowSelfSigned` в `GrpcManager`). Без состояния — потокобезопасен.
private final class SelfSignedTrustDelegate: NSObject, URLSessionDelegate, @unchecked Sendable {
    nonisolated func urlSession(
        _ session: URLSession,
        didReceive challenge: URLAuthenticationChallenge,
        completionHandler: @escaping @Sendable (URLSession.AuthChallengeDisposition, URLCredential?) -> Void
    ) {
        guard challenge.protectionSpace.authenticationMethod == NSURLAuthenticationMethodServerTrust,
              GrpcEndpoint.allowSelfSigned,
              challenge.protectionSpace.host == GrpcEndpoint.filesHost,
              let trust = challenge.protectionSpace.serverTrust else {
            completionHandler(.performDefaultHandling, nil)
            return
        }
        completionHandler(.useCredential, URLCredential(trust: trust))
    }
}

/// Общий `URLSession`, доверяющий self-signed сертификату сервера. Нужен для HTTP
/// upload/download и загрузки превью: стандартный `AsyncImage`/`URLSession` отвергает
/// self-signed TLS, на котором работает файловый сервис (:7025/web/...).
public enum InsecureHTTP {
    public static let session: URLSession = {
        let config = URLSessionConfiguration.default
        config.timeoutIntervalForRequest = 60
        config.timeoutIntervalForResource = 600
        return URLSession(configuration: config, delegate: SelfSignedTrustDelegate(), delegateQueue: nil)
    }()

    /// Сбросить URL-кэш, куки и хранилище учётных данных сессии. Вызывается при
    /// выходе из аккаунта, чтобы не осталось закэшированных ответов файлового сервиса.
    public static func clearCaches() {
        session.reset {}
    }
}
