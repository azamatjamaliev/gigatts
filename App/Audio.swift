import AppKit
import AVFoundation
import CoreAudio
import CoreMedia
import ScreenCaptureKit

enum AudioDevices {
    static func inputs() -> [AudioInputDevice] {
        var address = AudioObjectPropertyAddress(
            mSelector: kAudioHardwarePropertyDevices,
            mScope: kAudioObjectPropertyScopeGlobal,
            mElement: kAudioObjectPropertyElementMain
        )
        var size: UInt32 = 0
        let system = AudioObjectID(kAudioObjectSystemObject)
        guard AudioObjectGetPropertyDataSize(system, &address, 0, nil, &size) == noErr, size > 0 else {
            return []
        }
        let count = Int(size) / MemoryLayout<AudioDeviceID>.size
        var ids = [AudioDeviceID](repeating: 0, count: count)
        guard AudioObjectGetPropertyData(system, &address, 0, nil, &size, &ids) == noErr else {
            return []
        }
        return ids.compactMap { id in
            guard hasInput(id), let uid = copyString(id, kAudioDevicePropertyDeviceUID) else { return nil }
            let name = copyString(id, kAudioDevicePropertyDeviceNameCFString) ?? uid
            return AudioInputDevice(uid: uid, name: name, deviceID: id)
        }.sorted { $0.name.localizedCaseInsensitiveCompare($1.name) == .orderedAscending }
    }

    static func deviceID(forUID uid: String) -> AudioDeviceID? {
        inputs().first { $0.uid == uid }?.deviceID
    }

    private static func hasInput(_ id: AudioDeviceID) -> Bool {
        var address = AudioObjectPropertyAddress(
            mSelector: kAudioDevicePropertyStreamConfiguration,
            mScope: kAudioDevicePropertyScopeInput,
            mElement: kAudioObjectPropertyElementMain
        )
        var size: UInt32 = 0
        guard AudioObjectGetPropertyDataSize(id, &address, 0, nil, &size) == noErr, size > 0 else {
            return false
        }
        let raw = UnsafeMutableRawPointer.allocate(byteCount: Int(size), alignment: MemoryLayout<AudioBufferList>.alignment)
        defer { raw.deallocate() }
        guard AudioObjectGetPropertyData(id, &address, 0, nil, &size, raw) == noErr else { return false }
        let list = UnsafeMutableAudioBufferListPointer(raw.assumingMemoryBound(to: AudioBufferList.self))
        return list.contains { $0.mNumberChannels > 0 }
    }

    private static func copyString(_ id: AudioObjectID, _ selector: AudioObjectPropertySelector) -> String? {
        var address = AudioObjectPropertyAddress(
            mSelector: selector,
            mScope: kAudioObjectPropertyScopeGlobal,
            mElement: kAudioObjectPropertyElementMain
        )
        var value: CFString?
        var size = UInt32(MemoryLayout<CFString?>.size)
        let status = withUnsafeMutablePointer(to: &value) { pointer in
            AudioObjectGetPropertyData(id, &address, 0, nil, &size, pointer)
        }
        guard status == noErr else { return nil }
        return value as String?
    }
}

final class WAVWriter {
    let url: URL
    private let lock = NSLock()
    private var handle: FileHandle?
    private var dataBytes: UInt32 = 0
    private let sampleRate: UInt32 = 16_000

    init(url: URL) throws {
        self.url = url
        if FileManager.default.fileExists(atPath: url.path) {
            try FileManager.default.removeItem(at: url)
        }
        FileManager.default.createFile(atPath: url.path, contents: nil)
        let handle = try FileHandle(forWritingTo: url)
        try handle.write(contentsOf: Self.header(dataBytes: 0, sampleRate: sampleRate))
        self.handle = handle
    }

    func append(_ samples: [Float]) {
        guard !samples.isEmpty else { return }
        var pcm = Data(count: samples.count * 2)
        pcm.withUnsafeMutableBytes { raw in
            guard let destination = raw.baseAddress?.assumingMemoryBound(to: Int16.self) else { return }
            for index in 0..<samples.count {
                let clamped = max(-1, min(1, samples[index]))
                destination[index] = Int16(clamped * 32_767)
            }
        }
        lock.lock()
        defer { lock.unlock() }
        try? handle?.write(contentsOf: pcm)
        dataBytes += UInt32(pcm.count)
    }

