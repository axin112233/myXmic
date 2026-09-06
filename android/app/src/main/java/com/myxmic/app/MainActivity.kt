package com.myxmic.app

import android.Manifest
import android.annotation.SuppressLint
import android.bluetooth.BluetoothAdapter
import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.content.IntentFilter
import android.content.pm.PackageManager
import android.graphics.Color
import android.graphics.drawable.GradientDrawable
import android.os.Build
import android.os.Bundle
import android.view.View
import android.view.ViewGroup
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

    private val C_BG = Color.rgb(15, 17, 21)
    private val C_CARD = Color.rgb(23, 27, 36)
    private val C_FG = Color.rgb(232, 234, 242)
    private val C_SUB = Color.rgb(154, 164, 191)
    private val C_ACCENT = Color.rgb(79, 107, 255)

    private lateinit var spnMode: Spinner
    private lateinit var rgProto: RadioGroup
    private lateinit var editHost: EditText
    private lateinit var btnFind: Button
    private lateinit var spnBt: Spinner
    private lateinit var btMacs: List<String>
    private lateinit var editPort: EditText
    private lateinit var spnSr: Spinner
    private lateinit var seekGain: SeekBar
    private lateinit var txtGain: TextView
    private lateinit var chkNs: CheckBox
    private lateinit var chkAec: CheckBox
    private lateinit var txtStatus: TextView
    private lateinit var levelBar: ProgressBar
    private lateinit var btnToggle: Button

    private var streaming = false

    private val receiver = object : BroadcastReceiver() {
        override fun onReceive(ctx: Context, i: Intent) {
            i.getStringExtra(MicService.EXTRA_STATE)?.let { st ->
                when {
                    st == "streaming" -> txtStatus.text = getString(R.string.status_streaming)
                    st.startsWith("error:") -> { txtStatus.text = "❌ " + st.removePrefix("error:"); uiStopped() }
                }
            }
            levelBar.progress = (i.getFloatExtra(MicService.EXTRA_LEVEL, 0f) * 100).toInt()
        }
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContentView(buildUi())

        requestPerms()
        ContextCompat.registerReceiver(this, receiver,
            IntentFilter(MicService.ACTION_LEVEL), ContextCompat.RECEIVER_NOT_EXPORTED)

        spnMode.onItemSelectedListener = object : AdapterView.OnItemSelectedListener {
            override fun onItemSelected(p: AdapterView<*>?, v: View?, pos: Int, id: Long) = applyMode(pos)
            override fun onNothingSelected(p: AdapterView<*>?) {}
        }
        btnFind.setOnClickListener { autoFind() }
        btnToggle.setOnClickListener { if (streaming) stop() else start() }
        applyMode(0)
    }

    override fun onDestroy() {
        unregisterReceiver(receiver)
        super.onDestroy()
    }

    // ---------- UI 构建（深色卡片风） ----------

    private fun card(): LinearLayout {
        val d = GradientDrawable().apply {
            setColor(C_CARD); cornerRadius = dp(12).toFloat()
        }
        return LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            background = d
            setPadding(dp(14), dp(10), dp(14), dp(10))
            layoutParams = LinearLayout.LayoutParams(-1, -2).apply { bottomMargin = dp(10) }
        }
    }

    private fun label(t: String) = TextView(this).apply {
        text = t; textSize = 13f; setTextColor(C_SUB); setPadding(0, 0, 0, dp(4))
    }

    private fun dp(v: Int) = (v * resources.displayMetrics.density).toInt()

    private fun styleField(v: View) {
        v.setBackgroundColor(C_BG)
        if (v is TextView) { v.setTextColor(C_FG); v.setHintTextColor(C_SUB) }
    }

    private fun buildUi(): View {
        val scroll = ScrollView(this).apply { setBackgroundColor(C_BG) }
        val root = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(dp(16), dp(12), dp(16), dp(16))
        }
        scroll.addView(root)

        // 顶栏：标题 + 语言
        val top = LinearLayout(this).apply { orientation = LinearLayout.HORIZONTAL }
        top.addView(TextView(this).apply {
            text = "myXmic"; textSize = 22f; setTextColor(C_FG)
            setTypeface(typeface, android.graphics.Typeface.BOLD)
        }, LinearLayout.LayoutParams(0, -2, 1f))
        top.addView(Button(this).apply {
            text = getString(R.string.lang_switch)
            setBackgroundColor(Color.TRANSPARENT); setTextColor(C_SUB)
            setOnClickListener { switchLang() }
        })
        root.addView(top)

        // ---- 连接卡 ----
        val conn = card()
        conn.addView(label(getString(R.string.mode)))
        spnMode = Spinner(this)
        spnMode.adapter = ArrayAdapter(this, android.R.layout.simple_spinner_dropdown_item,
            listOf("USB (ADB)", getString(R.string.wifi), getString(R.string.bluetooth)))
        conn.addView(spnMode)

        conn.addView(label(getString(R.string.protocol)))
        rgProto = RadioGroup(this).apply {
            orientation = RadioGroup.HORIZONTAL
            addView(RadioButton(this@MainActivity).apply {
                text = "TCP"; id = 1; isChecked = true; setTextColor(C_FG) })
            addView(RadioButton(this@MainActivity).apply {
                text = "UDP"; id = 2; setTextColor(C_FG) })
        }
        conn.addView(rgProto)

        conn.addView(label(getString(R.string.server_ip)))
        val ipRow = LinearLayout(this).apply { orientation = LinearLayout.HORIZONTAL }
        editHost = EditText(this).apply { setText("127.0.0.1"); styleField(this) }
        ipRow.addView(editHost, LinearLayout.LayoutParams(0, -2, 1f))
        btnFind = Button(this).apply { text = getString(R.string.auto_find) }
        ipRow.addView(btnFind)
        conn.addView(ipRow)

        conn.addView(label(getString(R.string.bluetooth)))
        spnBt = Spinner(this)
        btMacs = emptyList(); refreshBtDevices()
        conn.addView(spnBt)

        conn.addView(label(getString(R.string.port)))
        editPort = EditText(this).apply { setText("8125"); styleField(this) }
        conn.addView(editPort)
        root.addView(conn)

        // ---- 状态卡 ----
        val stat = card()
        txtStatus = TextView(this).apply {
            text = getString(R.string.status_idle); textSize = 18f; setTextColor(C_FG)
            setTypeface(typeface, android.graphics.Typeface.BOLD)
        }
        stat.addView(txtStatus)
        levelBar = ProgressBar(this, null, android.R.attr.progressBarStyleHorizontal).apply {
            max = 100
        }
        stat.addView(levelBar, LinearLayout.LayoutParams(-1, dp(8)).apply { topMargin = dp(8) })
        root.addView(stat)

        // ---- 设置卡 ----
        val opt = card()
        opt.addView(label(getString(R.string.sample_rate)))
        spnSr = Spinner(this)
        spnSr.adapter = ArrayAdapter(this, android.R.layout.simple_spinner_dropdown_item,
            listOf("48000 Hz", "16000 Hz"))
        opt.addView(spnSr)

        opt.addView(label(getString(R.string.gain)))
        val gainRow = LinearLayout(this).apply { orientation = LinearLayout.HORIZONTAL }
        seekGain = SeekBar(this).apply { max = 250; progress = 90 } // +10 => 100%
        txtGain = TextView(this).apply { text = "100%"; setTextColor(C_FG); setPadding(dp(8), 0, 0, 0) }
        gainRow.addView(seekGain, LinearLayout.LayoutParams(0, -2, 1f))
        gainRow.addView(txtGain)
        opt.addView(gainRow)
        seekGain.setOnSeekBarChangeListener(object : SeekBar.OnSeekBarChangeListener {
            override fun onProgressChanged(sb: SeekBar?, p: Int, u: Boolean) { txtGain.text = "${p + 10}%" }
            override fun onStartTrackingTouch(sb: SeekBar?) {}
            override fun onStopTrackingTouch(sb: SeekBar?) {}
        })

        chkNs = CheckBox(this).apply { text = getString(R.string.ns); isChecked = true; setTextColor(C_FG) }
        chkAec = CheckBox(this).apply { text = getString(R.string.aec); isChecked = false; setTextColor(C_FG) }
        opt.addView(chkNs); opt.addView(chkAec)
        root.addView(opt)

        // 大启动按钮
        btnToggle = Button(this).apply {
            text = getString(R.string.start); textSize = 16f; setTextColor(Color.WHITE)
            setBackgroundColor(C_ACCENT)
        }
        root.addView(btnToggle, LinearLayout.LayoutParams(-1, dp(50)).apply { topMargin = dp(4) })
        return scroll
    }

    private fun applyMode(mode: Int) {
        // 0=USB 1=Wi-Fi 2=BT
        val net = if (mode == 2) View.GONE else View.VISIBLE
        rgProto.visibility = net
        btnFind.visibility = if (mode == 1) View.VISIBLE else View.GONE
        editHost.visibility = net
        editPort.visibility = net
        spnBt.visibility = if (mode == 2) View.VISIBLE else View.GONE
        if (mode == 0) { editHost.setText("127.0.0.1"); editHost.isEnabled = false }
        else editHost.isEnabled = true
        when (mode) {
            0 -> txtStatus.hint = getString(R.string.usb_hint)
            2 -> txtStatus.hint = getString(R.string.bt_hint)
        }
    }

    @SuppressLint("MissingPermission")
    private fun refreshBtDevices() {
        try {
            val names = mutableListOf<String>()
            val macs = mutableListOf<String>()
            BluetoothAdapter.getDefaultAdapter()?.bondedDevices?.forEach {
                names.add(it.name ?: it.address); macs.add(it.address)
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
                s.broadcast = true; s.soTimeout = 2000
                val msg = "MYXMIC_DISCOVER".toByteArray()
                s.send(DatagramPacket(msg, msg.size, InetAddress.getByName("255.255.255.255"), 8124))
                val pkt = DatagramPacket(ByteArray(64), 64)
                s.receive(pkt)
                val ip = pkt.address.hostAddress ?: ""
                s.close()
                runOnUiThread { editHost.setText(ip); txtStatus.text = getString(R.string.status_idle) }
            } catch (e: Exception) {
                runOnUiThread { txtStatus.text = getString(R.string.not_found) }
            }
        }.start()
    }

    private fun switchLang() {
        val cur = AppCompatDelegate.getApplicationLocales().toLanguageTags()
        AppCompatDelegate.setApplicationLocales(
            LocaleListCompat.forLanguageTags(if (cur.startsWith("en")) "zh" else "en"))
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
        val mode = spnMode.selectedItemPosition // 0=USB 1=Wi-Fi 2=BT
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
        txtStatus.text = getString(R.string.status_connecting)
        levelBar.progress = 0
    }

    private fun stop() {
        startService(Intent(this, MicService::class.java).setAction(MicService.ACTION_STOP))
        uiStopped()
    }

    private fun uiStopped() {
        streaming = false
        btnToggle.text = getString(R.string.start)
        txtStatus.text = getString(R.string.status_idle)
        levelBar.progress = 0
    }
}
