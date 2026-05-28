import SwiftUI

/// Пиксельная собачка-маскот для pull-to-refresh.
/// Рисуется в SwiftUI Canvas по фиксированной сетке без ассетов.
struct BarkMascot: View {
    enum Phase: Equatable {
        /// Сидит, выглядывает — для фазы потягивания.
        case peek
        /// Бежит — для фазы активного обновления. `tick` инкрементируется TimelineView.
        case run(tick: Int)
    }

    let phase: Phase
    let pixelSize: CGFloat

    var body: some View {
        Canvas { ctx, size in
            let sprite = Self.sprite(for: phase)
            guard let firstRow = sprite.first else { return }
            let cols = firstRow.count
            let rows = sprite.count
            let spriteW = CGFloat(cols) * pixelSize
            let spriteH = CGFloat(rows) * pixelSize
            let originX = (size.width - spriteW) / 2
            let originY = (size.height - spriteH) / 2
            for (y, row) in sprite.enumerated() {
                for (x, ch) in row.enumerated() {
                    guard let color = Self.color(for: ch) else { continue }
                    let rect = CGRect(
                        x: originX + CGFloat(x) * pixelSize,
                        y: originY + CGFloat(y) * pixelSize,
                        // +0.6 закрывает hairline-щели между квадратиками
                        // на нецелочисленных pixelSize.
                        width: pixelSize + 0.6,
                        height: pixelSize + 0.6
                    )
                    ctx.fill(Path(rect), with: .color(color))
                }
            }
        }
    }

    private static func color(for ch: Character) -> Color? {
        switch ch {
        case "B": return Color(red: 0.55, green: 0.36, blue: 0.18) // тело
        case "L": return Color(red: 0.84, green: 0.66, blue: 0.46) // светлое пузо/морда
        case "D": return Color(red: 0.10, green: 0.08, blue: 0.06) // глаз/нос
        default:  return nil
        }
    }

    private static func sprite(for phase: Phase) -> [String] {
        switch phase {
        case .peek:
            return peekSprite
        case .run(let tick):
            let n = runFrames.count
            let idx = ((tick % n) + n) % n
            return runFrames[idx]
        }
    }

    /// 16×10. Голова и ушки, как будто собачка только выглядывает снизу.
    private static let peekSprite: [String] = [
        "................",
        "................",
        "................",
        "................",
        ".....BB.B.......",
        "....BBBBBB......",
        "....BLBBBB......",
        "....BLLBDB......",
        "....BBBBBB......",
        ".....BBBB.......",
    ]

    /// 16×10. 4 кадра бега: ноги меняют фазу, хвостик виляет.
    private static let runFrames: [[String]] = [
        // frame 0: ноги сведены, хвост чуть вверх-вправо
        [
            "................",
            "..............B.",
            ".............BB.",
            ".....B...BBBB...",
            "....BBBBBBBBL...",
            "....BBLBBBBLD...",
            "....BBBBBBBBB...",
            "....BBBBBBBBB...",
            ".....B....B.....",
            ".....B....B.....",
        ],
        // frame 1: ноги разведены, хвост вверх
        [
            "................",
            ".............B..",
            "............BB..",
            ".....B...BBBB...",
            "....BBBBBBBBL...",
            "....BBLBBBBLD...",
            "....BBBBBBBBB...",
            "....BBBBBBBBB...",
            "....B.B..B.B....",
            "....B.....B.....",
        ],
        // frame 2: ноги сведены, хвост чуть вверх-влево
        [
            "................",
            "............B...",
            "...........BB...",
            ".....B...BBBB...",
            "....BBBBBBBBL...",
            "....BBLBBBBLD...",
            "....BBBBBBBBB...",
            "....BBBBBBBBB...",
            ".....B....B.....",
            ".....B....B.....",
        ],
        // frame 3: ноги разведены наоборот, хвост вверх
        [
            "................",
            ".............B..",
            "............BB..",
            ".....B...BBBB...",
            "....BBBBBBBBL...",
            "....BBLBBBBLD...",
            "....BBBBBBBBB...",
            "....BBBBBBBBB...",
            "....B.B..B.B....",
            ".....B....B.....",
        ],
    ]
}