    func finish() -> URL {
        lock.lock()
        defer { lock.unlock() }
        if let handle {
            try? handle.seek(toOffset: 0)
            try? handle.write(contentsOf: Self.header(dataBytes: dataBytes, sampleRate: sampleRate))
            try? handle.close()
        }
        handle = nil
        return url
    }

    var duration: TimeInterval {
        lock.lock()
        defer { lock.unlock() }
        return Double(dataBytes / 2) / Double(sampleRate)
    }

    private static func header(dataBytes: UInt32, sampleRate: UInt32) -> Data {
        var data = Data()
        data.append(contentsOf: [UInt8]("RIFF".utf8))
        data.appendLE(UInt32(36 + dataBytes))
        data.append(contentsOf: [UInt8]("WAVE".utf8))
        data.append(contentsOf: [UInt8]("fmt ".utf8))
        data.appendLE(UInt32(16))
        data.appendLE(UInt16(1))
        data.appendLE(UInt16(1))
        data.appendLE(sampleRate)
        data.appendLE(sampleRate * 2)
        data.appendLE(UInt16(2))
        data.appendLE(UInt16(16))
        data.append(contentsOf: [UInt8]("data".utf8))
        data.appendLE(dataBytes)
        return data
    }
}

private extension Data {
    mutating func appendLE<T: FixedWidthInteger>(_ value: T) {
        var little = value.littleEndian
        Swift.withUnsafeBytes(of: &little) { raw in
            append(contentsOf: raw)
        }
    }
}

final class LinearResampler {
    private var step = 1.0
    private var carry: [Float] = []
    private var fraction = 0.0

    func reset(sourceRate: Double, targetRate: Double = 16_000) {
        step = max(sourceRate, 1) / targetRate
        carry.removeAll(keepingCapacity: true)
        fraction = 0
    }

    func process(_ input: [Float]) -> [Float] {
        guard !input.isEmpty else { return [] }
        carry.append(contentsOf: input)
        var output: [Float] = []
        var position = fraction
        while position + 1 < Double(carry.count) {
            let index = Int(position)
            let fraction = Float(position - Double(index))
            let left = carry[index]
            let right = carry[index + 1]
            output.append(left + (right - left) * fraction)
            position += step
        }
        let consumed = Int(position)
        if consumed > 0 {
            carry.removeFirst(consumed)
            position -= Double(consumed)
        }
        fraction = position
        return output
    }
}

enum PCMSamples {
    static func monoFloat(from sampleBuffer: CMSampleBuffer) -> (rate: Double, samples: [Float])? {
        guard let format = sampleBuffer.formatDescription,
              let asbdPointer = CMAudioFormatDescriptionGetStreamBasicDescription(format) else {
            return nil
        }
        let asbd = asbdPointer.pointee
        var needed = 0
        let probe = CMSampleBufferGetAudioBufferListWithRetainedBlockBuffer(
            sampleBuffer,
            bufferListSizeNeededOut: &needed,
            bufferListOut: nil,
            bufferListSize: 0,
            blockBufferAllocator: nil,
            blockBufferMemoryAllocator: nil,
            flags: 0,
            blockBufferOut: nil
        )
        guard probe == noErr || probe == kCMSampleBufferError_ArrayTooSmall, needed > 0 else { return nil }

        let raw = UnsafeMutableRawPointer.allocate(byteCount: needed, alignment: MemoryLayout<AudioBufferList>.alignment)
        defer { raw.deallocate() }
        memset(raw, 0, needed)
        var blockBuffer: CMBlockBuffer?
        let status = CMSampleBufferGetAudioBufferListWithRetainedBlockBuffer(
            sampleBuffer,
            bufferListSizeNeededOut: nil,
            bufferListOut: raw.assumingMemoryBound(to: AudioBufferList.self),
            bufferListSize: needed,
            blockBufferAllocator: kCFAllocatorDefault,
            blockBufferMemoryAllocator: kCFAllocatorDefault,
            flags: kCMSampleBufferFlag_AudioBufferList_Assure16ByteAlignment,
            blockBufferOut: &blockBuffer
        )
        guard status == noErr else { return nil }
        let buffers = UnsafeMutableAudioBufferListPointer(raw.assumingMemoryBound(to: AudioBufferList.self))
        let samples = monoFloat(from: buffers, asbd: asbd)
        return (asbd.mSampleRate, samples)
    }

