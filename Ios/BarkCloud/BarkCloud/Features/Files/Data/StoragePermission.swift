import Foundation

enum StoragePermission {
    static var isGranted: Bool { true }
    static var externalRoot: URL {
        FileManager.default.urls(for: .documentDirectory, in: .userDomainMask).first!
    }
}
