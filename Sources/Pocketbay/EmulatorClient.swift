import Foundation
import GRPCCore
import GRPCNIOTransportHTTP2
import SwiftProtobuf

typealias EmuImageFormat = Android_Emulation_Control_ImageFormat
typealias EmuInputEvent = Android_Emulation_Control_InputEvent
typealias EmuTouch = Android_Emulation_Control_Touch
typealias EmuKeyEvent = Android_Emulation_Control_KeyboardEvent

/// One screen frame: RGBA8888 pixels, rows top-down (despite the proto comment).
struct Frame: @unchecked Sendable {
    let width: Int
    let height: Int
    let pixels: Data
    /// Clockwise quarter turns the emulator applied to the picture (0…3).
    let quarterTurns: Int
}

/// Talks to the emulator's gRPC endpoint: a screen frame stream, input via one
/// long-lived stream, plus a few one-shot calls.
///
/// Frames arrive inline in the gRPC messages. (The emulator's shared-memory
/// transport reports success but never writes pixels in emulator 37.1.)
final class EmulatorClient: @unchecked Sendable {
    let port: Int
    private let token: String
    private let grpc: GRPCClient<HTTP2ClientTransport.Posix>
    private let emu: Android_Emulation_Control_EmulatorController.Client<HTTP2ClientTransport.Posix>
    private var metadata: Metadata { ["authorization": "Bearer \(token)"] }

    private var inputContinuation: AsyncStream<EmuInputEvent>.Continuation?

    init(port: Int, token: String) throws {
        self.port = port
        self.token = token
        let transport = try HTTP2ClientTransport.Posix(
            target: .ipv4(address: "127.0.0.1", port: port),
            transportSecurity: .plaintext
        )
        grpc = GRPCClient(transport: transport)
        emu = .init(wrapping: grpc)

        let client = grpc
        Task.detached { try? await client.runConnections() }
    }

    deinit {
        grpc.beginGracefulShutdown()
    }

    // MARK: Status

    func isBooted() async -> Bool {
        let status = try? await emu.getStatus(Google_Protobuf_Empty(), metadata: metadata) { try $0.message }
        return status?.booted ?? false
    }

    /// True once the gRPC endpoint answers (booted or not).
    func isReachable() async -> Bool {
        (try? await emu.getStatus(Google_Protobuf_Empty(), metadata: metadata) { try $0.message }) != nil
    }

    // MARK: Screen

    /// Streams frames until cancelled or the stream ends.
    func streamScreen(onFrame: @escaping @Sendable (Frame) -> Void) async throws {
        var format = EmuImageFormat()
        format.format = .rgba8888
        // A 1920x1920 RGBA frame is ~14.7 MB; allow room for either orientation.
        var options = CallOptions.defaults
        options.maxResponseMessageBytes = 16 << 20
        options.maxRequestMessageBytes = 16 << 20   // the NIO transport checks this one when decoding responses
        try await emu.streamScreenshot(format, metadata: metadata, options: options) { response in
            for try await image in response.messages {
                let w = Int(image.format.width), h = Int(image.format.height)
                guard w > 0, h > 0, image.image.count >= w * h * 4 else { continue }
                onFrame(Frame(width: w, height: h, pixels: image.image,
                              quarterTurns: image.format.rotation.rotation.rawValue & 3))
            }
        }
    }

    // MARK: Input

    /// Opens the long-lived input stream. Events sent before it opens are buffered.
    func startInputStream() {
        let (stream, continuation) = AsyncStream<EmuInputEvent>.makeStream(bufferingPolicy: .unbounded)
        inputContinuation = continuation
        let emu = self.emu, metadata = self.metadata
        Task.detached {
            while !Task.isCancelled {
                do {
                    _ = try await emu.streamInputEvent(metadata: metadata, requestProducer: { writer in
                        for await event in stream {
                            try await writer.write(event)
                            self.queueLock.lock(); self.queued -= 1; self.queueLock.unlock()
                        }
                    })
                    return
                } catch {
                    try? await Task.sleep(for: .milliseconds(500))
                }
            }
        }
    }

    private let queueLock = NSLock()
    private var queued = 0
    private var lastBacklogLog = Date.distantPast

    func send(_ event: EmuInputEvent) {
        queueLock.lock(); queued += 1; let depth = queued; queueLock.unlock()
        if depth > 30, Date().timeIntervalSince(lastBacklogLog) > 1 {
            lastBacklogLog = Date()
            NSLog("Pocketbay: input backlog %d events", depth)
        }
        inputContinuation?.yield(event)
    }