    private static func monoFloat(from buffers: UnsafeMutableAudioBufferListPointer, asbd: AudioStreamBasicDescription) -> [Float] {
        let channels = max(Int(asbd.mChannelsPerFrame), 1)
        let isFloat = asbd.mFormatFlags & kAudioFormatFlagIsFloat != 0
        let nonInterleaved = asbd.mFormatFlags & kAudioFormatFlagIsNonInterleaved != 0
        if nonInterleaved {
            guard let first = buffers.first else { return [] }
            let sampleSize = isFloat ? MemoryLayout<Float>.size : MemoryLayout<Int16>.size
            let frames = Int(first.mDataByteSize) / sampleSize
            guard frames > 0 else { return [] }
            var mixed = [Float](repeating: 0, count: frames)
            for buffer in buffers {
                guard let data = buffer.mData else { continue }
                let count = min(frames, Int(buffer.mDataByteSize) / sampleSize)
                if isFloat {
                    let pointer = data.assumingMemoryBound(to: Float.self)
                    for index in 0..<count { mixed[index] += pointer[index] }
                } else {
                    let pointer = data.assumingMemoryBound(to: Int16.self)
                    for index in 0..<count { mixed[index] += Float(pointer[index]) / 32_768 }
                }
            }
            let divisor = Float(max(buffers.count, 1))
            for index in 0..<mixed.count { mixed[index] /= divisor }
            return mixed
        }
        guard let data = buffers.first?.mData else { return [] }
        let bytes = Int(buffers.first?.mDataByteSize ?? 0)
        if isFloat {
            let frames = bytes / (MemoryLayout<Float>.size * channels)
            let pointer = data.assumingMemoryBound(to: Float.self)
            return (0..<frames).map { frame in
                var sum: Float = 0
                for channel in 0..<channels { sum += pointer[frame * channels + channel] }
                return sum / Float(channels)
            }
        }
        let frames = bytes / (MemoryLayout<Int16>.size * channels)
        let pointer = data.assumingMemoryBound(to: Int16.self)
        return (0..<frames).map { frame in
            var sum: Float = 0
            for channel in 0..<channels { sum += Float(pointer[frame * channels + channel]) }
            return sum / Float(channels) / 32_768
        }
    }
}

final class MicrophoneRecorder {
    private var engine: AVAudioEngine?
    private var converter: AVAudioConverter?
    private var outputBuffer: AVAudioPCMBuffer?
    private var writer: WAVWriter?
    private let queue = DispatchQueue(label: "gigatts.mic")

    var isRunning: Bool { engine != nil }

    func start(url: URL, deviceUID: String?) throws {
        _ = stop()
        let engine = AVAudioEngine()
        let input = engine.inputNode
        if let deviceUID, !deviceUID.isEmpty, let deviceID = AudioDevices.deviceID(forUID: deviceUID) {
            try input.auAudioUnit.setDeviceID(deviceID)
        }
        engine.prepare()
        let format = input.outputFormat(forBus: 0)
        guard format.sampleRate > 0, format.channelCount > 0 else {
            throw RecorderError.message("Микрофон не отдал формат аудио. Проверьте разрешение и выбранное устройство.")
        }
        guard let target = AVAudioFormat(commonFormat: .pcmFormatFloat32, sampleRate: 16_000, channels: 1, interleaved: false),
              let converter = AVAudioConverter(from: format, to: target) else {
            throw RecorderError.message("Не удалось подготовить преобразование аудио в 16 кГц.")
        }
        let writer = try WAVWriter(url: url)
        self.engine = engine
        self.converter = converter
        self.writer = writer
        input.installTap(onBus: 0, bufferSize: 4096, format: format) { [weak self] buffer, _ in
            self?.consume(buffer)
        }
        try engine.start()
    }

