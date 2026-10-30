#if os(iOS)
import UIKit
import SwiftUI
import MeowUI

struct MusicLibraryImportState: Equatable {
    var available = false
    var importing = false
    var busy = false
    var owner = ""
    var preview: MusicImportReport?
}

@MainActor
public struct MusicLibraryImportContent: View {
    var state = MusicLibraryImportState()
    var owner: Binding<String> = .constant("")
    var theme: PageTheme
    var chooseFile: () -> Void = {}
    var preview: () -> Void = {}
    var confirm: () -> Void = {}
    var cancel: () -> Void = {}
    var history: () -> Void = {}

    public var body: some View {
        Form {
            Section {
                if state.importing {
                    TextField("原曲库所有者 ID", text: owner)
                        .textInputAutocapitalization(.never)
                        .autocorrectionDisabled()
                        .accessibilityLabel("原曲库所有者 ID")
                        .disabled(!state.available || state.busy || state.preview != nil)
                    HStack {
                        if state.preview == nil {
                            Button("预检", action: preview)
                                .disabled(!state.available || state.busy || state.owner.isEmpty)
                        } else {
                            Button("确认导入", action: confirm)
                                .disabled(!state.available || state.busy)
                        }
                        Spacer()
                        Button("取消", role: .cancel, action: cancel)
                    }
                    .buttonStyle(.borderless)
                } else {
                    Button("选择曲库文件", action: chooseFile)
                        .disabled(!state.available || state.busy)
                }
                if state.busy {
                    ProgressView(state.importing ? "正在导入" : "正在读取")
                }
                if let value = state.preview {
                    Text("歌曲 \(value.songs) · 歌单 \(value.collections) · 成员 \(value.members)")
                }
            }
            .listRowBackground(Color(uiColor: UIColor(theme.surface)).opacity(0.58))

            Section {
                Button("导入记录", action: history)
                    .disabled(!state.available)
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
