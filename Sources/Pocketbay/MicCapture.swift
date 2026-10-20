import AVFoundation
import os

private let log = Logger(subsystem: "com.devesh.pocketbay", category: "mic")

/// Captures the Mac's current input device and hands out 48 kHz mono signed 16-bit PCM
/// for Android's virtual microphone. Only runs while the microphone is switched on, so
/// the macOS mic indicator isn't lit permanently.
final class MicCapture {
    static let sampleRate = 48_000.0

    private var engine: AVAudioEngine?
    private var converter: AVAudioConverter?
    private var observer: NSObjectProtocol?
    private var onPCM: ((Data) -> Void)?
    private let outFormat = AVAudioFormat(commonFormat: .pcmFormatInt16, sampleRate: MicCapture.sampleRate, channels: 1, interleaved: true)!

    var isRunning: Bool { engine?.isRunning ?? false }

    /// Asks for microphone permission if needed.
    static func requestAccess() async -> Bool {
        switch AVCaptureDevice.authorizationStatus(for: .audio) {
        case .authorized: return true
        case .notDetermined: return await AVCaptureDevice.requestAccess(for: .audio)
        default: return false
        }
    }

    func start(onPCM: @escaping (Data) -> Void) throws {
        stop()
        self.onPCM = onPCM
        try startEngine()
    }

    /// (Re)binds to the current default input device.
    private func startEngine() throws {
        let engine = AVAudioEngine()
        let input = engine.inputNode
        let inFormat = input.outputFormat(forBus: 0)
        guard inFormat.sampleRate > 0, inFormat.channelCount > 0 else {
            throw NSError(domain: "Pocketbay", code: 30, userInfo: [NSLocalizedDescriptionKey: "No microphone found"])
        }
        guard let converter = AVAudioConverter(from: inFormat, to: outFormat) else {
            throw NSError(domain: "Pocketbay", code: 31, userInfo: [NSLocalizedDescriptionKey: "Unsupported microphone format"])
        }
        self.converter = converter
        // ~20 ms chunks keep latency low.
        input.installTap(onBus: 0, bufferSize: AVAudioFrameCount(inFormat.sampleRate / 50), format: inFormat) { [weak self] buffer, _ in
            self?.convert(buffer)
        }
        try engine.start()
        self.engine = engine
        // Follow input-device changes (headset plugged in, input switched in Control Center).
        observer = NotificationCenter.default.addObserver(forName: .AVAudioEngineConfigurationChange, object: engine, queue: .main) { [weak self] _ in
            guard let self, self.onPCM != nil else { return }
            self.teardownEngine()
            do { try self.startEngine() } catch { log.error("mic restart failed: \(error.localizedDescription, privacy: .public)") }
        }
        log.info("microphone capture started")
    }

    private func convert(_ buffer: AVAudioPCMBuffer) {
        guard let converter, let onPCM else { return }
        let ratio = outFormat.sampleRate / buffer.format.sampleRate
        let capacity = AVAudioFrameCount(Double(buffer.frameLength) * ratio) + 32
        guard let out = AVAudioPCMBuffer(pcmFormat: outFormat, frameCapacity: capacity) else { return }
        var fed = false
        var error: NSError?
        converter.convert(to: out, error: &error) { _, status in
            if fed { status.pointee = .noDataNow; return nil }
            fed = true
            status.pointee = .haveData
            return buffer
        }
        guard error == nil, out.frameLength > 0, let samples = out.int16ChannelData else { return }
        onPCM(Data(bytes: samples[0], count: Int(out.frameLength) * 2))
    }

    private func teardownEngine() {
        if let observer { NotificationCenter.default.removeObserver(observer) }
        observer = nil
        engine?.inputNode.removeTap(onBus: 0)
        engine?.stop()
        engine = nil
        converter = nil
    }

    func stop() {
        onPCM = nil
        teardownEngine()
    }
}
