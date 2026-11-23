public struct FilterTag: Equatable, Sendable {
    public let id: String
    public let title: String
    public let group: String
    public let isSelected: Bool?

    public init(id: String, title: String, group: String = "", isSelected: Bool? = nil) {
        self.id = id
        self.title = title
        self.group = group
        self.isSelected = isSelected
    }
}
