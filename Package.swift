// swift-tools-version: 6.0
import PackageDescription

let package = Package(
    name: "MDEditor",
    platforms: [.macOS(.v13)],
    targets: [
        .executableTarget(
            name: "MDEditor",
            path: "Sources/MDEditor"
        )
    ],
    swiftLanguageModes: [.v5]
)
