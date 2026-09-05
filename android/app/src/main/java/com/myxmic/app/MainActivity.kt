package com.myxmic.app

import android.Manifest
import android.content.Intent
import android.content.pm.PackageManager
import android.os.Bundle
import android.widget.*
import androidx.appcompat.app.AppCompatActivity
import androidx.core.app.ActivityCompat
import androidx.core.content.ContextCompat

class MainActivity : AppCompatActivity() {

    private lateinit var btnToggle: Button
    private lateinit var editHost: EditText
    private lateinit var editPort: EditText
    private lateinit var chkNs: CheckBox
    private lateinit var chkAec: CheckBox
    private lateinit var txtStatus: TextView

    private var streaming = false

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)

        // 极简布局，省资源：不用 XML/约束布局，代码构建
        val root = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(48, 64, 48, 48)
        }
        fun row(hint: String, def: String): EditText {
            val e = EditText(this).apply { this.hint = hint; setText(def) }
            root.addView(e)
            return e
        }
        editHost = row("电脑 IP（USB 模式填 127.0.0.1）", "127.0.0.1")
        editPort = row("端口", "8125")
        chkNs = CheckBox(this).apply { text = "降噪 (Noise Suppressor)"; isChecked = true }
        chkAec = CheckBox(this).apply { text = "回声消除 (AEC)"; isChecked = false }
        root.addView(chkNs); root.addView(chkAec)
        txtStatus = TextView(this).apply { text = "未连接" }
        root.addView(txtStatus)
        btnToggle = Button(this).apply { text = "开始" }
        root.addView(btnToggle)
        setContentView(root)

        if (ContextCompat.checkSelfPermission(this, Manifest.permission.RECORD_AUDIO)
            != PackageManager.PERMISSION_GRANTED
        ) {
            ActivityCompat.requestPermissions(this, arrayOf(Manifest.permission.RECORD_AUDIO), 1)
        }

        btnToggle.setOnClickListener { if (streaming) stop() else start() }
    }

    private fun start() {
        if (ContextCompat.checkSelfPermission(this, Manifest.permission.RECORD_AUDIO)
            != PackageManager.PERMISSION_GRANTED
        ) {
            Toast.makeText(this, "请先授予麦克风权限", Toast.LENGTH_SHORT).show()
            return
        }
        val intent = Intent(this, MicService::class.java).apply {
            action = MicService.ACTION_START
            putExtra(MicService.EXTRA_HOST, editHost.text.toString().trim())
            putExtra(MicService.EXTRA_PORT, editPort.text.toString().toIntOrNull() ?: 8125)
            putExtra(MicService.EXTRA_NS, chkNs.isChecked)
            putExtra(MicService.EXTRA_AEC, chkAec.isChecked)
        }
        ContextCompat.startForegroundService(this, intent)
        streaming = true
        btnToggle.text = "停止"
        txtStatus.text = "推流中…"
    }

    private fun stop() {
        startService(Intent(this, MicService::class.java).setAction(MicService.ACTION_STOP))
        streaming = false
        btnToggle.text = "开始"
        txtStatus.text = "未连接"
    }
}
