package com.myxmic.app

import android.annotation.SuppressLint
import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.Service
import android.content.Intent
import android.media.AudioFormat
import android.media.AudioRecord
import android.media.MediaRecorder
import android.media.audiofx.AcousticEchoCanceler
import android.media.audiofx.NoiseSuppressor
import android.os.IBinder
import android.os.Process
import java.io.BufferedOutputStream
import java.net.InetSocketAddress
import java.net.Socket
import java.util.concurrent.atomic.AtomicBoolean

/**
 * 采集 48kHz/16bit/mono PCM -> (可选 DSP 降噪) -> 裸 TCP 推送。
 * 每帧 20ms = 48000 * 0.02 * 2 = 1920 字节。
 */
class MicService : Service() {

    companion object {
        const val ACTION_START = "com.myxmic.app.START"
        const val ACTION_STOP = "com.myxmic.app.STOP"
        const val EXTRA_HOST = "host"
        const val EXTRA_PORT = "port"
        const val EXTRA_NS = "ns"
        const val EXTRA_AEC = "aec"

        const val SAMPLE_RATE = 48000
        const val FRAME_MS = 20
        const val FRAME_BYTES = SAMPLE_RATE / 1000 * FRAME_MS * 2 // 1920
        private const val CHANNEL_ID = "mic"
    }

    private val running = AtomicBoolean(false)
    private var thread: Thread? = null

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        when (intent?.action) {
            ACTION_START -> startStream(
                intent.getStringExtra(EXTRA_HOST) ?: "127.0.0.1",
                intent.getIntExtra(EXTRA_PORT, 8125),
                intent.getBooleanExtra(EXTRA_NS, true),
                intent.getBooleanExtra(EXTRA_AEC, false)
            )
            ACTION_STOP -> stopStream()
        }
        return START_STICKY
    }

    private fun startForeground() {
        val nm = getSystemService(NotificationManager::class.java)
        nm.createNotificationChannel(
            NotificationChannel(CHANNEL_ID, "麦克风", NotificationManager.IMPORTANCE_MIN)
        )
        val n = Notification.Builder(this, CHANNEL_ID)
            .setContentTitle("myXmic 正在推流")
            .setSmallIcon(android.R.drawable.ic_btn_speak_now)
            .build()
        startForeground(1, n)
    }

    private fun startStream(host: String, port: Int, ns: Boolean, aec: Boolean) {
        if (running.getAndSet(true)) return
        startForeground()
        thread = Thread {
            Process.setThreadPriority(Process.THREAD_PRIORITY_URGENT_AUDIO)
            runCapture(host, port, ns, aec)
            stopSelf()
        }.also { it.start() }
    }

    private fun stopStream() {
        running.set(false)
        thread?.join(2000)
        thread = null
        stopForeground(true)
        stopSelf()
    }

    @SuppressLint("MissingPermission")
    private fun runCapture(host: String, port: Int, ns: Boolean, aec: Boolean) {
        val minBuf = AudioRecord.getMinBufferSize(
            SAMPLE_RATE, AudioFormat.CHANNEL_IN_MONO, AudioFormat.ENCODING_PCM_16BIT
        )
        val record = AudioRecord(
            // VOICE_COMMUNICATION 走通话通路，可触发硬件 NS/AEC，最省电
            MediaRecorder.AudioSource.VOICE_COMMUNICATION,
            SAMPLE_RATE,
            AudioFormat.CHANNEL_IN_MONO,
            AudioFormat.ENCODING_PCM_16BIT,
            maxOf(minBuf, FRAME_BYTES * 4)
        )

        // 系统 DSP：降噪 / 回声消除（硬件加速，几乎不耗 CPU）
        var nsFx: NoiseSuppressor? = null
        var aecFx: AcousticEchoCanceler? = null
        try {
            if (ns && NoiseSuppressor.isAvailable())
                nsFx = NoiseSuppressor.create(record.audioSessionId)?.apply { enabled = true }
            if (aec && AcousticEchoCanceler.isAvailable())
                aecFx = AcousticEchoCanceler.create(record.audioSessionId)?.apply { enabled = true }
        } catch (t: Throwable) {
            android.util.Log.w("MicService", "DSP 不可用: ${t.message}")
        }

        var socket: Socket? = null
        try {
            socket = Socket()
            socket.tcpNoDelay = true
            socket.connect(InetSocketAddress(host, port), 5000)
            val out = BufferedOutputStream(socket.getOutputStream(), FRAME_BYTES * 8)

            record.startRecording()
            val buf = ByteArray(FRAME_BYTES)
            while (running.get()) {
                val n = record.read(buf, 0, buf.size)
                if (n > 0) {
                    out.write(buf, 0, n)
                    out.flush() // 每帧立即发，低延迟
                }
            }
        } catch (e: Exception) {
            android.util.Log.e("MicService", "推流失败", e)
        } finally {
            try { record.stop() } catch (_: Throwable) {}
            record.release()
            nsFx?.release(); aecFx?.release()
            try { socket?.close() } catch (_: Throwable) {}
            running.set(false)
        }
    }

    override fun onDestroy() {
        stopStream()
        super.onDestroy()
    }
}
