import AppKit
import UniformTypeIdentifiers

final class MainWindowController: NSWindowController, NSWindowDelegate, NSMenuDelegate {
    let manager = EmulatorManager()
    private(set) var client: EmulatorClient?
    let engine = KeymapEngine(keymap: KeymapStore.active())

    private let container = ContainerView()
    let screen = ScreenView()
    private let sidebar = SidebarView()
    private let status = StatusView()
    private let toast = ToastView()

    private var isFullScreen = false
    private var foregroundPackage: String?

    var keymapEnabled: Bool {
        get { UserDefaults.standard.object(forKey: "keymapEnabled") as? Bool ?? true }
        set { UserDefaults.standard.set(newValue, forKey: "keymapEnabled"); updateKeymapState() }
    }
    var showKeyHints: Bool {
        get { UserDefaults.standard.object(forKey: "showKeyHints") as? Bool ?? true }
        set { UserDefaults.standard.set(newValue, forKey: "showKeyHints"); updateKeymapState() }
    }
    var isEditingKeymap: Bool { screen.overlay.mode == .edit }

    init() {
        let window = NSWindow(contentRect: CGRect(x: 0, y: 0, width: 1280 + SidebarView.width, height: 720),
                              styleMask: [.titled, .closable, .miniaturizable, .resizable],
                              backing: .buffered, defer: false)
        window.title = "Pocketbay"
        window.appearance = NSAppearance(named: .darkAqua)
        window.titlebarAppearsTransparent = true
        window.backgroundColor = NSColor(white: 0.11, alpha: 1)
        window.collectionBehavior = [.fullScreenPrimary]
        window.acceptsMouseMovedEvents = true
        window.minSize = CGSize(width: 480, height: 300)
        window.setFrameAutosaveName("PocketbayMain")
        if !window.setFrameUsingName("PocketbayMain") { window.center() }
        super.init(window: window)
        window.delegate = self

        container.frame = window.contentView!.bounds
        container.autoresizingMask = [.width, .height]
        container.onLayout = { [weak self] in self?.layoutViews() }
        container.onMouseMoved = { [weak self] p in self?.revealSidebarIfNeeded(p) }
        for v in [screen, sidebar, status, toast] as [NSView] { container.addSubview(v) }
        window.contentView = container

        screen.engine = engine
        engine.toNative = { [unowned screen] p in screen.toNative(p) }
        screen.overlay.keymap = engine.keymap
        screen.overlay.onChange = { [weak self] map in
            self?.engine.keymap = map
            KeymapStore.save(map)
        }
        screen.overlay.onDone = { [weak self] in self?.setEditing(false) }
        screen.overlay.toolbarAccessory = layoutPopup
        layoutsMenu.delegate = self
        rebuildLayoutPopup()
        screen.onFirstFrame = { [weak self] in self?.status.isHidden = true }
        screen.onDeviceSizeChanged = { [weak self] size in self?.deviceSizeChanged(size) }
        screen.onFilesDropped = { [weak self] urls in self?.install(urls) }
        screen.onPaste = { [weak self] in self?.pasteFromMac() }
        engine.onAimingChanged = { [weak self] on in
            guard let self else { return }
            if on { self.setSidebarRevealed(false) }
            self.screen.setCursorCaptured(on)
            self.screen.overlay.isAiming = on
            if on { self.toast.show("Mouse aim on — press \(self.aimKeyName) or Esc to get the cursor back") }
        }
        sidebar.onAction = { [weak self] a in self?.perform(a) }
        updateKeymapState()
        status.show("Starting Android…", spinning: true)
        fitWindowToScreen()
    }

    required init?(coder: NSCoder) { fatalError() }

    private var aimKeyName: String {
        engine.keymap.controls.first { $0.type == .aim }?.key?.capLabel ?? "the aim key"
    }

    // MARK: Startup

    private var sessionTasks: [Task<Void, Never>] = []
    private var isQuitting = false

    func start() {
        Task { @MainActor in await runSession() }
    }

