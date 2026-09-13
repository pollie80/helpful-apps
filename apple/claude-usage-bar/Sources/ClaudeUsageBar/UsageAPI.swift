import Foundation

// MARK: - Wire types
//
// Shape of GET https://api.anthropic.com/api/oauth/usage — the endpoint Claude
// Code's own /usage panel reads.
//
// The response carries per-window objects at the top level (five_hour,
// seven_day, and a set of server-side model buckets), plus a `limits` array
// that is the display list the panel actually renders: one entry per bar, each
// already carrying an integer `percent`, a `resets_at`, and — for per-model
// windows — a server-supplied `display_name`. We drive the UI off `limits` so
// new model buckets appear without a client change, and fall back to the fixed
// top-level windows only if `limits` is ever absent.

/// One entry of the `limits` array.
struct LimitEntry: Decodable {
    struct Scope: Decodable {
        struct Model: Decodable {
            var id: String?
            var displayName: String?
            enum CodingKeys: String, CodingKey {
                case id
                case displayName = "display_name"
            }
        }
        var model: Model?
        var surface: String?
    }

    /// "session", "weekly_all", "weekly_scoped", … — unknown kinds are kept.
    var kind: String?
    /// "session" or "weekly".
    var group: String?
    var percent: Double?
    /// "normal", or a server-raised level when you're close to the cap.
    var severity: String?
    var resetsAt: Date?
    var scope: Scope?
    /// True for the window currently binding your requests.
    var isActive: Bool?

    enum CodingKeys: String, CodingKey {
        case kind, group, percent, severity, scope
        case resetsAt = "resets_at"
        case isActive = "is_active"
    }

    init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        kind = try c.decodeIfPresent(String.self, forKey: .kind)
        group = try c.decodeIfPresent(String.self, forKey: .group)
        percent = try c.decodeIfPresent(Double.self, forKey: .percent)
        severity = try c.decodeIfPresent(String.self, forKey: .severity)
        scope = try c.decodeIfPresent(Scope.self, forKey: .scope)
        isActive = try c.decodeIfPresent(Bool.self, forKey: .isActive)
        if let raw = try c.decodeIfPresent(String.self, forKey: .resetsAt) {
            resetsAt = FlexibleISO8601.date(from: raw)
        }
    }
}

/// A top-level per-window object, used only as a fallback.
struct RateLimitWindow: Decodable {
    var utilization: Double?
    var resetsAt: Date?
    var lockedReason: String?

    enum CodingKeys: String, CodingKey {
        case utilization
        case resetsAt = "resets_at"
        case lockedReason = "locked_reason"
    }

    init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        utilization = try c.decodeIfPresent(Double.self, forKey: .utilization)
        lockedReason = try c.decodeIfPresent(String.self, forKey: .lockedReason)
        if let raw = try c.decodeIfPresent(String.self, forKey: .resetsAt) {
            resetsAt = FlexibleISO8601.date(from: raw)
        }
    }
}

struct ExtraUsage: Decodable {
    var isEnabled: Bool?
    var monthlyLimit: Double?
    var usedCredits: Double?
    var utilization: Double?
    var currency: String?

    enum CodingKeys: String, CodingKey {
        case isEnabled = "is_enabled"
        case monthlyLimit = "monthly_limit"
        case usedCredits = "used_credits"
        case utilization
        case currency
    }
}

struct UsageResponse: Decodable {
    var limits: [LimitEntry]?
    var fiveHour: RateLimitWindow?
    var sevenDay: RateLimitWindow?
    var extraUsage: ExtraUsage?

    enum CodingKeys: String, CodingKey {
        case limits
        case fiveHour = "five_hour"
        case sevenDay = "seven_day"
        case extraUsage = "extra_usage"
    }
}

// MARK: - A flattened, display-ready view

/// One row in the popover: a labelled window with a percentage and a reset time.
struct UsageBar: Identifiable, Equatable {
    /// Stable key used to persist the menu bar selection across launches.
    let id: String
    let label: String
    let percent: Double?
    let resetsAt: Date?
    /// Server-raised severity, when it is something other than "normal".
    let severity: String?
    /// The window currently binding your requests.
    let isActive: Bool

