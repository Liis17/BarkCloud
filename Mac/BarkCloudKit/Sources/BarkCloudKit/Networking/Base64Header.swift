import Foundation

enum Base64Header {
    static func encode(_ raw: String) -> String {
        Data(raw.utf8).base64EncodedString()
    }
}
