import Foundation
import ObjectiveC

/// Подмена `Bundle.main` для живой смены языка интерфейса. SwiftUI `Text("key")`
/// реагирует на `.environment(\.locale,…)`, но программные обращения
/// (`String(localized:)`, `NSLocalizedString`) читают строки напрямую из
/// `Bundle.main` и environment не видят. Чтобы и они переключались на лету,
/// один раз подменяем класс `Bundle.main` на `LocalizedBundle`, который
/// перенаправляет поиск строки в выбранный `.lproj`-бандл.
private final class LocalizedBundle: Bundle {
    override func localizedString(forKey key: String, value: String?, table tableName: String?) -> String {
        if let bundle = objc_getAssociatedObject(self, &Bundle.languageBundleKey) as? Bundle {
            return bundle.localizedString(forKey: key, value: value, table: tableName)
        }
        return super.localizedString(forKey: key, value: value, table: tableName)
    }
}

extension Bundle {
    fileprivate static var languageBundleKey = 0

    /// Применяет язык интерфейса к `Bundle.main`. `code == nil` — системный режим
    /// (снимаем подмену, возвращаем стандартное поведение, следующее за языком
    /// устройства). `.xcstrings` компилируется в пер-языковые `*.lproj`, поэтому
    /// путь к нужной локали существует.
    static func setAppLanguage(_ code: String?) {
        object_setClass(Bundle.main, LocalizedBundle.self)
        let lproj = code.flatMap { Bundle.main.path(forResource: $0, ofType: "lproj") }
            .flatMap { Bundle(path: $0) }
        objc_setAssociatedObject(
            Bundle.main,
            &languageBundleKey,
            lproj,
            .OBJC_ASSOCIATION_RETAIN_NONATOMIC
        )
    }
}
