package com.esi.remotepc

import android.media.AudioAttributes
import android.media.AudioFormat
import android.media.AudioTrack
import android.os.Handler
import android.os.Looper
import io.flutter.embedding.android.FlutterActivity
import io.flutter.embedding.engine.FlutterEngine
import io.flutter.plugin.common.MethodChannel
import java.util.ArrayDeque

/**
 * Hosts the `phone as the PC's speaker` playback path.
 *
 * The Dart side picks audio frames off the session socket, expands the µ-law payload to 16-bit
 * PCM, and hands the samples here. This class owns the [AudioTrack] that turns those bytes back
 * into sound.
 *
 * Two decisions shape the whole file:
 *
 *  1. **Writes never run on the platform thread.** [AudioTrack.write] blocks whenever the
 *     output buffer is full, which is exactly what it is supposed to do - and blocking the
 *     platform thread would freeze every other platform channel, including the screen stream's
 *     image decoding and the input injector's taps. So each `write` is queued and consumed by a
 *     single background thread instead.
 *
 *  2. **The queue is bounded and drops the oldest.** If the phone cannot keep up - Wi-Fi
 *     hiccup, a phone thermally throttled - an unbounded queue would grow until the process is
 *     killed and would then play minutes of audio that is stale by the time it was heard. The
 *     PC's own capture queue makes the same choice for the same reason. What is already in
 *     front of the speaker is at most a few tens of milliseconds old; audio from half a second
 *     ago is worse than a gap.
 */
class MainActivity : FlutterActivity() {
    private val mainHandler = Handler(Looper.getMainLooper())

    private var channel: MethodChannel? = null

    @Volatile
    private var track: AudioTrack? = null

    @Volatile
    private var running = false

    private val lock = Any()
    private val queue = ArrayDeque<ByteArray>()
    private var writer: Thread? = null

    /** Cap on queued PCM bytes: ~1.3s at 24 kHz stereo. Generous, but not unbounded. */
    private val maxQueuedBytes = 128 * 1024

    override fun configureFlutterEngine(flutterEngine: FlutterEngine) {
        super.configureFlutterEngine(flutterEngine)
        val ch = MethodChannel(flutterEngine.dartExecutor.binaryMessenger, "esi_remote/audio")
        channel = ch
        ch.setMethodCallHandler { call, result ->
            when (call.method) {
                "start" -> start(
                    sampleRate = (call.argument<Number>("sampleRate") ?: 24000).toInt(),
                    channels = (call.argument<Number>("channels") ?: 2).toInt(),
                    result = result,
                )
                "write" -> write(call.argument<ByteArray>("data"), result)
                "stop" -> stop(result)
                else -> result.notImplemented()
            }
        }
    }

    private fun start(sampleRate: Int, channels: Int, result: MethodChannel.Result) {
        stopInternal()

        val channelMask = if (channels >= 2) AudioFormat.CHANNEL_OUT_STEREO else AudioFormat.CHANNEL_OUT_MONO
        val minBuffer = AudioTrack.getMinBufferSize(
            sampleRate,
            channelMask,
            AudioFormat.ENCODING_PCM_16BIT,
        )
        if (minBuffer <= 0) {
            // The device refused the format outright. Reporting it rather than pretending to
            // play is the whole point: the phone's meter would otherwise sit at zero with the
            // app claiming audio is on.
            result.error(
                "bad_format",
                "This device will not play $sampleRate Hz, $channels-channel 16-bit PCM.",
                null,
            )
            return
        }

        val attributes = AudioAttributes.Builder()
            .setUsage(AudioAttributes.USAGE_MEDIA)
            .setContentType(AudioAttributes.CONTENT_TYPE_MUSIC)
            .build()
        val format = AudioFormat.Builder()
            .setSampleRate(sampleRate)
            .setEncoding(AudioFormat.ENCODING_PCM_16BIT)
            .setChannelMask(channelMask)
            .build()

        val built = try {
            AudioTrack.Builder()
                .setAudioAttributes(attributes)
                .setAudioFormat(format)
                .setTransferMode(AudioTrack.MODE_STREAM)
                // Three times the minimum: one buffer being filled while one is playing keeps
                // the hardware fed without holding more than about a tenth of a second.
                .setBufferSizeInBytes(minBuffer * 3)
                .build()
        } catch (e: IllegalArgumentException) {
            result.error("bad_format", e.message, null)
            return
        }

        if (built.state != AudioTrack.STATE_INITIALIZED) {
            built.release()
            result.error(
                "unavailable",
                "The audio track could not be initialised; another app may be holding the output.",
                null,
            )
            return
        }

        synchronized(lock) {
            queue.clear()
            track = built
            running = true
            writer = Thread(::drain, "esi-audio-out").also {
                it.isDaemon = true
                it.start()
            }
        }

        built.play()
        result.success(null)
    }

    private fun write(bytes: ByteArray?, result: MethodChannel.Result) {
        if (bytes == null || bytes.isEmpty() || !running) {
            // Not running: the frame arrived after a stop. Dropping it is correct - there is no
            // track to play it into, and opening one here would resurrect audio the user turned
            // off.
            result.success(0)
            return
        }

        synchronized(lock) {
            // Drop the oldest queued audio, not this newest packet: what is already waiting is
            // older than what just arrived, and the speaker has not reached it yet.
            while (queue.size > 1 && queuedBytes() + bytes.size > maxQueuedBytes) {
                queue.removeFirst()
            }
            queue.addLast(bytes)
            (lock as Object).notifyAll()
        }
        result.success(bytes.size)
    }

    private fun queuedBytes(): Int {
        var total = 0
        for (b in queue) total += b.size
        return total
    }

    /** Consumes the queue on a background thread, one packet at a time. */
    private fun drain() {
        while (true) {
            val next: ByteArray
            val current: AudioTrack?
            synchronized(lock) {
                while (queue.isEmpty() && running) {
                    try {
                        (lock as Object).wait(250)
                    } catch (_: InterruptedException) {
                        return
                    }
                }
                if (!running && queue.isEmpty()) return
                next = queue.pollFirst() ?: continue
                current = track
            }
            if (current == null) continue

            var offset = 0
            while (offset < next.size && running) {
                // `write` blocks until the hardware takes the bytes, which is why this loop is
                // off the platform thread. A short write is legal and means "try again from
                // here", so the offset is advanced by what was actually accepted.
                val written = current.write(next, offset, next.size - offset)
                if (written <= 0) break
                offset += written
            }
        }
    }

    private fun stop(result: MethodChannel.Result) {
        stopInternal()
        result.success(null)
    }

    private fun stopInternal() {
        val old: AudioTrack?
        synchronized(lock) {
            running = false
            old = track
            track = null
            queue.clear()
            (lock as Object).notifyAll()
        }
        // The writer thread is left to notice `running == false` and exit on its own; joining
        // here would block the platform thread on a `write` that may be waiting for the
        // hardware. The thread is a daemon, so it cannot hold the process open.
        old?.let {
            try {
                it.stop()
            } catch (_: IllegalStateException) {
                // Never started, or already stopped. Nothing to do.
            }
            it.release()
        }
    }

    override fun onDestroy() {
        stopInternal()
        channel?.setMethodCallHandler(null)
        channel = null
        super.onDestroy()
    }
}
