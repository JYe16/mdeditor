#!/usr/bin/env swift
// Renders the MDEditor app icon and writes AppIcon.png + AppIcon.icns.
// Same visual language as SysMonitor: indigo-to-midnight squircle, soft glow, frosted-glass
// tile, and a glowing cyan → violet → pink stroke (here: the Markdown "M↓" mark).
//
// Usage (from the repo root):
//   swift scripts/make-icon.swift            # writes Resources/AppIcon.{png,icns}
//   swift scripts/make-icon.swift <outdir>   # writes them into <outdir> instead
import AppKit

let canvas: CGFloat = 1024

func drawIcon(in ctx: CGContext) {
    let space = CGColorSpaceCreateDeviceRGB()
    func color(_ hex: UInt32, _ a: CGFloat = 1) -> CGColor {
        CGColor(red: CGFloat((hex >> 16) & 0xff) / 255, green: CGFloat((hex >> 8) & 0xff) / 255,
                blue: CGFloat(hex & 0xff) / 255, alpha: a)
    }
    func gradient(_ stops: [(UInt32, CGFloat)], alpha: CGFloat = 1) -> CGGradient {
        CGGradient(colorsSpace: space, colors: stops.map { color($0.0, alpha) } as CFArray,
                   locations: stops.map { $0.1 })!
    }

    // macOS icon grid: 824pt body centred on a 1024pt canvas.
    let body = CGRect(x: 100, y: 100, width: 824, height: 824)
    let squircle = CGPath(roundedRect: body, cornerWidth: 186, cornerHeight: 186, transform: nil)

    // Drop shadow
    ctx.saveGState()
    ctx.setShadow(offset: CGSize(width: 0, height: -12), blur: 28, color: color(0x000000, 0.35))
    ctx.addPath(squircle)
    ctx.setFillColor(color(0x10132E))
    ctx.fillPath()
    ctx.restoreGState()

    ctx.saveGState()
    ctx.addPath(squircle)
    ctx.clip()

    // Background: deep indigo to midnight
    ctx.drawLinearGradient(gradient([(0x5149D6, 0), (0x24216E, 0.55), (0x0B0C2A, 1)]),
                           start: CGPoint(x: 512, y: 924), end: CGPoint(x: 512, y: 100), options: [])
    // Soft cyan glow behind the tile
    let glow = CGGradient(colorsSpace: space, colors: [color(0x48D6FF, 0.32), color(0x7A5CFF, 0.12), color(0x7A5CFF, 0)] as CFArray,
                          locations: [0, 0.5, 1])!
    ctx.drawRadialGradient(glow, startCenter: CGPoint(x: 512, y: 500), startRadius: 0,
                           endCenter: CGPoint(x: 512, y: 500), endRadius: 420, options: [])

    // Frosted-glass tile (same geometry as SysMonitor's chip, a touch larger)
    let tile = CGRect(x: 512 - 240, y: 500 - 240, width: 480, height: 480)
    let tilePath = CGPath(roundedRect: tile, cornerWidth: 92, cornerHeight: 92, transform: nil)

    // "Document lines": faint pills peeking out from behind the tile on both sides,
    // standing in for SysMonitor's chip pins.
    ctx.setFillColor(color(0xFFFFFF, 0.26))
    let lineH: CGFloat = 34
    let leftLines: [(y: CGFloat, w: CGFloat)] = [(0.80, 96), (0.60, 70), (0.40, 96), (0.20, 58)]
    let rightLines: [(y: CGFloat, w: CGFloat)] = [(0.80, 58), (0.60, 96), (0.40, 70), (0.20, 96)]
    for l in leftLines {
        let r = CGRect(x: tile.minX - 14 - l.w, y: tile.minY + l.y * tile.height - lineH / 2, width: l.w, height: lineH)
        ctx.addPath(CGPath(roundedRect: r, cornerWidth: lineH / 2, cornerHeight: lineH / 2, transform: nil))
    }
    for l in rightLines {
        let r = CGRect(x: tile.maxX + 14, y: tile.minY + l.y * tile.height - lineH / 2, width: l.w, height: lineH)
        ctx.addPath(CGPath(roundedRect: r, cornerWidth: lineH / 2, cornerHeight: lineH / 2, transform: nil))
    }
    ctx.fillPath()

    ctx.saveGState()
    ctx.addPath(tilePath)
    ctx.clip()
    let frost = CGGradient(colorsSpace: space, colors: [color(0xFFFFFF, 0.20), color(0xFFFFFF, 0.06)] as CFArray,
                           locations: [0, 1])!
    ctx.drawLinearGradient(frost, start: CGPoint(x: 512, y: tile.maxY), end: CGPoint(x: 512, y: tile.minY), options: [])
    ctx.restoreGState()
    ctx.addPath(tilePath)
    ctx.setStrokeColor(color(0xFFFFFF, 0.45))
    ctx.setLineWidth(9)
    ctx.strokePath()

    // The mark: "M" + down arrow, one continuous stroke style like SysMonitor's pulse line.
    let top: CGFloat = 626, bottom: CGFloat = 374           // 252pt tall, centred on y = 500
    // Horizontal layout (stroke half-width 16): ink spans 323…701, centred on x = 512,
    // with ~30pt of air between the M's right stem and the arrow head.
    let mLeft: CGFloat = 339, mRight: CGFloat = 519
    let mark = CGMutablePath()
    mark.move(to: CGPoint(x: mLeft, y: bottom))
    mark.addLine(to: CGPoint(x: mLeft, y: top))
    mark.addLine(to: CGPoint(x: (mLeft + mRight) / 2, y: 468))
    mark.addLine(to: CGPoint(x: mRight, y: top))
    mark.addLine(to: CGPoint(x: mRight, y: bottom))

    let ax: CGFloat = 633                                    // arrow shaft
    mark.move(to: CGPoint(x: ax, y: top))
    mark.addLine(to: CGPoint(x: ax, y: bottom))
    mark.move(to: CGPoint(x: ax - 52, y: bottom + 62))
    mark.addLine(to: CGPoint(x: ax, y: bottom))
    mark.addLine(to: CGPoint(x: ax + 52, y: bottom + 62))

    let stroked = mark.copy(strokingWithWidth: 32, lineCap: .round, lineJoin: .round, miterLimit: 10)
    let markGradient = gradient([(0x45E3FF, 0), (0x8F7BFF, 0.5), (0xFF5FB0, 1)])

    // Glow under the line
    ctx.saveGState()
    ctx.setShadow(offset: .zero, blur: 46, color: color(0x5CD6FF, 0.9))
    ctx.addPath(stroked)
    ctx.setFillColor(color(0x8F7BFF))
    ctx.fillPath()
    ctx.restoreGState()

    ctx.saveGState()
    ctx.addPath(stroked)
    ctx.clip()
    ctx.drawLinearGradient(markGradient, start: CGPoint(x: mLeft - 16, y: 0), end: CGPoint(x: ax + 68, y: 0), options: [])
    ctx.restoreGState()

    // Glass highlight on the upper half and a thin rim
    let gloss = CGGradient(colorsSpace: space, colors: [color(0xFFFFFF, 0.16), color(0xFFFFFF, 0)] as CFArray,
                           locations: [0, 1])!
    ctx.drawLinearGradient(gloss, start: CGPoint(x: 512, y: 924), end: CGPoint(x: 512, y: 560), options: [])
    ctx.restoreGState()

    ctx.addPath(squircle)
    ctx.setStrokeColor(color(0xFFFFFF, 0.14))
    ctx.setLineWidth(4)
    ctx.strokePath()
}

