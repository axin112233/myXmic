package com.myxmic.app

import android.annotation.SuppressLint
import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.Service
import android.bluetooth.BluetoothAdapter
import android.bluetooth.BluetoothManager
import android.content.Intent
import android.media.AudioFormat
import android.media.AudioRecord
import android.media.MediaRecorder
import android.media.audiofx.AcousticEchoCanceler
import android.media.audiofx.NoiseSuppressor
import android.os.IBinder
import android.os.Process
import java.io.BufferedOutputStream
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetAddress
import java.net.InetSocketAddress
import java.net.Socket
import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.util.UUID
import java.util.concurrent.atomic.AtomicBoolean

/**
 * 采集 PCM -> 可选 DSP(NS/AEC) -> 增益 -> TCP/UDP/蓝牙 RFCOMM 推流。
 * 每帧 20ms，帧头 4 字节小端采样率 + PCM 数据。
 */
class MicService : Service() {

    companion object {
        const val ACTION_START = "com.myxmic.app.START"
        const val ACTION_STOP = "com.myxmic.app.STOP"
        const val EXTRA_MODE = "mode"        // 0=WiFi/USB 网络, 1=蓝牙
        const val EXTRA_PROTOCOL = "proto"   // 0=TCP 1=UDP
        const val EXTRA_HOST = "host"
        const val EXTRA_PORT = "port"
        const val EXTRA_BT_MAC = "btmac"
        const val EXTRA_SR = "sr"
        const val EXTRA_GAIN = "gain"
        const val EXTRA_NS = "ns"
        const val EXTRA_AEC = "aec"
        private const val CHANNEL_ID = "mic"
        const val ACTION_LEVEL = "com.myxmic.app.LEVEL"
        const val EXTRA_LEVEL = "level"
        const val EXTRA_STATE = "state"
        private val SPP_UUID: UUID = UUID.fromString("00001101-0000-1000-8000-00805F9B34FB")
    }

