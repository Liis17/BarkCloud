import SwiftUI

/// Пиксель-арт лиса-маскот для pull-to-refresh. Рисуется в SwiftUI Canvas из
/// спрайт-сетки (без ассетов): лиса сидит ровно анфас, а пушистый хвост справа
/// виляет вверх-вниз. Виляние ведётся непрерывным временем `time` и работает
/// одинаково при потягивании и при активном обновлении.
struct BarkMascot: View {
    /// Часы анимации хвоста (секунды). Растут пока сцена видима.
    let time: Double
    /// 0…1 — общий масштаб появления (растёт при потягивании).
    let scale: CGFloat

    var body: some View {
        Canvas { ctx, size in
            Self.draw(in: ctx, size: size, time: time, scale: scale)
        }
    }

    // MARK: - Спрайт

    private static let cols = 21
    private static let rows = 17

    /// Тело лисы. '.' пусто, O оранжевый, W белый, D тёмный.
    private static let bodySprite: [String] = [
        "...DD.......DD.......",
        "..OOOO.....OOOO......",
        "...OOOOOOOOOOO.......",
        "..OOOOOOOOOOOOO......",
        "..OOOOOOOOOOOOO......",
        "..OOODOOOOODOOO......",
        "..OOOOOOOOOOOOO......",
        "...OOOWWWWWOOO.......",
        "...OOWWWDWWWOO.......",
        "....OOWWWWWOO........",
        "....OOOWWWOOO........",
        "...OOOOWWWOOOO.......",
        "...OOOOWWWOOOO.......",
        "...OOOOWWWOOOO.......",
        "...OOOOOOOOOOO.......",
        "...DDD.....DDD.......",
        ".....................",
    ]

    /// Пушистый хвост у правого бока, белый кончик сверху. Рисуется со сдвигом
    /// по вертикали — основание прячется за телом, на виду колышется свободная часть.
    private static let tailSprite: [String] = [
        ".....................",
        ".....................",
        ".............OWWW....",
        "............OOWWWW...",
        "............OOOWWWW..",
        "...........OOOOOWWW..",
        "...........OOOOOOWW..",
        "...........OOOOOOOO..",
        "...........OOOOOOOO..",
        "............OOOOOOO..",
        "............OOOOOO...",
        ".............OOOO....",
        ".....................",
        ".....................",
        ".....................",
        ".....................",
        ".....................",
    ]

    private static func color(_ ch: Character) -> Color? {
        switch ch {
        case "O": return Color(red: 0.93, green: 0.49, blue: 0.15)
        case "W": return Color(red: 0.97, green: 0.96, blue: 0.93)
        case "D": return Color(red: 0.13, green: 0.11, blue: 0.10)
        default: return nil
        }
    }

    // MARK: - Отрисовка

    private static func draw(in ctx: GraphicsContext, size: CGSize, time: Double, scale: CGFloat) {
        guard scale > 0.001 else { return }

        let pixel = min(size.width / CGFloat(cols), size.height / CGFloat(rows)) * scale
        let ox = (size.width - CGFloat(cols) * pixel) / 2
        let oy = (size.height - CGFloat(rows) * pixel) / 2

        // Виляние: кончик хвоста ходит на ±2 клетки, плавно по синусу.
        let wagCells: CGFloat = 2
        let wagDY = CGFloat(sin(time * 7.0)) * wagCells * pixel

        func cell(_ x: Int, _ y: Int, _ c: Color, dy: CGFloat) {
            let r = CGRect(x: ox + CGFloat(x) * pixel, y: oy + CGFloat(y) * pixel + dy,
                           width: pixel + 0.4, height: pixel + 0.4)
            ctx.fill(Path(r), with: .color(c))
        }

        // Хвост — под телом, со сдвигом виляния.
        for (y, row) in tailSprite.enumerated() {
            for (x, ch) in row.enumerated() {
                guard let c = color(ch) else { continue }
                cell(x, y, c, dy: wagDY)
            }
        }
        // Тело — поверх основания хвоста, неподвижно.
        for (y, row) in bodySprite.enumerated() {
            for (x, ch) in row.enumerated() {
                guard let c = color(ch) else { continue }
                cell(x, y, c, dy: 0)
            }
        }
    }
}
