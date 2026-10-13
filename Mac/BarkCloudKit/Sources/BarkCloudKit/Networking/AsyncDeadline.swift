import Foundation
import GRPCCore

/// A hard deadline for async work that may wait outside the network transport,
/// such as fetching an access token in a gRPC interceptor.
enum AsyncDeadline {
    static func withTimeout<Value: Sendable>(
        _ duration: Duration,
        operation: @escaping @Sendable () async throws -> Value
    ) async throws -> Value {
        guard duration > .zero else {
            throw RPCError(code: .deadlineExceeded, message: "Operation timed out")
        }

        let race = DeadlineRace<Value>()
        return try await withTaskCancellationHandler {
            try await withCheckedThrowingContinuation { continuation in
                race.start(duration: duration, operation: operation, continuation: continuation)
            }
        } onCancel: {
            race.cancel()
        }
    }
}

private final class DeadlineRace<Value: Sendable>: @unchecked Sendable {
    private let lock = NSLock()
    private var continuation: CheckedContinuation<Value, any Error>?
    private var operationTask: Task<Void, Never>?
    private var timeoutTask: Task<Void, Never>?
    private var isFinished = false
    private var cancellationRequested = false

    func start(
        duration: Duration,
        operation: @escaping @Sendable () async throws -> Value,
        continuation: CheckedContinuation<Value, any Error>
    ) {
        lock.lock()
        if cancellationRequested {
            isFinished = true
            lock.unlock()
            continuation.resume(throwing: CancellationError())
            return
        }

        self.continuation = continuation
        operationTask = Task {
            do {
                self.finish(.success(try await operation()))
            } catch {
                self.finish(.failure(error))
            }
        }
        timeoutTask = Task {
            do {
                try await Task.sleep(for: duration)
            } catch {
                return
            }
            self.finish(.failure(RPCError(code: .deadlineExceeded, message: "Operation timed out")))
        }
        lock.unlock()
    }

    func cancel() {
        lock.lock()
        guard !isFinished else {
            lock.unlock()
            return
        }
        guard let continuation else {
            cancellationRequested = true
            lock.unlock()
            return
        }
        isFinished = true
        self.continuation = nil
        let operationTask = self.operationTask
        let timeoutTask = self.timeoutTask
        self.operationTask = nil
        self.timeoutTask = nil
        lock.unlock()

        operationTask?.cancel()
        timeoutTask?.cancel()
        continuation.resume(throwing: CancellationError())
    }

    private func finish(_ result: Result<Value, any Error>) {
        lock.lock()
        guard !isFinished, let continuation else {
            lock.unlock()
            return
        }
        isFinished = true
        self.continuation = nil
        let operationTask = self.operationTask
        let timeoutTask = self.timeoutTask
        self.operationTask = nil
        self.timeoutTask = nil
        lock.unlock()

        operationTask?.cancel()
        timeoutTask?.cancel()
        continuation.resume(with: result)
    }
}
