// swift-tools-version: 6.0
import PackageDescription

// BarkCloudKit — общий сетевой слой BarkCloud для iOS и macOS-клиентов.
// Единый источник правды: сюда из iOS-таргета переносятся платформо-независимые
// файлы (gRPC-клиенты, токены, репозитории, сгенерированный proto), на пакет
// затем переводятся iOS-таргеты и новые macOS-таргеты (контейнер-app + FSKit-
// расширение). См. README.md и Mac/README.md (Этап 0 плана).
//
// Версии зависимостей зеркалят iOS-проект
// (Ios/BarkCloud/BarkCloud.xcodeproj/.../Package.resolved):
//   grpc-swift-2 2.4.1, grpc-swift-nio-transport 2.7.0, grpc-swift-protobuf 2.4.0.
let package = Package(
    name: "BarkCloudKit",
    platforms: [.macOS(.v15), .iOS(.v18)],
    products: [
        .library(name: "BarkCloudKit", targets: ["BarkCloudKit"]),
    ],
    dependencies: [
        .package(url: "https://github.com/grpc/grpc-swift-2.git", from: "2.4.1"),
        .package(url: "https://github.com/grpc/grpc-swift-nio-transport", from: "2.7.0"),
        .package(url: "https://github.com/grpc/grpc-swift-protobuf", from: "2.4.0"),
        .package(url: "https://github.com/apple/swift-protobuf.git", from: "1.29.0"),
    ],
    targets: [
        .target(
            name: "BarkCloudKit",
            dependencies: [
                .product(name: "GRPCCore", package: "grpc-swift-2"),
                .product(name: "GRPCNIOTransportHTTP2", package: "grpc-swift-nio-transport"),
                .product(name: "GRPCProtobuf", package: "grpc-swift-protobuf"),
                .product(name: "SwiftProtobuf", package: "swift-protobuf"),
            ]
        ),
    ]
)
