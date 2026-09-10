import Foundation
import ServiceManagement

/// "Launch at login", backed by SMAppService.
///
/// The system tracks this per app bundle, so it is read back from the service
/// rather than stored in preferences - that keeps the checkbox honest if the
/// user turns the login item off in System Settings instead.
enum LoginItem {
    static var status: SMAppService.Status { SMAppService.mainApp.status }

    static var isEnabled: Bool { status == .enabled }

    /// macOS can park a newly registered login item behind user approval.
    static var needsApproval: Bool { status == .requiresApproval }

    static func set(_ enabled: Bool) throws {
        if enabled {
            try SMAppService.mainApp.register()
        } else {
            try SMAppService.mainApp.unregister()
        }
    }

    /// Deep link to the Login Items pane, for the approval case.
    static func openLoginItemsSettings() {
        SMAppService.openSystemSettingsLoginItems()
    }

    /// Registration fails for an app that isn't in a stable location, which is
    /// worth saying plainly rather than leaving a checkbox that won't stick.
    static func explain(_ error: Error) -> String {
        let bundlePath = Foundation.Bundle.main.bundlePath
        if !bundlePath.hasPrefix("/Applications/") {
            return "Couldn't set launch at login: move the app to /Applications first (it's at \(bundlePath))."
        }
        return "Couldn't set launch at login: \(error.localizedDescription)"
    }
}
