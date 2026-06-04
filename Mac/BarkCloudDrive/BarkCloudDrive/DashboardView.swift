import SwiftUI
import BarkCloudKit

/// Дашборд: профиль, сервер, прогресс хранилища, кнопки монтаж/размонтаж/настройки.
struct DashboardView: View {
    @Environment(AppModel.self) private var model
    @State private var showSettings = false

    var body: some View {
        VStack(alignment: .leading, spacing: 20) {
            header
            storageCard
            mountCard
            if case let .failed(message) = model.mount.state {
                Label(message, systemImage: "exclamationmark.triangle")
                    .foregroundStyle(.orange).font(.callout)
            }
            Spacer()
        }
        .padding(24)
        .toolbar {
            Button { showSettings = true } label: { Image(systemName: "gearshape") }
        }
        .sheet(isPresented: $showSettings) { SettingsView() }
        .task { await model.loadProfile() }
    }

    private var header: some View {
        HStack(spacing: 14) {
            Image(systemName: "person.crop.circle.fill")
                .font(.system(size: 44))
                .foregroundStyle(.tint)
            VStack(alignment: .leading, spacing: 2) {
                Text(displayName).font(.title3).bold()
                if let u = model.user, !u.username.isEmpty {
                    Text("@\(u.username)").foregroundStyle(.secondary)
                }
                Text(ServerConfig.current.filesHost).font(.caption).foregroundStyle(.secondary)
            }
        }
    }

    private var displayName: String {
        guard let u = model.user else { return "BarkCloud" }
        let name = "\(u.firstName) \(u.lastName)".trimmingCharacters(in: .whitespaces)
        return name.isEmpty ? u.username : name
    }

    private var storageCard: some View {
        VStack(alignment: .leading, spacing: 6) {
            Text("Хранилище").font(.headline)
            ProgressView(value: Double(model.storageUsed),
                         total: Double(max(model.storageLimit, 1)))
            Text("\(bytes(model.storageUsed)) из \(bytes(model.storageLimit))")
                .font(.caption).foregroundStyle(.secondary)
        }
        .padding(14)
        .background(.quaternary, in: RoundedRectangle(cornerRadius: 10))
    }

    private var mountCard: some View {
        HStack {
            VStack(alignment: .leading) {
                Text(model.mount.isMounted ? "Том примонтирован" : "Том не примонтирован")
                    .font(.headline)
                Text(model.mount.mountPoint.path)
                    .font(.caption).foregroundStyle(.secondary)
            }
            Spacer()
            if model.mount.state == .mounting || model.mount.state == .unmounting {
                ProgressView().controlSize(.small)
            } else if model.mount.isMounted {
                Button("Размонтировать") { Task { await model.mount.unmount() } }
            } else {
                Button("Примонтировать") { Task { await model.mount.mount() } }
                    .buttonStyle(.borderedProminent)
            }
        }
        .padding(14)
        .background(.quaternary, in: RoundedRectangle(cornerRadius: 10))
    }

    private func bytes(_ n: Int64) -> String {
        ByteCountFormatter.string(fromByteCount: n, countStyle: .file)
    }
}
