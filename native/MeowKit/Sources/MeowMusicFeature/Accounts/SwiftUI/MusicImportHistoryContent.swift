#if os(iOS)
import UIKit
import SwiftUI
import MeowUI

struct MusicImportHistoryState: Equatable {
    var snapshot = MusicImportHistorySnapshot.empty
    var available = false
    var busy = false
}

@MainActor
public struct MusicImportHistoryContent: View {
    var state = MusicImportHistoryState()
    var theme: PageTheme
    var rollback: () -> Void = {}

    public var body: some View {
        Form {
            Section {
                switch state.snapshot {
                case .empty:
                    if !state.busy { Text("暂无导入记录") }
                case .imported(let batch, let date, let canRollback):
                    Text("批次 \(batch)")
                    Text(date.formatted(date: .abbreviated, time: .shortened))
                        .foregroundStyle(Color(uiColor: UIColor(theme.muted)))
                    if canRollback {
                        Button("回滚导入", action: rollback)
                            .disabled(!state.available || state.busy)
                    }
                case .rolledBack(let batch):
                    Text("批次 \(batch) 已回滚")
                }
                if state.busy {
                    ProgressView(state.snapshot == .empty ? "正在读取" : "正在处理")
                }
            }
            .listRowBackground(Color(uiColor: UIColor(theme.surface)).opacity(0.58))
        }
        .scrollContentBackground(.hidden)
        .background(Color.clear)
        .foregroundStyle(Color(uiColor: UIColor(theme.ink)))
        .tint(Color(uiColor: UIColor(theme.accent)))
    }
}
#endif