    /// Sends a set of touch updates in one event (pressure 0 lifts a finger).
    func touch(_ touches: [EmuTouch]) {
        var ev = EmuInputEvent()
        ev.touchEvent.touches = touches
        send(ev)
    }

    func key(macKeyCode: UInt16, down: Bool) {
        var k = EmuKeyEvent()
        k.eventType = down ? .keydown : .keyup
        if macKeyCode == 0 {
            // kVK_ANSI_A is 0, which proto3 treats as "unset" and the emulator ignores;
            // send the Linux evdev code for A (KEY_A = 30) instead.
            k.codeType = .evdev
            k.keyCode = 30
        } else {
            k.codeType = .mac
            k.keyCode = Int32(macKeyCode)
        }
        var ev = EmuInputEvent()
        ev.keyEvent = k
        send(ev)
    }

    /// W3C key names; Android specials: "GoBack", "GoHome", "AppSwitch", "Power",
    /// "AudioVolumeUp", "AudioVolumeDown".
    func press(key: String) {
        var k = EmuKeyEvent()
        k.eventType = .keypress
        k.key = key
        var ev = EmuInputEvent()
        ev.keyEvent = k
        send(ev)
    }

    func wheel(dx: Int32, dy: Int32) {
        var w = Android_Emulation_Control_WheelEvent()
        w.dx = dx
        w.dy = dy
        var ev = EmuInputEvent()
        ev.wheelEvent = w
        send(ev)
    }

    // MARK: Audio

    /// Streams Android's audio output as 48 kHz stereo signed 16-bit PCM (interleaved).
    func streamAudio(onPCM: @escaping @Sendable (Data) -> Void) async throws {
        var format = Android_Emulation_Control_AudioFormat()
        format.samplingRate = 48_000
        format.channels = .stereo
        format.format = .audFmtS16
        format.mode = .modeRealTime
        try await emu.streamAudio(format, metadata: metadata) { response in
            for try await packet in response.messages where !packet.audio.isEmpty { onPCM(packet.audio) }
        }
    }

    /// Feeds 48 kHz mono signed 16-bit PCM into Android's microphone until `pcm` finishes.
    func injectMicrophone(_ pcm: AsyncStream<Data>) async throws {
        var format = Android_Emulation_Control_AudioFormat()
        format.samplingRate = 48_000
        format.channels = .mono
        format.format = .audFmtS16
        format.mode = .modeRealTime
        let fmt = format
        _ = try await emu.injectAudio(metadata: metadata, requestProducer: { writer in
            for await chunk in pcm {
                var packet = Android_Emulation_Control_AudioPacket()
                packet.format = fmt   // only the first packet's format is honoured
                packet.timestamp = UInt64(Date().timeIntervalSince1970 * 1_000_000)
                packet.audio = chunk
                try await writer.write(packet)
            }
        })
    }

    // MARK: Clipboard

    func clipboard() async -> String? {
        try? await emu.getClipboard(Google_Protobuf_Empty(), metadata: metadata) { try $0.message.text }
    }

    func setClipboard(_ text: String) async {
        var clip = Android_Emulation_Control_ClipData()
        clip.text = text
        _ = try? await emu.setClipboard(clip, metadata: metadata) { try $0.message }
    }

    /// Calls `onText` whenever Android's clipboard changes.
    func streamClipboard(onText: @escaping @Sendable (String) -> Void) async throws {
        try await emu.streamClipboard(Google_Protobuf_Empty(), metadata: metadata) { response in
            for try await clip in response.messages { onText(clip.text) }
        }
    }

    // MARK: Device

    /// Rotates the virtual device by setting the physical-model rotation (degrees around z).
    func setRotation(zDegrees: Float) async {
        var v = Android_Emulation_Control_PhysicalModelValue()
        v.target = .rotation
        v.value.data = [0, 0, zDegrees]
        _ = try? await emu.setPhysicalModel(v, metadata: metadata) { try $0.message }
    }
}

extension EmuTouch {
    static func finger(_ id: Int32, x: Int32, y: Int32, down: Bool) -> EmuTouch {
        var t = EmuTouch()
        t.identifier = id
        t.x = x
        t.y = y
        t.pressure = down ? 1 : 0
        t.expiration = .neverExpire
        return t
    }
}
