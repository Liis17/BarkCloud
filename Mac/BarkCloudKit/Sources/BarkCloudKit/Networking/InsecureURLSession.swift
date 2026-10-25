import Foundation

/// Принимает любой TLS-сертификат только для filesHost при включённом обходе.
private final class TlsBypassTrustDelegate: NSObject, URLSessionDelegate, @unchecked Sendable {
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

/// Общий `URLSession` для HTTP upload/download и превью. При включённом обходе
/// принимает любой TLS-сертификат только от `filesHost`.
public enum InsecureHTTP {
    public static let session: URLSession = {
        let config = URLSessionConfiguration.default
        config.timeoutIntervalForRequest = 60
        config.timeoutIntervalForResource = 600
        return URLSession(configuration: config, delegate: TlsBypassTrustDelegate(), delegateQueue: nil)
    }()

    /// Сбросить URL-кэш, куки и хранилище учётных данных сессии. Вызывается при
    /// выходе из аккаунта, чтобы не осталось закэшированных ответов файлового сервиса.
    public static func clearCaches() async {
        await withCheckedContinuation { (continuation: CheckedContinuation<Void, Never>) in
            session.reset { continuation.resume() }
        }
    }
}
