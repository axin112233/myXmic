package com.myxmic.app

import android.Manifest
import android.annotation.SuppressLint
import android.bluetooth.BluetoothAdapter
import android.content.Intent
import android.content.pm.PackageManager
import android.os.Build
import android.os.Bundle
import android.view.View
import android.widget.*
import androidx.appcompat.app.AppCompatActivity
import androidx.appcompat.app.AppCompatDelegate
import androidx.core.app.ActivityCompat
import androidx.core.content.ContextCompat
import androidx.core.os.LocaleListCompat
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetAddress

class MainActivity : AppCompatActivity() {

    private lateinit var spnMode: Spinner      // Wi-Fi / USB / 蓝牙
    private lateinit var rgProto: RadioGroup   // TCP / UDP
    private lateinit var editHost: EditText
    private lateinit var btnFind: Button
    private lateinit var spnBt: Spinner        // 已配对蓝牙设备
    private lateinit var btMacs: List<String>
    private lateinit var editPort: EditText
    private lateinit var spnSr: Spinner        // 采样率
    private lateinit var seekGain: SeekBar
    private lateinit var txtGain: TextView
    private lateinit var chkNs: CheckBox
    private lateinit var chkAec: CheckBox
    private lateinit var txtStatus: TextView
    private lateinit var btnToggle: Button
    private lateinit var btnLang: Button
    private lateinit var txtHint: TextView

    private var streaming = false