    private val running = AtomicBoolean(false)
    private var thread: Thread? = null

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        when (intent?.action) {
            ACTION_START -> {
                startForeground()
                startStream(
                    mode = intent.getIntExtra(EXTRA_MODE, 0),
                    protocol = intent.getIntExtra(EXTRA_PROTOCOL, 0),
                    host = intent.getStringExtra(EXTRA_HOST) ?: "127.0.0.1",
                    port = intent.getIntExtra(EXTRA_PORT, 8125),
                    btMac = intent.getStringExtra(EXTRA_BT_MAC) ?: "",
                    sr = intent.getIntExtra(EXTRA_SR, 48000),
                    gain = intent.getFloatExtra(EXTRA_GAIN, 1f),
                    ns = intent.getBooleanExtra(EXTRA_NS, true),
                    aec = intent.getBooleanExtra(EXTRA_AEC, false)
                )
            }
            ACTION_STOP -> stopStream()
        }
        return START_STICKY
    }

    private fun startForeground() {
        val nm = getSystemService(NotificationManager::class.java)
        nm.createNotificationChannel(
            NotificationChannel(CHANNEL_ID, "myXmic", NotificationManager.IMPORTANCE_MIN)
        )
        startForeground(1, Notification.Builder(this, CHANNEL_ID)
            .setContentTitle("myXmic streaming")
            .setSmallIcon(android.R.drawable.ic_btn_speak_now).build())
    }

    private fun startStream(mode: Int, protocol: Int, host: String, port: Int,
                            btMac: String, sr: Int, gain: Float, ns: Boolean, aec: Boolean) {
        if (running.getAndSet(true)) return
        thread = Thread {
            Process.setThreadPriority(Process.THREAD_PRIORITY_URGENT_AUDIO)
            runCapture(mode, protocol, host, port, btMac, sr, gain, ns, aec)
            stopSelf()
        }.also { it.start() }
    }

    fun stopStream() {
        running.set(false)
        thread?.join(2000)
        thread = null
        stopForeground(true)
        stopSelf()
    }

    // ---------- 传输层 ----------

    private interface Transport : AutoCloseable { fun send(frame: ByteArray) }

    private class TcpTransport(host: String, port: Int) : Transport {
        private val sock = Socket().apply {
            tcpNoDelay = true
            connect(InetSocketAddress(host, port), 5000)
        }
        private val out = BufferedOutputStream(sock.getOutputStream(), 16384)
        override fun send(frame: ByteArray) { out.write(frame); out.flush() }
        override fun close() = sock.close()
    }

    private class UdpTransport(host: String, private val port: Int) : Transport {
        private val sock = DatagramSocket()
        private val addr = InetAddress.getByName(host)
        override fun send(frame: ByteArray) {
            sock.send(DatagramPacket(frame, frame.size, addr, port))
        }
        override fun close() = sock.close()
    }

    @SuppressLint("MissingPermission")
    private class BtTransport(mac: String) : Transport {
        private val sock = BluetoothAdapter.getDefaultAdapter()
            .getRemoteDevice(mac)
            .createRfcommSocketToServiceRecord(SPP_UUID)
        private lateinit var out: java.io.OutputStream
        init { sock.connect(); out = sock.outputStream }
        override fun send(frame: ByteArray) { out.write(frame); out.flush() }
        override fun close() = sock.close()
    }

    // ---------- 采集 + 推流 ----------

    @SuppressLint("MissingPermission")
    private fun runCapture(mode: Int, protocol: Int, host: String, port: Int,
                           btMac: String, sr: Int, gain: Float, ns: Boolean, aec: Boolean) {
        val frameBytes = sr / 1000 * 20 * 2 // 20ms
        val minBuf = AudioRecord.getMinBufferSize(
            sr, AudioFormat.CHANNEL_IN_MONO, AudioFormat.ENCODING_PCM_16BIT)
        val record = AudioRecord(
            MediaRecorder.AudioSource.VOICE_COMMUNICATION, sr,
            AudioFormat.CHANNEL_IN_MONO, AudioFormat.ENCODING_PCM_16BIT,
            maxOf(minBuf, frameBytes * 4)
        )

        var nsFx: NoiseSuppressor? = null
        var aecFx: AcousticEchoCanceler? = null
        try {
            if (ns && NoiseSuppressor.isAvailable())
                nsFx = NoiseSuppressor.create(record.audioSessionId)?.apply { enabled = true }
            if (aec && AcousticEchoCanceler.isAvailable())
                aecFx = AcousticEchoCanceler.create(record.audioSessionId)?.apply { enabled = true }
        } catch (t: Throwable) { android.util.Log.w("MicService", "DSP: ${t.message}") }

        var transport: Transport? = null
        try {
            transport = if (mode == 1) BtTransport(btMac)
                        else if (protocol == 1) UdpTransport(host, port)
                        else TcpTransport(host, port)

            // 帧头(采样率) + 数据
            val header = ByteBuffer.allocate(4).order(ByteOrder.LITTLE_ENDIAN).putInt(sr).array()
            val txBuf = ByteArray(4 + frameBytes).also { System.arraycopy(header, 0, it, 0, 4) }
            val pcm = ByteArray(frameBytes)

            record.startRecording()
            reportState("streaming")
            while (running.get()) {
                val n = record.read(pcm, 0, pcm.size)
                if (n <= 0) continue
                if (gain != 1f) applyGain(pcm, n, gain)
                reportLevel(pcm, n)
                System.arraycopy(pcm, 0, txBuf, 4, n)
                transport.send(txBuf.copyOf(4 + n))
            }
        } catch (e: Exception) {
            android.util.Log.e("MicService", "stream failed", e)
            reportState("error:" + (e.message ?: "?"))
        } finally {
            try { record.stop() } catch (_: Throwable) {}
            record.release()
            nsFx?.release(); aecFx?.release()
            try { transport?.close() } catch (_: Throwable) {}
            running.set(false)
        }
    }

    private fun reportState(s: String) =
        sendBroadcast(Intent(ACTION_LEVEL).putExtra(EXTRA_STATE, s))

    private var lastLevelAt = 0L
    private fun reportLevel(pcm: ByteArray, n: Int) {
        val now = System.currentTimeMillis()
        if (now - lastLevelAt < 200) return
        lastLevelAt = now
        var max = 0
        var i = 0
        while (i + 1 < n) {
            val v = Math.abs((((pcm[i + 1].toInt() and 0xFF) shl 8) or (pcm[i].toInt() and 0xFF))
                .toShort().toInt())
            if (v > max) max = v
            i += 2
        }
        sendBroadcast(Intent(ACTION_LEVEL).putExtra(EXTRA_LEVEL, max / 32768f))
    }

    private fun applyGain(buf: ByteArray, n: Int, g: Float) {
        var i = 0
        while (i + 1 < n) {
            val s = ((buf[i + 1].toInt() shl 8) or (buf[i].toInt() and 0xFF)).toShort()
            val v = (s * g).toInt().coerceIn(-32768, 32767).toShort()
            buf[i] = v.toByte(); buf[i + 1] = (v.toInt() shr 8).toByte()
            i += 2
        }
    }

    override fun onDestroy() { stopStream(); super.onDestroy() }
}
