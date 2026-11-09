#!/usr/bin/env swift
// Renders the MDEditor app icon and writes AppIcon.png + AppIcon.icns.
//
// The icon is the app's own layout in one picture: a squircle split down the middle, source on the left
// (ink) and rendered result on the right (paper), with one big "M" across the seam. The "M" is white on the
// ink half and ink on the paper half, so it reads as the same text before and after rendering. One idea, one
// shape: it stays legible at 16 px, like the other Markdown editors' icons (a single bold glyph, no ornament).
//
// Usage (from the repo root):
//   swift scripts/make-icon.swift            # writes Resources/AppIcon.{png,icns}
//   swift scripts/make-icon.swift <outdir>   # writes them into <outdir> instead
//
// The Windows icon (windows/scripts/make-ico.ps1) is cut from Resources/AppIcon.png: re-run that after this.
import AppKit

let canvas: CGFloat = 1024
let space = CGColorSpaceCreateDeviceRGB()

func color(_ hex: UInt32, _ a: CGFloat = 1) -> CGColor {
    CGColor(red: CGFloat((hex >> 16) & 0xff) / 255, green: CGFloat((hex >> 8) & 0xff) / 255,
            blue: CGFloat(hex & 0xff) / 255, alpha: a)
}

func gradient(_ stops: [(UInt32, CGFloat)]) -> CGGradient {
    CGGradient(colorsSpace: space, colors: stops.map { color($0.0) } as CFArray, locations: stops.map { $0.1 })!
}

/// A superellipse (exponent 5) is a close match for the continuous-corner shape of macOS icons.
func squircle(_ r: CGRect, exponent n: CGFloat = 5) -> CGPath {
    let path = CGMutablePath()
    let steps = 720
    for i in 0...steps {
        let t = CGFloat(i) / CGFloat(steps) * 2 * .pi
        let c = cos(t), s = sin(t)
        let x = r.midX + r.width / 2 * (c < 0 ? -1 : 1) * pow(abs(c), 2 / n)
        let y = r.midY + r.height / 2 * (s < 0 ? -1 : 1) * pow(abs(s), 2 / n)
        if i == 0 { path.move(to: CGPoint(x: x, y: y)) } else { path.addLine(to: CGPoint(x: x, y: y)) }
    }
    path.closeSubpath()
    return path
}

func drawIcon(in ctx: CGContext) {
    // macOS icon grid: an 824pt body centred on a 1024pt canvas.
    let body = CGRect(x: 100, y: 100, width: 824, height: 824)
    let shape = squircle(body)

    // Drop shadow
    ctx.saveGState()
    ctx.setShadow(offset: CGSize(width: 0, height: -14), blur: 30, color: color(0x000000, 0.38))
    ctx.addPath(shape)
    ctx.setFillColor(color(0x151826))
    ctx.fillPath()
    ctx.restoreGState()

    ctx.saveGState()
    ctx.addPath(shape)
    ctx.clip()

    // Source half (left, ink) and rendered half (right, paper).
    let seam = canvas / 2
    let source = CGRect(x: 0, y: 0, width: seam, height: canvas)
    let rendered = CGRect(x: seam, y: 0, width: canvas - seam, height: canvas)
    func paint(_ rect: CGRect, _ stops: [(UInt32, CGFloat)]) {
        ctx.saveGState()
        ctx.clip(to: rect)
        ctx.drawLinearGradient(gradient(stops), start: CGPoint(x: 0, y: 924), end: CGPoint(x: 0, y: 100), options: [])
        ctx.restoreGState()
    }
    paint(source, [(0x2A2F4A, 0), (0x161A2E, 0.55), (0x0D0F1C, 1)])
    paint(rendered, [(0xFFFFFF, 0), (0xF1F3FA, 0.5), (0xDDE2F0, 1)])

    // The "M", centred so the seam runs through its axis of symmetry (the point of the V).
    let top: CGFloat = 690, bottom: CGFloat = 334
    let left: CGFloat = 288, right: CGFloat = 736
    let m = CGMutablePath()
    m.move(to: CGPoint(x: left, y: bottom))
    m.addLine(to: CGPoint(x: left, y: top))
    m.addLine(to: CGPoint(x: seam, y: 468))
    m.addLine(to: CGPoint(x: right, y: top))
    m.addLine(to: CGPoint(x: right, y: bottom))
    let glyph = m.copy(strokingWithWidth: 84, lineCap: .round, lineJoin: .round, miterLimit: 10)

    func fill(_ c: CGColor, in rect: CGRect) {
        ctx.saveGState()
        ctx.clip(to: rect)
        ctx.setShadow(offset: CGSize(width: 0, height: -10), blur: 18, color: color(0x000000, 0.28))
        ctx.addPath(glyph)
        ctx.setFillColor(c)
        ctx.fillPath()
        ctx.restoreGState()
    }
    fill(color(0xFFFFFF), in: source)
    fill(color(0x161A2E), in: rendered)
    ctx.restoreGState()

    // Thin rim: light on the ink half, dark on the paper half so the edge survives a white taskbar or Dock.
    func rim(_ c: CGColor, in rect: CGRect) {
        ctx.saveGState()
        ctx.clip(to: rect)
        ctx.addPath(shape)
        ctx.setStrokeColor(c)
        ctx.setLineWidth(4)
        ctx.strokePath()
        ctx.restoreGState()
    }
    rim(color(0xFFFFFF, 0.16), in: source)
    rim(color(0x161A2E, 0.22), in: rendered)
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