    @SuppressLint("MissingPermission")
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)

        val root = ScrollView(this)
        val box = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(48, 40, 48, 40)
        }
        root.addView(box)
        setContentView(root)

        fun label(res: Int) = TextView(this).apply {
            text = getString(res); textSize = 14f; setPadding(0, 16, 0, 4)
        }.also { box.addView(it) }

        // 连接方式
        label(R.string.mode)
        spnMode = Spinner(this)
        spnMode.adapter = ArrayAdapter(this, android.R.layout.simple_spinner_dropdown_item,
            listOf(getString(R.string.wifi), getString(R.string.usb), getString(R.string.bluetooth)))
        box.addView(spnMode)

        // 协议
        label(R.string.protocol)
        rgProto = RadioGroup(this).apply {
            orientation = RadioGroup.HORIZONTAL
            addView(RadioButton(this@MainActivity).apply { text = "TCP"; id = 1; isChecked = true })
            addView(RadioButton(this@MainActivity).apply { text = "UDP"; id = 2 })
        }
        box.addView(rgProto)

        // 服务器地址 + 自动搜索
        label(R.string.server_ip)
        val ipRow = LinearLayout(this).apply { orientation = LinearLayout.HORIZONTAL }
        editHost = EditText(this).apply { setText("192.168.1.1") }
        ipRow.addView(editHost, LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1f))
        btnFind = Button(this).apply { text = getString(R.string.auto_find) }
        ipRow.addView(btnFind)
        box.addView(ipRow)

        // 蓝牙已配对设备
        label(R.string.bluetooth)
        spnBt = Spinner(this)
        btMacs = emptyList()
        refreshBtDevices()
        box.addView(spnBt)

        // 端口
        label(R.string.port)
        editPort = EditText(this).apply { setText("8125") }
        box.addView(editPort)

        // 采样率
        label(R.string.sample_rate)
        spnSr = Spinner(this)
        spnSr.adapter = ArrayAdapter(this, android.R.layout.simple_spinner_dropdown_item,
            listOf("48000 Hz (高音质)", "16000 Hz (省流量)"))
        box.addView(spnSr)

        // 增益
        label(R.string.gain)
        val gainRow = LinearLayout(this).apply { orientation = LinearLayout.HORIZONTAL }
        seekGain = SeekBar(this).apply { max = 250; progress = 100 } // 10%~260% => 实际是 0.1~2.6
        txtGain = TextView(this).apply { text = "100%"; setPadding(16, 0, 0, 0) }
        gainRow.addView(seekGain, LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1f))
        gainRow.addView(txtGain)
        box.addView(gainRow)
        seekGain.setOnSeekBarChangeListener(object : SeekBar.OnSeekBarChangeListener {
            override fun onProgressChanged(sb: SeekBar?, p: Int, u: Boolean) { txtGain.text = "${p + 10}%" }
            override fun onStartTrackingTouch(sb: SeekBar?) {}
            override fun onStopTrackingTouch(sb: SeekBar?) {}
        })

        // DSP
        chkNs = CheckBox(this).apply { text = getString(R.string.ns); isChecked = true }
        chkAec = CheckBox(this).apply { text = getString(R.string.aec); isChecked = false }
        box.addView(chkNs); box.addView(chkAec)

        // 状态 + 按钮
        txtStatus = TextView(this).apply { text = getString(R.string.status_idle); setPadding(0, 16, 0, 8) }
        box.addView(txtStatus)
        btnToggle = Button(this).apply { text = getString(R.string.start) }
        box.addView(btnToggle)
        btnLang = Button(this).apply { text = getString(R.string.lang_switch); setPadding(0, 8, 0, 0) }
        box.addView(btnLang)
        txtHint = TextView(this).apply { textSize = 12f; setPadding(0, 12, 0, 0) }
        box.addView(txtHint)

        requestPerms()

        spnMode.onItemSelectedListener = object : AdapterView.OnItemSelectedListener {
            override fun onItemSelected(p: AdapterView<*>?, v: View?, pos: Int, id: Long) = applyMode(pos)
            override fun onNothingSelected(p: AdapterView<*>?) {}
        }
        btnFind.setOnClickListener { autoFind() }
        btnToggle.setOnClickListener { if (streaming) stop() else start() }
        btnLang.setOnClickListener { switchLang() }
        applyMode(0)
    }

    private fun applyMode(mode: Int) {
        // 蓝牙只用 BT，其余显示网络设置
        val netVisible = if (mode == 2) View.GONE else View.VISIBLE
        rgProto.visibility = netVisible
        btnFind.visibility = if (mode == 0) View.VISIBLE else View.GONE
        editHost.visibility = netVisible
        spnBt.visibility = if (mode == 2) View.VISIBLE else View.GONE
        if (mode == 1) editHost.setText("127.0.0.1")
        editHost.isEnabled = mode != 1
        txtHint.text = when (mode) {
            1 -> getString(R.string.usb_hint)
            2 -> getString(R.string.bt_hint)
            else -> ""
        }
    }

    @SuppressLint("MissingPermission")
    private fun refreshBtDevices() {
        try {
            val names = mutableListOf<String>()
            val macs = mutableListOf<String>()
            BluetoothAdapter.getDefaultAdapter()?.bondedDevices?.forEach {
                names.add("${it.name}")
                macs.add(it.address)
            }
            if (names.isEmpty()) { names.add(getString(R.string.bt_hint)); macs.add("") }
            spnBt.adapter = ArrayAdapter(this, android.R.layout.simple_spinner_dropdown_item, names)
            btMacs = macs
        } catch (e: Exception) {
            Toast.makeText(this, "BT: ${e.message}", Toast.LENGTH_SHORT).show()
        }
    }

    private fun autoFind() {
        txtStatus.text = "…"
        Thread {
            try {
                val s = DatagramSocket()
                s.broadcast = true
                s.soTimeout = 2000
                val msg = "MYXMIC_DISCOVER".toByteArray()
                s.send(DatagramPacket(msg, msg.size,
                    InetAddress.getByName("255.255.255.255"), 8124))
                val buf = ByteArray(64)
                val pkt = DatagramPacket(buf, buf.size)
                s.receive(pkt)
                val ip = pkt.address.hostAddress ?: ""
                s.close()
                runOnUiThread {
                    editHost.setText(ip)
                    txtStatus.text = getString(R.string.status_idle)
                }
            } catch (e: Exception) {
                runOnUiThread { txtStatus.text = getString(R.string.not_found) }
            }
        }.start()
    }

    private fun switchLang() {
        val cur = AppCompatDelegate.getApplicationLocales().toLanguageTags()
        val next = if (cur.startsWith("en")) "zh" else "en"
        AppCompatDelegate.setApplicationLocales(LocaleListCompat.forLanguageTags(next))
    }

    private fun requestPerms() {
        val need = mutableListOf(Manifest.permission.RECORD_AUDIO)
        if (Build.VERSION.SDK_INT >= 31) need.add(Manifest.permission.BLUETOOTH_CONNECT)
        val missing = need.filter {
            ContextCompat.checkSelfPermission(this, it) != PackageManager.PERMISSION_GRANTED
        }
        if (missing.isNotEmpty())
            ActivityCompat.requestPermissions(this, missing.toTypedArray(), 1)
    }

    private fun start() {
        if (ContextCompat.checkSelfPermission(this, Manifest.permission.RECORD_AUDIO)
            != PackageManager.PERMISSION_GRANTED) {
            Toast.makeText(this, R.string.need_mic_perm, Toast.LENGTH_SHORT).show()
            requestPerms(); return
        }
        val mode = spnMode.selectedItemPosition
        val intent = Intent(this, MicService::class.java).apply {
            action = MicService.ACTION_START
            putExtra(MicService.EXTRA_MODE, if (mode == 2) 1 else 0)
            putExtra(MicService.EXTRA_PROTOCOL, if (rgProto.checkedRadioButtonId == 2) 1 else 0)
            putExtra(MicService.EXTRA_HOST, editHost.text.toString().trim())
            putExtra(MicService.EXTRA_PORT, editPort.text.toString().toIntOrNull() ?: 8125)
            putExtra(MicService.EXTRA_BT_MAC, btMacs.getOrElse(spnBt.selectedItemPosition) { "" })
            putExtra(MicService.EXTRA_SR, if (spnSr.selectedItemPosition == 1) 16000 else 48000)
            putExtra(MicService.EXTRA_GAIN, (seekGain.progress + 10) / 100f)
            putExtra(MicService.EXTRA_NS, chkNs.isChecked)
            putExtra(MicService.EXTRA_AEC, chkAec.isChecked)
        }
        ContextCompat.startForegroundService(this, intent)
        streaming = true
        btnToggle.text = getString(R.string.stop)
        txtStatus.text = getString(R.string.status_streaming)
    }

    private fun stop() {
        startService(Intent(this, MicService::class.java).setAction(MicService.ACTION_STOP))
        streaming = false
        btnToggle.text = getString(R.string.start)
        txtStatus.text = getString(R.string.status_idle)
    }
}
