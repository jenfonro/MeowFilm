import Foundation

public enum HorizontalPagerTopology {
    public static func unique(_ ids: [String]) -> [String] {
        var seen = Set<String>()
        return ids.filter { seen.insert($0).inserted }
    }
    public static func neighbor(_ id: String, step: Int, ids: [String], wraps: Bool) -> String? {
        guard ids.count > 1, step == 1 || step == -1, let index = ids.firstIndex(of: id) else { return nil }
        let next = index + step
        guard wraps || ids.indices.contains(next) else { return nil }
        return ids[(next + ids.count) % ids.count]
    }
    public static func forward(from: String?, to: String, ids: [String], wraps: Bool) -> Bool {
        guard let from, let old = ids.firstIndex(of: from), let next = ids.firstIndex(of: to) else { return true }
        let forward = next > old
        return wraps && abs(next - old) > ids.count / 2 ? !forward : forward
    }
}