    /// Boots (or attaches to) Android, wires up screen + input, and restarts
    /// everything if the emulator goes away unexpectedly.
    @MainActor private func runSession() async {
        status.show("Starting Android…", spinning: true)
        do {
            let endpoint = try await manager.start()
            let client = try EmulatorClient(port: endpoint.port, token: endpoint.token)
            self.client = client
            screen.client = client
            engine.client = client

            var booted = false
            for _ in 0..<360 where manager.isRunning {
                if await client.isBooted() { booted = true; break }
                try await Task.sleep(for: .milliseconds(500))
            }
            if booted {
                await manager.applyDeviceDefaults()
                client.startInputStream()
                screen.resetForNewSession()
                sessionTasks = [streamScreen(client), pollForegroundApp(), syncClipboardToMac(client), streamAudio(client)]
                // Mic auto-start disabled (see toggleMic).
            } else if manager.isRunning {
                throw NSError(domain: "Pocketbay", code: 12, userInfo: [NSLocalizedDescriptionKey: "Android took too long to start."])
            }

            // Watchdog: if Android stops without us asking, bring it back.
            while manager.isRunning { try await Task.sleep(for: .seconds(1)) }
            guard !isQuitting else { return }
            sessionTasks.forEach { $0.cancel() }
            stopMic()
            screen.releaseAllInput()
            screen.client = nil
            engine.client = nil
            self.client = nil
            await runSession()
        } catch {
            status.show(error.localizedDescription, spinning: false)
        }
    }

    /// Android's sound, played by Pocketbay so it follows the Mac's output device.
    private let audio = AudioPlayer()

    // MARK: Microphone (Mac input → Android's virtual mic)

    private let mic = MicCapture()
    private var micFeed: AsyncStream<Data>.Continuation?
    var micEnabled: Bool {
        get { UserDefaults.standard.bool(forKey: "micEnabled") }
        set { UserDefaults.standard.set(newValue, forKey: "micEnabled") }
    }

    func toggleMic() {
        // Injecting mic audio crashes the emulator while it runs with "-audio none"
        // (QemuAudioInputEngine segfaults in audio_forwarder_enable). Disabled until fixed.
        micEnabled = false
        toast.show("Microphone isn't available yet — it crashed Android, so it's switched off for now", duration: 6)
        if true { return }
        if micEnabled {
            micEnabled = false
            stopMic()
            toast.show("Microphone off")
            return
        }
        Task { @MainActor in
            guard await MicCapture.requestAccess() else {
                toast.show("Allow Pocketbay in System Settings › Privacy & Security › Microphone, then try again", duration: 7)
                return
            }
            micEnabled = true
            startMic()
            toast.show("Microphone on — turn on voice chat in the game")
        }
    }

    private func startMic() {
        stopMic()
        guard let client else { sidebar.setOn(.mic, micEnabled); return }
        let (stream, continuation) = AsyncStream<Data>.makeStream(bufferingPolicy: .bufferingNewest(8))
        micFeed = continuation
        do {
            try mic.start { continuation.yield($0) }
        } catch {
            micFeed = nil
            toast.show("Microphone: \(error.localizedDescription)", duration: 5)
            return
        }
        sidebar.setOn(.mic, true)
        Task.detached { [weak self] in
            do { try await client.injectMicrophone(stream) }
            catch { NSLog("Pocketbay: microphone stream ended: %@", "\(error)") }
            await MainActor.run { if self?.micFeed != nil { self?.stopMic() } }
        }
    }

    private func stopMic() {
        mic.stop()
        micFeed?.finish()
        micFeed = nil
        sidebar.setOn(.mic, false)
    }

    private func streamAudio(_ client: EmulatorClient) -> Task<Void, Never> {
        let audio = self.audio
        return Task.detached {
            while !Task.isCancelled {
                do { try await client.streamAudio { pcm in audio.enqueue(pcm) } }
                catch { NSLog("Pocketbay: audio stream ended: %@", "\(error)") }
                try? await Task.sleep(for: .seconds(1))
            }
        }
    }

