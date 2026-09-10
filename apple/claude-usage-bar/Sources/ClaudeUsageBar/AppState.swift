import Foundation
import SwiftUI

/// Poll cadence, user-selectable from the popover.
enum RefreshInterval: Int, CaseIterable, Identifiable {
    case thirtySeconds = 30
    case oneMinute = 60
    case fiveMinutes = 300
    case fifteenMinutes = 900

    var id: Int { rawValue }

    var label: String {
        switch self {
        case .thirtySeconds: return "30 seconds"
        case .oneMinute: return "1 minute"
        case .fiveMinutes: return "5 minutes"
        case .fifteenMinutes: return "15 minutes"
        }
    }
}

@MainActor
final class AppState: ObservableObject {
    @Published private(set) var snapshot: UsageSnapshot?
    @Published private(set) var errorText: String?
    @Published private(set) var isRefreshing = false

    /// Mirrors the system's login-item state; see `syncLoginItemState()`.
    @Published private(set) var launchAtLogin = LoginItem.isEnabled
    @Published private(set) var loginItemNeedsApproval = LoginItem.needsApproval
    @Published private(set) var loginItemError: String?

    /// Which bar's percentage is rendered as the menu bar title.
    @Published var selectedBarID: String {
        didSet { UserDefaults.standard.set(selectedBarID, forKey: Keys.selectedBar) }
    }

    @Published var interval: RefreshInterval {
        didSet {
            UserDefaults.standard.set(interval.rawValue, forKey: Keys.interval)
            restartTimer()
        }
    }

    private enum Keys {
        static let selectedBar = "selectedBarID"
        static let interval = "refreshIntervalSeconds"
    }

    private var timer: Timer?

    init() {
        let defaults = UserDefaults.standard
        selectedBarID = defaults.string(forKey: Keys.selectedBar) ?? "weekly_all"
        interval = RefreshInterval(rawValue: defaults.integer(forKey: Keys.interval)) ?? .oneMinute
    }

    // MARK: Menu bar title

    /// The bar currently driving the menu bar title, falling back to the first
    /// available row when the remembered selection isn't in this response.
    var selectedBar: UsageBar? {
        guard let bars = snapshot?.bars, !bars.isEmpty else { return nil }
        return bars.first { $0.id == selectedBarID } ?? bars.first
    }

    var menuBarTitle: String {
        if snapshot == nil && errorText != nil { return "—" }
        guard let percent = selectedBar?.wholePercent else { return "··" }
        return "\(percent)%"
    }

    /// The server's own severity wins when it raises one; otherwise amber past
    /// 75% and red past 90%, matching the thresholds Claude Code warns at.
    var menuBarTint: Color? {
        guard let bar = selectedBar else { return nil }
        if let severity = bar.severity {
            return severity == "warning" ? .orange : .red
        }
        guard let percent = bar.wholePercent else { return nil }
        if percent >= 90 { return .red }
        if percent >= 75 { return .orange }
        return nil
    }

    // MARK: Polling

    func start() {
        restartTimer()
        Task { await refresh() }
    }

    private func restartTimer() {
        timer?.invalidate()
        let t = Timer(timeInterval: TimeInterval(interval.rawValue), repeats: true) { [weak self] _ in
            Task { @MainActor in await self?.refresh() }
        }
        // .common lets the timer keep firing while a menu is open.
        RunLoop.main.add(t, forMode: .common)
        timer = t
    }

    func refresh() async {
        guard !isRefreshing else { return }
        isRefreshing = true
        defer { isRefreshing = false }

        do {
            let fresh = try await UsageClient.shared.fetch()
            snapshot = fresh
            errorText = nil
        } catch {
            errorText = error.localizedDescription
        }
    }

    // MARK: Launch at login

    /// Read back from the system on every popover open, so the checkbox stays
    /// truthful if the login item is changed in System Settings instead.
    func syncLoginItemState() {
        launchAtLogin = LoginItem.isEnabled
        loginItemNeedsApproval = LoginItem.needsApproval
    }

    func setLaunchAtLogin(_ enabled: Bool) {
        do {
            try LoginItem.set(enabled)
            loginItemError = nil
        } catch {
            loginItemError = LoginItem.explain(error)
        }
        syncLoginItemState()
    }
}
