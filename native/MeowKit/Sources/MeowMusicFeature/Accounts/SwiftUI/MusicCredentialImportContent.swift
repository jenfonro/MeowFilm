#if os(iOS)
import UIKit
import SwiftUI
import MeowUI

struct MusicCredentialImportState: Equatable {
    var platform = MusicAccountPlatform.qq
    var format = MusicCredentialImportFormat.iwebPlusSnapshot
    var formats = MusicCredentialImportFormat.allCases
    var preview: MusicCredentialImportPreview?
    var completed: String?
    var available = false
    var busy = false
}

@MainActor
final class MusicCredentialImportDraft: ObservableObject {
    @Published var text = ""
}

@MainActor
public struct MusicCredentialImportContent: View {
    var state = MusicCredentialImportState()
    @ObservedObject var draft: MusicCredentialImportDraft
    var theme: PageTheme
    var selectPlatform: (MusicAccountPlatform) -> Void = { _ in }
    var selectFormat: (MusicCredentialImportFormat) -> Void = { _ in }
    var chooseFile: () -> Void = {}
    var preview: () -> Void = {}
    var commit: () -> Void = {}

    public var body: some View {
        Form {
            Section {
                Picker("平台", selection: Binding(get: { state.platform }, set: { selectPlatform($0) })) {
                    ForEach(MusicAccountPlatform.allCases) { platform in
                        Text(platform.title).tag(platform)
                    }
                }
                .pickerStyle(.segmented)
                .disabled(!state.available)
                Picker("格式", selection: Binding(get: { state.format }, set: { selectFormat($0) })) {
                    ForEach(state.formats) { format in
                        Text(format.title).tag(format)
                    }
                }
                .pickerStyle(.menu)
                .disabled(!state.available)
                SecureField("凭据文本", text: $draft.text)
                    .textContentType(nil)
                    .textInputAutocapitalization(.never)
                    .autocorrectionDisabled()
                    .privacySensitive()
                    .accessibilityLabel("凭据文本")
                    .accessibilityIdentifier("music.credentials.input")
                    .disabled(!state.available || state.busy)
            }
            .listRowBackground(Color(uiColor: UIColor(theme.surface)).opacity(0.58))

            Section {
                HStack {
                    Button("选择文件", action: chooseFile)
                        .disabled(!state.available || state.busy)
                    Spacer()
                    Button("预览", action: preview)
                        .disabled(!state.available || state.busy || draft.text.isEmpty)
                    if let value = state.preview {
                        Spacer()
                        Button(value.requiresReplacement ? "替换会话" : "导入", action: commit)
                            .disabled(!state.available || state.busy)
                    }
                }
                .buttonStyle(.borderless)
                if state.busy {
                    ProgressView("正在处理")
                }
            }
            .listRowBackground(Color(uiColor: UIColor(theme.surface)).opacity(0.58))

            if let completed = state.completed {
                Section {
                    Text(completed)
                }
                .listRowBackground(Color(uiColor: UIColor(theme.surface)).opacity(0.58))
            } else if let value = state.preview {
                Section {
                    Text([
                        value.platform.title,
                        value.deviceState.title,
                        value.requiresReplacement ? "已有会话" : nil,
                    ].compactMap { $0 }.joined(separator: " · "))
                }
                .listRowBackground(Color(uiColor: UIColor(theme.surface)).opacity(0.58))
            }
        }
        .scrollContentBackground(.hidden)
        .background(Color.clear)
        .foregroundStyle(Color(uiColor: UIColor(theme.ink)))
        .tint(Color(uiColor: UIColor(theme.accent)))
    }
}
#endif