    private func streamScreen(_ client: EmulatorClient) -> Task<Void, Never> {
        let screen = self.screen
        return Task.detached {
            while !Task.isCancelled {
                do { try await client.streamScreen { frame in screen.frameArrived(frame) } }
                catch { NSLog("Pocketbay: screen stream ended: %@", "\(error)") }
                try? await Task.sleep(for: .seconds(1))   // stream ended (e.g. emulator busy); reconnect
            }
        }
    }

    // MARK: Clipboard

    /// Last text both sides agree on, so our own writes don't echo back.
    private var lastClipboard: String?

    /// Android → Mac: anything copied in Android lands on the Mac clipboard.
    private func syncClipboardToMac(_ client: EmulatorClient) -> Task<Void, Never> {
        Task { @MainActor in
            lastClipboard = await client.clipboard()   // don't push Android's old clipboard on launch
            while !Task.isCancelled {
                try? await client.streamClipboard { text in
                    DispatchQueue.main.async { self.androidCopied(text) }
                }
                try? await Task.sleep(for: .seconds(1))
            }
        }
    }

    private func androidCopied(_ text: String) {
        guard !text.isEmpty, text != lastClipboard else { return }
        lastClipboard = text
        NSPasteboard.general.clearContents()
        NSPasteboard.general.setString(text, forType: .string)
        toast.show("Copied to your Mac clipboard")
    }

    /// Mac → Android happens only on ⌘V, so Android apps can't read the Mac
    /// clipboard in the background.
    private func pasteFromMac() {
        guard let client, let text = NSPasteboard.general.string(forType: .string), !text.isEmpty else { return }
        Task { @MainActor in
            if text != lastClipboard {
                lastClipboard = text
                await client.setClipboard(text)
            }
            screen.sendControlShortcut(9)   // Ctrl+V
        }
    }

    private func pollForegroundApp() -> Task<Void, Never> {
        Task { @MainActor in
            while !Task.isCancelled {
                let pkg = await manager.foregroundPackage()
                if pkg != foregroundPackage {
                    foregroundPackage = pkg
                    updateKeymapState()
                }
                try? await Task.sleep(for: .milliseconds(1500))
            }
        }
    }

    // MARK: Keymap state

    /// Game controls apply when enabled and the mapped app is in front.
    private var keymapAppliesToForeground: Bool {
        let pkgs = engine.keymap.packages
        return pkgs.isEmpty || foregroundPackage.map(pkgs.contains) == true
    }

    func updateKeymapState() {
        let editing = isEditingKeymap
        let wasActive = engine.isActive
        engine.isActive = keymapEnabled && keymapAppliesToForeground && !editing
        if engine.isActive && !wasActive && !editing {
            toast.show("Game controls on — press \(aimKeyName) to aim with the mouse, Esc to get the cursor back", duration: 5)
        }
        if !editing {
            screen.overlay.mode = engine.isActive && showKeyHints ? .play : .hidden
        }
        sidebar.setOn(.keymapToggle, keymapEnabled)
        sidebar.setOn(.keymapEdit, editing)
    }

    func setEditing(_ on: Bool) {
        if on {
            engine.releaseAll()
            screen.overlay.keymap = engine.keymap
            screen.overlay.mode = .edit
            window?.makeFirstResponder(screen.overlay)
            toast.show("Editing game controls — Android won't respond until you click Done (or press Esc)", duration: 5)
        } else {
            screen.overlay.mode = .hidden
            window?.makeFirstResponder(screen)
            toast.show("Game controls saved")
        }
        updateKeymapState()
    }

    // MARK: Layouts (saved key mappings)

    /// Picker shown in the editor toolbar.
    private lazy var layoutPopup: NSPopUpButton = {
        let p = NSPopUpButton(frame: .zero, pullsDown: false)
        p.bezelStyle = .accessoryBarAction
        p.toolTip = "Switch, create, rename or delete layouts"
        return p
    }()

