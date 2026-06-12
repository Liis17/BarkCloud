import SwiftUI
import UIKit

/// Единый UX после создания публичной ссылки: диалог с выбором «Скопировать
/// ссылку» (в буфер обмена) или «Поделиться…» (системный `UIActivityViewController`,
/// чтобы быстро отправить в другое приложение). Навешивается на экран и биндится
/// к `Binding<ShareableURL?>` из view-model — как только VM кладёт туда URL
/// созданной ссылки, появляется диалог.
///
/// Используется во всех точках «Сделать публичной» (Gallery, MediaGrid,
/// AlbumDetail, CloudBrowser, MyShares) — заменил прямой показ Share Sheet.
private struct SharePresenterModifier: ViewModifier {
    @Binding var url: ShareableURL?
    @State private var activityItem: ShareableURL?
    @State private var showCopied = false

    func body(content: Content) -> some View {
        content
            .confirmationDialog(
                Text("share_ready_title"),
                isPresented: Binding(
                    get: { url != nil },
                    set: { if !$0 { url = nil } }
                ),
                titleVisibility: .visible
            ) {
                Button(String(localized: "shared_copy_link")) {
                    if let link = url {
                        UIPasteboard.general.url = link.url
                        showCopied = true
                    }
                    url = nil
                }
                Button(String(localized: "share_open_sheet")) {
                    // Сначала закрываем диалог, затем презентуем Share Sheet
                    // из внутреннего состояния — две независимые презентации.
                    activityItem = url
                    url = nil
                }
                Button(String(localized: "action_cancel"), role: .cancel) { url = nil }
            } message: {
                if let link = url {
                    Text(verbatim: link.url.absoluteString)
                }
            }
            .sheet(item: $activityItem) { item in
                ActivityViewController(activityItems: [item.url])
            }
            .overlay(alignment: .bottom) {
                if showCopied {
                    Text(String(localized: "share_link_copied"))
                        .font(AppTypography.bodySmall)
                        .foregroundStyle(AppColors.onSurface)
                        .padding(12)
                        .background(.regularMaterial)
                        .clipShape(RoundedRectangle(cornerRadius: 10))
                        .padding(.bottom, 16)
                        .transition(.move(edge: .bottom).combined(with: .opacity))
                        .task {
                            try? await Task.sleep(nanoseconds: 1_800_000_000)
                            showCopied = false
                        }
                }
            }
            .animation(.easeInOut(duration: 0.2), value: showCopied)
    }
}

extension View {
    /// Диалог «Скопировать ссылку / Поделиться…» после создания публичной ссылки.
    /// `url` — биндинг к `ShareableURL?` из VM; присвоение значения открывает диалог.
    func sharePresenter(url: Binding<ShareableURL?>) -> some View {
        modifier(SharePresenterModifier(url: url))
    }
}
