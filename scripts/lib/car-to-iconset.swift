// Renders the "AppIcon" icon of a compiled Assets.car into a full .iconset (16 px .. 1024 px, @1x and @2x).
// Used by scripts/gen-icons.sh: actool's own AppIcon.icns fallback for an Icon Composer .icon stops at 256 px,
// while Assets.car carries renditions up to 1024 px (issue #109).
// Usage: swift scripts/lib/car-to-iconset.swift <Assets.car> <out.iconset>
import AppKit

let args = CommandLine.arguments
guard args.count == 3 else {
    FileHandle.standardError.write(Data("usage: car-to-iconset.swift <Assets.car> <out.iconset>\n".utf8))
    exit(2)
}

// NSImage resolves named catalog images only through a bundle, so wrap the .car in a throwaway one.
let tmp = FileManager.default.temporaryDirectory.appendingPathComponent("penbridge-car-\(UUID().uuidString)")
defer { try? FileManager.default.removeItem(at: tmp) }
let bundleDir = tmp.appendingPathComponent("t.bundle/Contents")
do {
    try FileManager.default.createDirectory(at: bundleDir.appendingPathComponent("Resources"), withIntermediateDirectories: true)
    try FileManager.default.copyItem(at: URL(fileURLWithPath: args[1]), to: bundleDir.appendingPathComponent("Resources/Assets.car"))
    let plist = "<?xml version=\"1.0\"?><plist version=\"1.0\"><dict><key>CFBundleIdentifier</key><string>penbridge.icon.render</string><key>CFBundlePackageType</key><string>BNDL</string></dict></plist>"
    try plist.write(to: bundleDir.appendingPathComponent("Info.plist"), atomically: true, encoding: .utf8)
} catch {
    FileHandle.standardError.write(Data("car-to-iconset: \(error)\n".utf8))
    exit(1)
}
guard let bundle = Bundle(url: tmp.appendingPathComponent("t.bundle")), let image = bundle.image(forResource: "AppIcon") else {
    FileHandle.standardError.write(Data("car-to-iconset: no AppIcon image in \(args[1])\n".utf8))
    exit(1)
}

let out = URL(fileURLWithPath: args[2])
try? FileManager.default.removeItem(at: out)
try FileManager.default.createDirectory(at: out, withIntermediateDirectories: true)

// iconset name -> pixel size
let sizes: [(String, Int)] = [
    ("icon_16x16", 16), ("icon_16x16@2x", 32), ("icon_32x32", 32), ("icon_32x32@2x", 64),
    ("icon_128x128", 128), ("icon_128x128@2x", 256), ("icon_256x256", 256), ("icon_256x256@2x", 512),
    ("icon_512x512", 512), ("icon_512x512@2x", 1024),
]
for (name, px) in sizes {
    let rep = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: px, pixelsHigh: px, bitsPerSample: 8, samplesPerPixel: 4,
                               hasAlpha: true, isPlanar: false, colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0)!
    rep.size = NSSize(width: px, height: px)
    NSGraphicsContext.saveGraphicsState()
    NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: rep)
    NSGraphicsContext.current?.imageInterpolation = .high
    image.draw(in: NSRect(x: 0, y: 0, width: px, height: px), from: .zero, operation: .copy, fraction: 1)
    NSGraphicsContext.restoreGraphicsState()
    try rep.representation(using: .png, properties: [:])!.write(to: out.appendingPathComponent("\(name).png"))
}
