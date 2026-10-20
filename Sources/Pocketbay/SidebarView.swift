import AppKit

/// Vertical tool strip on the right edge, like MuMu's.
final class SidebarView: NSView {
    enum Action: CaseIterable {
        case back, home, recents, volumeUp, volumeDown, mic, rotate, screenshot, installApk, keymapToggle, keymapEdit, fullscreen

        var symbol: String {
            switch self {
            case .back: return "chevron.backward"
            case .home: return "house"
            case .recents: return "square.on.square"
            case .volumeUp: return "speaker.wave.3"
            case .volumeDown: return "speaker.wave.1"
            case .rotate: return "rotate.right"
            case .screenshot: return "camera"
            case .installApk: return "arrow.down.app"
            case .keymapToggle: return "keyboard"
            case .keymapEdit: return "slider.horizontal.3"
            case .mic: return "mic"
            case .fullscreen: return "arrow.up.left.and.arrow.down.right"
            }
        }

        var tip: String {
            switch self {
            case .back: return "Back  (Esc)"
            case .home: return "Home  (⇧⌘H)"
            case .recents: return "Recent apps  (⇧⌘R)"
            case .volumeUp: return "Volume up"
            case .volumeDown: return "Volume down"
            case .rotate: return "Rotate screen"
            case .screenshot: return "Screenshot to Desktop  (⇧⌘S)"
            case .installApk: return "Install an app (.apk) — or drag one onto the window"
            case .keymapToggle: return "Game controls on/off  (⌘K)"
            case .keymapEdit: return "Edit game controls  (⌘E)"
            case .mic: return "Microphone on/off (for in-game voice chat)"
            case .fullscreen: return "Full screen  (⌃⌘F)"
            }
        }
    }

    static let width: CGFloat = 52
    var onAction: ((Action) -> Void)?
    private var buttons: [Action: NSButton] = [:]

    override init(frame: NSRect) {
        super.init(frame: frame)
        wantsLayer = true
        layer?.backgroundColor = NSColor(white: 0.11, alpha: 1).cgColor

        func button(_ a: Action) -> NSButton {
            let config = NSImage.SymbolConfiguration(pointSize: 16, weight: .medium)
            let img = NSImage(systemSymbolName: a.symbol, accessibilityDescription: a.tip)!.withSymbolConfiguration(config)!
            let b = NSButton(image: img, target: nil, action: nil)
            b.isBordered = false
            b.contentTintColor = NSColor(white: 0.85, alpha: 1)
            b.toolTip = a.tip
            b.setAccessibilityLabel(a.tip)
            b.widthAnchor.constraint(equalToConstant: 36).isActive = true
            b.heightAnchor.constraint(equalToConstant: 34).isActive = true
            b.onClick { [weak self] in self?.onAction?(a) }
            buttons[a] = b
            return b
        }
        func divider() -> NSView {
            let v = NSBox()
            v.boxType = .separator
            v.widthAnchor.constraint(equalToConstant: 28).isActive = true
            return v
        }

        let top = NSStackView(views: [
            button(.back), button(.home), button(.recents), divider(),
            button(.volumeUp), button(.volumeDown), button(.mic), button(.rotate), button(.screenshot), button(.installApk), divider(),
            button(.keymapToggle), button(.keymapEdit),
        ])
        top.orientation = .vertical
        top.spacing = 4
        let bottom = button(.fullscreen)

        for v in [top, bottom] {
            v.translatesAutoresizingMaskIntoConstraints = false
            addSubview(v)
        }
        NSLayoutConstraint.activate([
            top.centerXAnchor.constraint(equalTo: centerXAnchor),
            top.topAnchor.constraint(equalTo: topAnchor, constant: 10),
            bottom.centerXAnchor.constraint(equalTo: centerXAnchor),
            bottom.bottomAnchor.constraint(equalTo: bottomAnchor, constant: -10),
        ])
    }

    required init?(coder: NSCoder) { fatalError() }

    /// Highlights a toggle button (e.g. game controls on).
    func setOn(_ action: Action, _ on: Bool) {
        buttons[action]?.contentTintColor = on ? .controlAccentColor : NSColor(white: 0.85, alpha: 1)
    }
}
