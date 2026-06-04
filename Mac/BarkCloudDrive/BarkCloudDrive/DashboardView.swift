import SwiftUI
import AppKit
import BarkCloudKit

/// Дашборд: профиль, сервер, прогресс хранилища, кнопки монтаж/размонтаж/настройки.
struct DashboardView: View {
    @Environment(AppModel.self) private var model
    @State private var showSettings = false

    var body: some View {
        VStack(alignment: .leading, spacing: 20) {
            header
            storageCard
            domainCard
            if case let .failed(message) = model.domain.state {
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
        .task {
            await model.loadProfile()
            await model.domain.refreshState()
        }
    }

    private var header: some View {
        HStack(spacing: 14) {
            RemoteAvatar(urlString: model.user?.profilePicture)
            VStack(alignment: .leading, spacing: 2) {
                Text(verbatim: displayName).font(.title3).bold()
                if let u = model.user, !u.username.isEmpty {
                    Text(verbatim: "@\(u.username)").foregroundStyle(.secondary)
                }
                Text(verbatim: ServerConfig.current.filesHost).font(.caption).foregroundStyle(.secondary)
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

    private var domainCard: some View {
        HStack {
            VStack(alignment: .leading) {
                Text(model.domain.isEnabled ? "Папка подключена" : "Папка не подключена")
                    .font(.headline)
                if let url = model.domain.visibleURL {
                    Text(url.path).font(.caption).foregroundStyle(.secondary)
                } else {
                    Text("~/Library/CloudStorage/BarkCloud")
                        .font(.caption).foregroundStyle(.secondary)
                }
            }
            Spacer()
            if model.domain.state == .enabling || model.domain.state == .disabling {
                ProgressView().controlSize(.small)
            } else if model.domain.isEnabled {
                Button("Открыть в Finder") { model.domain.revealInFinder() }
                Button("Отключить") { Task { await model.domain.disable() } }
            } else {
                Button("Подключить") { Task { await model.domain.enable() } }
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

/// Аватар профиля с подкачкой по self-signed TLS (`InsecureHTTP`). Плейсхолдер —
/// системная иконка, пока картинка не загружена / нет URL.
struct RemoteAvatar: View {
    let urlString: String?
    @State private var image: NSImage?

    var body: some View {
        Group {
            if let image {
                Image(nsImage: image).resizable().scaledToFill()
            } else {
                Image(systemName: "person.crop.circle.fill")
                    .resizable().scaledToFit().foregroundStyle(.tint)
            }
        }
        .frame(width: 44, height: 44)
        .clipShape(Circle())
        .task(id: urlString) { await load() }
    }

    private func load() async {
        guard let s = urlString, !s.isEmpty,
              let url = GrpcEndpoint.normalizedFileDownloadURL(s) else { image = nil; return }
        if let (data, _) = try? await InsecureHTTP.session.data(from: url) {
            image = NSImage(data: data)
        }
    }
}
