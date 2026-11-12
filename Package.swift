// swift-tools-version: 5.9
import PackageDescription

let package = Package(
    name: "GigaTTS",
    platforms: [.macOS(.v14)],
    targets: [
        .executableTarget(
            name: "GigaTTS",
            path: "App",
            exclude: ["Info.plist", "Assets"]
        )
    ]
)
