#if os(iOS)
import UIKit
import CoreImage
import ImageIO
import UniformTypeIdentifiers

/// Uses the same CoreImage QR encoder as the native music providers. Image
/// challenges remain images: their bytes are decoded according to the real type.
enum PanQRCode {
    static func image(_ payload: PanLoginChallenge.Payload) throws -> UIImage {
        switch payload {
        case .url(let text):
            guard let filter = CIFilter(name: "CIQRCodeGenerator") else { throw PanAccountError.invalidQRCode }
            filter.setValue(Data(text.utf8), forKey: "inputMessage")
            filter.setValue("M", forKey: "inputCorrectionLevel")
            guard let code = filter.outputImage else { throw PanAccountError.invalidQRCode }
            let background = CIImage(color: CIColor(red: 1, green: 1, blue: 1))
                .cropped(to: code.extent.insetBy(dx: -4, dy: -4))
            let padded = code.composited(over: background).transformed(by: CGAffineTransform(scaleX: 6, y: 6))
            guard let image = CIContext().createCGImage(padded, from: padded.extent) else {
                throw PanAccountError.invalidQRCode
            }
            return UIImage(cgImage: image)
        case .image(let bytes, _):
            guard let source = CGImageSourceCreateWithData(bytes as CFData, nil),
                  let identifier = CGImageSourceGetType(source), let type = UTType(identifier as String),
                  type.conforms(to: .image),
                  let image = CGImageSourceCreateImageAtIndex(source, 0, nil) else {
                throw PanAccountError.invalidQRCode
            }
            return UIImage(cgImage: image)
        }
    }
}
#endif