    func stop() -> URL? {
        engine?.inputNode.removeTap(onBus: 0)
        engine?.stop()
        engine = nil
        converter = nil
        outputBuffer = nil
        let url = writer?.finish()
        writer = nil
        return url
    }

    var duration: TimeInterval { writer?.duration ?? 0 }

    private func consume(_ buffer: AVAudioPCMBuffer) {
        guard let converter, let writer else { return }
        let capacity = AVAudioFrameCount(Double(buffer.frameLength) * (16_000 / buffer.format.sampleRate) + 32)
        if outputBuffer == nil || (outputBuffer?.frameCapacity ?? 0) < capacity {
            outputBuffer = AVAudioPCMBuffer(pcmFormat: converter.outputFormat, frameCapacity: max(capacity, 1024))
        }
        guard let output = outputBuffer else { return }
        output.frameLength = 0
        var error: NSError?
        var consumed = false
        converter.convert(to: output, error: &error) { _, status in
            if consumed {
                status.pointee = .noDataNow
                return nil
            }
            consumed = true
            status.pointee = .haveData
            return buffer
        }
        guard error == nil, let channel = output.floatChannelData else { return }
        let samples = Array(UnsafeBufferPointer(start: channel[0], count: Int(output.frameLength)))
        queue.async {
            writer.append(samples)
        }
    }
}

final class SystemAudioRecorder: NSObject, SCStreamOutput, SCStreamDelegate {
    private var stream: SCStream?
    private var writer: WAVWriter?
    private var resampler = LinearResampler()
    private var sourceRate = 48_000.0
    private let queue = DispatchQueue(label: "gigatts.system-audio")
    private var didFail: ((String) -> Void)?

    var isRunning: Bool { stream != nil }

    func start(url: URL, onFailure: @escaping (String) -> Void) async throws {
        didFail = onFailure
        let content = try await SCShareableContent.excludingDesktopWindows(false, onScreenWindowsOnly: true)
        let mainID = CGMainDisplayID()
        guard let display = content.displays.first(where: { $0.displayID == mainID }) ?? content.displays.first else {
            throw RecorderError.message("Не найден дисплей для захвата системного звука.")
        }
        let filter = SCContentFilter(display: display, excludingWindows: [])
        let configuration = SCStreamConfiguration()
        configuration.width = 2
        configuration.height = 2
        configuration.minimumFrameInterval = CMTime(value: 1, timescale: 1)
        configuration.capturesAudio = true
        configuration.excludesCurrentProcessAudio = true
        configuration.sampleRate = 48_000
        configuration.channelCount = 2
        configuration.showsCursor = false
        configuration.queueDepth = 4

        let writer = try WAVWriter(url: url)
        self.writer = writer
        sourceRate = 48_000
        resampler.reset(sourceRate: sourceRate)
        let stream = SCStream(filter: filter, configuration: configuration, delegate: self)
        do {
            try stream.addStreamOutput(self, type: .audio, sampleHandlerQueue: queue)
            try await stream.startCapture()
        } catch {
            _ = writer.finish()
            self.writer = nil
            throw error
        }
        self.stream = stream
    }

    func stop() -> URL? {
        let stream = self.stream
        self.stream = nil
        if let stream {
            stream.stopCapture { _ in }
        }
        let url = writer?.finish()
        writer = nil
        return url
    }

    var duration: TimeInterval { writer?.duration ?? 0 }

    func stream(_ stream: SCStream, didOutputSampleBuffer sampleBuffer: CMSampleBuffer, of type: SCStreamOutputType) {
        guard type == .audio, let writer, let extracted = PCMSamples.monoFloat(from: sampleBuffer) else { return }
        if abs(extracted.rate - sourceRate) > 1 {
            sourceRate = extracted.rate
            resampler.reset(sourceRate: extracted.rate)
        }
        let samples = resampler.process(extracted.samples)
        writer.append(samples)
    }

    func stream(_ stream: SCStream, didStopWithError error: Error) {
        let message = error.localizedDescription
        DispatchQueue.main.async { [weak self] in
            self?.didFail?(message)
        }
    }
}

enum RecorderError: LocalizedError {
    case message(String)
    var errorDescription: String? {
        switch self {
        case .message(let text): return text
        }
    }
}