    var wholePercent: Int? { percent.map { Int($0.rounded()) } }
}

struct UsageSnapshot {
    var subscriptionType: String?
    var bars: [UsageBar]
    var extraUsage: ExtraUsage?
    var fetchedAt: Date

    init(response: UsageResponse, subscriptionType: String? = nil, fetchedAt: Date = Date()) {
        self.subscriptionType = subscriptionType
        self.extraUsage = response.extraUsage
        self.fetchedAt = fetchedAt

        if let entries = response.limits, !entries.isEmpty {
            self.bars = Self.rows(from: entries)
        } else {
            self.bars = Self.fallbackRows(from: response)
        }
    }

    private static func rows(from entries: [LimitEntry]) -> [UsageBar] {
        var seen = Set<String>()
        return entries.compactMap { entry -> UsageBar? in
            guard entry.percent != nil else { return nil }
            let (id, label) = identify(entry)
            // Guard against a duplicate id breaking the picker's selection.
            guard seen.insert(id).inserted else { return nil }
            return UsageBar(id: id,
                            label: label,
                            percent: entry.percent,
                            resetsAt: entry.resetsAt,
                            severity: entry.severity == "normal" ? nil : entry.severity,
                            isActive: entry.isActive ?? false)
        }
    }

    /// Map a limit entry onto a stable id and the label the /usage panel uses.
    private static func identify(_ entry: LimitEntry) -> (id: String, label: String) {
        let modelName = entry.scope?.model?.displayName
        let surface = entry.scope?.surface

        switch entry.kind {
        case "session":
            return ("session", "5-hour limit")
        case "weekly_all":
            return ("weekly_all", "Weekly · all models")
        case "weekly_scoped":
            // Scoped weekly windows are named by the server, e.g. "Fable".
            let name = modelName ?? surface ?? "scoped"
            return ("weekly_scoped:\(name)", "Weekly · \(name)")
        default:
            // An unrecognised kind still gets a row rather than vanishing.
            let kind = entry.kind ?? "limit"
            let suffix = modelName ?? surface
            let label = suffix.map { "\(prettify(kind)) · \($0)" } ?? prettify(kind)
            return (suffix.map { "\(kind):\($0)" } ?? kind, label)
        }
    }

    /// "weekly_all" -> "Weekly all", for kinds this build hasn't seen before.
    private static func prettify(_ kind: String) -> String {
        let words = kind.split(separator: "_").map(String.init)
        guard let first = words.first else { return kind }
        return ([first.capitalized] + words.dropFirst()).joined(separator: " ")
    }

    /// Used only if the server stops sending `limits`.
    private static func fallbackRows(from response: UsageResponse) -> [UsageBar] {
        var rows: [UsageBar] = []
        if let w = response.fiveHour, let u = w.utilization {
            rows.append(UsageBar(id: "session", label: "5-hour limit", percent: u,
                                 resetsAt: w.resetsAt, severity: nil, isActive: false))
        }
        if let w = response.sevenDay, let u = w.utilization {
            rows.append(UsageBar(id: "weekly_all", label: "Weekly · all models", percent: u,
                                 resetsAt: w.resetsAt, severity: nil, isActive: false))
        }
        return rows
    }
}

// MARK: - Client

enum UsageError: LocalizedError {
    case notAuthenticated(String)
    case tokenExpired
    case http(Int, String)
    case decode(String)
    case noWindows

    var errorDescription: String? {
        switch self {
        case .notAuthenticated(let detail):
            return "Not signed in: \(detail)"
        case .tokenExpired:
            return "Waiting for Claude Code to refresh its token - use Claude Code once and this updates on the next poll."
        case .http(let code, let body):
            return "Usage request failed (HTTP \(code)): \(body.prefix(200))"
        case .decode(let detail):
            return "Could not read the usage response: \(detail)"
        case .noWindows:
            return "No plan limits reported for this account — subscription limits may not apply (API key or third-party provider)."
        }
    }
}

