// Rasterizes RoboExp.svg (AppKit's native SVG support) into the PNG/ICO assets.
// usage: swift render-icons.swift <RoboExp.svg> <iconset dir> <out.ico> <out-256.png>
import AppKit

let args = CommandLine.arguments
guard args.count == 5 else {
    FileHandle.standardError.write("usage: render-icons.swift <svg> <iconset dir> <out.ico> <out-256.png>\n".data(using: .utf8)!)
    exit(2)
}
let (svgPath, iconsetDir, icoPath, pngPath) = (args[1], args[2], args[3], args[4])

guard let image = NSImage(contentsOfFile: svgPath) else {
    FileHandle.standardError.write("cannot load \(svgPath)\n".data(using: .utf8)!)
    exit(1)
}

func png(_ size: Int) -> Data {
    let rep = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: size, pixelsHigh: size, bitsPerSample: 8,
                               samplesPerPixel: 4, hasAlpha: true, isPlanar: false, colorSpaceName: .deviceRGB,
                               bytesPerRow: 0, bitsPerPixel: 0)!
    rep.size = NSSize(width: size, height: size)
    NSGraphicsContext.saveGraphicsState()
    let ctx = NSGraphicsContext(bitmapImageRep: rep)!
    ctx.imageInterpolation = .high
    NSGraphicsContext.current = ctx
    NSColor.clear.set()
    NSRect(x: 0, y: 0, width: size, height: size).fill()
    image.draw(in: NSRect(x: 0, y: 0, width: size, height: size), from: .zero, operation: .sourceOver, fraction: 1)
    NSGraphicsContext.restoreGraphicsState()
    return rep.representation(using: .png, properties: [:])!
}

func write(_ data: Data, _ path: String) {
    try! data.write(to: URL(fileURLWithPath: path))
}

// macOS iconset: icon_<pt>x<pt>[@2x].png
try! FileManager.default.createDirectory(atPath: iconsetDir, withIntermediateDirectories: true)
for pt in [16, 32, 128, 256, 512] {
    write(png(pt), "\(iconsetDir)/icon_\(pt)x\(pt).png")
    write(png(pt * 2), "\(iconsetDir)/icon_\(pt)x\(pt)@2x.png")
}
write(png(256), pngPath)

// Windows ICO: ICONDIR + ICONDIRENTRY[] + PNG payloads (PNG entries are valid since Windows Vista).
let icoSizes = [16, 24, 32, 48, 64, 128, 256]
let payloads = icoSizes.map(png)
var ico = Data()
func u16(_ v: Int) { ico.append(contentsOf: [UInt8(v & 0xff), UInt8((v >> 8) & 0xff)]) }
func u32(_ v: Int) { u16(v & 0xffff); u16((v >> 16) & 0xffff) }
u16(0); u16(1); u16(icoSizes.count)
var offset = 6 + 16 * icoSizes.count
for (size, data) in zip(icoSizes, payloads) {
    ico.append(UInt8(size >= 256 ? 0 : size)) // width (0 = 256)
    ico.append(UInt8(size >= 256 ? 0 : size)) // height
    ico.append(0); ico.append(0)              // palette, reserved
    u16(1); u16(32)                            // planes, bpp
    u32(data.count); u32(offset)
    offset += data.count
}
for data in payloads { ico.append(data) }
write(ico, icoPath)
