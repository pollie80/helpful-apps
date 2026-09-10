import SwiftUI

@main
struct ClaudeUsageBarApp: App {
    @StateObject private var state = AppState()

    var body: some Scene {
        MenuBarExtra {
            UsagePopover(state: state)
        } label: {
            HStack(spacing: 3) {
                Image(systemName: "gauge.with.dots.needle.33percent")
                Text(state.menuBarTitle)
                    .monospacedDigit()
            }
            .foregroundStyle(state.menuBarTint ?? .primary)
            .task { state.start() }
        }
        .menuBarExtraStyle(.window)
    }
}