actor UsageClient {
    static let shared = UsageClient()

    private static let usageURL = URL(string: "https://api.anthropic.com/api/oauth/usage?at_wall=1&skip_spend=1")!
    private static let betaHeader = "oauth-2025-04-20"

    func fetch() async throws -> UsageSnapshot {
        let token = try readAccessToken()
        let (status, data) = try await request(token: token)

        // Never refresh: that would rotate the token out from under Claude Code.
        // An expired token resolves itself the next time Claude Code is used.
        if status == 401 {
            throw UsageError.tokenExpired
        }

        guard status == 200 else {
            throw UsageError.http(status, String(data: data, encoding: .utf8) ?? "")
        }

        let decoded: UsageResponse
        do {
            decoded = try JSONDecoder().decode(UsageResponse.self, from: data)
        } catch {
            throw UsageError.decode(String(describing: error))
        }

        // The plan name isn't in this response, but it is in the stored credential.
        let plan = try? Keychain.read().claudeAiOauth.subscriptionType
        let snapshot = UsageSnapshot(response: decoded, subscriptionType: plan)
        guard !snapshot.bars.isEmpty else { throw UsageError.noWindows }
        return snapshot
    }

    private func request(token: String) async throws -> (Int, Data) {
        var req = URLRequest(url: Self.usageURL)
        req.httpMethod = "GET"
        req.setValue("Bearer \(token)", forHTTPHeaderField: "Authorization")
        req.setValue(Self.betaHeader, forHTTPHeaderField: "anthropic-beta")
        req.setValue("application/json", forHTTPHeaderField: "Content-Type")
        req.cachePolicy = .reloadIgnoringLocalCacheData
        req.timeoutInterval = 20

        let (data, response) = try await URLSession.shared.data(for: req)
        let status = (response as? HTTPURLResponse)?.statusCode ?? 0
        return (status, data)
    }

    // MARK: Token handling

    /// Read the stored token. This app never refreshes and never writes - see
    /// the note in Keychain.swift for why that matters.
    private func readAccessToken() throws -> String {
        let creds: OAuthCredentials
        do {
            creds = try Keychain.read()
        } catch {
            throw UsageError.notAuthenticated(error.localizedDescription)
        }

        let bundle = creds.claudeAiOauth
        // A token expiring within the next minute counts as expired.
        let expired = bundle.expiresAt.map { $0 / 1000 <= Date().timeIntervalSince1970 + 60 } ?? false
        if expired { throw UsageError.tokenExpired }
        return bundle.accessToken
    }
}

/// Timestamp parsing.
///
/// The API emits microsecond precision and a numeric offset — e.g.
/// "2026-09-11T01:49:59.639034+00:00". ISO8601DateFormatter's fractional-seconds
/// option only accepts exactly 3 digits, so the fraction is dropped before
/// parsing (sub-second precision is meaningless for a reset countdown).
enum FlexibleISO8601 {
    private static let plain: ISO8601DateFormatter = {
        let f = ISO8601DateFormatter()
        f.formatOptions = [.withInternetDateTime]
        return f
    }()

    private static let withFraction: ISO8601DateFormatter = {
        let f = ISO8601DateFormatter()
        f.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        return f
    }()

    static func date(from string: String) -> Date? {
        if let d = plain.date(from: string) { return d }
        if let d = withFraction.date(from: string) { return d }
        if let stripped = dropFractionalSeconds(string),
           let d = plain.date(from: stripped) { return d }
        return nil
    }

    /// "…:59.639034+00:00" -> "…:59+00:00"
    private static func dropFractionalSeconds(_ s: String) -> String? {
        guard let dot = s.firstIndex(of: ".") else { return nil }
        // The fraction runs to the first character that isn't a digit.
        guard let end = s[s.index(after: dot)...].firstIndex(where: { !$0.isNumber }) else {
            return nil
        }
        return String(s[s.startIndex..<dot]) + String(s[end...])
    }
}
