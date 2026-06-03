import Foundation
import GRPCCore

// Батч-удаление записей каталога для FSKit-тома macOS.
//
// ⚠️ Этап 0 (миграция): требует, чтобы при переносе `CloudRepository` в пакет
//    его поле `grpc` стало доступным этому файлу — изменить
//    `private let grpc: GrpcManager` → `let grpc: GrpcManager` в CloudRepository.swift
//    (extension в отдельном файле не видит `private`). Сам тип `CloudRepository`
//    и нужные методы при миграции делаются `public`.
public extension CloudRepository {
    /// Массовое удаление записей каталога в корзину одним gRPC-вызовом
    /// `CloudApi.DeleteFileEntries` (идемпотентно: чужие/несуществующие/уже
    /// удалённые id молча пропускаются — ретрай безопасен). Сервер принимает до
    /// 100 id за запрос, поэтому режем на чанки по 100. Возвращает суммарное
    /// число реально перемещённых в корзину записей.
    ///
    /// Это упрощённый синхронный батч. Дебаунс-буфер Windows-движка
    /// (`_delPending`, окно тишины 1 c, тумбстоны, ретраи — см.
    /// `Drive/BarkCloud.Drive.Engine/CloudGateway.cs`) при необходимости
    /// добавляется отдельным слоем поверх этого метода.
    @discardableResult
    func batchDeleteFileEntries(_ entryIDs: [String]) async throws -> Int {
        guard !entryIDs.isEmpty else { return 0 }
        let stub = try await grpc.cloudStub()
        var deleted = 0
        for chunk in entryIDs.chunked(into: 100) {
            var req = Barkcloud_Files_DeleteFileEntriesRequest()
            req.entryIds = chunk
            let resp = try await stub.deleteFileEntries(req)
            deleted += Int(resp.deletedCount)
        }
        return deleted
    }
}

private extension Array {
    /// Разбить массив на чанки не больше `size` элементов.
    func chunked(into size: Int) -> [[Element]] {
        guard size > 0 else { return [self] }
        return stride(from: 0, to: count, by: size).map {
            Array(self[$0 ..< Swift.min($0 + size, count)])
        }
    }
}