func png(size: Int) -> Data {
    let rep = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: size, pixelsHigh: size, bitsPerSample: 8,
                               samplesPerPixel: 4, hasAlpha: true, isPlanar: false,
                               colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0)!
    let gc = NSGraphicsContext(bitmapImageRep: rep)!
    let ctx = gc.cgContext
    ctx.interpolationQuality = .high
    ctx.scaleBy(x: CGFloat(size) / canvas, y: CGFloat(size) / canvas)
    drawIcon(in: ctx)
    gc.flushGraphics()
    return rep.representation(using: .png, properties: [:])!
}

let outDir = CommandLine.arguments.count > 1 ? CommandLine.arguments[1] : "Resources"
let fm = FileManager.default
let iconset = URL(fileURLWithPath: NSTemporaryDirectory()).appendingPathComponent("MDEditorAppIcon.iconset")
try? fm.removeItem(at: iconset)
try fm.createDirectory(at: iconset, withIntermediateDirectories: true)
for base in [16, 32, 128, 256, 512] {
    try png(size: base).write(to: iconset.appendingPathComponent("icon_\(base)x\(base).png"))
    try png(size: base * 2).write(to: iconset.appendingPathComponent("icon_\(base)x\(base)@2x.png"))
}
try fm.createDirectory(atPath: outDir, withIntermediateDirectories: true)
try png(size: 1024).write(to: URL(fileURLWithPath: "\(outDir)/AppIcon.png"))

let p = Process()
p.executableURL = URL(fileURLWithPath: "/usr/bin/iconutil")
p.arguments = ["-c", "icns", iconset.path, "-o", "\(outDir)/AppIcon.icns"]
try p.run()
p.waitUntilExit()
try? fm.removeItem(at: iconset)
print(p.terminationStatus == 0 ? "Wrote \(outDir)/AppIcon.icns and AppIcon.png" : "iconutil failed")
