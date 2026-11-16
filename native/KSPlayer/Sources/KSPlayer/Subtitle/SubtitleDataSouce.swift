//
//  SubtitleDataSouce.swift
//  KSPlayer-7de52535
//
//  Created by kintan on 2018/8/7.
//
import Foundation

public class EmptySubtitleInfo: SubtitleInfo {
    public var isEnabled: Bool = true
    public let subtitleID: String = ""
    public var delay: TimeInterval = 0
    public let name = NSLocalizedString("no show subtitle", comment: "")
    public func search(for _: TimeInterval) -> [SubtitlePart] {
        []
    }
}

public class URLSubtitleInfo: KSSubtitle, SubtitleInfo {
    public var isEnabled: Bool = false {
        didSet {
            if isEnabled, parts.isEmpty {
                Task {
                    try? await parse(url: downloadURL, userAgent: userAgent)
                }
            }
        }
    }

    public private(set) var downloadURL: URL
    public var delay: TimeInterval = 0
    public private(set) var name: String
    public let subtitleID: String
    public var comment: String?
    public var userInfo: NSMutableDictionary?
    private let userAgent: String?
    public convenience init(url: URL) {
        self.init(subtitleID: url.absoluteString, name: url.lastPathComponent, url: url)
    }

    public init(subtitleID: String, name: String, url: URL, userAgent: String? = nil) {
        self.subtitleID = subtitleID
        self.name = name
        self.userAgent = userAgent
        downloadURL = url
        super.init()
        if !url.isFileURL, name.isEmpty {
            url.download(userAgent: userAgent) { [weak self] filename, tmpUrl in
                guard let self else {
                    return
                }
                self.name = filename
                self.downloadURL = tmpUrl
                var fileURL = URL(fileURLWithPath: NSTemporaryDirectory())
                fileURL.appendPathComponent(filename)
                try? FileManager.default.moveItem(at: tmpUrl, to: fileURL)
                self.downloadURL = fileURL
            }
        }
    }
}

public protocol SubtitleDataSouce: AnyObject {
    var infos: [any SubtitleInfo] { get }
}

public protocol FileURLSubtitleDataSouce: SubtitleDataSouce {
    func searchSubtitle(fileURL: URL?) async throws
}

public protocol CacheSubtitleDataSouce: FileURLSubtitleDataSouce {
    func addCache(fileURL: URL, downloadURL: URL)
}

public protocol SearchSubtitleDataSouce: SubtitleDataSouce {
    func searchSubtitle(query: String?, languages: [String]) async throws
}
