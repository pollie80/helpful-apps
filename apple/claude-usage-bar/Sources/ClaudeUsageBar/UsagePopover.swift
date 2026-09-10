import AppKit
import Combine
import SwiftUI

struct UsagePopover: View {
    @ObservedObject var state: AppState
    /// Drives the live "resets in …" countdowns between polls.
    @State private var now = Date()

    private let ticker = Timer.publish(every: 30, on: .main, in: .common).autoconnect()

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            header

            Divider().padding(.vertical, 8)

            if let error = state.errorText, state.snapshot == nil {
                errorView(error)
            } else if let bars = state.snapshot?.bars, !bars.isEmpty {
                VStack(alignment: .leading, spacing: 14) {
                    ForEach(bars) { bar in
                        BarRow(bar: bar,
                               now: now,
                               isSelected: bar.id == state.selectedBar?.id)
                            .contentShape(Rectangle())
                            .onTapGesture { state.selectedBarID = bar.id }
                    }
                }
                if let extra = state.snapshot?.extraUsage, extra.isEnabled == true {
                    Divider().padding(.vertical, 8)
                    extraUsageRow(extra)
                }
            } else {
                Text("Loading usage…")
                    .font(.callout)
                    .foregroundStyle(.secondary)
                    .frame(maxWidth: .infinity, alignment: .center)
                    .padding(.vertical, 12)
            }

            // A failure after an earlier success would otherwise show stale
            // numbers with no hint that they've stopped updating.
            if let error = state.errorText, state.snapshot != nil {
                Label(error, systemImage: "exclamationmark.triangle.fill")
                    .font(.system(size: 10))
                    .foregroundStyle(.orange)
                    .padding(.top, 8)
                    .fixedSize(horizontal: false, vertical: true)
            }

            Divider().padding(.vertical, 8)

            footer
        }
        .padding(14)
        .frame(width: 320)
        .onReceive(ticker) { now = $0 }
        .onAppear { state.syncLoginItemState() }
    }

    // MARK: Sections

    private var header: some View {
        HStack(alignment: .firstTextBaseline) {
            Text("Your usage limits")
                .font(.system(size: 12, weight: .semibold))
                .foregroundStyle(.secondary)
            if let plan = state.snapshot?.subscriptionType {
                Text("· \(plan.capitalized)")
                    .font(.system(size: 12))
                    .foregroundStyle(.secondary)
            }
            Spacer()
            if state.isRefreshing {
                ProgressView().controlSize(.small)
            }
        }
    }

    private func errorView(_ error: String) -> some View {
        VStack(alignment: .leading, spacing: 6) {
            Label("Couldn't read usage", systemImage: "exclamationmark.triangle.fill")
                .font(.system(size: 12, weight: .semibold))
                .foregroundStyle(.orange)
            Text(error)
                .font(.system(size: 11))
                .foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)
        }
    }

    private func extraUsageRow(_ extra: ExtraUsage) -> some View {
        HStack {
            Text("Extra usage")
                .font(.system(size: 12))
            Spacer()
            if let used = extra.usedCredits, let limit = extra.monthlyLimit {
                Text("\(Self.money(used, extra.currency)) / \(Self.money(limit, extra.currency))")
                    .font(.system(size: 12))
                    .foregroundStyle(.secondary)
                    .monospacedDigit()
            }
        }
    }

    private var footer: some View {
        VStack(alignment: .leading, spacing: 8) {
            HStack {
                Text("Menu bar shows")
                    .font(.system(size: 11))
                    .foregroundStyle(.secondary)
                Spacer()
                Picker("", selection: $state.selectedBarID) {
                    ForEach(state.snapshot?.bars ?? []) { bar in
                        Text(bar.label).tag(bar.id)
                    }
                }
                .labelsHidden()
                .frame(maxWidth: 165)
            }

            HStack {
                Text("Refresh every")
                    .font(.system(size: 11))
                    .foregroundStyle(.secondary)
                Spacer()
                Picker("", selection: $state.interval) {
                    ForEach(RefreshInterval.allCases) { option in
                        Text(option.label).tag(option)
                    }
                }
                .labelsHidden()
                .frame(maxWidth: 165)
            }

            Toggle("Launch at login", isOn: Binding(
                get: { state.launchAtLogin },
                set: { state.setLaunchAtLogin($0) }
            ))
            .font(.system(size: 11))
            .toggleStyle(.checkbox)

            if state.loginItemNeedsApproval {
                Button("Approve in System Settings…") {
                    LoginItem.openLoginItemsSettings()
                }
                .controlSize(.small)
                .font(.system(size: 10))
            }

            if let error = state.loginItemError {
                Text(error)
                    .font(.system(size: 10))
                    .foregroundStyle(.orange)
                    .fixedSize(horizontal: false, vertical: true)
            }

            HStack {
                Button("Refresh now") {
                    Task { await state.refresh() }
                }
                .controlSize(.small)

                Spacer()

                if let at = state.snapshot?.fetchedAt {
                    Text("Updated \(at.formatted(date: .omitted, time: .shortened))")
                        .font(.system(size: 10))
                        .foregroundStyle(.tertiary)
                }

                Button("Quit") { NSApplication.shared.terminate(nil) }
                    .controlSize(.small)
            }
        }
    }

    private static func money(_ value: Double, _ currency: String?) -> String {
        let f = NumberFormatter()
        f.numberStyle = .currency
        f.currencyCode = currency ?? "USD"
        f.maximumFractionDigits = value < 10 ? 2 : 0
        return f.string(from: value as NSNumber) ?? String(format: "%.2f", value)
    }
}