    /// Controls ▸ Layout submenu (filled on demand).
    let layoutsMenu = NSMenu(title: "Layout")

    func menuNeedsUpdate(_ menu: NSMenu) {
        if menu === layoutsMenu { populateLayoutMenu(menu) }
    }

    private func populateLayoutMenu(_ menu: NSMenu) {
        menu.removeAllItems()
        for map in KeymapStore.all() {
            let item = NSMenuItem(title: map.name, action: #selector(chooseLayout(_:)), keyEquivalent: "")
            item.target = self
            item.representedObject = map.id
            item.state = map.id == engine.keymap.id ? .on : .off
            menu.addItem(item)
        }
        menu.addItem(.separator())
        for (title, sel) in [("New Layout", #selector(newLayout)), ("Duplicate Layout", #selector(duplicateLayout)),
                             ("Rename Layout…", #selector(renameLayout)), ("Delete Layout…", #selector(deleteLayout))] {
            let item = NSMenuItem(title: title, action: sel, keyEquivalent: "")
            item.target = self
            menu.addItem(item)
        }
    }

    private func rebuildLayoutPopup() {
        populateLayoutMenu(layoutPopup.menu!)
        let index = layoutPopup.menu!.items.firstIndex { ($0.representedObject as? UUID) == engine.keymap.id } ?? 0
        layoutPopup.selectItem(at: index)
        layoutPopup.sizeToFit()
    }

    private func activate(_ map: Keymap, announce: Bool = true) {
        KeymapStore.setActive(map.id)
        engine.keymap = map
        screen.overlay.keymap = map
        rebuildLayoutPopup()
        updateKeymapState()
        if announce { toast.show("Layout: \(map.name)") }
    }

    @objc private func chooseLayout(_ sender: NSMenuItem) {
        guard let id = sender.representedObject as? UUID,
              let map = KeymapStore.all().first(where: { $0.id == id }) else { return }
        activate(map)
    }

    private func uniqueName(_ base: String) -> String {
        let names = Set(KeymapStore.all().map(\.name))
        if !names.contains(base) { return base }
        var n = 2
        while names.contains("\(base) \(n)") { n += 1 }
        return "\(base) \(n)"
    }

    @objc private func newLayout() {
        var map = Keymap.bgmi
        map.id = UUID()
        map.name = uniqueName("New Layout")
        KeymapStore.save(map)
        activate(map)
    }

    @objc private func duplicateLayout() {
        var map = engine.keymap
        map.id = UUID()
        map.name = uniqueName("\(engine.keymap.name) copy")
        KeymapStore.save(map)
        activate(map)
    }

    @objc private func renameLayout() {
        guard let window else { return }
        let alert = NSAlert()
        alert.messageText = "Rename layout"
        let field = NSTextField(string: engine.keymap.name)
        field.frame = CGRect(x: 0, y: 0, width: 240, height: 24)
        alert.accessoryView = field
        alert.addButton(withTitle: "Rename")
        alert.addButton(withTitle: "Cancel")
        alert.window.initialFirstResponder = field
        alert.beginSheetModal(for: window) { [weak self] resp in
            guard let self, resp == .alertFirstButtonReturn else { return }
            let name = field.stringValue.trimmingCharacters(in: .whitespaces)
            guard !name.isEmpty else { return }
            var map = self.engine.keymap
            map.name = name
            KeymapStore.save(map)
            self.activate(map, announce: false)
        }
    }

    @objc private func deleteLayout() {
        guard let window else { return }
        let maps = KeymapStore.all()
        guard maps.count > 1 else { toast.show("Keep at least one layout"); return }
        let alert = NSAlert()
        alert.messageText = "Delete “\(engine.keymap.name)”?"
        alert.informativeText = "This layout's key mapping will be removed."
        alert.addButton(withTitle: "Delete").hasDestructiveAction = true
        alert.addButton(withTitle: "Cancel")
        alert.beginSheetModal(for: window) { [weak self] resp in
            guard let self, resp == .alertFirstButtonReturn else { return }
            let doomed = self.engine.keymap.id
            KeymapStore.delete(doomed)
            if let next = KeymapStore.all().first { self.activate(next) }
        }
    }

    // MARK: Actions

    func perform(_ action: SidebarView.Action) {
        guard let client else { return }
        switch action {
        case .back: client.press(key: "GoBack")
        case .home: client.press(key: "GoHome")
        case .recents: client.press(key: "AppSwitch")
        case .volumeUp: client.press(key: "AudioVolumeUp")
        case .volumeDown: client.press(key: "AudioVolumeDown")
        case .rotate:
            // Always a further quarter turn clockwise: 0° → 90° → 180° → 270° → 0°.
            let next = (screen.quarterTurns + 1) % 4
            Task { await client.setRotation(zDegrees: Float(next * 90)) }
        case .screenshot: saveScreenshot()
        case .installApk: chooseApk()
        case .keymapToggle:
            keymapEnabled.toggle()
            toast.show(keymapEnabled ? "Game controls on (in \(engine.keymap.name))" : "Game controls off")
        case .keymapEdit: setEditing(!isEditingKeymap)
        case .mic: toggleMic()
        case .fullscreen: window?.toggleFullScreen(nil)
        }
    }

    private func saveScreenshot() {
        guard let image = screen.snapshotImage() else { return }
        let f = DateFormatter()
        f.dateFormat = "yyyy-MM-dd 'at' HH.mm.ss"
        let url = FileManager.default.urls(for: .desktopDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("Pocketbay \(f.string(from: Date())).png")
        let rep = NSBitmapImageRep(cgImage: image)
        if let data = rep.representation(using: .png, properties: [:]), (try? data.write(to: url)) != nil {
            toast.show("Screenshot saved to Desktop")
        }
    }

    private func chooseApk() {
        let panel = NSOpenPanel()
        panel.allowedContentTypes = [UTType(filenameExtension: "apk") ?? .data]
        panel.allowsMultipleSelection = true
        panel.message = "Choose Android apps (.apk) to install"
        panel.beginSheetModal(for: window!) { [weak self] resp in
            if resp == .OK { self?.install(panel.urls) }
        }
    }

    func install(_ urls: [URL]) {
        Task { @MainActor in
            for url in urls {
                toast.show("Installing \(url.deletingPathExtension().lastPathComponent)…", duration: 60)
                let result = await manager.install(apk: url)
                toast.show(result.message)
            }
        }
    }

    // MARK: Layout

    private var sidebarDocked: Bool { !isFullScreen }

    private func layoutViews() {
        let b = container.bounds
        let sw = SidebarView.width
        if sidebarDocked {
            screen.frame = CGRect(x: 0, y: 0, width: b.width - sw, height: b.height)
            sidebar.frame = CGRect(x: b.width - sw, y: 0, width: sw, height: b.height)
            sidebar.alphaValue = 1
            sidebar.isHidden = false
        } else {
            screen.frame = b
            sidebar.frame = CGRect(x: b.width - sw, y: 0, width: sw, height: b.height)
        }
        status.frame = screen.frame
        toast.frame = CGRect(x: screen.frame.minX, y: 24, width: screen.frame.width, height: 40)
    }

    private var titlebarHeight: CGFloat {
        guard let window else { return 0 }
        return window.frame.height - window.contentLayoutRect.height
    }

    private var sidebarRevealed = false

    /// Full screen: the sidebar appears only when the pointer hits the very right edge,
    /// stays while the pointer is over it, and is truly hidden otherwise so it never
    /// catches clicks meant for game buttons near the edge.
    private func revealSidebarIfNeeded(_ p: CGPoint) {
        guard !sidebarDocked, !engine.isAiming else { return }
        let w = container.bounds.width
        let show = sidebarRevealed ? p.x >= w - SidebarView.width - 12 : p.x >= w - 2
        setSidebarRevealed(show)
    }

    private func setSidebarRevealed(_ show: Bool) {
        guard show != sidebarRevealed else { return }
        sidebarRevealed = show
        if show { sidebar.isHidden = false }
        NSAnimationContext.runAnimationGroup({ ctx in
            ctx.duration = 0.15
            sidebar.animator().alphaValue = show ? 1 : 0
        }, completionHandler: { [weak self] in
            guard let self, !self.sidebarRevealed, !self.sidebarDocked else { return }
            self.sidebar.isHidden = true
        })
    }

    /// A window frame that keeps the Android picture's aspect ratio (sidebar and titlebar
    /// excluded) and always fits the visible screen: if the height would overflow, the
    /// width shrinks instead. Keeps the top edge where it was when possible.
    private func fittedFrame(width proposedWidth: CGFloat, anchor: NSRect, aspect: CGFloat? = nil) -> NSRect {
        guard let window, let visible = (window.screen ?? NSScreen.main)?.visibleFrame else { return anchor }
        let aspect = aspect ?? screen.deviceSize.width / screen.deviceSize.height
        let chrome = titlebarHeight
        var w = min(proposedWidth, visible.width)
        var h = ((w - SidebarView.width) / aspect).rounded() + chrome
        if h > visible.height {
            h = visible.height
            w = ((h - chrome) * aspect).rounded() + SidebarView.width
        }
        var f = NSRect(x: anchor.minX, y: anchor.maxY - h, width: w, height: h)
        f.origin.x = min(max(f.minX, visible.minX), visible.maxX - w)
        f.origin.y = min(max(f.minY, visible.minY), visible.maxY - h)
        return f.integral
    }

    /// Pulls a restored or stale frame back onto the screen at the right shape.
    private func fitWindowToScreen() {
        guard let window, !isFullScreen else { return }
        window.setFrame(fittedFrame(width: window.frame.width, anchor: window.frame), display: true)
    }

    func windowWillResize(_ sender: NSWindow, to frameSize: NSSize) -> NSSize {
        guard !isFullScreen else { return frameSize }
        return fittedFrame(width: frameSize.width, anchor: sender.frame).size
    }

    /// Green-button zoom: the biggest right-shaped window that fits, centred.
    func windowWillUseStandardFrame(_ window: NSWindow, defaultFrame newFrame: NSRect) -> NSRect {
        var f = fittedFrame(width: newFrame.width, anchor: newFrame)
        f.origin.x = newFrame.midX - f.width / 2
        return f.integral
    }

    private func deviceSizeChanged(_ size: CGSize) {
        guard let window, !isFullScreen else { return }
        let aspect = size.width / size.height
        let contentHeight = window.frame.height - titlebarHeight
        let frame = fittedFrame(width: contentHeight * aspect + SidebarView.width, anchor: window.frame, aspect: aspect)
        window.setFrame(frame, display: true, animate: true)
    }

    func windowWillEnterFullScreen(_ notification: Notification) {
        isFullScreen = true
        sidebarRevealed = false
        sidebar.alphaValue = 0
        sidebar.isHidden = true
        container.needsLayout = true
        toast.show("Move the pointer to the very right edge to show the sidebar", duration: 4)
    }

    func windowDidExitFullScreen(_ notification: Notification) {
        isFullScreen = false
        container.needsLayout = true
    }

    func windowDidResignKey(_ notification: Notification) {
        screen.releaseAllInput()
    }

    func windowDidBecomeKey(_ notification: Notification) {
        window?.makeFirstResponder(isEditingKeymap ? screen.overlay : screen)
    }

    func windowShouldClose(_ sender: NSWindow) -> Bool {
        NSApp.terminate(nil)
        return false
    }

    /// Saves Android's state and stops it; used on quit.
    func shutdown() async {
        isQuitting = true
        screen.releaseAllInput()
        status.isHidden = false
        status.show("Saving and closing Android…", spinning: true)
        await manager.shutdown()
    }
}

// MARK: - Small views

/// Content view that reports layout and mouse movement (for the full-screen sidebar).
final class ContainerView: NSView {
    var onLayout: (() -> Void)?
    var onMouseMoved: ((CGPoint) -> Void)?
    private var tracking: NSTrackingArea?

    override func layout() {
        super.layout()
        onLayout?()
    }

    override func updateTrackingAreas() {
        super.updateTrackingAreas()
        if let tracking { removeTrackingArea(tracking) }
        let t = NSTrackingArea(rect: bounds, options: [.mouseMoved, .activeInKeyWindow, .inVisibleRect], owner: self)
        addTrackingArea(t)
        tracking = t
    }

    override func mouseMoved(with event: NSEvent) {
        onMouseMoved?(convert(event.locationInWindow, from: nil))
        super.mouseMoved(with: event)
    }
}

/// Centered message with an optional spinner (boot / shutdown / errors).
final class StatusView: NSView {
    private let label = NSTextField(labelWithString: "")
    private let spinner = NSProgressIndicator()
    private let icon = NSImageView()

    override init(frame: NSRect) {
        super.init(frame: frame)
        wantsLayer = true
        layer?.backgroundColor = NSColor.black.cgColor
        icon.image = NSApp.applicationIconImage
        icon.imageScaling = .scaleProportionallyUpOrDown
        label.font = .systemFont(ofSize: 15, weight: .medium)
        label.textColor = NSColor(white: 0.85, alpha: 1)
        label.alignment = .center
        spinner.style = .spinning
        spinner.controlSize = .small
        let stack = NSStackView(views: [icon, label, spinner])
        stack.orientation = .vertical
        stack.spacing = 14
        stack.translatesAutoresizingMaskIntoConstraints = false
        addSubview(stack)
        NSLayoutConstraint.activate([
            icon.widthAnchor.constraint(equalToConstant: 96), icon.heightAnchor.constraint(equalToConstant: 96),
            stack.centerXAnchor.constraint(equalTo: centerXAnchor), stack.centerYAnchor.constraint(equalTo: centerYAnchor),
            label.widthAnchor.constraint(lessThanOrEqualToConstant: 520),
        ])
    }

    required init?(coder: NSCoder) { fatalError() }

    func show(_ text: String, spinning: Bool) {
        isHidden = false
        label.stringValue = text
        spinner.isHidden = !spinning
        spinning ? spinner.startAnimation(nil) : spinner.stopAnimation(nil)
    }
}

/// Transient pill message at the bottom of the screen area.
final class ToastView: NSView {
    private let label = NSTextField(labelWithString: "")
    private let pill = NSVisualEffectView()
    private var hideWork: DispatchWorkItem?

    override init(frame: NSRect) {
        super.init(frame: frame)
        pill.material = .hudWindow
        pill.state = .active
        pill.wantsLayer = true
        pill.layer?.cornerRadius = 16
        pill.appearance = NSAppearance(named: .darkAqua)
        label.font = .systemFont(ofSize: 13, weight: .medium)
        label.textColor = .white
        pill.addSubview(label)
        addSubview(pill)
        alphaValue = 0
    }

    required init?(coder: NSCoder) { fatalError() }

    override func hitTest(_ point: NSPoint) -> NSView? { nil }

    func show(_ text: String, duration: TimeInterval = 2.5) {
        label.stringValue = text
        label.sizeToFit()
        let size = CGSize(width: label.frame.width + 32, height: 32)
        pill.frame = CGRect(x: (bounds.width - size.width) / 2, y: (bounds.height - size.height) / 2, width: size.width, height: size.height)
        label.frame.origin = CGPoint(x: 16, y: (size.height - label.frame.height) / 2)
        NSAnimationContext.runAnimationGroup { $0.duration = 0.15; animator().alphaValue = 1 }
        hideWork?.cancel()
        let work = DispatchWorkItem { [weak self] in
            NSAnimationContext.runAnimationGroup { $0.duration = 0.3; self?.animator().alphaValue = 0 }
        }
        hideWork = work
        DispatchQueue.main.asyncAfter(deadline: .now() + duration, execute: work)
    }
}