// MARK: - One window row

private struct BarRow: View {
    let bar: UsageBar
    let now: Date
    let isSelected: Bool

    var body: some View {
        VStack(alignment: .leading, spacing: 5) {
            HStack(alignment: .firstTextBaseline, spacing: 6) {
                Text(bar.label)
                    .font(.system(size: 12, weight: isSelected ? .semibold : .regular))
                if isSelected {
                    Image(systemName: "menubar.arrow.up.rectangle")
                        .font(.system(size: 9))
                        .foregroundStyle(.tertiary)
                }
                Spacer(minLength: 8)
                if let resets = bar.resetsAt {
                    Text("Resets in \(Self.relative(resets, from: now))")
                        .font(.system(size: 11))
                        .foregroundStyle(.secondary)
                }
                Text(bar.wholePercent.map { "\($0)%" } ?? "—")
                    .font(.system(size: 12, weight: .medium))
                    .monospacedDigit()
                    .frame(minWidth: 34, alignment: .trailing)
            }

            GeometryReader { geo in
                let fraction = min(max((bar.percent ?? 0) / 100, 0), 1)
                ZStack(alignment: .leading) {
                    Capsule()
                        .fill(Color.primary.opacity(0.12))
                    Capsule()
                        .fill(Self.tint(for: bar))
                        .frame(width: max(geo.size.width * fraction, fraction > 0 ? 3 : 0))
                }
            }
            .frame(height: 4)
        }
    }

    private static func tint(for bar: UsageBar) -> Color {
        if let severity = bar.severity { return severity == "warning" ? .orange : .red }
        guard let percent = bar.wholePercent else { return .secondary }
        if percent >= 90 { return .red }
        if percent >= 75 { return .orange }
        return .accentColor
    }

    /// "4 hr 50 min" / "17 hr" / "3 d 4 hr", matching the /usage panel's phrasing.
    private static func relative(_ date: Date, from now: Date) -> String {
        let seconds = Int(date.timeIntervalSince(now))
        guard seconds > 0 else { return "moments" }

        let minutes = seconds / 60
        let days = minutes / (60 * 24)
        let hours = (minutes / 60) % 24
        let mins = minutes % 60

        if days > 0 { return hours > 0 ? "\(days) d \(hours) hr" : "\(days) d" }
        if minutes >= 60 { return mins > 0 ? "\(minutes / 60) hr \(mins) min" : "\(minutes / 60) hr" }
        return "\(max(mins, 1)) min"
    }
}
